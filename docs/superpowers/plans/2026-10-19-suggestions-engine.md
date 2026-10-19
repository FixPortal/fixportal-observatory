# Suggestions Engine Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Evaluate fixed, versioned rules over panel-seat and subscription data daily and surface open suggestions, with their evidence, on the Overview.

**Architecture:** Pure rule functions over an immutable `SuggestionInputs` snapshot; one service loads inputs, runs the rules and applies the suggestion lifecycle under a Postgres advisory lock; a dedicated worker runs it daily and `POST /api/suggestions/refresh` runs it on demand; the web app reads `GET /api/suggestions`.

**Tech Stack:** .NET 10 / EF Core / Postgres, NodaTime, xUnit v3 + AwesomeAssertions, React/Vite/vitest.

**Spec:** `docs/superpowers/specs/2026-10-19-suggestions-engine-design.md` (the "Rulings" section binds)

## Global Constraints

- Rule ids, exact: `seat.no-unique-catch`, `seat.high-false-positive`, `seat.confirmation-only`, `seat.cost-outlier`, `plan.costs-more-than-use`, `plan.unused`. Severity: `info` for `seat.confirmation-only`, `attention` for the rest. Status: `open | dismissed | snoozed | resolved`. Key: `<ruleId>:<subject>`.
- Defaults bind from `Suggestions:*` configuration and are validated at startup: SeatWindowDays 60, MinRuns 5, MinSettledFindings 10, FalsePositiveThreshold 0.5, MinAcceptedForConfirmationOnly 10, CostOutlierMultiple 3, CostOutlierMinPeers 3 (seat groups with at least 3 runs), PlanUsageRatio 0.5, PlanMinKnownCostRequests 10, PlanUnusedGraceDays 14, PlanUnusedWindowDays 30, ReopenChangeFraction 0.25.
- Time through `IClock` only. NodaTime for dates; BCL types at the EF boundary. The window ends on the last complete UTC day.
- Evidence is `{ window: {from, to}, sampleSize, figures: [{label, value, unit}], runIds: [..<=20] }`. No finding text anywhere.
- No emoji. One push per branch at the end after the full local gate. Rebase-merge only. `gh pr create --repo FixPortal/fixportal-observatory`.
- .NET gate order: `dotnet tool restore`, `dotnet csharpier check .` (read-only), restore, build Release, test Release (start only the db). Web gate: `npm run format`, `npx tsc -b --noEmit`, `npm run lint`, `npx vitest run`.
- A delegate never reviews its own diff. Claude reviews every delegated diff before its PR.
- Reuse, do not duplicate: `FxRateProvider` (injectable concrete class, `GetUsdToGbpAsync`), `IUsageRepository.GetAggregatesAsync(from, to)` and `GetActiveSubscriptionsAsync(today)`, `IAdversarialReviewRepository.GetRunsAsync()` and `GetFindingsAsync()`. The USD to GBP seam is resolved: `InsightGenerator` already receives `FxRateProvider` by constructor injection.

## Review Focus

- A seat rule never fires from counts-only runs; with no findings every seat rule is `notEvaluated` with a reason.
- A dismissed suggestion does not reopen on a day-to-day wobble below 25%.
- A plan mid-period (under half elapsed) is `notEvaluated`, never compared against the full price.
- A subscription in a currency other than GBP or USD is `notEvaluated`, not mis-converted.
- Two concurrent evaluations (daily run plus refresh) produce one consistent set and no 500.
- The C# seat metrics and the TypeScript seat metrics agree on the shared fixture.

---

### Task 1: Contract fixtures and TS parity test (Claude)

**Files:**
- Create: `docs/superpowers/contracts/suggestions/get-response.json`, `docs/superpowers/contracts/seat-metrics/input.json`, `docs/superpowers/contracts/seat-metrics/expected.json`
- Modify: `src/AiObservatory.Web/src/components/seatInfluence.test.ts` (add one parity test)

**Interfaces:**
- Produces: `get-response.json` is `{ suggestions: [...], notEvaluated: [{ruleId, reason}] }`; each suggestion `{ id, key, ruleId, subject, severity, title, status, snoozedUntil, firstSeenAt, lastSeenAt, evidence: {...} }` with at least one seat suggestion, one plan suggestion and two notEvaluated entries. `seat-metrics/input.json` is `{ runs: RunRow[], findings: FindingRow[] }` covering a mixed `a+b` model row, an unknown-cost run, a counts-only run, and a unique plus a corroborated finding; `expected.json` is the `SeatMetrics[]` for pivot `unique` (the fields of `SeatMetrics` in `seatInfluence.ts`, `byWeek` included).

- [ ] **Step 1:** Write the three fixtures by running the existing TS `computeSeatMetrics` over `input.json` once (a throwaway node/vitest call) and saving its output as `expected.json`; check the numbers by hand for the mixed, unknown-cost and counts-only cases.
- [ ] **Step 2:** Add `test('matches the shared seat-metrics fixture', ...)` that loads both JSON files and asserts `computeSeatMetrics(input.runs, input.findings, 'unique')` equals `expected.json`. Run it; expect PASS.
- [ ] **Step 3:** Commit `docs: suggestions and seat-metrics contract fixtures`.

### Task 2: Pure rules, calculator and billing period (Claude)

**Files:**
- Create under `src/AiObservatory.Api/Services/Suggestions/`: `SuggestionOptions.cs`, `SuggestionInputs.cs`, `SuggestionResult.cs`, `SeatMetricsCalculator.cs`, `BillingPeriod.cs`, `ISuggestionRule.cs`, `SeatRules.cs`, `PlanRules.cs`
- Test: `tests/AiObservatory.Api.Tests/Suggestions/SeatMetricsCalculatorTests.cs`, `SeatRulesTests.cs`, `PlanRulesTests.cs`, `BillingPeriodTests.cs`

**Interfaces:**
- Produces:
  - `record SuggestionInputs(LocalDate WindowStart, LocalDate WindowEnd, IReadOnlyList<AdversarialReviewRun> Runs, IReadOnlyList<AdversarialReviewFinding> Findings, IReadOnlyList<Subscription> Subscriptions, IReadOnlyList<DailyAggregate> Aggregates, decimal UsdToGbp, LocalDate Today)`
  - `record SuggestionCandidate(string RuleId, string Subject, string Severity, string Title, SuggestionEvidence Evidence, string PrimaryFigureLabel)` and `record RuleOutcome(string RuleId, IReadOnlyList<SuggestionCandidate> Candidates, bool Evaluated, string? Reason, int SubjectsEvaluated)`
  - `interface ISuggestionRule { string RuleId { get; } RuleOutcome Evaluate(SuggestionInputs inputs, SuggestionOptions options); }`
  - `static class SeatMetricsCalculator { IReadOnlyList<SeatMetrics> Compute(IReadOnlyList<AdversarialReviewRun> runs, IReadOnlyList<AdversarialReviewFinding> findings); }` where `SeatMetrics` mirrors the TS fields (key, label, mixed, runs, raised, accepted, unique, corroborated, peerAgreement, refuted, dropped, indeterminate, falsePositiveRate, acceptanceRate, knownCostUsd, unknownCostRuns, costPerUnique, countsOnlyRuns, smallSample, byWeek).
  - `static class BillingPeriod { LocalDate CurrentStart(int billingDay, LocalDate today, SubscriptionBillingInterval interval, int? billingMonth); }` (month-end clamp, same results as the web `currentBillingPeriodStart`).

- [ ] **Step 1: Write failing tests.**
  - `SeatMetricsCalculatorTests`: one `[Fact]` loads `docs/superpowers/contracts/seat-metrics/input.json` and `expected.json` (walk up from `AppContext.BaseDirectory` as the WAF findings tests do) and asserts the C# output equals `expected.json` field for field with a numeric tolerance of 1e-9.
  - `BillingPeriodTests`: a `[Theory]` with the web test's cases (billing day 31 in February, billing day 29 in a non-leap February, annual before and after the renewal, January wrap).
  - `SeatRulesTests`: one `[Fact]` per rule for fires, below-floor-emits-nothing, and no-findings gives `Evaluated=false` with a reason; cost-outlier uses a peer set of exactly 3 and 2.
  - `PlanRulesTests`: fires; under half the period elapsed gives not evaluated; two subscriptions on one provider sum; a EUR subscription gives not evaluated; `plan.unused` respects the grace days and counts requests of any basis.
- [ ] **Step 2:** Run `dotnet test --filter FullyQualifiedName~Suggestions`; expect FAIL (types missing).
- [ ] **Step 3: Implement** per the signatures above. Rules read `SuggestionOptions`; no static now; `SeatMetricsCalculator` keys by the run row's model string joined on `(runId, reviewer)`, reviewer rows only, as `seatInfluence.ts` does. Titles are fixed templates filled from the figures (for example "Seat {label} added no unique accepted catch in {n} runs").
- [ ] **Step 4:** Run the filter again; expect PASS. Run the full gate. Commit `feat: suggestion rules and seat metrics calculator`.

### Task 3: Persistence, lifecycle and service (Claude)

**Files:**
- Create: `src/AiObservatory.Data/Entities/Suggestion.cs`, `SuggestionRuleRun.cs`, migration `AddSuggestions`, `src/AiObservatory.Data/Repositories/ISuggestionRepository.cs`, `SuggestionRepository.cs`, `src/AiObservatory.Api/Services/Suggestions/SuggestionService.cs`, `SuggestionInputsLoader.cs`
- Modify: `AiObservatoryDbContext.cs`, `ServiceCollectionExtensions.cs` (repository), `src/AiObservatory.Api/Program.cs` (DI)
- Test: `tests/AiObservatory.Data.Tests/Repositories/SuggestionRepositoryTests.cs`, `tests/AiObservatory.Api.IntegrationTests/SuggestionLifecycleTests.cs`

**Interfaces:**
- Consumes: Task 2 types.
- Produces: `Suggestion` and `SuggestionRuleRun` entities per the spec's storage section (`BaselineFiguresJson` jsonb; unique index on `Suggestion.Key`; `SuggestionRuleRun.RuleId` primary key). `ISuggestionRepository` with `Task<IReadOnlyList<Suggestion>> ListAsync(string? status, ct)`, `Task<IReadOnlyList<SuggestionRuleRun>> ListRuleRunsAsync(ct)`, `Task<Suggestion?> GetAsync(Guid id, ct)`, `Task SaveEvaluationAsync(IReadOnlyList<RuleOutcome> outcomes, Instant now, SuggestionOptions options, ct)` (applies the lifecycle in one transaction under `pg_advisory_xact_lock(hashtext('suggestions'))`), `Task<bool> DismissAsync(Guid id, ct)`, `Task<bool> SnoozeAsync(Guid id, int days, Instant now, ct)`. `SuggestionService.EvaluateAsync(CancellationToken)` loads inputs, runs every `ISuggestionRule`, saves.

- [ ] **Step 1: Write failing tests** for the lifecycle: insert on first fire; update on second fire (one row); condition gone resolves; resolved row reopens and resets `FirstSeenAt`; dismissed stays dismissed under a 10% move and reopens at 30% or at double the sample; expired snooze reopens; a rule with no subject at the floor records `notEvaluated`; eight concurrent `SaveEvaluationAsync` calls yield one row per key and no exception.
- [ ] **Step 2:** Run; expect FAIL.
- [ ] **Step 3: Implement** entities, mapping (`Key` max 200, `RuleId` 60, `Subject` 200, `Title` 300, jsonb columns), migration via `dotnet ef migrations add AddSuggestions` (install `dotnet-ef` 10.0.13 with `--tool-path` in the scratchpad; do not edit the tool manifest), repository, loader (window = last `SeatWindowDays` complete days; aggregates for the plan window; `FxRateProvider` for the rate; runs and findings via the adversarial repository) and service. Dismiss and snooze set `BaselineFiguresJson` to the current evidence figures.
- [ ] **Step 4:** Run the new tests, then the full gate and the AI-smell scan (`pwsh -File .github/ai-smells/pr-detect.ps1 -RepoPath . -Base origin/main -Head HEAD -OutFile <scratch>.json`). Commit `feat: persist suggestions and apply their lifecycle`.

### Task 4: Endpoints, worker and configuration (Claude)

**Files:**
- Create: `src/AiObservatory.Api/Endpoints/SuggestionsEndpoints.cs`, `src/AiObservatory.Api/Services/Suggestions/SuggestionWorkerService.cs`
- Modify: `Program.cs` (map endpoints; register worker outside demo mode; bind and validate `Suggestions` options), `appsettings.json` (defaults documented in a comment-free section), `docs/provider-setup.md` or the nearest settings doc (list the `Suggestions:*` keys)
- Test: `tests/AiObservatory.Api.IntegrationTests/SuggestionsEndpointsWafTests.cs`, `tests/AiObservatory.Api.Tests/Suggestions/SuggestionWorkerTests.cs`

**Interfaces:**
- Produces: `GET /api/suggestions?status=open` returning the `get-response.json` shape; `POST /api/suggestions/{id}/dismiss`; `POST /api/suggestions/{id}/snooze?days=` (1 to 90); `POST /api/suggestions/refresh`. Worker: daily at the next UTC midnight like `IntelligenceWorkerService`, isolated try/catch per run, its own `DelayAsync` test seam.

- [ ] **Step 1: Write failing tests.** WAF: GET with the read key succeeds; non-GET without the admin key is 401 (read-only key refused); unknown id gives 404; `days=0` and `days=91` give 400; dismiss then GET `status=open` excludes it; refresh twice gives the same set. Worker unit test: a thrown evaluation is logged and the loop continues; cancellation stops it.
- [ ] **Step 2:** Run; expect FAIL. **Step 3:** Implement. Invalid options (negative window, fraction outside 0 to 1) fail startup with a message naming the key.
- [ ] **Step 4:** Full gate, AI-smell scan. Commit `feat: suggestions endpoints and daily worker`.

### Task 5: Overview Suggestions card (GLM; own worktree of the Observatory)

**Files:**
- Create: `src/AiObservatory.Web/src/api/useSuggestions.ts`, `src/AiObservatory.Web/src/components/SuggestionsCard.tsx`, `SuggestionsCard.test.tsx`
- Modify: `src/AiObservatory.Web/src/pages/Dashboard.tsx` (render the card under `SummaryCards` on the Overview tab only), `src/AiObservatory.Web/src/api/client.ts` (suggestion types and calls)

**Interfaces:**
- Consumes: `docs/superpowers/contracts/suggestions/get-response.json` for types and test data.
- Produces: `useSuggestions(): { suggestions: SuggestionRow[]; notEvaluated: {ruleId: string; reason: string}[]; isLoading: boolean; isError: boolean }`; mutation helpers `dismissSuggestion(id)`, `snoozeSuggestion(id, days)` that invalidate the `['suggestions']` query. Auth headers follow `client.ts`.

- [ ] **Step 1: Write `SuggestionsCard.test.tsx`** from the fixture: rows ordered attention before info then oldest first; expanding a row shows its figures and, for seat rules, the run ids; dismiss and snooze (7, 30 days) call the helpers; the footer shows "2 rules not evaluated" with reasons on expand; the empty state names the rules that ran; loading and error states. Run; expect FAIL.
- [ ] **Step 2:** Implement with the repository's card styling and the existing `InfoPopover`/`Card` components; no emoji; no new dependency.
- [ ] **Step 3:** Web gate. Commit `feat(web): suggestions card on the overview`.

### Task 6: Integration and first live evaluation (Claude)

- [ ] **Step 1:** After Tasks 2 to 5 merge and deploy, run `POST /api/suggestions/refresh` against the live app (script reading `sweep.env` without printing the key). Expected today: every `seat.*` rule `notEvaluated` (no findings rows yet); `plan.*` rules evaluated against real subscriptions.
- [ ] **Step 2:** Hand-check each plan suggestion's figures against `GET /api/aggregates` and the subscription list; any disagreement is a rules bug.
- [ ] **Step 3:** Record the live result and the first real panel run's seat outcome in the PR description; add the rules table to the documentation project's inputs. Commit any doc change.

## Dispatch notes (Claude, not the plan's executors)

- Task 5 goes to GLM through the interactive Codex command the owner uses, with a brief that cites Task 5's text and the fixture by path; the owner runs it. Tasks 1 to 4 and 6 are Claude's, in that order. Task 5 depends only on Task 1.
- Branches: this plan and the design land first on `suggestions-design`; each task group gets its own branch off `main` after that merges.
