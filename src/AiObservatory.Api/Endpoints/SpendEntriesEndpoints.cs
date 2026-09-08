using System.Globalization;
using AiObservatory.Data;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Spend;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;

namespace AiObservatory.Api.Endpoints;

// Request records are instantiated by ASP.NET Core model binding.
// ReSharper disable ClassNeverInstantiated.Global

/// <summary>
/// The ledger itself. <c>POST</c> always takes an array — the manual form posts an array of
/// one — which is what lets the form, CSV import and the tax-portal feed share a single
/// endpoint with one contract and one code path.
/// </summary>
public static class SpendEntriesEndpoints
{
    // ReSharper disable once UnusedMethodReturnValue.Global
    public static IEndpointRouteBuilder MapSpendEntriesEndpoints(this IEndpointRouteBuilder app)
    {
        // Admin-only, matching the dashboard's own intent: the Spend tab is readonlyHidden, so a
        // share-link viewer is not meant to reach the row-level ledger. /spend/reporting below is
        // deliberately NOT gated — the Reporting tab IS visible to readonly viewers, and it
        // returns aggregates rather than ledger rows.
        app.MapGet("/spend/entries", GetEntriesAsync).AddEndpointFilter<AdminOnlyApiKeyEndpointFilter>();
        app.MapGet("/spend/reporting", GetReportingAsync);
        app.MapPost("/spend/entries", RecordEntriesAsync);
        app.MapPatch("/spend/entries/{id:guid}", PatchEntryAsync);
        app.MapDelete("/spend/entries/{id:guid}", DeleteEntryAsync);

        return app;
    }

    private const int MaxBatch = 1000;

    private static async Task<IResult> RecordEntriesAsync(
        SpendEntryRequest[] requests,
        AiObservatoryDbContext db,
        FxRateProvider fx,
        IClock clock,
        ILoggerFactory loggerFactory,
        CancellationToken ct
    )
    {
        if (requests.Length == 0)
        {
            return Results.BadRequest("Provide at least one entry");
        }

        if (requests.Length > MaxBatch)
        {
            return Results.BadRequest($"At most {MaxBatch} entries per request");
        }

        // Loaded once rather than per row: a CSV import is overwhelmingly the same handful
        // of vendors and categories repeated.
        var vendorIds = await db.SpendVendors.AsNoTracking().Select(v => v.Id).ToHashSetAsync(ct);
        var categoryIds = await db.SpendCategories.AsNoTracking().Select(c => c.Id).ToHashSetAsync(ct);

        var results = new List<SpendEntryResult>(requests.Length);
        var now = clock.GetCurrentInstant();

        foreach (var req in requests)
        {
            var rejection = Validate(req, vendorIds, categoryIds, out var source, out var currency);
            if (rejection is not null)
            {
                results.Add(new SpendEntryResult(null, "rejected", rejection));
                continue;
            }

            decimal rate;
            try
            {
                rate = await fx.GetGbpRateOnAsync(currency, req.OccurredOn, ct);
            }
            catch (FxUnavailableException ex)
            {
                // Rather than freeze an undetectably wrong conversion onto a permanent
                // ledger row, reject just this row and let the rest of the batch land.
                results.Add(new SpendEntryResult(null, "rejected", ex.Message));
                continue;
            }

            var amountGbp = decimal.Round(req.Amount * rate, 4, MidpointRounding.ToEven);
            if (RejectIfRoundsToZeroGbp(amountGbp) is { } roundingRejection)
            {
                results.Add(new SpendEntryResult(null, "rejected", roundingRejection));
                continue;
            }

            var (sourceId, sourceKind) = ProvenanceFor(source);
            var entry = new SpendEntry
            {
                OccurredOn = req.OccurredOn,
                VendorId = req.VendorId,
                CategoryId = req.CategoryId,
                Amount = req.Amount,
                Currency = currency,
                // Frozen here, deliberately. See SpendEntry.AmountGbp.
                AmountGbp = amountGbp,
                FxRate = rate,
                Description = string.IsNullOrWhiteSpace(req.Description) ? null : req.Description.Trim(),
                Source = source,
                EntryKey = string.IsNullOrWhiteSpace(req.EntryKey) ? null : req.EntryKey.Trim(),
                RecordedAt = now,
                SourceId = sourceId,
                SourceKind = sourceKind,
                UsageScope = UsageScope.Unknown,
                CostBasis = CostBasis.Billed,
                ObservedAt = now,
            };

            db.SpendEntries.Add(entry);
            results.Add(await SaveRowAsync(db, entry, loggerFactory, ct));
        }

        return Results.Ok(results);
    }

    /// <summary>
    /// Saves one already-validated, already-added entry and translates whatever
    /// SaveChangesAsync does into that row's verdict. Split out of the request loop above
    /// purely to keep RecordEntriesAsync's cognitive complexity down -- the two catches
    /// below (duplicate-detection, then general failure) are unchanged in behaviour.
    /// </summary>
    private static async Task<SpendEntryResult> SaveRowAsync(
        AiObservatoryDbContext db,
        SpendEntry entry,
        ILoggerFactory loggerFactory,
        CancellationToken ct
    )
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return new SpendEntryResult(entry.Id, "created", null);
        }
        catch (DbUpdateException ex)
            when (entry.EntryKey is not null
                && ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }
            )
        {
            // The row already exists for this source and key. Report it rather than
            // failing the batch: re-importing an overlapping statement is routine.
            //
            // Ceiling: this does not check the constraint NAME, so it assumes SpendEntry
            // carries exactly one unique index -- (Source, EntryKey) filtered to EntryKey
            // IS NOT NULL (Task 1). A future second unique index on this table would be
            // silently misreported as a duplicate spend entry too; narrow the `when` to
            // the specific constraint name if that ever happens.
            db.Entry(entry).State = EntityState.Detached;
            var existingId = await db
                .SpendEntries.AsNoTracking()
                .Where(e => e.Source == entry.Source && e.EntryKey == entry.EntryKey)
                .Select(e => (Guid?)e.Id)
                .FirstOrDefaultAsync(ct);
            return new SpendEntryResult(existingId, "duplicate", null);
        }
        catch (DbUpdateException ex)
        {
            // Any other row-level failure (e.g. a check constraint, or a vendor/category
            // deleted out from under the RecordEntriesAsync-wide vendorIds/categoryIds
            // snapshot) must reject just this row, not the batch: SaveChangesAsync is
            // called per row specifically so earlier rows already committed. Detach so
            // the tracked, half-saved entity doesn't get retried (and fail again) on the
            // next row's SaveChangesAsync.
            //
            // The exception is logged, not surfaced -- this repo is public, and the raw
            // Postgres message can carry column/constraint detail. Nothing here carries an
            // amount or description; only identifiers.
            loggerFactory
                .CreateLogger("AiObservatory.Api.SpendEntries")
                .LogError(
                    ex,
                    "Failed to save spend entry {EntryId} (source {Source}, vendor {VendorId}, category {CategoryId})",
                    entry.Id,
                    entry.Source,
                    entry.VendorId,
                    entry.CategoryId
                );
            db.Entry(entry).State = EntityState.Detached;
            return new SpendEntryResult(null, "rejected", "Could not save this entry");
        }
    }

    /// <summary>
    /// Returns a rejection reason, or null when the request is sound.
    /// <para>
    /// Public so the unit lane can exercise it directly — every branch here decides whether a
    /// real charge reaches the ledger, and reaching them through the HTTP pipeline would put
    /// this in the integration project, outside Stryker's lane. Same reasoning as
    /// <see cref="GitHubActivityEndpoints.ComputeSuccessRate"/>.
    /// </para>
    /// </summary>
    internal static string? Validate(
        SpendEntryRequest req,
        HashSet<Guid> vendorIds,
        HashSet<Guid> categoryIds,
        out SpendSource source,
        out string currency
    )
    {
        source = SpendSource.Manual;
        currency = "GBP";

        if (req.OccurredOn == default)
        {
            return "OccurredOn is required";
        }

        if (!vendorIds.Contains(req.VendorId))
        {
            return $"Unknown VendorId: {req.VendorId}";
        }

        if (!categoryIds.Contains(req.CategoryId))
        {
            return $"Unknown CategoryId: {req.CategoryId}";
        }

        // Signed: negative is a refund or credit (see SpendEntry.Amount). Only zero is
        // rejected — a zero-value charge carries no information and is a mistake either way.
        if (req.Amount == 0)
        {
            return "Amount must not be zero";
        }

        currency = (req.Currency ?? "GBP").Trim().ToUpperInvariant();
        if (currency.Length != 3 || !currency.All(char.IsAsciiLetterUpper))
        {
            return $"Currency must be a 3-letter ISO 4217 code, got: {req.Currency}";
        }

        if (req.Description is { Length: > 200 })
        {
            return "Description must be 200 characters or fewer";
        }

        if (req.EntryKey is { Length: > 200 })
        {
            return "EntryKey must be 200 characters or fewer";
        }

        if (!Enum.TryParse(req.Source, ignoreCase: true, out source) || !Enum.IsDefined(source))
        {
            return $"Unknown source: {req.Source}";
        }

        return null;
    }

    /// <summary>
    /// Maps the parsed ledger <see cref="SpendSource"/> to the provenance columns written on the
    /// same row, so SourceId/SourceKind never disagree with <c>Source</c> — hardcoding the manual
    /// values would mark every CSV import, portal feed and vendor-API row as hand-keyed.
    /// <para>
    /// Internal so the unit lane can exercise it directly — same reasoning as
    /// <see cref="Validate"/>.
    /// </para>
    /// </summary>
    internal static (string SourceId, SourceKind SourceKind) ProvenanceFor(SpendSource source) =>
        source switch
        {
            // Vendor-authoritative figures (e.g. GitHub org billing) keyed in via the API arm.
            SpendSource.Api => ("vendor-api-entry", SourceKind.ProviderApi),
            SpendSource.Csv => ("csv-import", SourceKind.Manual),
            SpendSource.Portal => ("portal-feed", SourceKind.Manual),
            _ => (UsageSourceIds.ManualLedger, SourceKind.Manual),
        };

    private static async Task<IResult> GetEntriesAsync(
        AiObservatoryDbContext db,
        CancellationToken ct,
        string? from = null,
        string? to = null,
        Guid? vendorId = null,
        Guid? categoryId = null,
        int limit = 5000
    )
    {
        // Bound as string and parsed here rather than LocalDate?: ASP.NET Core minimal-API
        // query binding needs a TryParse in the exact shape it expects, which NodaTime's
        // LocalDate does not offer. AggregatesEndpoints.GetAggregatesAsync takes the same
        // approach for the same reason.
        if (ParseDate(from, out var fromDate) is { } fromError)
        {
            return Results.BadRequest(fromError);
        }
        if (ParseDate(to, out var toDate) is { } toError)
        {
            return Results.BadRequest(toError);
        }

        if (fromDate is { } fromBound && toDate is { } toBound && fromBound > toBound)
        {
            return Results.BadRequest("from must be on or before to");
        }

        var q = db.SpendEntries.AsNoTracking();
        if (fromDate is { } f)
        {
            q = q.Where(e => e.OccurredOn >= f);
        }
        if (toDate is { } t)
        {
            q = q.Where(e => e.OccurredOn <= t);
        }
        if (vendorId is { } v)
        {
            q = q.Where(e => e.VendorId == v);
        }
        if (categoryId is { } c)
        {
            q = q.Where(e => e.CategoryId == c);
        }

        // Hard ceiling so an unbounded range cannot OOM the response; callers page by date.
        var capped = Math.Clamp(limit, 1, 5000);

        // Projected, never the entity. SpendEntry.RawPayload holds the provider billing response
        // verbatim and unredacted, and its own doc comment says not to surface it without review;
        // returning the entity put it on the wire for every caller this route admits, and nothing
        // in the client contract declares or renders it. Every other column is retained.
        var rows = await q.OrderByDescending(e => e.OccurredOn)
            .ThenByDescending(e => e.RecordedAt)
            .Take(capped)
            .Select(e => new SpendEntryResponse(
                e.Id,
                e.OccurredOn,
                e.VendorId,
                e.CategoryId,
                e.Amount,
                e.Currency,
                e.AmountGbp,
                e.FxRate,
                e.Description,
                e.Source,
                e.EntryKey,
                e.RecordedAt,
                e.SourceId,
                e.SourceKind,
                e.UsageScope,
                e.CostBasis,
                e.ObservedAt
            ))
            .ToListAsync(ct);

        return Results.Ok(rows);
    }

    private static async Task<IResult> GetReportingAsync(
        AiObservatoryDbContext db,
        CancellationToken ct,
        string? from = null,
        string? to = null,
        Guid? vendorId = null,
        Guid? categoryId = null
    )
    {
        if (ParseDate(from, out var fromDate) is { } fromError)
        {
            return Results.BadRequest(fromError);
        }
        if (ParseDate(to, out var toDate) is { } toError)
        {
            return Results.BadRequest(toError);
        }
        if (fromDate is null || toDate is null)
        {
            return Results.BadRequest("from and to are required");
        }
        if (fromDate > toDate)
        {
            return Results.BadRequest("from must be on or before to");
        }

        // One grouped statement gives every card and series the same PostgreSQL statement
        // snapshot. Only date/vendor aggregates cross the wire; the capped ledger endpoint is
        // intentionally not involved in financial reporting.
        var entries = db
            .SpendEntries.AsNoTracking()
            .Where(entry => entry.OccurredOn >= fromDate && entry.OccurredOn <= toDate);
        if (vendorId is { } vendor)
        {
            entries = entries.Where(entry => entry.VendorId == vendor);
        }
        if (categoryId is { } category)
        {
            entries = entries.Where(entry => entry.CategoryId == category);
        }

        var aggregateRows = await entries
            .Join(
                db.SpendVendors.AsNoTracking(),
                entry => entry.VendorId,
                vendor => vendor.Id,
                (entry, vendor) => new { Entry = entry, Vendor = vendor }
            )
            .Join(
                db.SpendCategories.AsNoTracking(),
                row => row.Entry.CategoryId,
                category => category.Id,
                (row, category) =>
                    new
                    {
                        row.Entry,
                        row.Vendor,
                        Category = category,
                    }
            )
            .GroupBy(row => new
            {
                row.Entry.OccurredOn,
                row.Vendor.Id,
                VendorName = row.Vendor.DisplayName,
                row.Vendor.Provider,
                CategoryId = row.Category.Id,
                CategoryName = row.Category.DisplayName,
            })
            .Select(group => new
            {
                Date = group.Key.OccurredOn,
                VendorId = group.Key.Id,
                group.Key.VendorName,
                group.Key.Provider,
                group.Key.CategoryId,
                group.Key.CategoryName,
                AmountGbp = group.Sum(row => row.Entry.AmountGbp),
                EntryCount = group.Count(),
            })
            .ToListAsync(ct);

        var entryCount = aggregateRows.Sum(point => point.EntryCount);
        var totalGbp = aggregateRows.Sum(point => point.AmountGbp);
        var dailySeries = aggregateRows
            .GroupBy(point => point.Date)
            .OrderBy(group => group.Key)
            .Select(group => new BilledDailyPoint(group.Key, group.Sum(point => point.AmountGbp)))
            .ToList();
        var vendorSeries = aggregateRows
            .GroupBy(point => new
            {
                point.VendorId,
                point.VendorName,
                point.Provider,
            })
            .Select(group => new BilledVendorPoint(
                group.Key.VendorId,
                group.Key.VendorName,
                group.Key.Provider,
                group.Sum(point => point.AmountGbp)
            ))
            .OrderByDescending(point => point.AmountGbp)
            .ThenBy(point => point.Name)
            .ToList();
        var categorySeries = aggregateRows
            .GroupBy(point => new { point.CategoryId, point.CategoryName })
            .Select(group => new BilledCategoryPoint(
                group.Key.CategoryId,
                group.Key.CategoryName,
                group.Sum(point => point.AmountGbp)
            ))
            .OrderByDescending(point => point.AmountGbp)
            .ThenBy(point => point.Name)
            .ToList();
        var daysInRange = Period.Between(fromDate.Value, toDate.Value, PeriodUnits.Days).Days + 1;
        var topVendor = vendorSeries.FirstOrDefault();

        return Results.Ok(
            new BilledReportingResponse(
                entryCount,
                totalGbp,
                entryCount == 0 ? 0m : totalGbp / daysInRange,
                entryCount == 0 ? 0m : totalGbp / daysInRange * 30,
                topVendor?.Name,
                topVendor?.AmountGbp,
                dailySeries,
                vendorSeries,
                categorySeries
            )
        );
    }

    /// <summary>Parses an optional yyyy-MM-dd query value, returning an error message on failure.</summary>
    private static string? ParseDate(string? raw, out LocalDate? parsed)
    {
        parsed = null;
        if (raw is null)
        {
            return null;
        }

        if (
            !DateOnly.TryParseExact(
                raw,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var dateOnly
            )
        )
        {
            return "from/to must be yyyy-MM-dd";
        }

        parsed = LocalDate.FromDateOnly(dateOnly);
        return null;
    }

    private static async Task<IResult> PatchEntryAsync(
        Guid id,
        SpendEntryPatchRequest req,
        AiObservatoryDbContext db,
        FxRateProvider fx,
        CancellationToken ct
    )
    {
        var entry = await db.SpendEntries.FindAsync([id], ct);
        if (entry is null)
        {
            return Results.NotFound();
        }

        if (req.OccurredOn is { } occurredOn && occurredOn == default)
        {
            return Results.BadRequest("OccurredOn is required");
        }

        if (req.Amount is 0)
        {
            return Results.BadRequest("Amount must not be zero");
        }

        if (req.Description is { Length: > 200 })
        {
            return Results.BadRequest("Description must be 200 characters or fewer");
        }

        if (await ValidateReferencesAsync(req, db, ct) is { } refError)
        {
            return Results.BadRequest(refError);
        }

        ApplyScalarFields(entry, req);

        // Amount, currency or date changing all invalidate the stored conversion, so
        // re-resolve at the (possibly new) charge date rather than leave a stale GBP figure.
        if (
            (req.Amount is not null || req.Currency is not null || req.OccurredOn is not null)
            && await ReResolveFxAsync(entry, req.Currency, fx, ct) is { } fxError
        )
        {
            return Results.BadRequest(fxError);
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(entry);
    }

    /// <summary>
    /// Returns a rejection reason when the converted amount rounds to zero at the stored
    /// 4-decimal scale, or null when the entry is recordable. CK_SpendEntry_AmountGbp_SameSign
    /// requires Amount * AmountGbp strictly positive, so without this check a legitimate
    /// sub-rounding entry (a tiny charge in a weak currency) reached SaveChangesAsync and came
    /// back as the opaque "Could not save this entry" — naming neither the cause nor the
    /// remedy. Mirrors the explicit guard the billing path already has
    /// (BillingObservationWriter throws "The billed amount rounds to zero GBP.").
    /// <para>
    /// Internal so the unit lane can exercise it directly — same reasoning as
    /// <see cref="Validate"/> above.
    /// </para>
    /// </summary>
    internal static string? RejectIfRoundsToZeroGbp(decimal amountGbp) =>
        amountGbp == 0m
            ? "Amount converts to zero GBP at the stored 4-decimal scale — the entry is too small to record"
            : null;

    /// <summary>Applies every field the request set, after validation has passed.</summary>
    private static void ApplyScalarFields(SpendEntry entry, SpendEntryPatchRequest req)
    {
        if (req.Amount is { } amount)
        {
            entry.Amount = amount;
        }
        if (req.OccurredOn is { } occurredOn)
        {
            entry.OccurredOn = occurredOn;
        }
        if (req.VendorId is { } vendorId)
        {
            entry.VendorId = vendorId;
        }
        if (req.CategoryId is { } categoryId)
        {
            entry.CategoryId = categoryId;
        }
        if (req.Description is { } description)
        {
            entry.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        }
    }

    /// <summary>
    /// Checks that a changed VendorId or CategoryId still refers to a real row. Without this,
    /// an unknown id would reach SaveChangesAsync and fail as a foreign-key DbUpdateException
    /// (a 500) rather than the clean 400 the POST path already gives for the same mistake.
    /// </summary>
    private static async Task<string?> ValidateReferencesAsync(
        SpendEntryPatchRequest req,
        AiObservatoryDbContext db,
        CancellationToken ct
    )
    {
        if (req.VendorId is { } vendorId && !await db.SpendVendors.AnyAsync(v => v.Id == vendorId, ct))
        {
            return $"Unknown VendorId: {vendorId}";
        }

        if (req.CategoryId is { } categoryId && !await db.SpendCategories.AnyAsync(c => c.Id == categoryId, ct))
        {
            return $"Unknown CategoryId: {categoryId}";
        }

        return null;
    }

    /// <summary>Re-resolves currency, FX rate and AmountGbp on <paramref name="entry"/>; returns an error, or null on success.</summary>
    private static async Task<string?> ReResolveFxAsync(
        SpendEntry entry,
        string? requestedCurrency,
        FxRateProvider fx,
        CancellationToken ct
    )
    {
        var currency = (requestedCurrency ?? entry.Currency).Trim().ToUpperInvariant();
        if (currency.Length != 3 || !currency.All(char.IsAsciiLetterUpper))
        {
            return $"Currency must be a 3-letter ISO 4217 code, got: {requestedCurrency}";
        }

        entry.Currency = currency;
        try
        {
            entry.FxRate = await fx.GetGbpRateOnAsync(currency, entry.OccurredOn, ct);
        }
        catch (FxUnavailableException ex)
        {
            return ex.Message;
        }

        entry.AmountGbp = decimal.Round(entry.Amount * entry.FxRate, 4, MidpointRounding.ToEven);
        // Same sub-rounding guard as the POST path: without it the constraint violation
        // surfaces here as an unhandled DbUpdateException (a 500) instead of a 400.
        return RejectIfRoundsToZeroGbp(entry.AmountGbp);
    }

    private static async Task<IResult> DeleteEntryAsync(Guid id, AiObservatoryDbContext db, CancellationToken ct)
    {
        var deleted = await db.SpendEntries.Where(e => e.Id == id).ExecuteDeleteAsync(ct);
        return deleted == 0 ? Results.NotFound() : Results.NoContent();
    }
}

public sealed record SpendEntryRequest(
    LocalDate OccurredOn,
    Guid VendorId,
    Guid CategoryId,
    decimal Amount,
    string? Currency,
    string? Description,
    string Source,
    string? EntryKey
);

public sealed record SpendEntryPatchRequest(
    LocalDate? OccurredOn,
    Guid? VendorId,
    Guid? CategoryId,
    decimal? Amount,
    string? Currency,
    string? Description
);

/// <param name="Status">created | duplicate | rejected</param>
// Serialized as the per-row API response; reflection-based JSON use is invisible to InspectCode.
// ReSharper disable NotAccessedPositionalProperty.Global
public sealed record SpendEntryResult(Guid? Id, string Status, string? Reason);

/// <summary>
/// The wire shape of a ledger row. Deliberately NOT the <see cref="SpendEntry"/> entity: that
/// carries <c>RawPayload</c>, the provider billing response stored verbatim and unredacted, which
/// its own doc comment says must not be surfaced without review. Every other column is carried
/// through, so this is the entity minus that one field.
/// </summary>
public sealed record SpendEntryResponse(
    Guid Id,
    LocalDate OccurredOn,
    Guid VendorId,
    Guid CategoryId,
    decimal Amount,
    string Currency,
    decimal AmountGbp,
    decimal FxRate,
    string? Description,
    SpendSource Source,
    string? EntryKey,
    Instant RecordedAt,
    string SourceId,
    SourceKind SourceKind,
    UsageScope UsageScope,
    CostBasis CostBasis,
    Instant ObservedAt
);

public sealed record BilledReportingResponse(
    int EntryCount,
    decimal TotalGbp,
    decimal DailyAverageGbp,
    decimal ProjectedMonthlyGbp,
    string? TopVendorName,
    decimal? TopVendorGbp,
    IReadOnlyList<BilledDailyPoint> DailySeries,
    IReadOnlyList<BilledVendorPoint> VendorSeries,
    IReadOnlyList<BilledCategoryPoint> CategorySeries
);

public sealed record BilledDailyPoint(LocalDate Date, decimal AmountGbp);

public sealed record BilledVendorPoint(Guid VendorId, string Name, Provider? Provider, decimal AmountGbp);

public sealed record BilledCategoryPoint(Guid CategoryId, string Name, decimal AmountGbp);
// ReSharper restore NotAccessedPositionalProperty.Global
