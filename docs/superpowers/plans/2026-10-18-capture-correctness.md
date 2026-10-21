# Capture correctness Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Observatory capture correct for the October 2026 vendor set: seeded vendors and providers for z.AI, DeepSeek, Grok, Inworld and Firecrawl; correct Codex attribution; honest unknown cost on adversarial-review rows; visible skipped invoices; quiet dormant lanes.

**Architecture:** Additive changes at the existing seams (`docs/adding-a-provider.md`): a `Provider` enum plus seed migration, a bundled price catalog per new provider, a pure provider-attribution function in the sweep, a `CostUnknown` flag column, and one admin endpoint reusing `SourceSyncState` for the portal feed's health. Two repos: Observatory first, then `personal-tax-portal`.

**Tech Stack:** .NET 10 / EF Core / Postgres / xUnit v3 / AwesomeAssertions / NSubstitute; Node 24 sweep (`node --test`); React/Vite/Vitest frontend.

**Spec:** `docs/superpowers/specs/2026-10-18-capture-correctness-design.md`

## Global Constraints

- Public repo, private data: no invoice descriptors, account ids, amounts or keys in code, fixtures, docs or commits.
- Migrations are additive only. `infra/**` is HIGH tier: do not touch it.
- Never rewrite `Billed`, `ProviderEstimated` or `legacy` rows. Unknown models price to `null` with a warning; no generic fallback price.
- Do the work in a worktree on branch `capture-correctness` (`superpowers:using-git-worktrees`). Commit per task; push once at the end, after the full pre-push gate (Task 9). Multi-line commit messages go in a temp file and `git commit -F`.
- Provider wire names are lower-case: `zai`, `deepseek`.
- Cost-unknown is a `bool CostUnknown` column (default false). `CostUsd` stays non-nullable and is 0 when unknown.
- Test stack: xUnit v3, AwesomeAssertions `.Should()`, NSubstitute; prefer one `[Theory]` over near-duplicate `[Fact]`s.
- No emoji anywhere, including commit messages.

## Review Focus

- A Codex session with no `session_meta.model_provider` and a non-GLM, non-DeepSeek model name: must still post as OpenAI only if the model name is a recognised OpenAI id; otherwise it is skipped and counted, never filed under OpenAI by default.
- A panel run posted with `costUnknown=true` and `issuesAccepted=0`: no divide, no cost contribution, still counted as a run.
- A group of runs where every row is cost-unknown: average cost and cost-per-accepted are null, not 0.
- Re-running the sweep after the attribution fix: old OpenAI-filed GLM/DeepSeek keys are replaced, not duplicated, and Billed/ProviderEstimated rows are untouched.
- The feed-status endpoint called twice with the same payload: one `SourceSyncState` row, idempotent; called with the read-only key: 403.

---

### Task 1: Provider members and frontend registry

**Files:**
- Modify: `src/AiObservatory.Data/Entities/Provider.cs`
- Modify: `src/AiObservatory.Data/Entities/ObservationProvenance.cs`, `src/AiObservatory.Data/Pricing/PricingSnapshotCandidate.cs` (stable ids)
- Modify: `src/AiObservatory.Web/src/config/providers.ts`, `src/AiObservatory.Web/src/index.css` (provider colour tokens), `docs/provider-setup.md`
- Test: `src/AiObservatory.Web/src/config/providers.test.ts`, a Data test asserting the enum wire names

**Interfaces:**
- Produces: `Provider.Zai`, `Provider.DeepSeek` (wire `zai`, `deepseek`); `ProviderKey` union gains `'zai' | 'deepseek'`; pricing source ids `PricingSourceIds.ZaiList = "zai-pricing"` and `PricingSourceIds.DeepSeekList = "deepseek-pricing"`.

- [ ] **Step 1: Write failing tests.** Data test `Provider_serialises_to_lowercase_wire_names` (Theory over `Provider.Zai`/`"zai"`, `Provider.DeepSeek`/`"deepseek"`, `Provider.OpenAI`/`"openai"`) using the API's configured `JsonSerializerOptions`; `providers.test.ts`: `getProvider('zai')` and `getProvider('deepseek')` return configs with sources, and `PROVIDER_ORDER` contains both.
- [ ] **Step 2: Run both; expect FAIL.** `dotnet test tests/AiObservatory.Data.Tests --filter Provider_serialises` and `npm --prefix src/AiObservatory.Web test -- --run src/config/providers.test.ts`.
- [ ] **Step 3: Implement.** Add `Zai` and `DeepSeek` to `Provider`; pin `[JsonStringEnumMemberName("deepseek")]` on `DeepSeek` (camel-case would give `deepSeek`). Add `providers.ts` entries following the `moonshot` shape. Each lists only its pricing source (`zai-pricing`, `deepseek-pricing`); `codex-local` stays under OpenAI because that is the source id, and the review wrappers are not sources. Add `--provider-zai` and `--provider-deepseek` tokens next to the existing `--provider-*` tokens in both themes (take values from the design-standard app-local token guidance; keep contrast with existing providers). Add the stable pricing source ids. Add both providers to the `docs/provider-setup.md` matrix with the access note "interactive via Codex; panel via review wrappers".
- [ ] **Step 4: Run the two tests plus `dotnet build`; expect PASS** (the build also shows every exhaustive `switch` over `Provider` that now needs a case; fix those with explicit arms returning null/not-priced until Task 3).
- [ ] **Step 5: Commit** `feat(providers): add z.AI and DeepSeek providers`.

### Task 2: Link the existing vendors to their providers (data change, no migration)

**Observed 2026-10-18:** the live catalog (`GET /api/spend/vendors?includeArchived=true`) already holds `zai`, `deepseek`, `grok`, `inworld` and `firecrawl`, created through the catalog panel, all with an empty `provider`. A seed migration would collide with them, so none is written. `zai` and `deepseek` need a `Provider` link so the vendor can join to the token pipeline (billed versus estimated); `grok` links to `Xai`; `inworld` and `firecrawl` stay unlinked.

**Files:**
- Modify (only if `PatchVendorAsync` in `src/AiObservatory.Api/Endpoints/SpendCatalogEndpoints.cs` does not accept `Provider`): that endpoint and `SpendVendorRequest` (line 324)
- Test: `tests/AiObservatory.Api.Tests` catalog endpoint tests

**Interfaces:**
- Produces: `PATCH /api/spend/vendors/{id}` with `{ "provider": "zai" }` sets `SpendVendor.Provider`; an unknown provider returns 400 (existing behaviour at line 185).

- [ ] **Step 1: Read `PatchVendorAsync`.** If it already updates `Provider`, skip to Step 4.
- [ ] **Step 2: Write the failing test:** patching a vendor with `provider: "zai"` persists `Provider.Zai`; `provider: "nonsense"` returns 400; omitting `provider` leaves the existing link unchanged.
- [ ] **Step 3: Run; expect FAIL. Implement the minimal change; run; expect PASS. Commit** `feat(spend): allow linking a vendor to a provider`.
- [ ] **Step 4 (after deploy, owner-visible): link the three vendors** with three PATCH calls using the admin key, ids taken from the live listing: `zai` -> `zai`, `deepseek` -> `deepseek`, `grok` -> `xai`. Verify with the listing. No default-category changes: the live defaults are the owner's.

### Task 3: Bundled price catalogs for z.AI and DeepSeek

**Files:**
- Create: `src/AiObservatory.Data/Pricing/Catalogs/ZaiPriceCatalog.cs`, `DeepSeekPriceCatalog.cs`; `src/AiObservatory.Data/Pricing/ZaiPriceCalculator.cs`, `DeepSeekPriceCalculator.cs`; `src/AiObservatory.Data/Pricing/Bundled/zai.json`, `deepseek.json`
- Modify: `PricingSnapshotStore.cs` (the four `Provider.Meta` sites at ~381, 396, 488), `UsagePriceResolver.cs` (~178), `ServiceCollectionExtensions.cs:30` (register calculators), `BundledPricingCatalogLoader.cs:77` (load both), `PricingSnapshotCandidate.cs` (ids)
- Test: `tests/AiObservatory.Data.Tests/Pricing/` new `ZaiPriceCalculatorTests.cs`, `DeepSeekPriceCalculatorTests.cs`; extend `BundledPricingCatalogLoaderTests`

**Interfaces:**
- Produces: `ZaiPriceCalculator : IProviderPriceCalculator` (`Provider => Provider.Zai`), `DeepSeekPriceCalculator` likewise; `Calculate(UsageEvent, string normalizedCatalog) -> UsagePriceQuote?`. Both bundled-only: no `IPricingSource` fetcher and no `PricingSourceDefinition` registration, as the Gemini catalog does.

- [ ] **Step 1: Source the prices; do not invent them.** Per the global rule use the FireCrawl skills: scrape z.AI's and DeepSeek's official pricing pages to files in the scratchpad and read them. Record the retrieval date and URL in each catalog. DeepSeek publishes cache-hit and off-peak tiers: model the cache-hit input rate as `CacheRead`; if an off-peak or other dimension cannot be derived from a usage event, price at the standard rate only when the event carries no ambiguity, otherwise return null. State the rule in the calculator's doc comment.
- [ ] **Step 2: Write failing tests** modelled on `MetaPriceCalculator` behaviour: known model prices correctly (hand-computed expected cost from the catalog rates), unknown model returns null, cache-read tokens with no cache-read rate return null, notional basis prices at catalog retrieval date.
- [ ] **Step 3: Run; expect FAIL.**
- [ ] **Step 4: Implement** the catalogs (record + `Validate()` + exact-id `Resolve`, copying the `MetaPriceCatalog` structure), calculators, registrations and bundled JSON. Models to include are those in the owner's Codex configs and review wrappers: `glm-5.3`, `glm-5.3-flash`, `deepseek-flash` and whatever the DeepSeek wrapper resolves for the frontier seat; confirm ids against `~/.agents/skills/model-registry` output.
- [ ] **Step 5: Run `dotnet test tests/AiObservatory.Data.Tests tests/AiObservatory.Ingest.Tests`; expect PASS.**
- [ ] **Step 6: Commit** `feat(pricing): bundled z.AI and DeepSeek catalogs`.

### Task 4: Codex provider attribution

**Files:**
- Modify: `clients/observatory-sweep.mjs` (`parseCodex` ~119, `sourceMetadata` ~502, `PARSE_CACHE_VERSION` 4 -> 5)
- Test: `clients/observatory-sweep.test.mjs`

**Interfaces:**
- Produces: `export function codexProvider(modelProvider, model) -> 'Zai' | 'DeepSeek' | 'OpenAI' | null`; `parseCodex` result gains `modelProvider: string | null` read from the `session_meta` row's `payload.model_provider`; `sourceMetadata('codex', model, machine, modelProvider)` uses `codexProvider` and returns `null` when it returns `null`.

- [ ] **Step 1: Confirm the key path** by reading one real `session_meta` line from a GLM session under `~/.codex/sessions/2026/10/18` (the first line of any rollout). Expect `payload.model_provider` (`ZAI` for GLM, `deepseek` for DeepSeek). If the path differs, use the real one throughout.
- [ ] **Step 2: Write failing tests.** `codexProvider` Theory: (`'ZAI'`,`'glm-5.3-flash'`) -> `'Zai'`; (`'deepseek'`,`'deepseek-flash'`) -> `'DeepSeek'`; (`null`,`'glm-5.3'`) -> `'Zai'` by prefix; (`null`,`'deepseek-flash'`) -> `'DeepSeek'`; (`'openai'`,`'gpt-6-sol'`) -> `'OpenAI'`; (`null`,`'gpt-5.4'`) -> `'OpenAI'`; (`null`,`'mystery-1'`) -> `null`. A `parseCodex` test with a fixture carrying a `session_meta` row asserts `modelProvider`. A `buildDailySnapshots` test asserts a GLM codex record posts `provider: 'Zai'` and an unknown one is absent.
- [ ] **Step 3: Run `node --test clients/observatory-sweep.test.mjs`; expect the new tests FAIL.**
- [ ] **Step 4: Implement.** Provider id match is case-insensitive; prefix list `glm-` -> Zai, `deepseek` -> DeepSeek, `gpt-`, `o1`, `o3`, `o4`, `codex` -> OpenAI. Change `case 'codex'` in `sourceMetadata` to use it. Keep `sourceId` `codex-local@<host>` and `runtime: 'codex'`. Interactive GLM posts `costBasis: 'notional'`; interactive DeepSeek posts `costBasis: 'listPriceEstimate'`; OpenAI keeps its current default.
- [ ] **Step 5: Run the sweep tests; expect PASS.**
- [ ] **Step 6: Commit** `fix(sweep): attribute Codex sessions by model provider`.

### Task 5: Unplaceable sessions and dormant lanes

**Files:**
- Modify: `clients/observatory-sweep.mjs` (`ALL_LOCAL_SOURCES`, `parseLocalSources` ~759, end-of-run summary), `clients/README.md`
- Test: `clients/observatory-sweep.test.mjs`

**Interfaces:**
- Produces: `export const DEFAULT_LOCAL_SOURCES = ['codex','copilot','claude','gemini','antigravity']`; `parseLocalSources(undefined)` returns that set; explicit values still accept every name in `ALL_LOCAL_SOURCES` (including `kimi`, `grok`, `pi`). `buildDailySnapshots` returns records it dropped for no provider via a second exported function `countUnplaced(records, machine) -> { tool, model, count }[]`.

- [ ] **Step 1: Write failing tests:** `parseLocalSources(undefined)` equals the default set and excludes `kimi`/`grok`/`pi`; `parseLocalSources('kimi')` still works; `countUnplaced` returns the codex `mystery-1` record with its tool, model and count.
- [ ] **Step 2: Run; expect FAIL.**
- [ ] **Step 3: Implement.** Print the unplaced list in the run summary as a warning line (`UNPLACED codex model=mystery-1 sessions=N`); the process exit code stays 0. Add a README paragraph: dormant lanes are re-enabled with `OBSERVATORY_LOCAL_SOURCES`, and the machine-local `sweep.env` is where the deployed value lives.
- [ ] **Step 4: Run the sweep tests; expect PASS.**
- [ ] **Step 5: Commit** `feat(sweep): default dormant vendor lanes off and report unplaced sessions`.

### Task 6: Honest unknown cost on adversarial-review rows

**Files:**
- Modify: `src/AiObservatory.Data/Entities/AdversarialReviewRun.cs`, `src/AiObservatory.Api/Services/AdversarialReviewService.cs` (request record line 178, the run construction ~146-165, `ModelAliases`), `src/AiObservatory.Data/Repositories/AdversarialReviewRepository.cs` (`UpdateMetricsAsync` ~56, `GetStatsAsync` ~88), `IAdversarialReviewRepository.cs` (`AdversarialReviewStats` ~16), `AiObservatoryDbContext.cs` (column default)
- Create: migration `<timestamp>_AddAdversarialReviewCostUnknown` (adds `CostUnknown boolean not null default false`, then `UPDATE "AdversarialReviewRuns" SET "CostUnknown" = true WHERE "Reviewer" = 'zai'`)
- Test: `tests/AiObservatory.Api.Tests/Services/AdversarialReviewServiceTests.cs`, `tests/AiObservatory.Data.Tests/Repositories/AdversarialReviewRepositoryTests.cs`

**Interfaces:**
- Produces: `AdversarialReviewRunRequest` gains `bool CostUnknown = false` (last positional, defaulted); `AdversarialReviewRun.CostUnknown`; `AdversarialReviewStats` gains `int UnknownCostRuns` and its `AvgCostPerRun` / `AvgCostPerAcceptedFinding` are computed only over rows with `CostUnknown == false`, and are null when no such row exists.

- [ ] **Step 1: Write failing tests.** Service: a request with `CostUnknown = true`, `CostUsd = 0`, `IssuesAccepted = 3` stores `CostUnknown = true` and `CostPerAcceptedFinding = null`. Repository: group of three runs (two known at cost 1.0 and 3.0 with accepted 1 and 1, one unknown with accepted 5) yields `AvgCost == 2.0`, `AvgCostPerAccepted == 2.0`, `UnknownCostRuns == 1`; a group where all runs are unknown yields null averages and `UnknownCostRuns == run count`. `UpdateMetrics` overwrites `CostUnknown` on an existing participant row.
- [ ] **Step 2: Run; expect FAIL.** `dotnet test tests/AiObservatory.Api.Tests --filter AdversarialReviewService` and the Data repository tests.
- [ ] **Step 3: Implement** the flag through request, entity, `UpdateMetricsAsync` and stats as specified. For the stale `ModelAliases`: first query stored `Model` values (`SELECT DISTINCT "Model" FROM "AdversarialReviewRuns"` against a dev database, or read the integration fixtures); if no stored or fixture value is a bare `opus`/`sonnet`/`haiku`, delete the map and `NormalizeModel`; otherwise refresh the targets to the current registry ids.
- [ ] **Step 4: Run both test projects; expect PASS.**
- [ ] **Step 5: Commit** `fix(review): record unknown panel cost as unknown, not zero`.

### Task 7: Correct historical misfiled Codex rows

**Files:**
- Modify: `clients/observatory-sweep.mjs` (replacement path), or create a one-off script `clients/fix-codex-provider.mjs`
- Test: `clients/observatory-sweep.test.mjs`

**Interfaces:**
- Consumes: Task 4 attribution. Produces: after one sweep run, `codex-local@<host>` snapshots for GLM/DeepSeek models exist under their new provider and none remain under OpenAI.

- [ ] **Step 1: Observe how a provider change interacts with replacement.** The event identity is `SourceId + EventKey` (`docs/truth-and-pricing.md`), where the event key is `codex:<date>:<model>` and carries no provider. Write a failing test in the sweep tests that models the server inventory holding an `OpenAI` snapshot for key `codex:2026-10-17:glm-5.3-flash` and asserts `planSnapshotSubmissions` submits the corrected snapshot as an update, not a duplicate and not a no-op.
- [ ] **Step 2: Run; expect either PASS (the existing replace path already covers it; record that in the commit message and stop here) or FAIL.**
- [ ] **Step 3: If it fails, implement** the smallest change in `planSnapshotSubmissions` so a differing `provider` on an existing key counts as a changed snapshot. Server side, confirm by reading `UsageRepository`'s snapshot replacement (`~210-227`) that a provider change replaces the prior aggregate contribution in the right provider's `DailyAggregates` row; if it does not, add a repository test and the fix, limited to events whose `CostBasis` is `Notional` or `ListPriceEstimate`.
- [ ] **Step 4: Run sweep tests and `dotnet test tests/AiObservatory.Data.Tests --filter Usage`; expect PASS.**
- [ ] **Step 5: Commit** `fix(ingest): replace misfiled Codex snapshots when the provider changes`.

### Task 8: Feed health endpoint and portal reporting

**Files (Observatory):**
- Create: `src/AiObservatory.Api/Endpoints/SpendFeedStatusEndpoints.cs`; modify the endpoint registration in `Program.cs`
- Test: `tests/AiObservatory.Api.Tests/` endpoint test and an integration test in `tests/AiObservatory.Api.IntegrationTests/`

**Files (personal-tax-portal, separate branch and PR, after the Observatory side is deployed):**
- Modify: `src/TaxPortal.Api/Services/SpendFeedService.cs`, `SpendFeedMapper.cs`; tests in `tests/TaxPortal.Api.Tests/SpendFeedServiceTests.cs`

**Interfaces:**
- Produces: `POST /api/spend/feed-status` (admin key only, `AdminOnlyApiKeyEndpointFilter`) with body `{ "attempted": int, "posted": int, "skipped": [ { "expenseId": long, "reason": "no-vendor" | "no-category" | "aged-out" | "foreign-unreviewed" } ] }`. It upserts one `SourceSyncState` with `SourceId = "portal-spend-feed"`: `LastAttemptAt` now, `LastSuccessAt` now when `skipped` is empty, `LastError = "<n> expenses skipped: <reason counts>"` otherwise, `IsConfigured = true`. It returns 204. It stores ids and reason codes only.

- [ ] **Step 1: Write failing Observatory tests.** Admin key with two skipped rows creates the `portal-spend-feed` state with the counted `LastError`; the same payload twice leaves one row; an empty `skipped` clears `LastError` and sets `LastSuccessAt`; the read-only key returns 403; a body with an unknown `reason` returns 400.
- [ ] **Step 2: Run; expect FAIL.**
- [ ] **Step 3: Implement** the endpoint with validation of `reason` against the four literals. Reuse the existing `SourceSyncState` fields only; no new table or migration. Confirm the frontend `getSource` fallback renders `portal-spend-feed` readably; add it to `NON_PROVIDER_SOURCES` in `providers.ts` with display name "Portal spend feed".
- [ ] **Step 4: Run Observatory tests; expect PASS. Commit** `feat(spend): portal feed health endpoint`.
- [ ] **Step 5 (portal repo, own worktree and branch off its `main`): write failing tests** in `SpendFeedServiceTests` that a run with one unmatched-vendor expense, one uncategorised AI-family expense and one past-window eligible expense posts the three reason codes with their `Expense.Id`s and that a 404 from the endpoint is logged at Warning and does not fail the run. Implement by collecting skips where the mapper already logs them (`SpendFeedMapper.cs:121-141`) and POSTing once at the end of `RunOnceAsync`. No descriptor, vendor text, amount or comment may appear in the payload; add a test asserting the serialized body contains only the documented keys.
- [ ] **Step 6: Run the portal's own full gate (its CI order) and commit** `feat(spend-feed): report skipped expenses to the observatory`.

### Task 9: Gate and documentation

**Files:**
- Modify: `docs/provider-setup.md`, `docs/truth-and-pricing.md` (pricing section lists the two bundled catalogs), `clients/README.md`

- [ ] **Step 1: Documentation.** Update the three docs to match Tasks 1-8 (providers, bundled catalogs, dormant lanes, unplaced-session warning, `portal-spend-feed` source, `CostUnknown` meaning).
- [ ] **Step 2: Run the full .NET gate in CI order:** `dotnet tool restore`; `dotnet csharpier check .`; `dotnet restore <solution>.slnx`; `dotnet build <solution>.slnx --configuration Release --no-restore`; `dotnet test` with the repo's own invocation copied from `.github/workflows/ci.yml`. Expect all green; a CSharpier line-ending-only complaint is checked with `git diff --numstat` after formatting.
- [ ] **Step 3: Frontend gate:** in `src/AiObservatory.Web`, `npx tsc -b --noEmit`, `npx eslint .`, `npx vitest run`, and `npx vite build` (an SSR-rendered component is not touched, but the build is cheap). Also `node --test clients/` and the three root `*.test.mjs`.
- [ ] **Step 4: AI-smell scan:** `pwsh -File .github/ai-smells/pr-detect.ps1 -RepoPath . -Base origin/main -Head HEAD -OutFile <scratch>.json`; fix or justify every hit.
- [ ] **Step 5: Whole-branch review on a frontier model**, then fix findings in one commit, then a single push and PR (`gh pr create --repo FixPortal/fixportal-observatory`, after writing the `finishing-gate` sentinel).
- [ ] **Step 6: Post-deploy verification** (owner-visible): the feed logs no `does not exist in the observatory catalog` error for the five slugs; `GET /api/sources/status`-equivalent (`/sources/status`) shows `portal-spend-feed`; one sweep run leaves no GLM/DeepSeek snapshot under OpenAI.
