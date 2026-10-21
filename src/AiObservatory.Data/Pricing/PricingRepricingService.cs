using AiObservatory.Data.Entities;
using AiObservatory.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiObservatory.Data.Pricing;

public sealed class PricingRepricingService(
    AiObservatoryDbContext db,
    IUsageRepository repository,
    UsagePriceResolver resolver,
    PricingSnapshotStore store,
    ILogger<PricingRepricingService>? logger = null
)
{
    private readonly ILogger<PricingRepricingService> _logger = logger ?? NullLogger<PricingRepricingService>.Instance;

    public async Task RepriceProviderAsync(Provider provider, CancellationToken cancellationToken = default)
    {
        // Called two ways: from a catalog activation's beforeCommit — where the activation already
        // holds the exclusive advisory lock inside its transaction and the pass joins it — and
        // standalone at startup.
        if (db.Database.CurrentTransaction is not null)
        {
            var activationEvents = await LoadRepricingCandidatesAsync(provider, cancellationToken);
            await RepriceLockedAsync(activationEvents, snapshotsBySourceId: null, cancellationToken);
            return;
        }

        // Standalone pass. The shared activation lock is held only for a short read transaction
        // that loads the candidate events and each source's snapshot rows, so the pass prices
        // from one catalog generation without blocking a concurrent activation for the sweep.
        // Each event is then repriced in its own transaction (UpdateEventPricingAsync opens one
        // when none is current): holding every per-event FOR UPDATE and aggregate upsert until a
        // pass-level commit deadlocked against concurrent ingest (40P01), rolled the whole
        // provider's repricing back on a single failure, and held the activation lock for the
        // full pass. The lock acquisition stays inside the await using so a throw there cannot
        // leave a half-open transaction attached to the scoped context.
        IReadOnlyList<UsageEvent> events;
        Dictionary<string, List<PricingSnapshot>> snapshotsBySourceId;
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            try
            {
                await store.AcquireSharedActivationLocksAsync(provider, cancellationToken);
                events = await LoadRepricingCandidatesAsync(provider, cancellationToken);
                snapshotsBySourceId = await LoadSnapshotCacheAsync(events, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                db.ChangeTracker.Clear();
                throw;
            }
        }

        await RepriceLockedAsync(events, snapshotsBySourceId, cancellationToken);
    }

    // ponytail: pricing changes are rare and Observatory volume is modest; target by effective date/model if this scan is measured as slow.
    private async Task<List<UsageEvent>> LoadRepricingCandidatesAsync(
        Provider provider,
        CancellationToken cancellationToken
    ) =>
        await db
            .UsageEvents.AsNoTracking()
            .Where(usage =>
                usage.Provider == provider
                && (usage.CostBasis == CostBasis.ListPriceEstimate || usage.CostBasis == CostBasis.Notional)
            )
            .OrderBy(usage => usage.Id)
            .ToListAsync(cancellationToken);

    // Reads each source's snapshot rows once through the pass's cache while the shared
    // activation lock is still held, so every event in the pass prices from the same catalog
    // generation. Notional events are cached too: GetCoveringSnapshotsAsync serves them from
    // the same per-source cache, so skipping them would let a notional event whose source is
    // not yet cached trigger a live read inside RepriceLockedAsync — AFTER the read
    // transaction commits and the shared activation lock is released — and a concurrent
    // activation in between would price part of the pass from a newer catalog generation.
    private async Task<Dictionary<string, List<PricingSnapshot>>> LoadSnapshotCacheAsync(
        IReadOnlyList<UsageEvent> events,
        CancellationToken cancellationToken
    )
    {
        var snapshotsBySourceId = new Dictionary<string, List<PricingSnapshot>>(StringComparer.Ordinal);
        foreach (var usage in events)
        {
            await store.GetCoveringSnapshotsAsync(usage, snapshotsBySourceId, cancellationToken);
        }

        return snapshotsBySourceId;
    }

    private async Task RepriceLockedAsync(
        IReadOnlyList<UsageEvent> events,
        Dictionary<string, List<PricingSnapshot>>? snapshotsBySourceId,
        CancellationToken cancellationToken
    )
    {
        // The snapshot rows a pass prices from cannot change underneath it: on the activation
        // path the pass runs inside the activation's transaction holding the exclusive lock, and
        // on the standalone path the cache was populated under the shared lock above. The cache
        // dies with the pass, so no later pass can see a stale catalog; the activation path
        // builds its own here so snapshot rows are still read once per source, not per event.
        // The effective-date filter still runs per event.
        snapshotsBySourceId ??= new Dictionary<string, List<PricingSnapshot>>(StringComparer.Ordinal);
        var skipped = new Dictionary<(Provider Provider, string Model), int>();
        foreach (var usage in events)
        {
            var quote = await resolver.ResolveAsync(usage, snapshotsBySourceId, cancellationToken);
            if (quote is null && usage.CostUsd is not null)
            {
                // No snapshot covers this event (e.g. a model every retained catalog has dropped).
                // An unresolvable event keeps its last known price and basis — blanking a figure
                // we once knew converts priced history into "Not reported" on both the row and
                // its aggregate.
                var key = (usage.Provider, usage.Model ?? "<missing>");
                skipped[key] = skipped.GetValueOrDefault(key) + 1;
                continue;
            }

            if (usage.CostUsd != quote?.CostUsd || usage.CacheSavingsUsd != quote?.CacheSavingsUsd)
            {
                await repository.UpdateEventPricingAsync(usage, quote, cancellationToken);
            }
        }

        // One line per (provider, model) per pass, not one per event: a startup pass over many
        // unpriceable events otherwise repeats the identical warning N times per restart and
        // buries real signal.
        foreach (var ((provider, model), count) in skipped)
        {
            _logger.LogWarning(
                "Repricing skipped {SkippedCount} event(s) for {Provider}/{Model}: no pricing snapshot covers them; keeping the existing costs.",
                count,
                provider,
                model.Replace('\r', ' ').Replace('\n', ' ')
            );
        }
    }
}
