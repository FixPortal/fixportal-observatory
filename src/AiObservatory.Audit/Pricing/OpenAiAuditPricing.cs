using AiObservatory.Audit.Contracts;

namespace AiObservatory.Audit.Pricing;

public static class OpenAiAuditPricing
{
    public static IReadOnlyList<(PriceMeter Meter, long Tokens)> Terms(FrozenEvent e)
    {
        var t = e.Tokens;
        var read = t.CacheReadTokens ?? 0;
        return
        [
            (PriceMeter.Input, t.InputTokens),
            (PriceMeter.CacheRead, read),
            (PriceMeter.CacheWrite5m, t.CacheWriteTokens ?? 0),
            (PriceMeter.Output, t.OutputTokens),
        ];
    }
}
