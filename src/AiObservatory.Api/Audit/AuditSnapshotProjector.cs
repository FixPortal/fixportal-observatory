using System.Text;
using AiObservatory.Audit.Contracts;
using AiObservatory.Audit.Serialization;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Pricing;
using NodaTime;
using Application = AiObservatory.Data.Audit.PricingApplication;
using ContractApplication = AiObservatory.Audit.Contracts.PricingApplication;

namespace AiObservatory.Api.Audit;

/// <summary>Production-to-audit boundary. Only typed allowlisted facts cross it; identifiers are opaque.</summary>
public static class AuditSnapshotProjector
{
    public static FrozenEvent Event(UsageEvent e, Application? application)
    {
        var sourceDimensions = PricingEvidence.SourceDimensions(e);
        ContractApplication? provenance = null;
        if (application is not null && application.Kind is "catalog" or "ruleDerived")
        {
            provenance = new(
                application.SnapshotId?.ToString(),
                application.SnapshotHash,
                application.RateKey!,
                AuditCanonicalJson.Deserialize<PricingDimensions>(application.Dimensions),
                application.PricingDate
            )
            {
                Kind = application.Kind,
                RuleVersion = application.RuleVersion,
            };
        }
        return new(
            e.Id,
            Opaque(e.EventKey),
            e.SourceId,
            Provenance(e.SourceKind, e.UsageScope, e.CostBasis),
            e.OccurredAt.ToDateTimeOffset(),
            e.ObservedAt.ToDateTimeOffset(),
            e.IngestedAt.ToDateTimeOffset(),
            e.Provider.ToString(),
            e.Model,
            new(
                e.InputTokens,
                e.OutputTokens,
                e.CacheReadTokens,
                e.CacheWriteTokens,
                e.CacheWrite1hTokens,
                e.ThoughtTokens
            ),
            e.CostUsd,
            e.CacheSavingsUsd,
            Opaque(e.Runtime),
            Opaque(e.SessionId),
            Opaque(e.AgentId),
            e.CorrectedAt?.ToDateTimeOffset(),
            PricingEvidence.RevisionDigest(e),
            provenance
        )
        {
            SourceDimensions = sourceDimensions,
            CacheDurations = e.Provider == Provider.Anthropic ? PricingEvidence.ObserveCacheDurations(e) : null,
        };
    }

    public static FrozenAggregate Aggregate(DailyAggregate a) =>
        new(
            new(
                Date(a.Date),
                a.Provider.ToString(),
                a.Model,
                a.SourceId,
                Enum.Parse<AiObservatory.Audit.Contracts.SourceKind>(a.SourceKind.ToString()),
                Enum.Parse<AiObservatory.Audit.Contracts.UsageScope>(a.UsageScope.ToString()),
                Enum.Parse<AccountingBasis>(a.CostBasis.ToString())
            ),
            a.InputTokens,
            a.OutputTokens,
            a.CacheReadTokens,
            a.CacheWriteTokens,
            a.CacheWrite1hTokens,
            a.CostUsd,
            a.UnknownCostCount,
            a.CacheSavingsUsd,
            a.UnknownCacheSavingsCount,
            a.RequestCount
        );

    public static FrozenSpend Spend(SpendEntry s)
    {
        var value = new FrozenSpend(
            s.Id,
            s.SourceId,
            Opaque(s.EntryKey),
            null,
            null,
            s.VendorId,
            s.CategoryId,
            Date(s.OccurredOn),
            null,
            null,
            Provenance(s.SourceKind, s.UsageScope, s.CostBasis),
            s.Currency,
            s.Amount,
            s.AmountGbp,
            s.FxRate,
            s.Id.ToString(),
            s.RecordedAt.ToDateTimeOffset(),
            s.ObservedAt.ToDateTimeOffset(),
            new string('0', 64)
        );
        return value with { RevisionDigest = AuditCanonicalJson.ContentHash(AuditCanonicalJson.Serialize(value)) };
    }

    public static FrozenFx Fx(FrozenSpend s)
    {
        var value = new FrozenFx(
            "stored-spend",
            s.RowId.ToString(),
            new string('0', 64),
            s.OccurredOn,
            s.Currency,
            "GBP",
            s.FxRate,
            s.RecordedAt
        );
        return value with { ContentHash = AuditCanonicalJson.ContentHash(AuditCanonicalJson.Serialize(value)) };
    }

    public static FrozenSourceStatus Status(SourceSyncState s) =>
        new(
            s.SourceId,
            s.IsConfigured,
            s.IsAvailable,
            s.ExpectedRefreshIntervalSeconds,
            s.LastAttemptAt?.ToDateTimeOffset(),
            s.LastSuccessAt?.ToDateTimeOffset(),
            s.LatestObservationAt?.ToDateTimeOffset(),
            s.PendingFromDate is null ? null : Date(s.PendingFromDate.Value),
            s.ConsecutiveFailureCount,
            s.LastError is null ? null : "source_error"
        );

    public static FrozenFinancialObservation Financial(BillingObservation b) =>
        new(
            b.Id,
            b.SourceId,
            b.ProviderKey,
            Opaque(b.ObservationKey)!,
            Date(b.OccurredOn),
            Opaque(b.BillingPeriod),
            Provenance(b.SourceKind, b.UsageScope, b.CostBasis),
            b.Currency,
            b.GrossAmount,
            b.CreditAmount,
            b.NetAmount,
            b.ObservedAt.ToDateTimeOffset()
        );

    public static ObservationProvenance Provenance(
        AiObservatory.Data.Entities.SourceKind kind,
        AiObservatory.Data.Entities.UsageScope scope,
        CostBasis basis
    ) =>
        new(
            Enum.Parse<AiObservatory.Audit.Contracts.SourceKind>(kind.ToString()),
            Enum.Parse<AiObservatory.Audit.Contracts.UsageScope>(scope.ToString()),
            Enum.Parse<AccountingBasis>(basis.ToString())
        );

    public static DateOnly Date(LocalDate d) => new(d.Year, d.Month, d.Day);

    public static string? Opaque(string? value) =>
        value is null ? null : AuditCanonicalJson.ContentHash(Encoding.UTF8.GetBytes(value));
}
