namespace AiObservatory.Audit.Contracts;

/// <summary>Evaluation policy is frozen input. Resource/retention limits belong to server settings.</summary>
public sealed record AuditOptions
{
    public RoundingPolicy CurrencyRounding { get; init; } = RoundingPolicy.MidpointAwayFromZero;
    public int PresentationDecimalPlaces { get; init; } = 2;
    public bool IncludeProvisionalAttribution { get; init; } = true;
}

public enum RoundingPolicy
{
    MidpointAwayFromZero,
}

/// <summary>A null scale denotes unconstrained numeric and requires exact comparison.</summary>
public sealed record NamedTolerance(
    string Name,
    ValueUnit Unit,
    string? Currency,
    int? StoredScale,
    decimal AbsoluteTolerance
);

public enum ValueUnit
{
    Tokens,
    Money,
    Count,
    Ratio,
}
