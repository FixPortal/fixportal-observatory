namespace AiObservatory.Audit.Contracts;

public sealed record ObservationProvenance(SourceKind SourceKind, UsageScope UsageScope, AccountingBasis CostBasis);

public enum SourceKind
{
    ProviderApi,
    LocalTelemetry,
    Manual,
    Synthetic,
    Legacy,
}

public enum UsageScope
{
    Api,
    Subscription,
    Mixed,
    Unknown,
}

public enum AccountingBasis
{
    Billed,
    ProviderEstimated,
    ListPriceEstimate,
    Notional,
    None,
    Unknown,
}
