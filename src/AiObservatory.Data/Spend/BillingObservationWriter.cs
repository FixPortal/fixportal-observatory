using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiObservatory.Data.Entities;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace AiObservatory.Data.Spend;

public enum BillingWriteDisposition
{
    Created,
    Corrected,
    Unchanged,
}

/// <summary>Atomically retains a provider billing fact and its non-zero billed spend.</summary>
public class BillingObservationWriter(AiObservatoryDbContext db, FxRateProvider fx, IClock clock)
{
    public virtual async Task<BillingWriteDisposition> RecordAsync(
        BillingObservation observation,
        string vendorKey,
        string categoryKey,
        CancellationToken cancellationToken = default
    )
    {
        Validate(observation, vendorKey, categoryKey);
        cancellationToken.ThrowIfCancellationRequested();

        decimal? fxRate = null;
        decimal? amountGbp = null;
        if (observation.NetAmount != 0m)
        {
            fxRate = await fx.GetGbpRateOnAsync(observation.Currency, observation.OccurredOn, cancellationToken);
            if (fxRate <= 0m)
            {
                throw new InvalidOperationException("FX rate must be positive.");
            }

            amountGbp = decimal.Round(observation.NetAmount * fxRate.Value, 4, MidpointRounding.ToEven);
            if (amountGbp == 0m)
            {
                throw new InvalidOperationException("The billed amount rounds to zero GBP.");
            }
        }

        var entryKey = DeriveEntryKey(observation.SourceId, observation.ObservationKey);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var result = await RecordWithinTransactionAsync(
                observation,
                vendorKey,
                categoryKey,
                entryKey,
                fxRate,
                amountGbp,
                cancellationToken
            );
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            throw;
        }
    }

    private async Task<BillingWriteDisposition> RecordWithinTransactionAsync(
        BillingObservation observation,
        string vendorKey,
        string categoryKey,
        string entryKey,
        decimal? fxRate,
        decimal? amountGbp,
        CancellationToken cancellationToken
    )
    {
        var lockMaterial = $"{Part(observation.SourceId)}{Part(observation.ObservationKey)}";
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({lockMaterial}, 0))",
            cancellationToken
        );

        var (created, observationChanged) = await ApplyObservationAsync(observation, cancellationToken);
        var spend = await FindSpendAsync(observation, entryKey, cancellationToken);
        var spendChanged = await ApplySpendAsync(
            spend,
            observation,
            vendorKey,
            categoryKey,
            entryKey,
            fxRate,
            amountGbp,
            observationChanged,
            cancellationToken
        );

        if (observationChanged || spendChanged)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        if (created)
        {
            return BillingWriteDisposition.Created;
        }
        return observationChanged || spendChanged
            ? BillingWriteDisposition.Corrected
            : BillingWriteDisposition.Unchanged;
    }

    private async Task<(bool Created, bool Changed)> ApplyObservationAsync(
        BillingObservation observation,
        CancellationToken cancellationToken
    )
    {
        var stored = await db.BillingObservations.SingleOrDefaultAsync(
            candidate =>
                candidate.SourceId == observation.SourceId && candidate.ObservationKey == observation.ObservationKey,
            cancellationToken
        );
        if (stored is null)
        {
            db.BillingObservations.Add(observation);
            return (true, true);
        }
        if (HasSameFacts(stored, observation))
        {
            return (false, false);
        }

        CopyFacts(observation, stored);
        return (false, true);
    }

    private async Task<SpendEntry?> FindSpendAsync(
        BillingObservation observation,
        string entryKey,
        CancellationToken cancellationToken
    )
    {
        var canonicalMatches = await db
            .SpendEntries.Where(entry =>
                entry.Source == SpendSource.Api
                && entry.SourceId == observation.SourceId
                && (entry.EntryKey == entryKey || entry.EntryKey == observation.ObservationKey)
            )
            .ToListAsync(cancellationToken);
        var canonical = canonicalMatches.Count switch
        {
            0 => null,
            1 => canonicalMatches[0],
            _ => throw new InvalidOperationException("More than one spend row matches the billing observation."),
        };
        if (
            canonical is not null
            || observation.SourceId != UsageSourceIds.GitHubBillingApi
            || !observation.ObservationKey.StartsWith("github:", StringComparison.Ordinal)
        )
        {
            return canonical;
        }

        // The provenance migration deliberately labelled every existing spend row as
        // legacy-spend. GitHub rows still have an unambiguous source-specific key, so the
        // first retained observation can adopt that row instead of inserting a second copy.
        return await db.SpendEntries.SingleOrDefaultAsync(
            entry =>
                entry.Source == SpendSource.Api
                && entry.SourceId == UsageSourceIds.LegacySpend
                && entry.EntryKey == observation.ObservationKey,
            cancellationToken
        );
    }

    private async Task<bool> ApplySpendAsync(
        SpendEntry? spend,
        BillingObservation observation,
        string vendorKey,
        string categoryKey,
        string entryKey,
        decimal? fxRate,
        decimal? amountGbp,
        bool observationChanged,
        CancellationToken cancellationToken
    )
    {
        if (observation.NetAmount == 0m)
        {
            if (spend is null)
            {
                return false;
            }

            db.SpendEntries.Remove(spend);
            return true;
        }

        var resolvedRate = fxRate ?? throw new InvalidOperationException("FX rate is missing.");
        var resolvedAmountGbp = amountGbp ?? throw new InvalidOperationException("GBP amount is missing.");
        if (spend is null)
        {
            var vendorId = await ResolveVendorAsync(vendorKey, cancellationToken);
            var categoryId = await ResolveCategoryAsync(categoryKey, cancellationToken);
            db.SpendEntries.Add(
                NewSpendEntry(observation, entryKey, vendorId, categoryId, resolvedRate, resolvedAmountGbp)
            );
            return true;
        }
        if (!observationChanged && HasSameProviderFacts(spend, observation, entryKey))
        {
            return false;
        }

        CopyProviderFacts(observation, spend, entryKey, resolvedRate, resolvedAmountGbp);
        return true;
    }

    private async Task<Guid> ResolveVendorAsync(string vendorKey, CancellationToken cancellationToken) =>
        await db
            .SpendVendors.Where(vendor => vendor.Key == vendorKey)
            .Select(vendor => (Guid?)vendor.Id)
            .SingleOrDefaultAsync(cancellationToken)
        ?? throw new InvalidOperationException($"Spend vendor '{vendorKey}' does not exist.");

    private async Task<Guid> ResolveCategoryAsync(string categoryKey, CancellationToken cancellationToken) =>
        await db
            .SpendCategories.Where(category => category.Key == categoryKey)
            .Select(category => (Guid?)category.Id)
            .SingleOrDefaultAsync(cancellationToken)
        ?? throw new InvalidOperationException($"Spend category '{categoryKey}' does not exist.");

    private SpendEntry NewSpendEntry(
        BillingObservation observation,
        string entryKey,
        Guid vendorId,
        Guid categoryId,
        decimal fxRate,
        decimal amountGbp
    )
    {
        var recordedAt = clock.GetCurrentInstant();
        return new SpendEntry
        {
            OccurredOn = observation.OccurredOn,
            VendorId = vendorId,
            CategoryId = categoryId,
            Amount = observation.NetAmount,
            Currency = observation.Currency,
            AmountGbp = amountGbp,
            FxRate = fxRate,
            Description = DescriptionFor(observation),
            Source = SpendSource.Api,
            EntryKey = entryKey,
            RecordedAt = recordedAt,
            RawPayload = observation.RawPayload,
            SourceId = observation.SourceId,
            SourceKind = observation.SourceKind,
            UsageScope = observation.UsageScope,
            CostBasis = observation.CostBasis,
            ObservedAt = observation.ObservedAt,
        };
    }

    private void CopyProviderFacts(
        BillingObservation observation,
        SpendEntry spend,
        string entryKey,
        decimal fxRate,
        decimal amountGbp
    )
    {
        spend.OccurredOn = observation.OccurredOn;
        spend.Amount = observation.NetAmount;
        spend.Currency = observation.Currency;
        spend.AmountGbp = amountGbp;
        spend.FxRate = fxRate;
        spend.Description = DescriptionFor(observation);
        spend.EntryKey = entryKey;
        spend.RecordedAt = clock.GetCurrentInstant();
        spend.RawPayload = observation.RawPayload;
        spend.SourceId = observation.SourceId;
        spend.SourceKind = observation.SourceKind;
        spend.UsageScope = observation.UsageScope;
        spend.CostBasis = observation.CostBasis;
        spend.ObservedAt = observation.ObservedAt;
    }

    private static bool HasSameProviderFacts(SpendEntry spend, BillingObservation observation, string entryKey) =>
        spend.OccurredOn == observation.OccurredOn
        && spend.Amount == observation.NetAmount
        && spend.Currency == observation.Currency
        && spend.Description == DescriptionFor(observation)
        && spend.EntryKey == entryKey
        && JsonEquals(spend.RawPayload, observation.RawPayload)
        && spend.SourceKind == observation.SourceKind
        && spend.UsageScope == observation.UsageScope
        && spend.CostBasis == observation.CostBasis;

    private static bool HasSameFacts(BillingObservation stored, BillingObservation candidate) =>
        stored.ProviderKey == candidate.ProviderKey
        && stored.SourceKind == candidate.SourceKind
        && stored.UsageScope == candidate.UsageScope
        && stored.CostBasis == candidate.CostBasis
        && stored.OccurredOn == candidate.OccurredOn
        && stored.BillingPeriod == candidate.BillingPeriod
        && stored.Service == candidate.Service
        && stored.Sku == candidate.Sku
        && stored.Currency == candidate.Currency
        && stored.GrossAmount == candidate.GrossAmount
        && stored.CreditAmount == candidate.CreditAmount
        && stored.NetAmount == candidate.NetAmount
        && JsonEquals(stored.RawPayload, candidate.RawPayload);

    private static void CopyFacts(BillingObservation source, BillingObservation target)
    {
        target.ProviderKey = source.ProviderKey;
        target.SourceKind = source.SourceKind;
        target.UsageScope = source.UsageScope;
        target.CostBasis = source.CostBasis;
        target.OccurredOn = source.OccurredOn;
        target.BillingPeriod = source.BillingPeriod;
        target.Service = source.Service;
        target.Sku = source.Sku;
        target.Currency = source.Currency;
        target.GrossAmount = source.GrossAmount;
        target.CreditAmount = source.CreditAmount;
        target.NetAmount = source.NetAmount;
        target.RawPayload = source.RawPayload;
        target.ObservedAt = source.ObservedAt;
    }

    private static bool JsonEquals(string left, string right)
    {
        using var leftDocument = JsonDocument.Parse(left);
        using var rightDocument = JsonDocument.Parse(right);
        return JsonElement.DeepEquals(leftDocument.RootElement, rightDocument.RootElement);
    }

    private static string DescriptionFor(BillingObservation observation)
    {
        var description = observation.Sku ?? observation.Service ?? observation.ProviderKey;
        return description.Length <= 200 ? description : description[..200];
    }

    private static string DeriveEntryKey(string sourceId, string observationKey)
    {
        var readable = $"billing:{sourceId}:{observationKey}";
        if (readable.Length <= 200)
        {
            return readable;
        }

        var material = $"{Part(sourceId)}{Part(observationKey)}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return $"billing:{Convert.ToHexStringLower(hash)}";
    }

    private static string Part(string value) => $"{value.Length.ToString(CultureInfo.InvariantCulture)}:{value}";

    private static void Validate(BillingObservation observation, string vendorKey, string categoryKey)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ValidateIdentity(observation.ProviderKey, 200, nameof(observation.ProviderKey));
        ValidateIdentity(observation.SourceId, 200, nameof(observation.SourceId));
        ValidateIdentity(observation.ObservationKey, 200, nameof(observation.ObservationKey));
        ValidateIdentity(vendorKey, 60, nameof(vendorKey));
        ValidateIdentity(categoryKey, 60, nameof(categoryKey));

        if (observation.ProviderKey != observation.ProviderKey.ToLowerInvariant())
        {
            throw new ArgumentException("ProviderKey must be lower-case.", nameof(observation));
        }
        if (observation.Currency.Length != 3 || observation.Currency.Any(character => character is < 'A' or > 'Z'))
        {
            throw new ArgumentException("Currency must be a three-letter upper-case code.", nameof(observation));
        }
        if (observation.SourceKind != SourceKind.ProviderApi || observation.CostBasis != CostBasis.Billed)
        {
            throw new ArgumentException("Billing observations must be ProviderApi/Billed.", nameof(observation));
        }
        if (observation.GrossAmount + observation.CreditAmount != observation.NetAmount)
        {
            throw new ArgumentException("GrossAmount plus CreditAmount must equal NetAmount.", nameof(observation));
        }
        // Mirrors CK_BillingObservation_Credit_Sign: a credit reduces the gross amount, so
        // a positive credit would balance the equation above while inflating net spend.
        ArgumentOutOfRangeException.ThrowIfGreaterThan(observation.CreditAmount, 0m);
        if (observation.BillingPeriod is { } period && string.IsNullOrWhiteSpace(period))
        {
            throw new ArgumentException("BillingPeriod cannot be blank.", nameof(observation));
        }
        if (observation.Service is { } service && string.IsNullOrWhiteSpace(service))
        {
            throw new ArgumentException("Service cannot be blank.", nameof(observation));
        }
        if (observation.Sku is { } sku && string.IsNullOrWhiteSpace(sku))
        {
            throw new ArgumentException("Sku cannot be blank.", nameof(observation));
        }
        if (string.IsNullOrWhiteSpace(observation.RawPayload))
        {
            throw new ArgumentException("RawPayload must be valid JSON.", nameof(observation));
        }

        try
        {
            using var _ = JsonDocument.Parse(observation.RawPayload);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("RawPayload must be valid JSON.", nameof(observation), exception);
        }
    }

    private static void ValidateIdentity(string value, int maximumLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value != value.Trim())
        {
            throw new ArgumentException($"{parameterName} is invalid.", parameterName);
        }
    }
}
