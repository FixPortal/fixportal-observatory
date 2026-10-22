using AiObservatory.Audit.Contracts;

namespace AiObservatory.Data.Pricing;

public sealed record ResolvedPriceApplication(
    UsagePriceQuote Quote,
    Guid? SnapshotId,
    string? SnapshotHash,
    string RateKey,
    PricingDimensions Dimensions,
    DateOnly PricingDate,
    string PricingRuleVersion,
    bool RuleDerived = false
);
