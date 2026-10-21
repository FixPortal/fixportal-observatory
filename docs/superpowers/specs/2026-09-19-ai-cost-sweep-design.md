# AI cost sweep — design

Written 2026-09-19. Status: approved, not yet implemented.

## Problem

Three coding allowances were exhausted inside one week (Moonshot, OpenAI/Codex, and Anthropic at 98%), while the questions that decide what to do about it — which subscription to cut at renewal, which lane should carry which work — are currently answered from intuition. The Observatory already records the consumption that would answer them; nothing joins that consumption to the configuration that causes it.

The binding constraint is not money. It is allowance headroom. Subscription lanes post `CostBasis.Notional`, meaning no money changed hands, so per-event "savings" on those lanes are fiction. The real money levers are the flat subscriptions at renewal and any metered lane; the real capacity lever is routing.

## What already exists, and must not be rebuilt

The Observatory runs `InsightGenerator` and `IntelligenceWorkerService` daily, producing `InsightType.Summary | Efficiency | Anomaly | Recommendation | BudgetAlert` over stored data. The relevant read surfaces are `/subscriptions`, `/spend/{vendors,entries,categories,reporting}`, `/aggregates`, `/events`, `/activity/sessions`, `/adversarial-review/{runs,stats}`, `/caveman-stats`, `/routing-snapshot` and `/sources/status`.

The gap this design fills is not analysis of stored data. It is that **the Observatory knows consumption and does not know policy**. Policy lives on disk, outside it: `reviewers.json` (the adversarial panel's seats), the model-registry `registry.json` (capability tiers), each repo's `.claude/review-policy.json` (risk tiers), `.coderabbit.yaml`, and the routing rules in `CLAUDE.md`.

The skill is therefore the exact analogue of `azure-cost-sweep`, which joins live Azure spend to the IaC that requires it. This one joins live consumption to the configuration that causes it, and every recommendation names the file that implements it.

## Section 1 — Observatory: allowance modelling

`Subscription` today carries `Provider, Name, CostAmount, Currency, BillingInterval, BillingMonth, BillingDay, ActiveFrom, ActiveTo, ExtraUsageCost`. That is enough for a renewal calendar and for cost per subscription. It models no ceiling, so headroom — the thing that actually hurt — is not derivable.

Add four nullable fields:

- `AllowanceUnit` — `NotionalUsd | Tokens | Credits | Opaque`
- `AllowanceAmount` — decimal
- `AllowancePeriod` — `Rolling5h | Weekly | CalendarMonth`
- `ResetAnchor` — the instant or day-of-period the window resets from

All four nullable, so every existing row stays valid and no backfill is required. A subscription with no allowance declared is reported as "ceiling not declared", never as unlimited.

Headroom is **computed, not ingested**. No provider here exposes a usage-quota API, so the figure is measured consumption in the current period over the declared ceiling. `NotionalUsd` is the default unit because it is the only one already recorded for every lane, which makes vendors comparable; `Tokens` exists for a plan whose published ceiling is a token count, and `Opaque` for a plan whose ceiling is real but unpublished, where the skill reports consumption trend without a percentage.

`AllowancePeriod` is heterogeneous by necessity — a rolling five-hour window and a calendar month are both real ceilings, and a plan may have more than one. Where a plan has two, model the one that binds first and note the other in `Name`.

## Section 2 — Skill report contract

Three ranked sections, mirroring `azure-cost-sweep`'s split between what is safe now and what needs work:

1. **Headroom.** Per allowance: consumption against declared ceiling, which work classes consumed it, and where a wall was hit. This leads, because it is the presenting problem.
2. **Routing.** Per work class, the cheapest lane that delivers equivalent accepted output. The evidence type is cost per accepted finding, already recorded per reviewer and model on `/adversarial-review/runs`.
3. **Money.** Value per pound per subscription, the renewal calendar derived from `BillingInterval` and `BillingDay`, and a keep / downgrade / cut recommendation naming the capability a downgrade drops.

Invariants, carried over from `azure-cost-sweep` because they are what stop a cost report from being confidently wrong:

- Never recommend a cut until the consumption evidence shows what the cut drops.
- One metrics window drives the analysis; report the actual returned timestamps.
- Separate a realised saving from a proposed one. A proposal is not banked until the subscription actually changed.
- Never present a notional figure as money. Label the basis on every number.
- An unpriced lane is reported as unpriced, never as zero.

That last invariant is not theoretical — see the Google gap below.

## Section 3 — Substitution engine, and the telemetry that blocks it

Substitution answers "if work class W moves off lane S onto vendor V, what does it cost?" by repricing measured token volumes against V's catalog. The Observatory already has `IProviderPriceCalculator` per provider and a pricing-snapshot store, so this is reuse rather than new machinery.

Worked example, measured 2026-09-19 over 30 days. The Anthropic lane consumed 1.0M input, 134.7M output and 54.4B cache-read tokens against a £150/month plan. Repriced at DeepSeek v4-pro (cache-hit $0.044/1M, cache-miss $1.32/1M, output $3.96/1M, peak): cache read $2,393, cache write billed as miss $1,346, output $533 — roughly $4,273/month peak, $2,137 off-peak. At DeepSeek flash, the weakest model they sell, roughly $794 peak and $397 off-peak. Against about $190. The cache-read term dominates and is the general result: a flat allowance does not charge for re-sent context and a meter does, so a meter cannot win on baseline. It can win only as overflow, where cost is proportional to the blocked remainder.

Three telemetry gaps constrain the engine:

1. **Google is unpriced.** Over 90 days, `google-local` recorded 2,534M `gemini-3.1-pro` input tokens at $0.00, because the lane posted `CostBasis.ListPriceEstimate` and `GooglePriceCalculator` takes its Gemini developer-API branch only on `Notional` or an explicit `service = "Gemini Developer API"` payload marker. Fixed forward in fixportal-claude PR #270. **Historical rows remain unpriced**: basis is stored per event, the repricing pass will not rebase a row the calculator refuses, and re-running the sweep cannot repair them because its arms post deltas keyed on cumulative totals — clearing the watermark would replace each final snapshot with an inflated count. A separate migration that re-posts each existing event with identical tokens under the corrected basis is the safe route, and is a prerequisite for valuing the Google lane historically.
2. **No work-class tag on events.** Adversarial review has its own table, so review is measurable. Ordinary build work carries no class, so "which work class consumed this allowance" is unanswerable outside review. Needs a tag set at ingest.
3. **No record of hitting a wall.** The event that caused this work — quota exhausted, blocked, tool switched — is recorded nowhere. The CLIs emit a rate-limit message; a hook can post a wall-hit event carrying provider and timestamp. Without it, headroom is inferred from consumption alone and the skill cannot report what it most needs to.

## Baseline, measured 2026-09-19

30-day totals by provider, notional USD at list price:

| Provider | Input | Output | Cache read | Notional USD |
|---|---|---|---|---|
| anthropic | 1.0M | 134.7M | 54.4B | 18,877 |
| openai | 922.2M | 94.9M | 40.1B | 12,555 |
| moonshot | 172.4M | 42.7M | 4.9B | 977 |
| xai | 6.7M | 0.6M | 84.8M | 29 |
| meta | 2.0M | 0.1M | 6.7M | 0.23 |
| google | 2,148.3M | 0.06M | 0 | unpriced |

Value per pound, total tokens over monthly cost:

| Provider | Tokens (30d) | Cost/month | Tokens per £ |
|---|---|---|---|
| anthropic | 54,514M | £150 | 363M |
| openai | 41,118M | £200 | 206M |
| google | 2,148M | £15.83 | 136M |
| moonshot | 5,071M | ~£157 | 32M |

Review economics, cost per accepted finding across the full recorded history:

| Seat | Accepted per run | USD per accepted |
|---|---|---|
| anthropic sonnet-5 + fable-5 | 5.0 | 0.086 |
| openai gpt-5.6-sol | 5.8 | 0.122 |
| openai gpt-5.4 | 4.4 | 0.130 |
| google gemini-2.5-pro | 3.9 | 0.138 |
| anthropic sonnet-4-6 | 18.3 | 0.292 |
| anthropic sonnet-5 | 14.0 | 0.339 |
| openai gpt-6-astra | 5.1 | 1.239 |

Two findings fall straight out and are the kind of output the skill should produce: `gpt-6-astra` costs ten times the cheapest seat per accepted finding while accepting fewer per run than `gpt-5.6-sol`; and the judge role moved from `claude-opus-4-8` at $6.94 per run to `claude-opus-5` at $0.21, a 32x reduction already realised across 59 and 41 runs respectively.

Caveat on all Anthropic and Moonshot figures: `claude-pricing` has been failing since 2026-09-01 (`"Claude '## Model pricing' pricing columns changed."`) and `kimi-pricing` since 2026-08-30. Both fall back to last-known-good, so these numbers are priced off roughly three-week-stale catalogs.

## Out of scope

Azure hosting cost of the Observatory itself belongs to `azure-cost-sweep`. The two failing pricing parsers are a separate repair. Neither is folded in here.

## Decisions taken

- Headroom leads the report; money is the second section, not the first.
- The allowance ceiling lives in the Observatory schema, not in a hand-maintained file inside the skill, so it cannot drift away from the consumption it is measured against.
- Substitution reprices against the existing per-provider calculators rather than a new pricing path.
- The skill itself is read-only and advisory, like `azure-cost-sweep`. It recommends configuration changes; it does not make them.
