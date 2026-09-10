using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Pricing.Catalogs;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace AiObservatory.Data.Pricing;

public sealed class PricingSnapshotStore(AiObservatoryDbContext db)
{
    public async Task<PricingActivationResult> ActivateAsync(
        PricingSnapshotCandidate candidate,
        CancellationToken cancellationToken,
        Func<PricingSnapshot, CancellationToken, Task>? beforeCommit = null
    ) => await ActivateAsync(candidate, onlyIfMissing: false, cancellationToken, beforeCommit);

    public async Task<PricingActivationResult> ActivateIfMissingAsync(
        PricingSnapshotCandidate candidate,
        CancellationToken cancellationToken,
        Func<PricingSnapshot, CancellationToken, Task>? beforeCommit = null
    ) => await ActivateAsync(candidate, onlyIfMissing: true, cancellationToken, beforeCommit);

    private async Task<PricingActivationResult> ActivateAsync(
        PricingSnapshotCandidate candidate,
        bool onlyIfMissing,
        CancellationToken cancellationToken,
        Func<PricingSnapshot, CancellationToken, Task>? beforeCommit
    )
    {
        Validate(candidate);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({candidate.SourceId}, 0))",
                cancellationToken
            );

            if (
                onlyIfMissing
                && await db.PricingSnapshots.AnyAsync(
                    snapshot => snapshot.SourceId == candidate.SourceId && snapshot.IsActive,
                    cancellationToken
                )
            )
            {
                await transaction.CommitAsync(cancellationToken);
                return PricingActivationResult.Unchanged;
            }

            // The hash check must distinguish "already active" from "seen before": the
            // (SourceId, ContentHash) unique index means a historic row carrying the hash
            // blocks a fresh insert, so a source that reverts to an earlier document
            // (A -> B -> A) can only be represented by reactivating the existing row.
            // AsNoTracking: the insert path deactivates rows via ExecuteUpdate, so a
            // previously activated snapshot can still be tracked here with a stale
            // IsActive. Identity resolution must not turn a reactivation into Unchanged.
            var hashMatch = await db
                .PricingSnapshots.AsNoTracking()
                .SingleOrDefaultAsync(
                    snapshot =>
                        snapshot.SourceId == candidate.SourceId && snapshot.ContentHash == candidate.ContentHash,
                    cancellationToken
                );
            if (hashMatch is not null)
            {
                if (hashMatch.IsActive)
                {
                    await transaction.CommitAsync(cancellationToken);
                    return PricingActivationResult.Unchanged;
                }

                await db
                    .PricingSnapshots.Where(snapshot => snapshot.SourceId == candidate.SourceId && snapshot.IsActive)
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(snapshot => snapshot.IsActive, false),
                        cancellationToken
                    );
                // ExecuteUpdate, not tracked mutation: the AsNoTracking hash match above can
                // coexist with a stale tracked copy. RetrievedAt is not re-stamped: validation
                // pins the candidate's retrieval timestamp to the embedded catalog's, which is
                // the value this row already carries.
                await db
                    .PricingSnapshots.Where(snapshot => snapshot.Id == hashMatch.Id)
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(snapshot => snapshot.IsActive, true),
                        cancellationToken
                    );

                if (beforeCommit is not null)
                {
                    var reactivated = await db.PricingSnapshots.SingleAsync(
                        snapshot => snapshot.Id == hashMatch.Id,
                        cancellationToken
                    );
                    await beforeCommit(reactivated, cancellationToken);
                    await db.SaveChangesAsync(cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);
                return PricingActivationResult.Activated;
            }

            await db
                .PricingSnapshots.Where(snapshot => snapshot.SourceId == candidate.SourceId && snapshot.IsActive)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(snapshot => snapshot.IsActive, false),
                    cancellationToken
                );

            var activated = new PricingSnapshot
            {
                Provider = candidate.Provider,
                SourceId = candidate.SourceId,
                RetrievedAt = candidate.RetrievedAt,
                SourceUrl = candidate.SourceUrl,
                ContentHash = candidate.ContentHash,
                RawEvidence = candidate.RawEvidence,
                NormalizedCatalog = candidate.NormalizedCatalog,
                IsActive = true,
            };
            db.PricingSnapshots.Add(activated);
            await db.SaveChangesAsync(cancellationToken);

            if (beforeCommit is not null)
            {
                await beforeCommit(activated, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return PricingActivationResult.Activated;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            throw;
        }
    }

    public Task<PricingSnapshot?> GetActiveAsync(string sourceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        return db
            .PricingSnapshots.AsNoTracking()
            .SingleOrDefaultAsync(snapshot => snapshot.SourceId == sourceId && snapshot.IsActive, cancellationToken);
    }

    public Task<PricingSnapshot?> GetCatalogForDateAsync(
        Provider provider,
        LocalDate usageDate,
        CancellationToken cancellationToken = default
    ) => GetCatalogForDateAsync(GetSourceId(provider), usageDate, snapshotsBySourceId: null, cancellationToken);

    public Task<PricingSnapshot?> GetCatalogForDateAsync(
        UsageEvent usage,
        CancellationToken cancellationToken = default
    ) => GetCatalogForDateAsync(usage, snapshotsBySourceId: null, cancellationToken);

    /// <summary>
    /// As <see cref="GetCatalogForDateAsync(UsageEvent, CancellationToken)"/>, but reads each source's
    /// snapshot rows through <paramref name="snapshotsBySourceId"/>, so a pass over many events queries
    /// them once rather than once per event. The effective-date filter still runs per event, so events on
    /// different dates resolve to different snapshots exactly as they do uncached.
    /// </summary>
    internal Task<PricingSnapshot?> GetCatalogForDateAsync(
        UsageEvent usage,
        Dictionary<string, List<PricingSnapshot>>? snapshotsBySourceId,
        CancellationToken cancellationToken
    ) => FirstOrDefaultAsync(GetCoveringSnapshotsAsync(usage, snapshotsBySourceId, cancellationToken));

    /// <summary>
    /// Every snapshot covering <paramref name="usage"/>'s date, the active snapshot first and
    /// then newest retrieval. Callers resolving a price must walk the list until one actually
    /// prices the event's model: the first-listed snapshot wins on date alone, but a catalog
    /// refresh that retires a model must fall through to an older retained snapshot that still
    /// carries it, not return nothing.
    /// </summary>
    internal async Task<IReadOnlyList<PricingSnapshot>> GetCoveringSnapshotsAsync(
        UsageEvent usage,
        Dictionary<string, List<PricingSnapshot>>? snapshotsBySourceId,
        CancellationToken cancellationToken
    )
    {
        var sourceId = GetSourceId(usage);
        if (sourceId is null)
        {
            return [];
        }

        // Notional events price from the active catalog rather than a dated window, but with
        // the same retained-snapshot fall-through as dated pricing: a refresh that retires a
        // model the local sweepers still report must not leave them permanently unpriced.
        if (usage.CostBasis == CostBasis.Notional)
        {
            return await GetSnapshotsAsync(sourceId, snapshotsBySourceId, cancellationToken);
        }

        var snapshots = await GetSnapshotsAsync(sourceId, snapshotsBySourceId, cancellationToken);
        var usageDate = usage.OccurredAt.InUtc().Date;
        return CoveringSnapshots(snapshots, usageDate);
    }

    // Strict date coverage first: the first-listed snapshot (active, then newest retrieval)
    // with a window genuinely spanning the usage date wins. Only when NO snapshot strictly
    // covers the date — e.g. history predating the first bundled fetch — fall back to
    // snapshots carrying assumed (non-provider-declared) effective dates, whose earliest
    // window is treated as open-ended backwards (see EffectiveWindow). Keeping the fallback
    // second preserves the retained-snapshot design: an older window in a superseded catalog
    // still serves the dates it strictly covers.
    private static List<PricingSnapshot> CoveringSnapshots(List<PricingSnapshot> snapshots, LocalDate usageDate)
    {
        var covering = snapshots.Where(snapshot => Covers(snapshot, usageDate)).ToList();
        if (covering.Count > 0)
        {
            return covering;
        }

        // Live sources stamp assumed effective dates with the fetch date, so a fallback left
        // in retrieval order would price history predating every catalog from the NEWEST
        // fetched rates. The earliest assumed window reaches open-ended backwards, so the
        // earliest-reaching snapshot is the best estimate for such dates.
        return snapshots
            .Where(snapshot => CoverageOf(snapshot).HasAssumedEffectiveDate)
            .OrderBy(snapshot => CoverageOf(snapshot).EarliestEffectiveFrom)
            .ToList();
    }

    private static SnapshotCoverage CoverageOf(PricingSnapshot snapshot) =>
        CoverageByCatalogHash.GetOrAdd(CatalogCacheKey(snapshot.NormalizedCatalog), _ => ParseCoverage(snapshot));

    private static async Task<PricingSnapshot?> FirstOrDefaultAsync(Task<IReadOnlyList<PricingSnapshot>> covering)
    {
        var snapshots = await covering;
        return snapshots.Count == 0 ? null : snapshots[0];
    }

    private async Task<PricingSnapshot?> GetCatalogForDateAsync(
        string? sourceId,
        LocalDate usageDate,
        Dictionary<string, List<PricingSnapshot>>? snapshotsBySourceId,
        CancellationToken cancellationToken
    )
    {
        if (sourceId is null)
        {
            return null;
        }

        var snapshots = await GetSnapshotsAsync(sourceId, snapshotsBySourceId, cancellationToken);
        var covering = CoveringSnapshots(snapshots, usageDate);
        return covering.Count == 0 ? null : covering[0];
    }

    private async Task<List<PricingSnapshot>> GetSnapshotsAsync(
        string sourceId,
        Dictionary<string, List<PricingSnapshot>>? snapshotsBySourceId,
        CancellationToken cancellationToken
    )
    {
        if (snapshotsBySourceId is null || !snapshotsBySourceId.TryGetValue(sourceId, out var snapshots))
        {
            // Activation outranks retrieval time: reactivating a historic snapshot (the A -> B
            // -> A path) deliberately does not re-stamp RetrievedAt, so a list ordered by
            // retrieval alone would keep the superseded newer catalog outranking the
            // reactivated one, and estimated pricing would ignore the activation.
            snapshots = await db
                .PricingSnapshots.AsNoTracking()
                .Where(candidate => candidate.SourceId == sourceId)
                .OrderByDescending(candidate => candidate.IsActive)
                .ThenByDescending(candidate => candidate.RetrievedAt)
                .ToListAsync(cancellationToken);
            snapshotsBySourceId?.Add(sourceId, snapshots);
        }

        return snapshots;
    }

    internal Task AcquireSharedActivationLockAsync(Provider provider, CancellationToken cancellationToken = default) =>
        AcquireSharedActivationLockAsync(GetSourceId(provider), cancellationToken);

    internal Task AcquireSharedActivationLockAsync(UsageEvent usage, CancellationToken cancellationToken = default) =>
        AcquireSharedActivationLockAsync(GetSourceId(usage), cancellationToken);

    private async Task AcquireSharedActivationLockAsync(string? sourceId, CancellationToken cancellationToken)
    {
        if (sourceId is null)
        {
            return;
        }

        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("A pricing read lock requires an active database transaction.");
        }

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock_shared(hashtextextended({sourceId}, 0))",
            cancellationToken
        );
    }

    /// <summary>
    /// Takes the shared activation lock for every pricing source <paramref name="provider"/> can
    /// resolve against — Google prices from both the Cloud catalog and the Gemini Developer API
    /// source, so both must be locked. Requires an active transaction on this context; an
    /// activation holds the matching exclusive lock, so this blocks until any in-flight
    /// activation (and its repricing) has committed.
    /// </summary>
    internal async Task AcquireSharedActivationLocksAsync(
        Provider provider,
        CancellationToken cancellationToken = default
    )
    {
        await AcquireSharedActivationLockAsync(GetSourceId(provider), cancellationToken);
        if (provider == Provider.Google)
        {
            await AcquireSharedActivationLockAsync(PricingSourceIds.GeminiDeveloperApi, cancellationToken);
        }
    }

    private static bool Covers(PricingSnapshot snapshot, LocalDate usageDate) =>
        CoverageOf(snapshot).AppliesTo(usageDate);

    // Catalog JSON is parsed once per unique catalog content, not once per (event x snapshot):
    // a repricing pass over N events would otherwise perform O(N x snapshots) deserialisations.
    private static readonly ConcurrentDictionary<string, SnapshotCoverage> CoverageByCatalogHash = new(
        StringComparer.Ordinal
    );

    private static string CatalogCacheKey(string normalizedCatalog) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedCatalog)));

    private sealed record SnapshotCoverage(LocalDate? EarliestEffectiveFrom, bool HasAssumedEffectiveDate)
    {
        public bool AppliesTo(LocalDate usageDate) => EarliestEffectiveFrom is { } earliest && earliest <= usageDate;
    }

    private static SnapshotCoverage ParseCoverage(PricingSnapshot snapshot)
    {
        var entries = Entries(snapshot);
        return new SnapshotCoverage(
            entries.Count == 0 ? null : entries.Min(entry => entry.EffectiveFrom),
            entries.Any(entry => !entry.Declared)
        );
    }

    private static List<(LocalDate EffectiveFrom, bool Declared)> Entries(PricingSnapshot snapshot) =>
        snapshot.Provider switch
        {
            Provider.OpenAI => PricingCatalogJson
                .Deserialize<OpenAiPriceCatalog>(snapshot.NormalizedCatalog)
                .Entries.Select(entry => (entry.EffectiveFrom, entry.EffectiveDateIsProviderDeclared))
                .ToList(),
            Provider.Anthropic => PricingCatalogJson
                .Deserialize<AnthropicPriceCatalog>(snapshot.NormalizedCatalog)
                .Entries.Select(entry => (entry.EffectiveFrom, entry.EffectiveDateIsProviderDeclared))
                .ToList(),
            Provider.Moonshot => PricingCatalogJson
                .Deserialize<KimiPriceCatalog>(snapshot.NormalizedCatalog)
                .Entries.Select(entry => (entry.EffectiveFrom, entry.EffectiveDateIsProviderDeclared))
                .ToList(),
            Provider.Google when snapshot.SourceId == PricingSourceIds.GeminiDeveloperApi => PricingCatalogJson
                .Deserialize<GeminiDeveloperPriceCatalog>(snapshot.NormalizedCatalog)
                .Entries.Select(entry => (entry.EffectiveFrom, entry.EffectiveDateIsProviderDeclared))
                .ToList(),
            Provider.Google => PricingCatalogJson
                .Deserialize<GooglePriceCatalog>(snapshot.NormalizedCatalog)
                .Entries.Select(entry => (entry.EffectiveFrom, entry.EffectiveDateIsProviderDeclared))
                .ToList(),
            _ => [],
        };

    private static string? GetSourceId(Provider provider) =>
        provider switch
        {
            Provider.OpenAI => PricingSourceIds.OpenAi,
            Provider.Anthropic => PricingSourceIds.Claude,
            Provider.Moonshot => PricingSourceIds.Kimi,
            Provider.Google => PricingSourceIds.GoogleCloudCatalog,
            _ => null,
        };

    private static string? GetSourceId(UsageEvent usage)
    {
        if (usage.Provider == Provider.Google && usage.CostBasis == CostBasis.Notional)
        {
            return PricingSourceIds.GeminiDeveloperApi;
        }

        if (usage.Provider != Provider.Google)
        {
            return GetSourceId(usage.Provider);
        }

        using var evidence = ProviderPricingJson.Evidence(usage.RawPayload);
        return
            ProviderPricingJson.TryString(evidence.RootElement, "service", out var service)
            && service.Equals("Gemini Developer API", StringComparison.OrdinalIgnoreCase)
            ? PricingSourceIds.GeminiDeveloperApi
            : PricingSourceIds.GoogleCloudCatalog;
    }

    private static void Validate(PricingSnapshotCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var expectedSourceId = GetSourceId(candidate.Provider);
        if (expectedSourceId is null)
        {
            throw new ArgumentException("The provider has no first-party pricing source.", nameof(candidate));
        }
        if (
            !string.Equals(candidate.SourceId, expectedSourceId, StringComparison.Ordinal)
            && !(candidate.Provider == Provider.Google && candidate.SourceId == PricingSourceIds.GeminiDeveloperApi)
        )
        {
            throw new ArgumentException("The pricing source does not match the provider.", nameof(candidate));
        }

        if (
            !Uri.TryCreate(candidate.SourceUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || candidate.SourceUrl.Length > 2048
            || string.IsNullOrEmpty(candidate.ContentHash)
            || candidate.ContentHash.Length != 64
            || !candidate.ContentHash.All(Uri.IsHexDigit)
            || string.IsNullOrWhiteSpace(candidate.RawEvidence)
            || string.IsNullOrWhiteSpace(candidate.NormalizedCatalog)
        )
        {
            throw new ArgumentException(
                "The pricing candidate contains invalid trust-boundary data.",
                nameof(candidate)
            );
        }

        var evidenceHash = PricingSnapshotCandidate.ComputeContentHash(
            candidate.RawEvidence,
            candidate.NormalizedCatalog
        );
        if (!string.Equals(candidate.ContentHash, evidenceHash, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The content hash must be the lowercase SHA-256 of the raw evidence and normalized catalog.",
                nameof(candidate)
            );
        }

        try
        {
            var (catalogSourceUrl, catalogRetrievedAt) = candidate.Provider switch
            {
                Provider.OpenAI => ValidateAndGetMetadata(
                    PricingCatalogJson.Deserialize<OpenAiPriceCatalog>(candidate.NormalizedCatalog)
                ),
                Provider.Anthropic => ValidateAndGetMetadata(
                    PricingCatalogJson.Deserialize<AnthropicPriceCatalog>(candidate.NormalizedCatalog)
                ),
                Provider.Moonshot => ValidateAndGetMetadata(
                    PricingCatalogJson.Deserialize<KimiPriceCatalog>(candidate.NormalizedCatalog)
                ),
                Provider.Google when candidate.SourceId == PricingSourceIds.GeminiDeveloperApi =>
                    ValidateAndGetMetadata(
                        PricingCatalogJson.Deserialize<GeminiDeveloperPriceCatalog>(candidate.NormalizedCatalog)
                    ),
                Provider.Google => ValidateAndGetMetadata(
                    PricingCatalogJson.Deserialize<GooglePriceCatalog>(candidate.NormalizedCatalog)
                ),
                _ => throw new UnreachableException(),
            };
            if (!string.Equals(catalogSourceUrl, candidate.SourceUrl, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The normalized catalog source URL does not match its evidence.");
            }
            // Snapshot rows order by the candidate's RetrievedAt and notional pricing uses the
            // embedded catalog's RetrievedAt as the pricing date — if the two disagree, ordering
            // and pricing silently use different clocks.
            if (catalogRetrievedAt != candidate.RetrievedAt)
            {
                throw new InvalidDataException(
                    "The snapshot retrieval timestamp does not match the embedded catalog's."
                );
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            throw new ArgumentException("The normalized pricing catalog is invalid.", nameof(candidate), exception);
        }
    }

    private static (string SourceUrl, Instant RetrievedAt) ValidateAndGetMetadata(OpenAiPriceCatalog catalog)
    {
        catalog.Validate();
        return (catalog.SourceUrl, catalog.RetrievedAt);
    }

    private static (string SourceUrl, Instant RetrievedAt) ValidateAndGetMetadata(AnthropicPriceCatalog catalog)
    {
        catalog.Validate();
        return (catalog.SourceUrl, catalog.RetrievedAt);
    }

    private static (string SourceUrl, Instant RetrievedAt) ValidateAndGetMetadata(KimiPriceCatalog catalog)
    {
        catalog.Validate();
        return (catalog.SourceUrl, catalog.RetrievedAt);
    }

    private static (string SourceUrl, Instant RetrievedAt) ValidateAndGetMetadata(GooglePriceCatalog catalog)
    {
        catalog.Validate();
        return (catalog.SourceUrl, catalog.RetrievedAt);
    }

    private static (string SourceUrl, Instant RetrievedAt) ValidateAndGetMetadata(GeminiDeveloperPriceCatalog catalog)
    {
        catalog.Validate();
        return (catalog.SourceUrl, catalog.RetrievedAt);
    }
}
