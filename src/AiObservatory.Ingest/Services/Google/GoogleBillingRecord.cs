using NodaTime;

namespace AiObservatory.Ingest.Services.Google;

public sealed record GoogleBillingRecord(
    LocalDate UsageDate,
    string BillingPeriod,
    string ServiceId,
    string ServiceDescription,
    string SkuId,
    string SkuDescription,
    string Currency,
    decimal GrossAmount,
    decimal CreditAmount,
    decimal NetAmount,
    Instant ObservedAt,
    string RawJson
);

/// <param name="OutOfRangeAffectedKeyCount">
/// Affected keys whose usage dates fall below the 31-day scan floor of the line_items query:
/// late corrections the export announced but the pruned scan can never return, so the stored
/// observations for those keys silently stay stale. Zero means every affected key was covered.
/// </param>
public sealed record GoogleBillingExportResult(
    IReadOnlyList<GoogleBillingRecord> Records,
    long OutOfRangeAffectedKeyCount
);
