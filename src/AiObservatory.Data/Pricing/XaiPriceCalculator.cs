using AiObservatory.Data.Entities;
using AiObservatory.Data.Pricing.Catalogs;

namespace AiObservatory.Data.Pricing;

public sealed class XaiPriceCalculator : IProviderPriceCalculator
{
    public Provider Provider => Provider.Xai;

    public UsagePriceQuote? Calculate(UsageEvent usage, string normalizedCatalog)
    {
        if (string.IsNullOrWhiteSpace(usage.Model))
        {
            return null;
        }

        using var evidence = ProviderPricingJson.Evidence(usage.RawPayload);
        var isNotional = usage.CostBasis == CostBasis.Notional;
        var hasLongContext = ProviderPricingJson.TryBoolean(evidence.RootElement, "long_context", out var longContext);

        // Which lane a request took is a property of that single request's prompt, so only the
        // source that saw the request can state it. A measured event that does not is refused
        // rather than guessed — the two lanes differ by 2x, so guessing is a 2x error either way.
        // A notional event is an aggregate of many requests and can never carry the flag; it
        // takes the standard lane, which is the same shape as Kimi's notional handling.
        //
        // This refusal does NOT strand the grok-local lane, which carries no flag either. That
        // lane posts CostBasis.ProviderEstimated with the cost the CLI itself recorded, and
        // provider-estimated events never reach a calculator: the API routes only
        // ListPriceEstimate and Notional through RecordEstimatedEventAsync, and
        // PricingRepricingService loads only those two bases as repricing candidates. What this
        // guards is a future measured lane — an xAI API usage source, which would see each
        // request's prompt and so can state the lane. Defaulting such an event to the standard
        // lane instead would halve the cost of every long-context request, silently.
        if (!isNotional && !hasLongContext)
        {
            return null;
        }

        var catalog = PricingCatalogJson.Deserialize<XaiPriceCatalog>(normalizedCatalog);
        var pricingDate = isNotional ? catalog.RetrievedAt.InUtc().Date : usage.OccurredAt.InUtc().Date;
        var entry = catalog.Resolve(usage.Model, pricingDate);
        if (entry is null)
        {
            return null;
        }

        var (input, cachedInput, output) = longContext
            ? (entry.LongContextInput, entry.LongContextCachedInput, entry.LongContextOutput)
            : (entry.Input, entry.CachedInput, entry.Output);

        // xAI publishes no separate cache-write rate: writing to the prompt cache is billed as
        // ordinary input, so cache-write tokens join the uncached side rather than being dropped.
        var cacheRead = usage.CacheReadTokens ?? 0;
        var uncached = usage.InputTokens + (usage.CacheWriteTokens ?? 0);

        // ThoughtTokens is deliberately NOT added. xAI bills reasoning as completion tokens, and
        // the Grok CLI already counts them inside its output total — its own session totals
        // reconcile as input + output with reasoning nested inside output, so adding the field
        // again would charge reasoning twice. Sources report it for visibility, not for billing.
        var cost =
            PerMillion(uncached, input) + PerMillion(cacheRead, cachedInput) + PerMillion(usage.OutputTokens, output);
        var savings = PerMillion(cacheRead, input - cachedInput);
        return new UsagePriceQuote(cost, savings);
    }

    private static decimal PerMillion(long tokens, decimal rate) => tokens / 1_000_000m * rate;
}
