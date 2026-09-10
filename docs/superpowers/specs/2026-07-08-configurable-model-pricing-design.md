# Configurable model pricing for Anthropic/OpenAI ingest

**Status: SUPERSEDED on 2026-08-24 by
[`2026-08-24-source-aware-observability-design.md`](2026-08-24-source-aware-observability-design.md).**
The replacement preserves this design's effective-dated intent but removes unsafe fallback
pricing, adds first-party daily renewal, and models the pricing dimensions providers now
publish.

## Problem

`AnthropicUsageClient` and `OpenAiUsageClient` hardcode per-1M-token pricing
tables as `private static readonly Dictionary<...>` fields, plus a
special-cased `if` block in `AnthropicUsageClient.ComputeCost` for the
Sonnet-5 introductory-pricing window (ends 2026-08-31). A price change or new
model requires a code change + redeploy. This moves pricing to configuration.

Out of scope: `GoogleBillingClient` — the Cloud Billing API returns actual
`Cost` per record directly; there is no per-token rate table to externalize.

## Config shape

Provider-specific sections under `Ingest`, each with a `Pricing` list and a
`FallbackPricing` entry (used when no `ModelPrefix` matches, same as today's
default-to-Sonnet / default-to-gpt-4o behavior, now config-driven too).

```jsonc
"Ingest": {
  "Anthropic": {
    "Pricing": [
      { "ModelPrefix": "claude-3-5-sonnet", "Input": 3.0, "Output": 15.0, "CacheRead": 0.30, "CacheWrite": 3.75 },
      { "ModelPrefix": "claude-3-5-haiku",  "Input": 0.8, "Output": 4.0,  "CacheRead": 0.08, "CacheWrite": 1.00 },
      { "ModelPrefix": "claude-opus-4",     "Input": 15.0,"Output": 75.0, "CacheRead": 1.50, "CacheWrite": 18.75 },
      { "ModelPrefix": "claude-sonnet-4",   "Input": 3.0, "Output": 15.0, "CacheRead": 0.30, "CacheWrite": 3.75 },
      { "ModelPrefix": "claude-sonnet-5",   "Input": 2.0, "Output": 10.0, "CacheRead": 0.20, "CacheWrite": 2.50, "EffectiveTo": "2026-08-31" },
      { "ModelPrefix": "claude-sonnet-5",   "Input": 3.0, "Output": 15.0, "CacheRead": 0.30, "CacheWrite": 3.75, "EffectiveFrom": "2026-09-01" },
      { "ModelPrefix": "claude-haiku-4",    "Input": 0.8, "Output": 4.0,  "CacheRead": 0.08, "CacheWrite": 1.00 },
      { "ModelPrefix": "claude-3-opus",     "Input": 15.0,"Output": 75.0, "CacheRead": 1.50, "CacheWrite": 18.75 },
      { "ModelPrefix": "claude-3-sonnet",   "Input": 3.0, "Output": 15.0, "CacheRead": 0.30, "CacheWrite": 3.75 },
      { "ModelPrefix": "claude-3-haiku",    "Input": 0.25,"Output": 1.25, "CacheRead": 0.03, "CacheWrite": 0.3125 }
    ],
    "FallbackPricing": { "Input": 3.0, "Output": 15.0, "CacheRead": 0.30, "CacheWrite": 3.75 }
  },
  "OpenAi": {
    "Pricing": [
      { "ModelPrefix": "gpt-4.1",      "Input": 2.00, "Output": 8.00,  "CacheRead": 0.50 },
      { "ModelPrefix": "gpt-4.1-mini", "Input": 0.40, "Output": 1.60,  "CacheRead": 0.10 },
      { "ModelPrefix": "gpt-4.1-nano", "Input": 0.10, "Output": 0.40,  "CacheRead": 0.025 },
      { "ModelPrefix": "gpt-4o",       "Input": 2.50, "Output": 10.00, "CacheRead": 1.25 },
      { "ModelPrefix": "gpt-4o-mini",  "Input": 0.15, "Output": 0.60,  "CacheRead": 0.075 },
      { "ModelPrefix": "o1",           "Input": 15.0, "Output": 60.00, "CacheRead": 7.50 },
      { "ModelPrefix": "o1-mini",      "Input": 1.10, "Output": 4.40,  "CacheRead": 0.55 },
      { "ModelPrefix": "o3",           "Input": 2.00, "Output": 8.00,  "CacheRead": 0.50 },
      { "ModelPrefix": "o3-mini",      "Input": 1.10, "Output": 4.40,  "CacheRead": 0.55 },
      { "ModelPrefix": "o4-mini",      "Input": 1.10, "Output": 4.40,  "CacheRead": 0.275 }
    ],
    "FallbackPricing": { "Input": 2.50, "Output": 10.00, "CacheRead": 1.25 }
  }
}
```

`EffectiveFrom`/`EffectiveTo` are optional `LocalDate?`. A dated entry folds
the Sonnet-5 intro-pricing window into data — no special-cased code path.

Anthropic and OpenAI get separate schemas (4-field vs 3-field rates) rather
than one shared type with a dangling nullable `CacheWrite` on OpenAI entries.

## Options classes

```csharp
public sealed class AnthropicPricingOptions
{
    [MinLength(1)]
    public List<AnthropicPricingEntry> Pricing { get; init; } = [];
    public required PricingRates4 FallbackPricing { get; init; }
}

public sealed record AnthropicPricingEntry(
    string ModelPrefix, decimal Input, decimal Output, decimal CacheRead, decimal CacheWrite,
    LocalDate? EffectiveFrom = null, LocalDate? EffectiveTo = null);

public sealed record PricingRates4(decimal Input, decimal Output, decimal CacheRead, decimal CacheWrite);

public sealed class OpenAiPricingOptions
{
    [MinLength(1)]
    public List<OpenAiPricingEntry> Pricing { get; init; } = [];
    public required PricingRates3 FallbackPricing { get; init; }
}

public sealed record OpenAiPricingEntry(
    string ModelPrefix, decimal Input, decimal Output, decimal CacheRead,
    LocalDate? EffectiveFrom = null, LocalDate? EffectiveTo = null);

public sealed record PricingRates3(decimal Input, decimal Output, decimal CacheRead);
```

## Resolution logic

Replaces the dictionary lookup (and, for Anthropic, the Sonnet-5 intro-window
`if` block) in `ComputeCost`:

```csharp
var match = options.Value.Pricing
    .Where(e => model.StartsWith(e.ModelPrefix, StringComparison.OrdinalIgnoreCase))
    .Where(e => (e.EffectiveFrom is null || usageDate >= e.EffectiveFrom)
             && (e.EffectiveTo   is null || usageDate <= e.EffectiveTo))
    .OrderByDescending(e => e.ModelPrefix.Length)
    .ThenByDescending(e => e.EffectiveFrom is not null || e.EffectiveTo is not null)
    .FirstOrDefault();

if (match is null)
{
    logger.LogWarning("No {Provider} pricing entry for model '{Model}'; using fallback rates. Add an explicit entry to keep cost accurate.", providerName, model);
}
var rates = match ?? options.Value.FallbackPricing;
```

Longest-prefix match wins first (as today); among same-length-prefix matches
for a given date, a dated (bounded) entry beats an always-on one — this is
what makes the intro-pricing window "just another entry" instead of a special
case.

Both clients take a constructor dependency on `IOptions<TPricingOptions>`
instead of reading the `static readonly Dictionary`.

## Wiring

`Program.cs` registers both options types unconditionally (not gated behind
the `IsConfigured(...)` provider-enablement checks), so `ValidateOnStart`
fails fast on a broken pricing section even before the provider's API key is
configured:

```csharp
services.AddOptions<AnthropicPricingOptions>()
    .Bind(cfg.GetSection("Ingest:Anthropic"))
    .ValidateDataAnnotations()
    .Validate(o => o.Pricing.Count > 0, "Ingest:Anthropic:Pricing must have at least one entry")
    .ValidateOnStart();

services.AddOptions<OpenAiPricingOptions>()
    .Bind(cfg.GetSection("Ingest:OpenAi"))
    .ValidateDataAnnotations()
    .Validate(o => o.Pricing.Count > 0, "Ingest:OpenAi:Pricing must have at least one entry")
    .ValidateOnStart();
```

`appsettings.json` gets the full pricing arrays shown above (today's
dictionary values moved verbatim, including the Sonnet-5 intro window split
into two dated entries). Environment-specific `appsettings.*.json` can
override individual entries if ever needed; no such override exists today.

## Testing

Existing `ComputeCost`-exercising tests construct the client directly and
must switch to passing `Options.Create(new AnthropicPricingOptions { ... })` /
`Options.Create(new OpenAiPricingOptions { ... })` with the same rate table
values used today. The Sonnet-5 intro-window test continues to pass — it's
now exercising the dated-entry tie-break in `ComputeCost` rather than the old
`if (model.StartsWith("claude-sonnet-5") && usageDate <= ...)` branch.

No new integration/DB-touching tests needed; this is a pure config-binding +
in-memory resolution change.
