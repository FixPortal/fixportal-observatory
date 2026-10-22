using AiObservatory.Audit.Contracts;

namespace AiObservatory.Data.Pricing;

public sealed record CalculatedPrice(
    UsagePriceQuote Quote,
    string RateKey,
    PricingDimensions Dimensions,
    DateOnly PricingDate
);
