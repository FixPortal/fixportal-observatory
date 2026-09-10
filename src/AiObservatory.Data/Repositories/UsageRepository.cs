using System.Text.Json;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Pricing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NodaTime;
using Npgsql;

namespace AiObservatory.Data.Repositories;

public class UsageRepository(
    AiObservatoryDbContext ctx,
    PricingSnapshotStore? pricingStore = null,
    UsagePriceResolver? priceResolver = null,
    IClock? clock = null
) : IUsageRepository
{
    private const int BudgetAlertEmailBatchSize = 50;
    private static readonly JsonDocumentOptions RawPayloadJsonOptions = new() { AllowDuplicateProperties = false };

    // Optional so tests and tooling can construct the repository without a clock; DI always
    // supplies the registered singleton. Only PatchEventCostAsync reads it (CorrectedAt stamp).
    private readonly IClock _clock = clock ?? SystemClock.Instance;

    public async Task<RecordEventResult> RecordEventAsync(UsageEvent evt, CancellationToken ct = default)
    {
        evt = PrepareEvent(evt, out var rawEventKey);
        return await RecordPreparedEventAsync(evt, rawEventKey, beforeWrite: null, ct);
    }

    public async Task<RecordEventResult> RecordEstimatedEventAsync(UsageEvent evt, CancellationToken ct = default)
    {
        evt = PrepareEvent(evt, out var rawEventKey);
        if (evt.CostBasis is not (CostBasis.ListPriceEstimate or CostBasis.Notional))
        {
            throw new ArgumentException("Only estimated usage can use atomic price resolution.", nameof(evt));
        }

        if (pricingStore is null || priceResolver is null)
        {
            throw new InvalidOperationException("Estimated usage pricing services are not configured.");
        }

        return await RecordPreparedEventAsync(
            evt,
            rawEventKey,
            async (usage, cancellationToken) =>
            {
                await pricingStore.AcquireSharedActivationLockAsync(usage, cancellationToken);
                var quote = await priceResolver.ResolveAsync(usage, cancellationToken);
                usage.CostUsd = quote?.CostUsd;
                usage.CacheSavingsUsd = quote?.CacheSavingsUsd;
            },
            ct
        );
    }

    private async Task<RecordEventResult> RecordPreparedEventAsync(
        UsageEvent evt,
        string? rawEventKey,
        Func<UsageEvent, CancellationToken, Task>? beforeWrite,
        CancellationToken ct
    )
    {
        // Capture cost provenance before beforeWrite resolves server-side pricing: the
        // correction-preservation guard must know whether the *source* supplied a cost,
        // which the post-resolution evt.CostUsd can no longer tell.
        var sourceSuppliedCost = evt.CostUsd is not null;
        // Every path through the body returns or throws, so there is no exit condition or
        // trailing throw: the filtered catch only retries attempt 0 (falling through to the
        // increment), and a repeat unique violation rethrows from the generic catch.
        for (var attempt = 0; ; )
        {
            await using var tx = await ctx.Database.BeginTransactionAsync(ct);
            try
            {
                if (beforeWrite is not null)
                {
                    await beforeWrite(evt, ct);
                }

                var existing = await FindEventForKeyLookupAsync(
                    evt.Provider,
                    evt.SourceId,
                    evt.EventKey,
                    rawEventKey,
                    ct
                );
                evt = ReconcileStoredKey(evt, existing);
                var result = await ApplyLockedSnapshotAsync(existing, evt, sourceSuppliedCost, ct);
                if (result.Disposition != RecordEventDisposition.Unchanged || result.WatermarkAdvanced)
                {
                    await ctx.SaveChangesAsync(ct);
                }

                if (await RollbackIfNoOpAsync(tx, evt, result, ct) is { } noOpResult)
                {
                    return noOpResult;
                }

                await tx.CommitAsync(ct);
                return result;
            }
            catch (DbUpdateException ex)
                when (attempt == 0
                    && evt.EventKey is not null
                    && ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }
                )
            {
                await tx.RollbackAsync(ct);
                ctx.ChangeTracker.Clear();
            }
            catch
            {
                await tx.RollbackAsync(ct);
                ctx.ChangeTracker.Clear();
                throw;
            }

            attempt++;
        }
    }

    // Returns the no-op result after rolling back when a duplicate wrote nothing; returns null
    // when the caller should commit — marking sync success first for local telemetry. A
    // watermark-advanced duplicate DID write, so it falls to the null (commit) path too.
    private async Task<RecordEventResult?> RollbackIfNoOpAsync(
        IDbContextTransaction tx,
        UsageEvent evt,
        RecordEventResult result,
        CancellationToken ct
    )
    {
        if (evt.SourceKind == SourceKind.LocalTelemetry)
        {
            await MarkLocalTelemetrySyncAsync(evt, ct);
            return null;
        }

        if (result is { Disposition: RecordEventDisposition.Unchanged, WatermarkAdvanced: false })
        {
            await tx.RollbackAsync(ct);
            return result;
        }

        return null;
    }

    private async Task MarkLocalTelemetrySyncAsync(UsageEvent evt, CancellationToken ct)
    {
        await SourceSyncStateStore.MarkSuccessAsync(
            ctx,
            evt.SourceId,
            Duration.FromDays(1),
            evt.IngestedAt,
            evt.ObservedAt,
            ct
        );
    }

    private static UsageEvent PrepareEvent(UsageEvent evt, out string? rawEventKey)
    {
        ArgumentNullException.ThrowIfNull(evt);
        JsonDocument.Parse(evt.RawPayload, RawPayloadJsonOptions).Dispose();
        if (evt.ObservedAt == default)
        {
            evt.ObservedAt = evt.IngestedAt;
        }

        // CostBasis.None means "usage is reported but no price applies" (docs/truth-and-pricing.md),
        // so a positive cost under it is a contradiction — reject rather than store a row whose
        // basis denies the figure it carries.
        if (evt.CostBasis == CostBasis.None && evt.CostUsd is > 0m)
        {
            throw new ArgumentException(
                "CostBasis.None declares that no price applies; a positive CostUsd contradicts it.",
                nameof(evt)
            );
        }

        // Canonicalise legacy keys to their provider-prefixed stored form while the raw key is
        // still at hand: the raw key feeds the fallback lookup in RecordPreparedEventAsync,
        // which recognises an input that already IS a stored key by lookup, not by shape.
        rawEventKey = evt.EventKey;
        var storedEventKey = ToStoredEventKey(evt.Provider, evt.SourceId, rawEventKey);
        return storedEventKey == rawEventKey ? evt : CopyWithEventKey(evt, storedEventKey);
    }

    public async Task UpdateEventPricingAsync(UsageEvent priced, UsagePriceQuote? quote, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(priced);
        IDbContextTransaction? transaction = null;
        if (ctx.Database.CurrentTransaction is null)
        {
            transaction = await ctx.Database.BeginTransactionAsync(ct);
        }

        await using (transaction)
        {
            try
            {
                var existing = await FindEventByIdForUpdateAsync(priced.Id, ct);
                if (
                    existing is null
                    || existing.CostBasis is not (CostBasis.ListPriceEstimate or CostBasis.Notional)
                    || !PricingInputsEqual(existing, priced)
                    // The scan read the row unlocked; if its cost moved since, another writer
                    // (an activation's own pass committing mid-standalone-pass, or a correction)
                    // already priced it and this quote is stale for the row. Leave it for the
                    // next pass rather than overwrite a newer figure.
                    || existing.CostUsd != priced.CostUsd
                    || existing.CacheSavingsUsd != priced.CacheSavingsUsd
                    || existing.CostUsd == quote?.CostUsd && existing.CacheSavingsUsd == quote?.CacheSavingsUsd
                )
                {
                    if (transaction is not null)
                    {
                        await transaction.CommitAsync(ct);
                    }

                    return;
                }

                var previousCostUsd = existing.CostUsd;
                var previousCacheSavingsUsd = existing.CacheSavingsUsd;
                existing.CostUsd = quote?.CostUsd;
                existing.CacheSavingsUsd = quote?.CacheSavingsUsd;
                await ApplyRepricingCostDeltaAsync(existing, previousCostUsd, previousCacheSavingsUsd, ct);
                await ctx.SaveChangesAsync(ct);
                if (transaction is not null)
                {
                    await transaction.CommitAsync(ct);
                }
            }
            catch
            {
                if (transaction is not null)
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    ctx.ChangeTracker.Clear();
                }

                throw;
            }
        }
    }

    private async Task<RecordEventResult> ApplyLockedSnapshotAsync(
        UsageEvent? existing,
        UsageEvent evt,
        bool sourceSuppliedCost,
        CancellationToken ct
    )
    {
        if (existing is null)
        {
            ctx.UsageEvents.Add(evt);
            await ApplyAggregateDeltaAsync(evt, +1, ct);
            return new RecordEventResult(evt.Id, RecordEventDisposition.Created);
        }

        if (evt.ObservedAt < existing.ObservedAt)
        {
            return new RecordEventResult(existing.Id, RecordEventDisposition.Unchanged);
        }

        if (CanonicalEquals(existing, evt))
        {
            var wrote = false;
            // CanonicalEquals excludes ObservedAt, so an identical replay would otherwise leave the
            // stored watermark behind and a delayed stale snapshot with an ObservedAt between the
            // two would then pass the ordering guard above. Advance the watermark (never backwards)
            // without touching values or aggregates.
            if (evt.ObservedAt > existing.ObservedAt)
            {
                existing.ObservedAt = evt.ObservedAt;
                wrote = true;
            }

            // An identical replay whose source supplied a cost re-asserts source authority over a
            // manual correction even though every canonical value matches: the source figure and
            // the corrected figure agree, so the CorrectedAt marker is stale. Clear it exactly as
            // the changed-values path below does, or a later cost-less replay keeps preserving a
            // figure the operator no longer authored.
            if (sourceSuppliedCost && existing.CorrectedAt is not null)
            {
                existing.CorrectedAt = null;
                wrote = true;
            }

            // WatermarkAdvanced doubles as "Unchanged but wrote state that must persist" — it is
            // what drives SaveChanges and the no-op rollback in the caller.
            return new RecordEventResult(existing.Id, RecordEventDisposition.Unchanged, WatermarkAdvanced: wrote);
        }

        // A manual cost correction (the CorrectedAt marker) outranks a replay whose source
        // carried no cost of its own: the local sweepers re-post every snapshot with costUsd
        // null on every run, always with a fresh ObservedAt, so the ordering guard above
        // cannot defend the corrected figure. Preserve it (and the basis that keeps the row
        // out of the repricing scan); a post whose source supplied an explicit cost
        // re-asserts source authority and clears the marker instead. Provenance is captured
        // before server-side pricing assigns a figure, so an estimated replay resolved by
        // the resolver never masquerades as a source-supplied cost.
        var preserveCorrectedCost = existing.CorrectedAt is not null && !sourceSuppliedCost;
        var correctedCostUsd = existing.CostUsd;
        var correctedCacheSavingsUsd = existing.CacheSavingsUsd;
        var correctedCostBasis = existing.CostBasis;
        await ApplyAggregateDeltaAsync(existing, -1, ct);
        CopyCanonicalValues(existing, evt);
        if (preserveCorrectedCost)
        {
            existing.CostUsd = correctedCostUsd;
            existing.CacheSavingsUsd = correctedCacheSavingsUsd;
            existing.CostBasis = correctedCostBasis;
        }
        else
        {
            existing.CorrectedAt = null;
        }

        await ApplyAggregateDeltaAsync(existing, +1, ct);
        return new RecordEventResult(existing.Id, RecordEventDisposition.Corrected);
    }

    private async Task ApplyAggregateDeltaAsync(UsageEvent evt, int sign, CancellationToken ct)
    {
        var date = evt.OccurredAt.InUtc().Date;
        var provider = evt.Provider.ToString();
        var model = evt.Model ?? "unknown";
        var sourceKind = evt.SourceKind.ToString();
        var usageScope = evt.UsageScope.ToString();
        var costBasis = evt.CostBasis.ToString();
        var inputDelta = checked(evt.InputTokens * sign);
        var outputDelta = checked(evt.OutputTokens * sign);
        var cacheReadDelta = checked((evt.CacheReadTokens ?? 0L) * sign);
        var cacheWriteDelta = checked((evt.CacheWriteTokens ?? 0L) * sign);
        var cacheWrite1hDelta = checked((evt.CacheWrite1hTokens ?? 0L) * sign);
        var costDelta = (evt.CostUsd ?? 0m) * sign;
        // CostBasis.None means "no price applies" (docs/truth-and-pricing.md), i.e. a known zero,
        // not missing pricing data; only genuinely undescribed nulls count as unknown.
        var unknownCostDelta = (evt.CostUsd is null && evt.CostBasis != CostBasis.None ? 1 : 0) * sign;
        var cacheSavingsDelta = (evt.CacheSavingsUsd ?? 0m) * sign;
        var unknownCacheSavingsDelta = (evt.CacheSavingsUsd is null && evt.CostBasis != CostBasis.None ? 1 : 0) * sign;
        var requestDelta = sign;
        var insertInput = Math.Max(0, inputDelta);
        var insertOutput = Math.Max(0, outputDelta);
        var insertCacheRead = Math.Max(0, cacheReadDelta);
        var insertCacheWrite = Math.Max(0, cacheWriteDelta);
        var insertCacheWrite1h = Math.Max(0, cacheWrite1hDelta);
        var insertCost = Math.Max(0, costDelta);
        var insertUnknownCost = Math.Max(0, unknownCostDelta);
        var insertCacheSavings = sign > 0 ? cacheSavingsDelta : 0m;
        var insertUnknownCacheSavings = Math.Max(0, unknownCacheSavingsDelta);
        var insertRequest = Math.Max(0, requestDelta);

        await ctx.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "DailyAggregates" ("Date", "Provider", "Model", "SourceId", "SourceKind", "UsageScope", "CostBasis", "InputTokens", "OutputTokens", "CacheReadTokens", "CacheWriteTokens", "CacheWrite1hTokens", "CostUsd", "UnknownCostCount", "CacheSavingsUsd", "UnknownCacheSavingsCount", "RequestCount")
            VALUES ({date}, {provider}, {model}, {evt.SourceId}, {sourceKind}, {usageScope}, {costBasis}, {insertInput}, {insertOutput}, {insertCacheRead}, {insertCacheWrite}, {insertCacheWrite1h}, {insertCost}, {insertUnknownCost}, {insertCacheSavings}, {insertUnknownCacheSavings}, {insertRequest})
            ON CONFLICT ("Date", "Provider", "Model", "SourceId", "SourceKind", "UsageScope", "CostBasis") DO UPDATE SET
                "InputTokens" = "DailyAggregates"."InputTokens" + {inputDelta},
                "OutputTokens" = "DailyAggregates"."OutputTokens" + {outputDelta},
                "CacheReadTokens" = "DailyAggregates"."CacheReadTokens" + {cacheReadDelta},
                "CacheWriteTokens" = "DailyAggregates"."CacheWriteTokens" + {cacheWriteDelta},
                "CacheWrite1hTokens" = "DailyAggregates"."CacheWrite1hTokens" + {cacheWrite1hDelta},
                "CostUsd" = "DailyAggregates"."CostUsd" + {costDelta},
                "UnknownCostCount" = "DailyAggregates"."UnknownCostCount" + {unknownCostDelta},
                "CacheSavingsUsd" = "DailyAggregates"."CacheSavingsUsd" + {cacheSavingsDelta},
                "UnknownCacheSavingsCount" = "DailyAggregates"."UnknownCacheSavingsCount" + {unknownCacheSavingsDelta},
                "RequestCount" = "DailyAggregates"."RequestCount" + {requestDelta}
            """,
            ct
        );

        await ctx
            .DailyAggregates.Where(a =>
                a.Date == date
                && a.Provider == evt.Provider
                && a.Model == model
                && a.SourceId == evt.SourceId
                && a.SourceKind == evt.SourceKind
                && a.UsageScope == evt.UsageScope
                && a.CostBasis == evt.CostBasis
                && a.RequestCount == 0
            )
            .ExecuteDeleteAsync(ct);
    }

    // ponytail: repricing changes only CostUsd/CacheSavingsUsd, and neither is part of the conflict key, so
    // the old -1 then +1 delta pair always hit the same row and collapse to one net upsert. The INSERT branch
    // still carries the full event values so a missing aggregate row is repaired exactly as the +1 leg did.
    // RequestCount and the token columns net to zero, so no row can reach RequestCount 0 here and the cleanup
    // delete is unreachable. ApplyAggregateDeltaAsync stays as-is for the ingest path, where CopyCanonicalValues
    // can move key dimensions and the pair is genuinely not collapsible.
    //
    // Precondition, because the SET list deliberately diverges from the INSERT list: only reprice an event whose
    // aggregate row is maintained by this same per-event delta discipline. On conflict this preserves the token
    // and RequestCount columns rather than rebuilding them from evt, so reusing it for a key whose row is
    // populated from some other source would keep that row's existing token values. UpdateEventPricingAsync is
    // the only caller and satisfies this. The invariant is not asserted here on purpose: checking it needs a read
    // of the row, which is the round trip this method exists to remove.
    private async Task ApplyRepricingCostDeltaAsync(
        UsageEvent evt,
        decimal? previousCostUsd,
        decimal? previousCacheSavingsUsd,
        CancellationToken ct
    )
    {
        var date = evt.OccurredAt.InUtc().Date;
        var provider = evt.Provider.ToString();
        var model = evt.Model ?? "unknown";
        var sourceKind = evt.SourceKind.ToString();
        var usageScope = evt.UsageScope.ToString();
        var costBasis = evt.CostBasis.ToString();
        var costDelta = (evt.CostUsd ?? 0m) - (previousCostUsd ?? 0m);
        var unknownCostDelta = (evt.CostUsd is null ? 1 : 0) - (previousCostUsd is null ? 1 : 0);
        var cacheSavingsDelta = (evt.CacheSavingsUsd ?? 0m) - (previousCacheSavingsUsd ?? 0m);
        var unknownCacheSavingsDelta =
            (evt.CacheSavingsUsd is null ? 1 : 0) - (previousCacheSavingsUsd is null ? 1 : 0);
        var insertInput = Math.Max(0, evt.InputTokens);
        var insertOutput = Math.Max(0, evt.OutputTokens);
        var insertCacheRead = Math.Max(0, evt.CacheReadTokens ?? 0L);
        var insertCacheWrite = Math.Max(0, evt.CacheWriteTokens ?? 0L);
        var insertCacheWrite1h = Math.Max(0, evt.CacheWrite1hTokens ?? 0L);
        var insertCost = Math.Max(0, evt.CostUsd ?? 0m);
        var insertUnknownCost = evt.CostUsd is null ? 1 : 0;
        var insertCacheSavings = evt.CacheSavingsUsd ?? 0m;
        var insertUnknownCacheSavings = evt.CacheSavingsUsd is null ? 1 : 0;

        await ctx.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "DailyAggregates" ("Date", "Provider", "Model", "SourceId", "SourceKind", "UsageScope", "CostBasis", "InputTokens", "OutputTokens", "CacheReadTokens", "CacheWriteTokens", "CacheWrite1hTokens", "CostUsd", "UnknownCostCount", "CacheSavingsUsd", "UnknownCacheSavingsCount", "RequestCount")
            VALUES ({date}, {provider}, {model}, {evt.SourceId}, {sourceKind}, {usageScope}, {costBasis}, {insertInput}, {insertOutput}, {insertCacheRead}, {insertCacheWrite}, {insertCacheWrite1h}, {insertCost}, {insertUnknownCost}, {insertCacheSavings}, {insertUnknownCacheSavings}, 1)
            ON CONFLICT ("Date", "Provider", "Model", "SourceId", "SourceKind", "UsageScope", "CostBasis") DO UPDATE SET
                "CostUsd" = "DailyAggregates"."CostUsd" + {costDelta},
                "UnknownCostCount" = "DailyAggregates"."UnknownCostCount" + {unknownCostDelta},
                "CacheSavingsUsd" = "DailyAggregates"."CacheSavingsUsd" + {cacheSavingsDelta},
                "UnknownCacheSavingsCount" = "DailyAggregates"."UnknownCacheSavingsCount" + {unknownCacheSavingsDelta}
            """,
            ct
        );
    }

    private async Task<UsageEvent?> FindEventForUpdateAsync(string sourceId, string eventKey, CancellationToken ct)
    {
        var existing = await ctx
            .UsageEvents.FromSqlInterpolated(
                $"""SELECT * FROM "UsageEvents" WHERE "SourceId" = {sourceId} AND "EventKey" = {eventKey} FOR UPDATE"""
            )
            .SingleOrDefaultAsync(ct);
        if (existing is not null)
        {
            await ctx.Entry(existing).ReloadAsync(ct);
        }

        return existing;
    }

    // Resolves the stored row for a key by lookup rather than string shape: the canonical
    // (prefixed) form first — a raw legacy key that starts with the provider name belongs to
    // the double-prefixed row — then the input verbatim, which is the already-stored key when
    // a replay addresses a row the shape-guard era stored as-is, or a caller echoes the
    // GetEventsByProviderAsync projection back into PatchEventCostAsync. The verbatim form
    // may be ANOTHER provider's stored key under the shared legacy-api source (the lookup is
    // source-scoped, not provider-scoped), so a fallback hit only counts for the same provider.
    private async Task<UsageEvent?> FindEventForKeyLookupAsync(
        Provider provider,
        string sourceId,
        string? eventKey,
        string? rawEventKey,
        CancellationToken ct
    )
    {
        if (eventKey is null)
        {
            return null;
        }

        var existing = await FindEventForUpdateAsync(sourceId, eventKey, ct);
        if (existing is null && rawEventKey is not null && rawEventKey != eventKey)
        {
            existing = await FindEventForUpdateAsync(sourceId, rawEventKey, ct);
            if (existing is not null && existing.Provider != provider)
            {
                existing = null;
            }
        }

        return existing;
    }

    private async Task<UsageEvent?> FindEventByIdForUpdateAsync(Guid eventId, CancellationToken ct)
    {
        var existing = await ctx
            .UsageEvents.FromSqlInterpolated($"""SELECT * FROM "UsageEvents" WHERE "Id" = {eventId} FOR UPDATE""")
            .SingleOrDefaultAsync(ct);
        if (existing is not null)
        {
            await ctx.Entry(existing).ReloadAsync(ct);
        }

        return existing;
    }

    private static string? ToStoredEventKey(Provider provider, string sourceId, string? eventKey)
    {
        if (eventKey is null || !string.Equals(sourceId, UsageSourceIds.LegacyApi, StringComparison.OrdinalIgnoreCase))
        {
            return eventKey;
        }

        // Always prefix, never shape-test: "starts with the provider name" cannot distinguish an
        // already-stored key from a raw legacy key that happens to begin with it. The shape test
        // resolved raw keys "x" and "OpenAI:x" to the same stored "OpenAI:x" (merging two
        // distinct events) and missed rows stored as "OpenAI:OpenAI:x" by the old unconditional
        // prefixing (the shape the AddObservationProvenance migration writes), re-inserting them
        // on replay. Callers holding a possibly-stored key resolve it by lookup instead — see
        // RecordPreparedEventAsync and PatchEventCostAsync.
        return $"{provider}:{eventKey}";
    }

    // A fallback lookup hit found the row under the input key verbatim, while evt carries the
    // canonical (re-prefixed) form: reconcile evt to the row's stored key so CanonicalEquals
    // compares equal keys. CopyCanonicalValues never copies EventKey, so the row keeps the
    // form it was stored under.
    private static UsageEvent ReconcileStoredKey(UsageEvent evt, UsageEvent? existing) =>
        existing is not null && existing.EventKey != evt.EventKey ? CopyWithEventKey(evt, existing.EventKey) : evt;

    private static UsageEvent CopyWithEventKey(UsageEvent source, string? eventKey) =>
        new()
        {
            Id = source.Id,
            Provider = source.Provider,
            OccurredAt = source.OccurredAt,
            IngestedAt = source.IngestedAt,
            Model = source.Model,
            InputTokens = source.InputTokens,
            OutputTokens = source.OutputTokens,
            CacheReadTokens = source.CacheReadTokens,
            CacheWriteTokens = source.CacheWriteTokens,
            CacheWrite1hTokens = source.CacheWrite1hTokens,
            ThoughtTokens = source.ThoughtTokens,
            CostUsd = source.CostUsd,
            CacheSavingsUsd = source.CacheSavingsUsd,
            Runtime = source.Runtime,
            SessionId = source.SessionId,
            AgentId = source.AgentId,
            RawPayload = source.RawPayload,
            SourceId = source.SourceId,
            SourceKind = source.SourceKind,
            UsageScope = source.UsageScope,
            CostBasis = source.CostBasis,
            ObservedAt = source.ObservedAt,
            CorrectedAt = source.CorrectedAt,
            EventKey = eventKey,
        };

    // The repricing scan reads events unlocked, prices them, then writes each one under a row lock. A
    // concurrent ingest correction landing in that gap would otherwise have its tokens overwritten with a
    // price calculated from the tokens it replaced, and its aggregate delta routed by the stale key
    // dimensions. Every field the resolver, the calculators or the aggregate key read is compared here, so a
    // moved row is left for the next reprice pass to price from its current values.
    //
    // ponytail: ordinal compare on RawPayload rather than the JSON deep-equals CanonicalEquals uses. Both
    // reads return the same stored text unless a correction rewrote it, and a spurious mismatch only defers
    // one event by one pass. Compare structurally if a producer starts rewriting payload text in place.
    private static bool PricingInputsEqual(UsageEvent locked, UsageEvent priced) =>
        locked.Provider == priced.Provider
        && locked.OccurredAt == priced.OccurredAt
        && locked.Model == priced.Model
        && locked.InputTokens == priced.InputTokens
        && locked.OutputTokens == priced.OutputTokens
        && locked.CacheReadTokens == priced.CacheReadTokens
        && locked.CacheWriteTokens == priced.CacheWriteTokens
        && locked.CacheWrite1hTokens == priced.CacheWrite1hTokens
        && locked.ThoughtTokens == priced.ThoughtTokens
        && locked.CostBasis == priced.CostBasis
        && locked.SourceId == priced.SourceId
        && locked.SourceKind == priced.SourceKind
        && locked.UsageScope == priced.UsageScope
        && string.Equals(locked.RawPayload, priced.RawPayload, StringComparison.Ordinal);

    private static bool CanonicalEquals(UsageEvent left, UsageEvent right) =>
        left.Provider == right.Provider
        && left.OccurredAt == right.OccurredAt
        && left.Model == right.Model
        && left.InputTokens == right.InputTokens
        && left.OutputTokens == right.OutputTokens
        && left.CacheReadTokens == right.CacheReadTokens
        && left.CacheWriteTokens == right.CacheWriteTokens
        && left.CacheWrite1hTokens == right.CacheWrite1hTokens
        && left.ThoughtTokens == right.ThoughtTokens
        && left.CostUsd == right.CostUsd
        && left.CacheSavingsUsd == right.CacheSavingsUsd
        && left.Runtime == right.Runtime
        && left.SessionId == right.SessionId
        && left.AgentId == right.AgentId
        && JsonEquals(left.RawPayload, right.RawPayload)
        && left.SourceId == right.SourceId
        && left.SourceKind == right.SourceKind
        && left.UsageScope == right.UsageScope
        && left.CostBasis == right.CostBasis
        && left.EventKey == right.EventKey;

    private static bool JsonEquals(string left, string right)
    {
        using var leftJson = JsonDocument.Parse(left, RawPayloadJsonOptions);
        using var rightJson = JsonDocument.Parse(right, RawPayloadJsonOptions);
        return JsonElement.DeepEquals(leftJson.RootElement, rightJson.RootElement);
    }

    private static void CopyCanonicalValues(UsageEvent target, UsageEvent source)
    {
        target.Provider = source.Provider;
        target.OccurredAt = source.OccurredAt;
        target.Model = source.Model;
        target.InputTokens = source.InputTokens;
        target.OutputTokens = source.OutputTokens;
        target.CacheReadTokens = source.CacheReadTokens;
        target.CacheWriteTokens = source.CacheWriteTokens;
        target.CacheWrite1hTokens = source.CacheWrite1hTokens;
        target.ThoughtTokens = source.ThoughtTokens;
        target.CostUsd = source.CostUsd;
        target.CacheSavingsUsd = source.CacheSavingsUsd;
        target.Runtime = source.Runtime;
        target.SessionId = source.SessionId;
        target.AgentId = source.AgentId;
        target.RawPayload = source.RawPayload;
        target.SourceId = source.SourceId;
        target.SourceKind = source.SourceKind;
        target.UsageScope = source.UsageScope;
        target.CostBasis = source.CostBasis;
        target.ObservedAt = source.ObservedAt;
    }

    public async Task<PurgeResult> PurgeProviderAsync(Provider provider, CancellationToken ct = default)
    {
        await using var tx = await ctx.Database.BeginTransactionAsync(ct);
        // ExecuteDeleteAsync issues a single bulk DELETE per table (no entity tracking).
        // The EventKey/Provider value converters make the enum comparison translate to SQL.
        var deletedEvents = await ctx.UsageEvents.Where(e => e.Provider == provider).ExecuteDeleteAsync(ct);
        var deletedAggregates = await ctx.DailyAggregates.Where(a => a.Provider == provider).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
        return new PurgeResult(deletedEvents, deletedAggregates);
    }

    public async Task<IReadOnlyList<DailyAggregate>> GetAggregatesAsync(
        LocalDate from,
        LocalDate to,
        CancellationToken ct = default
    )
    {
        return await ctx
            .DailyAggregates.AsNoTracking()
            .Where(a => a.Date >= from && a.Date <= to)
            .OrderBy(a => a.Date)
            .ToListAsync(ct);
    }

    public async Task<decimal> GetBilledSpendGbpAsync(
        LocalDate from,
        LocalDate to,
        Provider? provider = null,
        CancellationToken ct = default
    )
    {
        var entries = ctx.SpendEntries.AsNoTracking().Where(e => e.OccurredOn >= from && e.OccurredOn <= to);
        if (provider is not null)
        {
            entries =
                from entry in entries
                join vendor in ctx.SpendVendors.AsNoTracking() on entry.VendorId equals vendor.Id
                where vendor.Provider == provider
                select entry;
        }

        return await entries.SumAsync(e => (decimal?)e.AmountGbp, ct) ?? 0m;
    }

    public async Task<IReadOnlyList<DailyBilledSpend>> GetDailyBilledSpendGbpAsync(
        LocalDate from,
        LocalDate to,
        Provider? provider = null,
        CancellationToken ct = default
    )
    {
        var entries = ctx.SpendEntries.AsNoTracking().Where(e => e.OccurredOn >= from && e.OccurredOn <= to);
        if (provider is not null)
        {
            entries =
                from entry in entries
                join vendor in ctx.SpendVendors.AsNoTracking() on entry.VendorId equals vendor.Id
                where vendor.Provider == provider
                select entry;
        }

        var rows = await entries
            .GroupBy(entry => entry.OccurredOn)
            .Select(group => new { Date = group.Key, AmountGbp = group.Sum(entry => entry.AmountGbp) })
            .OrderBy(row => row.Date)
            .ToListAsync(ct);
        return rows.Select(row => new DailyBilledSpend(row.Date, row.AmountGbp)).ToList();
    }

    public async Task<IReadOnlyList<BudgetRule>> GetBudgetRulesAsync(CancellationToken ct = default)
    {
        return await ctx.BudgetRules.AsNoTracking().ToListAsync(ct);
    }

    public async Task<NotificationSettings?> GetNotificationSettingsAsync(CancellationToken ct = default)
    {
        return await ctx
            .NotificationSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == NotificationSettings.SingletonId, ct);
    }

    public async Task<BudgetAlertClaimResult> GetOrCreateBudgetAlertAsync(
        Guid ruleId,
        LocalDate periodStart,
        LocalDate periodEnd,
        decimal thresholdGbp,
        decimal actualSpendGbp,
        Insight insight,
        Instant triggeredAt,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(insight);
        var existingClaim = await ctx
            .BudgetAlertClaims.AsNoTracking()
            .SingleOrDefaultAsync(
                candidate =>
                    candidate.BudgetRuleId == ruleId
                    && candidate.PeriodStart == periodStart
                    && candidate.PeriodEnd == periodEnd,
                ct
            );
        if (existingClaim is not null)
        {
            return new BudgetAlertClaimResult(
                existingClaim.Id,
                false,
                existingClaim.ThresholdGbp,
                existingClaim.ActualSpendGbp,
                existingClaim.CreatedAt
            );
        }

        var claim = new BudgetAlertClaim
        {
            BudgetRuleId = ruleId,
            PeriodStart = periodStart,
            PeriodEnd = periodEnd,
            InsightId = insight.Id,
            ThresholdGbp = thresholdGbp,
            ActualSpendGbp = actualSpendGbp,
            CreatedAt = triggeredAt,
        };

        await using var tx = await ctx.Database.BeginTransactionAsync(ct);
        try
        {
            ctx.Insights.Add(insight);
            ctx.BudgetAlertClaims.Add(claim);
            await ctx.SaveChangesAsync(ct);
            var updated = await ctx
                .BudgetRules.Where(rule => rule.Id == ruleId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(rule => rule.LastTriggeredAt, triggeredAt), ct);
            if (updated != 1)
            {
                throw new InvalidOperationException($"Budget rule {ruleId} no longer exists.");
            }

            await tx.CommitAsync(ct);
            return new BudgetAlertClaimResult(claim.Id, true, thresholdGbp, actualSpendGbp, triggeredAt);
        }
        catch (DbUpdateException ex)
            when (ex.InnerException
                    is PostgresException
                    {
                        SqlState: PostgresErrorCodes.UniqueViolation,
                        ConstraintName: "UX_BudgetAlertClaims_RulePeriod",
                    }
            )
        {
            await tx.RollbackAsync(ct);
            ctx.Entry(claim).State = EntityState.Detached;
            ctx.Entry(insight).State = EntityState.Detached;
            var existing = await ctx
                .BudgetAlertClaims.AsNoTracking()
                .SingleAsync(
                    candidate =>
                        candidate.BudgetRuleId == ruleId
                        && candidate.PeriodStart == periodStart
                        && candidate.PeriodEnd == periodEnd,
                    ct
                );
            return new BudgetAlertClaimResult(
                existing.Id,
                false,
                existing.ThresholdGbp,
                existing.ActualSpendGbp,
                existing.CreatedAt
            );
        }
        catch
        {
            await tx.RollbackAsync(ct);
            ctx.Entry(claim).State = EntityState.Detached;
            ctx.Entry(insight).State = EntityState.Detached;
            throw;
        }
    }

    public async Task<bool> TryAcquireBudgetAlertEmailLeaseAsync(
        Guid claimId,
        Guid leaseId,
        Instant acquiredAt,
        Instant leaseExpiredBefore,
        CancellationToken ct = default
    ) =>
        await ctx
            .BudgetAlertClaims.Where(claim =>
                claim.Id == claimId
                && claim.EmailSentAt == null
                && (claim.EmailLeaseAcquiredAt == null || claim.EmailLeaseAcquiredAt <= leaseExpiredBefore)
            )
            .ExecuteUpdateAsync(
                setters =>
                    setters
                        .SetProperty(claim => claim.EmailLeaseId, leaseId)
                        .SetProperty(claim => claim.EmailLeaseAcquiredAt, acquiredAt),
                ct
            ) == 1;

    public async Task<IReadOnlyList<BudgetAlertEmail>> GetDeliverableBudgetAlertEmailsAsync(
        Instant leaseExpiredBefore,
        Instant createdOnOrAfter,
        CancellationToken ct = default
    ) =>
        // The CreatedAt floor age-bounds the pending set: a claim that can never be delivered
        // (nothing configured, a terminally dead channel) would otherwise be re-leased and
        // re-warned on every pass forever, starving newer claims behind the batch-size Take and
        // bursting every historical alert at an operator who configures a channel late. Older
        // claims are left pending, not closed — a channel configured inside the age window
        // still receives them.
        await (
            from claim in ctx.BudgetAlertClaims.AsNoTracking()
            join rule in ctx.BudgetRules.AsNoTracking() on claim.BudgetRuleId equals rule.Id
            where
                claim.EmailSentAt == null
                && claim.CreatedAt >= createdOnOrAfter
                && (claim.EmailLeaseAcquiredAt == null || claim.EmailLeaseAcquiredAt <= leaseExpiredBefore)
            orderby claim.CreatedAt, claim.Id
            select new BudgetAlertEmail(
                claim.Id,
                claim.BudgetRuleId,
                rule.Provider,
                rule.Period,
                claim.PeriodStart,
                claim.PeriodEnd,
                claim.ThresholdGbp,
                claim.ActualSpendGbp,
                claim.CreatedAt
            )
        )
            .Take(BudgetAlertEmailBatchSize)
            .ToListAsync(ct);

    public async Task ReleaseBudgetAlertEmailLeaseAsync(Guid claimId, Guid leaseId, CancellationToken ct = default)
    {
        await ctx
            .BudgetAlertClaims.Where(claim =>
                claim.Id == claimId && claim.EmailSentAt == null && claim.EmailLeaseId == leaseId
            )
            .ExecuteUpdateAsync(
                setters =>
                    setters
                        .SetProperty(claim => claim.EmailLeaseId, (Guid?)null)
                        .SetProperty(claim => claim.EmailLeaseAcquiredAt, (Instant?)null),
                ct
            );
    }

    public async Task MarkBudgetAlertEmailSentAsync(
        Guid claimId,
        Guid leaseId,
        Instant sentAt,
        CancellationToken ct = default
    )
    {
        await ctx
            .BudgetAlertClaims.Where(claim =>
                claim.Id == claimId && claim.EmailSentAt == null && claim.EmailLeaseId == leaseId
            )
            .ExecuteUpdateAsync(setters => setters.SetProperty(claim => claim.EmailSentAt, sentAt), ct);
    }

    public async Task<bool> GetBudgetAlertSlackSentAsync(Guid claimId, CancellationToken ct = default) =>
        await ctx
            .BudgetAlertClaims.AsNoTracking()
            .Where(claim => claim.Id == claimId)
            .Select(claim => claim.SlackSentAt != null)
            .SingleOrDefaultAsync(ct);

    public async Task MarkBudgetAlertSlackSentAsync(Guid claimId, Instant at, CancellationToken ct = default)
    {
        await ctx
            .BudgetAlertClaims.Where(claim => claim.Id == claimId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(claim => claim.SlackSentAt, at), ct);
    }

    public async Task AddInsightAsync(Insight insight, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(insight);
        ctx.Insights.Add(insight);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<Insight>> GetUnacknowledgedInsightsAsync(CancellationToken ct = default)
    {
        return await ctx
            .Insights.AsNoTracking()
            .Where(i => i.AcknowledgedAt == null)
            .OrderByDescending(i => i.GeneratedAt)
            .ToListAsync(ct);
    }

    public async Task AcknowledgeInsightAsync(Guid insightId, Instant at, CancellationToken ct = default)
    {
        await ctx
            .Insights.Where(i => i.Id == insightId)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.AcknowledgedAt, at), ct);
    }

    public async Task<LocalDate?> GetLatestInsightPeriodEndAsync(CancellationToken ct = default)
    {
        // Exclude budget-alert insights: they carry PeriodEnd = today (a notification, not
        // an analysis of a completed day), so counting them would advance the daily-analysis
        // watermark past the current day and permanently skip that day's AI analysis.
        return await ctx
            .Insights.AsNoTracking()
            .Where(i => i.InsightType != InsightType.BudgetAlert)
            .OrderByDescending(i => i.PeriodEnd)
            .Select(i => (LocalDate?)i.PeriodEnd)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<Subscription>> GetActiveSubscriptionsAsync(
        LocalDate today,
        CancellationToken ct = default
    )
    {
        return await ctx
            .Subscriptions.AsNoTracking()
            .Where(s => s.ActiveFrom <= today && (s.ActiveTo == null || s.ActiveTo >= today))
            .ToListAsync(ct);
    }

    public async Task<PatchEventCostResult?> PatchEventCostAsync(
        Provider provider,
        string sourceId,
        string eventKey,
        decimal newCostUsd,
        decimal? newCacheSavingsUsd = null,
        CancellationToken ct = default
    )
    {
        var rawEventKey = eventKey;
        eventKey = ToStoredEventKey(provider, sourceId, rawEventKey)!;
        await using var tx = await ctx.Database.BeginTransactionAsync(ct);
        try
        {
            var existing = await FindEventForKeyLookupAsync(provider, sourceId, eventKey, rawEventKey, ct);
            if (existing is null || existing.Provider != provider)
            {
                await tx.RollbackAsync(ct);
                return null;
            }

            var oldCostUsd = existing.CostUsd;
            var replacement = CopyWithCost(existing, newCostUsd, newCacheSavingsUsd ?? existing.CacheSavingsUsd);
            await ApplyLockedSnapshotAsync(existing, replacement, sourceSuppliedCost: true, ct);

            // The marker is set on the tracked row directly: CopyCanonicalValues never copies
            // CorrectedAt from a replay, so the stamp survives until a source post carrying an
            // explicit cost re-asserts authority (see ApplyLockedSnapshotAsync). It is stamped on
            // the no-op (Unchanged) path too: a re-applied or equal-to-estimate patch is still an
            // operator correction, and without the persisted marker a later cost-less replay would
            // silently undo it.
            existing.CorrectedAt = _clock.GetCurrentInstant();
            await ctx.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new PatchEventCostResult(existing.Id, oldCostUsd, newCostUsd);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            ctx.ChangeTracker.Clear();
            throw;
        }
    }

    // A manual correction replaces an estimated/notional figure with an operator-entered one read
    // from the provider, so it is rebased to ProviderEstimated: that removes the row from the
    // repricer's scan (which only touches ListPriceEstimate/Notional) and keeps the aggregate
    // buckets honest. None ("no price applies") is likewise rebased now that a price exists.
    // The flip happens on any patch, including an equal-value one: an operator confirming the
    // existing figure has still taken authority over it, so the row must leave the scan set or
    // the next catalog activation silently rewrites the confirmed figure. The savings figure is
    // applied verbatim so a correction can clear a stranded unknown-savings count on an event
    // the rebase has just removed from the repricer's reach.
    private static UsageEvent CopyWithCost(UsageEvent source, decimal costUsd, decimal? cacheSavingsUsd) =>
        new()
        {
            Provider = source.Provider,
            OccurredAt = source.OccurredAt,
            IngestedAt = source.IngestedAt,
            Model = source.Model,
            InputTokens = source.InputTokens,
            OutputTokens = source.OutputTokens,
            CacheReadTokens = source.CacheReadTokens,
            CacheWriteTokens = source.CacheWriteTokens,
            CacheWrite1hTokens = source.CacheWrite1hTokens,
            ThoughtTokens = source.ThoughtTokens,
            CostUsd = costUsd,
            CacheSavingsUsd = cacheSavingsUsd,
            Runtime = source.Runtime,
            SessionId = source.SessionId,
            AgentId = source.AgentId,
            RawPayload = source.RawPayload,
            SourceId = source.SourceId,
            SourceKind = source.SourceKind,
            UsageScope = source.UsageScope,
            CostBasis = source.CostBasis is CostBasis.ListPriceEstimate or CostBasis.Notional or CostBasis.None
                ? CostBasis.ProviderEstimated
                : source.CostBasis,
            ObservedAt = source.ObservedAt,
            EventKey = source.EventKey,
        };

    public async Task<IReadOnlyList<EventCostRecord>> GetEventsByProviderAsync(
        Provider provider,
        Instant? from = null,
        Instant? to = null,
        int limit = 10_000,
        CancellationToken ct = default
    )
    {
        // Defense-in-depth: the endpoint already clamps, but a 0/negative limit here would
        // make Take throw or return nothing, and an unbounded one could OOM the response.
        limit = Math.Clamp(limit, 1, 10_000);
        var query = ctx.UsageEvents.AsNoTracking().Where(e => e.Provider == provider);
        if (from is { } f)
        {
            query = query.Where(e => e.OccurredAt >= f);
        }
        if (to is { } t)
        {
            query = query.Where(e => e.OccurredAt <= t);
        }

        // ponytail: Take(limit) is a hard row ceiling so an unbounded provider can't OOM
        // the response; callers needing more page by the from/to date window.
        return await query
            .OrderBy(e => e.OccurredAt)
            .Take(limit)
            .Select(e => new EventCostRecord(
                e.Id,
                e.SourceId,
                e.EventKey,
                e.Runtime,
                e.SessionId,
                e.AgentId,
                e.Model,
                e.InputTokens,
                e.OutputTokens,
                e.CacheWriteTokens,
                e.ThoughtTokens,
                e.CostUsd
            ))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<LocalSnapshotRecord>> GetLocalSnapshotsAsync(
        string sourceId,
        CancellationToken ct = default
    ) =>
        await ctx
            .UsageEvents.AsNoTracking()
            .Where(e =>
                e.SourceId == sourceId
                && e.SourceKind == SourceKind.LocalTelemetry
                && e.EventKey != null
                && !EF.Functions.JsonContains(e.RawPayload, """{"source":"observatory-sweep","tombstone":true}""")
            )
            .OrderBy(e => e.EventKey)
            .Select(e => new LocalSnapshotRecord(
                e.Provider,
                e.OccurredAt,
                e.Model,
                e.CostUsd != null,
                e.Runtime,
                e.SourceId,
                e.SourceKind,
                e.UsageScope,
                e.CostBasis,
                e.EventKey!
            ))
            .ToListAsync(ct);
}
