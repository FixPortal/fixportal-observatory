using AiObservatory.Audit.Contracts;

namespace AiObservatory.Audit.Pricing;

public static class OpenAiAuditPricing
{
    public static IReadOnlyList<(PriceMeter Meter, long Tokens)> Terms(FrozenEvent e)
    {
        var t = e.Tokens;
        var read = t.CacheReadTokens ?? 0;
        if (read > t.InputTokens || t.CacheWriteTokens is > 0)
        {
            throw new AuditValidationException("openai_token_subset_invalid");
        }
        return
        [
            (PriceMeter.Input, checked(t.InputTokens - read)),
            (PriceMeter.CacheRead, read),
            (PriceMeter.Output, t.OutputTokens),
        ];
    }
}
