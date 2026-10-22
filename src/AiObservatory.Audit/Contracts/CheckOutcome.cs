using System.Text.Json.Serialization;

namespace AiObservatory.Audit.Contracts;

public sealed record CheckOutcome(
    CheckCode Code,
    CheckVerdict Verdict,
    AuditCoverage Coverage,
    IReadOnlyList<AuditFinding> Findings
)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<PricingEquationTrace>? PricingTraces { get; init; }
}

/// <summary>Each term means tokens times unit price times multiplier divided by unit tokens.</summary>
public sealed record PricingEquationTrace(
    string EvidenceRef,
    string CatalogId,
    string RateId,
    PriceMeter Meter,
    long Tokens,
    long UnitTokens,
    decimal UnitPrice,
    decimal Multiplier,
    decimal Amount,
    string Currency
);
