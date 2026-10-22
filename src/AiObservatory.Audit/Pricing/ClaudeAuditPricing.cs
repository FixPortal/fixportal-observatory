using AiObservatory.Audit.Contracts;

namespace AiObservatory.Audit.Pricing;

public static class ClaudeAuditPricing
{
    public static IReadOnlyList<(PriceMeter Meter, long Tokens)>? Terms(FrozenEvent e, out string? diagnostic)
    {
        diagnostic = null;
        var t = e.Tokens;
        var writes = t.CacheWriteTokens ?? 0;
        var hour = t.CacheWrite1hTokens ?? 0;
        if (hour > writes)
        {
            diagnostic = "cache_write_subset_invalid";
            return null;
        }
        var five = checked(writes - hour);
        if (writes > 0)
        {
            if (e.CacheDurations == null)
            {
                diagnostic = "cache_duration_not_frozen";
                return null;
            }
            if (e.CacheDurations.FiveMinuteTokens != five || e.CacheDurations.OneHourTokens != hour)
            {
                diagnostic = "cache_duration_not_corroborated";
                if (e.Provenance.CostBasis != AccountingBasis.Notional || e.PricingApplication?.RuleVersion != "1")
                {
                    return null;
                }
                five = writes;
                hour = 0;
            }
        }
        return
        [
            (PriceMeter.Input, t.InputTokens),
            (PriceMeter.Output, t.OutputTokens),
            (PriceMeter.CacheRead, t.CacheReadTokens ?? 0),
            (PriceMeter.CacheWrite5m, five),
            (PriceMeter.CacheWrite1h, hour),
        ];
    }
}
