using System.Data;
using AiObservatory.Audit;
using AiObservatory.Audit.Contracts;
using AiObservatory.Audit.Serialization;
using AiObservatory.Data;
using AiObservatory.Data.Audit;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace AiObservatory.Api.Audit;

/// <summary>Lease-bound freezer. No verification or network work runs in either database transaction.</summary>
public sealed class AuditSnapshotService
{
    private readonly AiObservatoryDbContext _db;
    private readonly AuditStore _store;
    private readonly RunLease _lease;
    private readonly long _maximumBytes;

    public AuditSnapshotService(
        AiObservatoryDbContext db,
        AuditStore store,
        RunLease lease,
        long maxSnapshotBytes = 512L * 1024 * 1024
    )
    {
        if (maxSnapshotBytes is <= 0 or > 512L * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSnapshotBytes));
        }
        _db = db;
        _store = store;
        _lease = lease;
        _maximumBytes = maxSnapshotBytes;
    }

    public async Task<Guid> CreateAsync(Guid runId, CancellationToken ct)
    {
        if (runId != _lease.RunId)
        {
            throw new AuditStoreException("snapshot_lease_lost", 409);
        }
        var evidence = await _store.ReadSnapshotMaterializationAsync(_lease, ct);
        var retained = await _store.ReadSnapshotAsync(runId, ct);
        if (retained is not null)
        {
            return retained.SnapshotId;
        }
        var budget = new ProjectionBudget(_maximumBytes);
        var (captures, records) = CaptureFacts(evidence, budget);
        AuditInput input;
        await using (var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct))
        {
            input = await Materialize(evidence.Scope, budget, ct);
            await tx.CommitAsync(ct);
        }
        input = input with { Captures = captures, Records = records };
        var bytes = AuditCanonicalJson.Serialize(input);
        if (bytes.LongLength > _maximumBytes)
        {
            throw new AuditStoreException("snapshot_size_limit", 413);
        }
        // Digest the actual canonical frozen input, removing operational links only for computation identity.
        var digest = AuditComputationInput.ContentDigest(input);
        if (!await _store.TryStoreSnapshotAsync(_lease, bytes, digest, ct))
        {
            throw new AuditStoreException("snapshot_lease_lost", 409);
        }
        return (await _store.ReadSnapshotAsync(runId, ct))?.SnapshotId
            ?? throw new AuditStoreException("snapshot_lease_lost", 409);
    }

    private async Task<AuditInput> Materialize(AuditScope scope, ProjectionBudget budget, CancellationToken ct)
    {
        var from = new LocalDate(scope.From.Year, scope.From.Month, scope.From.Day);
        var to = new LocalDate(scope.To.Year, scope.To.Month, scope.To.Day);
        var start = from.AtStartOfDayInZone(DateTimeZone.Utc).ToInstant();
        var end = to.PlusDays(1).AtStartOfDayInZone(DateTimeZone.Utc).ToInstant();
        var sourceIds = scope.Sources.Select(s => s.SourceId).Distinct().ToArray();
        var events = new List<FrozenEvent>();
        var eventQuery =
            from e in _db.UsageEvents.AsNoTracking()
            join a in _db.PricingApplications.AsNoTracking()
                on new { EventId = e.Id, Revision = e.CurrentPricingRevision } equals new { a.EventId, a.Revision }
                into apps
            from a in apps.DefaultIfEmpty()
            where e.OccurredAt >= start && e.OccurredAt < end && sourceIds.Contains(e.SourceId)
            orderby e.Id
            select new { Event = e, Application = a };
        await foreach (var row in eventQuery.AsAsyncEnumerable().WithCancellation(ct))
        {
            events.Add(budget.Take(AuditSnapshotProjector.Event(row.Event, row.Application)));
        }
        var aggregates = new List<FrozenAggregate>();
        await foreach (
            var row in _db
                .DailyAggregates.AsNoTracking()
                .Where(a => a.Date >= from && a.Date <= to && sourceIds.Contains(a.SourceId))
                .AsAsyncEnumerable()
                .WithCancellation(ct)
        )
        {
            aggregates.Add(budget.Take(AuditSnapshotProjector.Aggregate(row)));
        }
        var spend = await Spend(from, to, budget, ct);
        var financial = new List<FrozenFinancialObservation>();
        await foreach (
            var row in _db
                .BillingObservations.AsNoTracking()
                .Where(b => b.OccurredOn >= from && b.OccurredOn <= to)
                .Select(b => new AiObservatory.Data.Entities.BillingObservation
                {
                    Id = b.Id,
                    SourceId = b.SourceId,
                    ProviderKey = b.ProviderKey,
                    ObservationKey = b.ObservationKey,
                    OccurredOn = b.OccurredOn,
                    BillingPeriod = b.BillingPeriod,
                    SourceKind = b.SourceKind,
                    UsageScope = b.UsageScope,
                    CostBasis = b.CostBasis,
                    Currency = b.Currency,
                    GrossAmount = b.GrossAmount,
                    CreditAmount = b.CreditAmount,
                    NetAmount = b.NetAmount,
                    ObservedAt = b.ObservedAt,
                })
                .AsAsyncEnumerable()
                .WithCancellation(ct)
        )
        {
            financial.Add(budget.Take(AuditSnapshotProjector.Financial(row)));
        }
        var statuses = new List<FrozenSourceStatus>();
        await foreach (var row in _db.SourceSyncStates.AsNoTracking().AsAsyncEnumerable().WithCancellation(ct))
        {
            statuses.Add(budget.Take(AuditSnapshotProjector.Status(row)));
        }
        var catalogs = new List<FrozenPricing>();
        await foreach (
            var row in _db
                .PricingSnapshots.AsNoTracking()
                .Select(p => new AiObservatory.Data.Entities.PricingSnapshot
                {
                    Id = p.Id,
                    Provider = p.Provider,
                    SourceId = p.SourceId,
                    RetrievedAt = p.RetrievedAt,
                    ContentHash = p.ContentHash,
                    NormalizedCatalog = p.NormalizedCatalog,
                })
                .AsAsyncEnumerable()
                .WithCancellation(ct)
        )
        {
            catalogs.Add(budget.Take(AuditPricingProjector.Catalog(row)));
        }
        return new()
        {
            SchemaVersion = AuditVerifierIdentity.SchemaVersion,
            VerifierVersion = AuditVerifierIdentity.VerifierVersion,
            RuleVersion = AuditVerifierIdentity.RuleVersion,
            Scope = scope,
            Events = events,
            Aggregates = aggregates,
            Spend = spend,
            FinancialObservations = financial,
            SourceStatuses = statuses,
            PricingCatalogs = catalogs,
            FxRates = spend.Select(AuditSnapshotProjector.Fx).Select(budget.Take).ToArray(),
            // Current EF mappings are unconstrained PostgreSQL numeric; their stored scale is null and comparison is exact.
            Tolerances =
            [
                new("tokens", ValueUnit.Tokens, null, null, 0m),
                new("eventAggregation.costUsd", ValueUnit.Money, "USD", null, 0m),
                new("eventAggregation.cacheSavingsUsd", ValueUnit.Money, "USD", null, 0m),
                new("spend.amountGbp", ValueUnit.Money, "GBP", null, 0m),
            ],
        };
    }

    private async Task<List<FrozenSpend>> Spend(
        LocalDate from,
        LocalDate to,
        ProjectionBudget budget,
        CancellationToken ct
    )
    {
        var result = new List<FrozenSpend>();
        await foreach (
            var row in _db
                .SpendEntries.AsNoTracking()
                .Where(s => s.OccurredOn >= from && s.OccurredOn <= to)
                .Select(s => new AiObservatory.Data.Entities.SpendEntry
                {
                    Id = s.Id,
                    SourceId = s.SourceId,
                    EntryKey = s.EntryKey,
                    VendorId = s.VendorId,
                    CategoryId = s.CategoryId,
                    OccurredOn = s.OccurredOn,
                    SourceKind = s.SourceKind,
                    UsageScope = s.UsageScope,
                    CostBasis = s.CostBasis,
                    Currency = s.Currency,
                    Amount = s.Amount,
                    AmountGbp = s.AmountGbp,
                    FxRate = s.FxRate,
                    RecordedAt = s.RecordedAt,
                    ObservedAt = s.ObservedAt,
                })
                .AsAsyncEnumerable()
                .WithCancellation(ct)
        )
        {
            result.Add(budget.Take(AuditSnapshotProjector.Spend(row)));
        }
        return result;
    }

    private static (List<CaptureManifest>, List<SourceUsageRecord>) CaptureFacts(
        SnapshotMaterialization evidence,
        ProjectionBudget budget
    )
    {
        var manifests = new List<CaptureManifest>();
        var records = new List<SourceUsageRecord>();
        foreach (var capture in evidence.Captures)
        {
            manifests.Add(budget.Take(AuditCanonicalJson.Deserialize<CaptureManifest>(capture.Manifest)));
            foreach (var chunk in capture.Chunks)
            {
                var offset = 0;
                for (var i = 0; i < chunk.Length; i++)
                {
                    if (chunk[i] != 10)
                    {
                        continue;
                    }
                    records.Add(
                        budget.Take(AuditCanonicalJson.Deserialize<SourceUsageRecord>(chunk.AsSpan(offset, i - offset)))
                    );
                    offset = i + 1;
                }
                if (offset != chunk.Length)
                {
                    throw new AuditStoreException("invalid_chunk_records", 400);
                }
            }
        }
        return (manifests, records);
    }

    private sealed class ProjectionBudget(long maximumBytes)
    {
        private long _bytes;

        public T Take<T>(T value)
        {
            _bytes = checked(_bytes + AuditCanonicalJson.Serialize(value).LongLength);
            if (_bytes > maximumBytes)
            {
                throw new AuditStoreException("snapshot_size_limit", 413);
            }
            return value;
        }
    }
}
