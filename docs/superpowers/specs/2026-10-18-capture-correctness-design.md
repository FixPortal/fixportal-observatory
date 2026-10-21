---
title: Capture correctness for the October 2026 vendor set - design
date: 2026-10-18
status: draft - awaiting owner approval
tags: [design, ingest, spend, adversarial-review, vendors]
---

# Capture correctness for the October 2026 vendor set

> Piece 1 of 3. Piece 2 (glance metrics and drill-through) and piece 3 (in-app
> documentation) get their own designs and are written after this one ships,
> because both describe the data this piece makes true.

## Intent

The owner has dropped every Max-tier subscription and the wider FixPortal estate
except the Learning Portal. The vendor set is now Anthropic (Pro), OpenAI
(ChatGPT Pro/Codex), Google (Antigravity plan), GitHub Copilot, z.AI GLM
(Coding Plan, Lite) and DeepSeek (pay-as-you-go API credit). Kimi and Grok are
cancelled; Meta Muse was never used. The Observatory was built for the old set.

The owner will use the Observatory for three questions: how much headroom is
left on each subscription, what each pay-as-you-go vendor costs, and which model
did the work. Subscription and extra-usage invoices are saved to disk and picked
up by `personal-tax-portal`; the owner must be able to trust that anything the
portal picks up reaches the Observatory. The adversarial review panel's
per-seat figures must be accurate enough to judge whether a model earns its
place.

This piece makes **capture** correct. It does not redesign the dashboard.

## Goals

1. A GLM or DeepSeek invoice confirmed in the portal reaches the ledger under the
   right vendor, and an invoice the feed cannot place is visible rather than
   silently logged.
2. Interactive GLM and DeepSeek usage through Codex is attributed to z.AI and
   DeepSeek, not OpenAI.
3. Adversarial review rows record unknown cost as unknown, never as zero.
4. Dropped vendors stop producing noise but keep their history.

## Non-goals

- Dashboard cards, panel/seat views, drill-through, freshness indicators
  (piece 2).
- In-app documentation (piece 3).
- Deleting any historical Kimi, xAI, or Meta data.
- A quota-share cost model for subscription seats (see "Left to the builder").

## Anti-goals

- A fix that makes GLM or DeepSeek look cheap or free by omission.
- A mapping that guesses a vendor. The portal feed's rule stands: an unmatched
  expense is skipped, never guessed.
- Any change that rewrites `Billed`, `ProviderEstimated` or historical rows.

## What was observed

Each item below was read from code or data on 2026-10-18.

| Finding | Evidence |
| --- | --- |
| Portal feed is built and idempotent: 6-hour poll, 90-day window, key `expense:{Id}`, scope = category with parent `AI`, vendor from `VendorMap` | `personal-tax-portal` `SpendFeedService.cs`, `ObservatoryOptions.cs` |
| An unmatched vendor is skipped and logged at Information only | spec `2026-07-27-tax-portal-spend-feed-design.md` section 5, section 8 |
| An expense confirmed more than `WindowDays` after its `InvoiceDate` never posts and gives no signal | `ObservatoryOptions.WindowDays` doc comment |
| Portal `VendorMap` (re-read 2026-10-18 after the portal fix, commit `c5903a0`) now maps the z.AI legal-entity descriptor to `zai`, `DEEPSEEK` to `deepseek`, `GROK`/`XAI` to `grok`, `INWORLD` to `inworld`, `FIRECRAWL` to `firecrawl`, and adds the Moonshot legal-entity descriptor to `moonshot` | `personal-tax-portal` `appsettings.json` lines 20-36 |
| **Corrected the same day.** The live catalog already holds vendors `zai`, `deepseek`, `grok`, `inworld` and `firecrawl` (created through the catalog panel, so absent from migrations), each with no provider link. The earlier reading "no vendor rows, feed skips everything" came from grepping source only and was wrong. There is still no `Provider` member for `zai` or `deepseek` | `GET /api/spend/vendors?includeArchived=true` on the live app, 2026-10-18; `Provider.cs` |
| The sweep assigns provider `OpenAI` to every Codex session | `clients/observatory-sweep.mjs:504` |
| 8 Codex session files since 2026-10-17 contain `glm`/`deepseek` model names (65 occurrences); `session_meta` carries `model_provider` | grep of `~/.codex/sessions` |
| GLM review wrapper posts `costUsd=0.0, costUnknown=true`; the emitter forwards `costUnknown` | `glm-review.ps1:104`, `emit-review-telemetry.ps1:168` |
| `AdversarialReviewRunRequest` has no `CostUnknown`; `CostPerAcceptedFinding` is computed from the posted cost | `AdversarialReviewService.cs:178-193`, `:146` |
| The review skill reaches GLM through `claude.exe` at `api.z.ai`, and DeepSeek through one HTTPS call | `glm-review.ps1:37-60`, `deepseek-review.ps1:126` |
| Copilot lane already infers provider from model-name prefix | `observatory-sweep.mjs:505-513` |

## Design

### A. Vendors and providers

Add `Zai` and `DeepSeek` to `Provider`, following `docs/adding-a-provider.md`:
enum member, stable IDs, `providers.ts` entry with sources, CSS tokens for the
two identities, and a provider-setup matrix row. Persistence is string-backed,
so no migration is needed for the enum. Seed spend vendors `zai` and `deepseek`
through a migration in the style of `SeedCopilotVendor`, default category
`subscription` for `zai` and `credits` for `deepseek`. Wire names are lower-case
(`zai`, `deepseek`); pin them with `JsonStringEnumMemberName` if the global
camel-case converter would diverge, as `OpenAI` did.

The portal `VendorMap` already carries the new descriptors, so no portal map
change is needed. The spend vendors it points at (`zai`, `deepseek`, `grok`,
`inworld`, `firecrawl`) already exist in the live catalog, so nothing is seeded.
What is missing is the provider link: `zai` and `deepseek` link to the new
providers, `grok` to `Xai`, and `inworld` and `firecrawl` stay unlinked. The link
is set by PATCH through the catalog API (extended if it cannot yet set a
provider), not by migration.

### B. Codex attribution

Replace the hardcoded `provider: 'OpenAI'` for `codex` with attribution from the
session itself. Order of evidence: `session_meta.model_provider`, then the
model-name prefix (`glm-` to `Zai`, `deepseek` to `DeepSeek`, otherwise
`OpenAI`). An unrecognised provider falls to `OpenAI` only when the model name
also looks like an OpenAI id; otherwise the row posts under an explicit
`Unknown` provider rather than being filed under OpenAI by default. The source
id stays `codex-local@<host>` and `runtime` stays `codex`, so the route remains
visible in the source and the model remains visible per row.

Cost basis for interactive GLM is `notional` (flat Coding Plan fee, nothing
billed per token). Cost basis for interactive DeepSeek is `listPriceEstimate`
until a billed source exists, because it is pay-as-you-go and the sweep cannot
see an invoice. Both require price catalogs; see C.

Existing rows already filed under OpenAI for GLM or DeepSeek models must be
corrected. The sweep's existing tombstone-and-replace behaviour (source-scoped
keys, snapshot replacement) is the mechanism; the builder confirms it applies to
a provider change and writes a one-off correction if it does not.

### C. Pricing

Prices resolve per provider through `IProviderPriceCalculator` and the pricing
snapshot store. Add a z.AI catalog (list price, for notional rating) and a
DeepSeek catalog. Unknown models resolve to null with a warning, per the
existing rule; nothing falls back to a generic price. DeepSeek's published
off-peak and cache-hit tiers are dimensions the catalog must model or explicitly
decline to rate. If a source page cannot be parsed strictly, ship the bundled
cold-start catalog only and mark the source status accordingly, as the Gemini
catalog does.

### D. Adversarial review cost honesty

Add `CostUnknown` (bool, default false) to `AdversarialReviewRunRequest` and to
`AdversarialReviewRun` (additive migration, existing rows default false).
When `CostUnknown` is true, store `CostUsd` as null-equivalent for aggregation
and set `CostPerAcceptedFinding` to null. Aggregations over runs must exclude
unknown-cost rows from cost sums and report the count of excluded rows.
Existing GLM rows (cost 0 and no flag) are backfilled to `CostUnknown = true`
by reviewer slug `zai`, because that wrapper has never produced a cost.
Remove or refresh the stale `ModelAliases` map (resolved registry ids are what
the skill records); the builder confirms by querying stored `Model` values
before deleting it.

### E. Visibility of skipped invoices

The portal feed counts rows skipped for unmatched vendor, for a category absent
from `CategoryMap`, and for aged-out eligibility, and posts the counts and the
affected `Expense.Id` values to a new admin endpoint, or writes them in a form
`GET /api/sources` already surfaces. The feed's own health becomes a source row,
so a silent drop becomes a visible state. The builder picks the lighter of the
two mechanisms. No descriptor text leaves the portal (privacy boundary, spend
spec section 9): only ids and reason codes.

### F. Dormant lanes

Kimi, Grok and PI/Meta lanes stay in the sweep but are off by default
(`OBSERVATORY_LOCAL_SOURCES` default excludes them); their providers, pricing
sources and history remain. Pricing sources for xAI, Meta and Kimi stop being
fetched daily, and source status must show them as dormant rather than failing
or stale. Re-enabling is a config change.

## Constraints

- Public repo, private data: no invoice descriptors, account identifiers,
  amounts or keys in code, fixtures, docs or commits.
- House pre-push gate applies: `dotnet csharpier check`, build, tests, the
  frontend lint/type/test set, and the AI-smell scan. One push per branch.
- `infra/**` is HIGH tier; this piece should not touch it.
- Migrations are additive only.

## Verification

The double-count guard stays the most important test. New checks:

- A Codex session fixture with a GLM model and one with a DeepSeek model
  posts under the right provider; an OpenAI fixture is unchanged; an unknown
  provider posts as `Unknown`.
- A panel run posted with `costUnknown=true` yields null cost-per-finding and is
  excluded from cost sums with the excluded count reported.
- A portal-feed run with one unmatched-vendor expense produces a visible
  skipped count and the expense id.
- Re-running the sweep after the provider fix does not duplicate or leave the
  old OpenAI-filed rows.

## Left to the builder

Descriptor strings for the `VendorMap`; the exact mechanism in E; whether the
sweep's tombstone path covers a provider change; DeepSeek tier modelling; the
CSS token values for the two new identities (take them from the design
standard's app-local token guidance).

## Calls made after the builder check (author's, not the owner's)

- **Two repos, two PRs, Observatory first.** A (`VendorMap`) and E (skip counts)
  edit `personal-tax-portal`; everything else is this repo. The Observatory
  endpoint contract for E ships and deploys before the portal posts to it, and
  the portal tolerates a 404 from it. The pre-push gate and one-push-per-branch
  rule apply per repo.
- **GLM through Claude Code is not a second attribution bug.** `glm-review.ps1`
  sets `CLAUDE_CONFIG_DIR` to its own isolated profile, so its transcripts do not
  land under `~/.claude/projects`, where the Claude lane reads. Interactive
  Claude sessions are not pointed at GLM. The Copilot lane is out of scope.
- **Unknown provider is not an enum member.** The sweep does not invent a
  provider identity: a Codex session whose provider and model it cannot place is
  not posted, is counted in the sweep's end-of-run summary, and is reported as a
  source-status warning. Nothing is filed under OpenAI by default.
- **Unknown cost is a flag, not a nullable column.** `AdversarialReviewRun.CostUsd`
  is a non-nullable `decimal` today. Add a `CostUnknown` boolean column (default
  false); keep `CostUsd` at 0 when unknown; every aggregation and
  `CostPerAcceptedFinding` filters on the flag. The backfill sets the flag where
  `Reviewer = 'zai'`. The DeepSeek wrapper posts a real estimate
  (`costEstimated=true`), so it is not backfilled.
- **Re-provider in place is allowed for derived rows.** Old Codex rows for GLM
  and DeepSeek models filed under OpenAI carry `notional` or `listPriceEstimate`
  cost, which is repriced by design. They are corrected in place by provider.
  `Billed`, `ProviderEstimated` and `legacy` rows are never touched.
- **Dormant lanes are local config.** The sweep's source list is set by
  `OBSERVATORY_LOCAL_SOURCES` in the machine-local `sweep.env`, not by `infra/**`.
  Dormant reuses an existing source-status state if one fits; otherwise the
  builder adds the minimum.
- **Interactive DeepSeek stays `listPriceEstimate`.** The balance-endpoint check
  is a later improvement, not a precondition.
- **The portal's `CategoryMap` already holds the five `AI - *` categories.** The
  portal `VendorMap` has no z.AI or DeepSeek entry and its `GITHUB` entry maps to
  `copilot`. What is not known is how the owner will categorise the new invoices.

## Open items needing the owner

- UNVERIFIED: that GLM Coding Plan and DeepSeek invoices, once confirmed in the
  portal, receive an `AI - *` child category. Scope in the feed comes only from
  the category the owner sets at confirmation. Refuted if a confirmed z.AI or
  DeepSeek expense in the portal sits outside the `AI` family.
- Ingest path as read from `InboxWatcherService`: a file dropped in the inbox is
  picked up by a file watcher plus a 5-second sweep and queued for OCR within
  seconds. It reaches the Observatory only after the owner confirms it as an
  expense, then at the next poll (6 hours). The gate is confirmation and
  category, not file pickup.
- Whether interactive DeepSeek should stay `listPriceEstimate` or wait for a
  billed export (DeepSeek exposes a balance endpoint; whether it gives a usable
  per-day figure has not been checked).
