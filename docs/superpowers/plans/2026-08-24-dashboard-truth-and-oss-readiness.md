# Dashboard Truth and OSS Readiness Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Present billed, estimated, provider-estimated, notional, missing, and stale data without merging unlike facts, then document a credible OSS setup and provider extension path.

**Architecture:** Extend the existing aggregate contract and add one read-only source-status endpoint. The frontend derives cost cards through one pure summarizer, groups charts at source/scope/basis grain, and uses its existing provider registry for all known-provider presentation while retaining the unknown-provider fallback.

**Tech Stack:** .NET 10 minimal APIs, EF Core 10, NodaTime, xUnit v3, React 19, TypeScript 6, TanStack Query, Recharts, Vitest/Testing Library, ESLint, Vite.

**Spec:** `docs/superpowers/specs/2026-08-24-source-aware-observability-design.md`

## Global Constraints

- Run after the source-aware usage, automatic pricing, and provider acquisition plans.
- `Billed spend` contains only provider-reported or ledger financial data.
- Public-list estimates, provider estimates, and subscription notional value never merge.
- Missing information renders `Not reported`, never `$0.00` or zero tokens.
- Charts/tooltips retain source, scope, and cost-basis labels.
- `/healthz` remains independent process liveness.
- Unconfigured integrations remain visible with setup guidance.
- Existing visual language, responsive behavior, keyboard behavior, and accessible names remain.
- Unknown providers use the existing visual fallback.
- Do not add a UI framework, chart library, runtime configuration system, or generated client.

## File Structure

- `SourceStatusEndpoints.cs` owns status classification and wire projection.
- `api/client.ts` mirrors backend truth enums as open string unions with a string fallback.
- `costSummary.ts` is the only place that totals costs by basis.
- Existing charts and breakdowns consume source-grained rows rather than silently regrouping them.
- `SourceStatusPanel.tsx` is a compact overview panel, not a second operations dashboard.
- `config/providers.ts` owns provider/source display metadata but no prices.
- Focused docs cover setup, truth/pricing behavior, and adding a compile-time provider.

---

### Task 1: Expose source freshness without changing liveness

**Files:**
- Create: `src/AiObservatory.Api/Endpoints/SourceStatusEndpoints.cs`
- Modify: `src/AiObservatory.Api/Program.cs`
- Create: `tests/AiObservatory.Api.Tests/SourceStatusEndpointsTests.cs`
- Create: `tests/AiObservatory.Api.IntegrationTests/SourceStatusEndpointsWafTests.cs`
- Modify: `tests/AiObservatory.Api.IntegrationTests/StartupGuardsTests.cs`

**Interfaces:**
- Produces: `GET /api/sources/status` returning `SourceStatusResponse[]`.
- Produces status values `configured`, `fresh`, `stale`, `failing`, `unavailable`, and `notConfigured`.
- Preserves: the existing `/healthz` body and status-code behavior byte-for-byte.

- [ ] **Step 1: Write classification and route tests**

```csharp
[Theory]
[InlineData(false, null, 0, null, "notConfigured")]
[InlineData(true, false, 1, null, "unavailable")]
[InlineData(true, null, 2, "2026-08-24T11:00:00Z", "failing")]
[InlineData(true, true, 0, "2026-08-24T11:30:00Z", "fresh")]
[InlineData(true, true, 0, "2026-08-20T11:30:00Z", "stale")]
public void Classify_returns_truthful_status(bool configured, bool? available, int failures, string? lastSuccess, string expected)
{
    var state = State(configured, available, failures, lastSuccess, expectedIntervalSeconds: 86_400);
    SourceStatusEndpoints.Classify(state, Instant.FromUtc(2026, 8, 24, 12, 0)).Should().Be(expected);
}
```

Add WAF tests for ordering, sanitized error projection, null timestamps, and authentication. Snapshot `/healthz` before and after to prove no source state changes liveness.

- [ ] **Step 2: Run tests and confirm the route is missing**

```powershell
dotnet test tests/AiObservatory.Api.Tests/AiObservatory.Api.Tests.csproj --filter "FullyQualifiedName~SourceStatusEndpointsTests"
```

- [ ] **Step 3: Implement the read-only endpoint**

Return:

```csharp
public sealed record SourceStatusResponse(
    string SourceId,
    string Status,
    bool IsConfigured,
    Instant? LastAttemptAt,
    Instant? LastSuccessAt,
    Instant? LatestObservationAt,
    int ConsecutiveFailureCount,
    string? LastError
);
```

Classification order is: not configured; explicitly unavailable; failing; never succeeded (`configured`); overdue by more than twice `ExpectedRefreshIntervalSeconds` (`stale`); otherwise fresh. Query `SourceSyncStates.AsNoTracking()` ordered by source ID. Return only the already-sanitized/truncated error.

- [ ] **Step 4: Run API tests**

```powershell
dotnet test tests/AiObservatory.Api.Tests/AiObservatory.Api.Tests.csproj --filter "FullyQualifiedName~SourceStatusEndpointsTests"
```

```powershell
dotnet test tests/AiObservatory.Api.IntegrationTests/AiObservatory.Api.IntegrationTests.csproj --filter "FullyQualifiedName~SourceStatusEndpointsWafTests|FullyQualifiedName~StartupGuardsTests"
```

- [ ] **Step 5: Commit**

```powershell
git add src/AiObservatory.Api tests/AiObservatory.Api.Tests tests/AiObservatory.Api.IntegrationTests
```

```powershell
git commit -m "feat(api): expose source freshness status"
```

### Task 2: Split dashboard totals by financial meaning

**Files:**
- Modify: `src/AiObservatory.Web/src/api/client.ts`
- Modify: `src/AiObservatory.Web/src/api/client.test.ts`
- Modify: `src/AiObservatory.Web/src/api/queries.ts`
- Create: `src/AiObservatory.Web/src/lib/costSummary.ts`
- Create: `src/AiObservatory.Web/src/lib/costSummary.test.ts`
- Modify: `src/AiObservatory.Web/src/components/SummaryCards.tsx`
- Create: `src/AiObservatory.Web/src/components/SummaryCards.test.tsx`
- Modify: `src/AiObservatory.Web/src/index.css`

**Interfaces:**
- Consumes: aggregate provenance/cache-savings fields, existing spend-ledger entries, and source status response.
- Produces: `summarizeCosts(aggregates, spendEntries) -> CostSummary`.
- Produces: `useSourceStatuses()` and includes it in `useDashboardStatus()`.

- [ ] **Step 1: Add exact wire types and failing summary tests**

Change `DailyAggregate.provider` from the closed `ProviderKey` union to `string`, then add:

```typescript
export type SourceKind = 'providerApi' | 'localTelemetry' | 'manual' | 'legacy' | string
export type UsageScope = 'api' | 'subscription' | 'mixed' | 'unknown' | string
export type CostBasis = 'billed' | 'providerEstimated' | 'listPriceEstimate' | 'notional' | 'none' | 'unknown' | string

export interface DailyAggregate {
  date: string
  provider: string
  model: string
  sourceId: string
  sourceKind: SourceKind
  usageScope: UsageScope
  costBasis: CostBasis
  inputTokens: number
  outputTokens: number
  cacheReadTokens: number
  cacheWriteTokens: number
  cacheWrite1hTokens: number
  costUsd: number
  unknownCostCount: number
  cacheSavingsUsd: number
  unknownCacheSavingsCount: number
  requestCount: number
}
```

The pure test must prove unlike bases do not merge and absence stays null:

```typescript
expect(summarizeCosts(rows, spend)).toEqual({
  billedGbp: 8,
  listPriceEstimateUsd: 2,
  providerEstimateUsd: 3,
  notionalUsd: 4,
  unknownCostObservations: 1,
  cacheSavingsUsd: null,
})
expect(summarizeCosts([], []).billedGbp).toBeNull()
```

- [ ] **Step 2: Run the focused frontend tests and confirm failure**

```powershell
npm --prefix src/AiObservatory.Web test -- --run src/lib/costSummary.test.ts src/components/SummaryCards.test.tsx
```

- [ ] **Step 3: Implement the single-pass summarizer**

`billedGbp` sums signed `AmountGbp` from existing ledger rows and is null when there are no rows. Aggregate costs route only by exact `costBasis`; no default branch adds money. A basis with at least one row reports its sum, including legitimate zero. Cache savings is null if every applicable row has unknown savings; otherwise sum known savings and surface the unknown count separately.

- [ ] **Step 4: Replace the misleading Spend card and hand-priced savings**

Use the same rolling date range for `useSpendEntries`. Render separate cards for `Billed spend`, `List-price estimate`, `Provider estimate`, `Subscription notional`, `Tokens`, and `New insights`. Billed is formatted from stored GBP directly; USD-derived cards continue through the existing USD→GBP presentation helper and state their USD basis in the info popover.

Render the literal `Not reported` when a summary field is null. Remove `getCacheSavingsRate`, every frontend rate, and the `saved £…` claim when server savings are unknown. Keep cache-hit percentage based on observed tokens.

- [ ] **Step 5: Run summary, client, and architecture tests**

```powershell
npm --prefix src/AiObservatory.Web test -- --run src/lib/costSummary.test.ts src/components/SummaryCards.test.tsx src/api/client.test.ts src/architecture.spec.ts
```

- [ ] **Step 6: Commit**

```powershell
git add src/AiObservatory.Web
```

```powershell
git commit -m "fix(web): separate billed estimated and notional totals"
```

### Task 3: Retain source truth in charts, breakdowns, and provider metadata

**Files:**
- Modify: `src/AiObservatory.Web/src/config/providers.ts`
- Modify: `src/AiObservatory.Web/src/config/providers.test.ts`
- Modify: `src/AiObservatory.Web/src/theme/providerColors.ts`
- Modify: `src/AiObservatory.Web/src/components/SpendChart.tsx`
- Create: `src/AiObservatory.Web/src/components/SpendChart.test.tsx`
- Modify: `src/AiObservatory.Web/src/components/ProviderSplit.tsx`
- Create: `src/AiObservatory.Web/src/components/ProviderSplit.test.tsx`
- Modify: `src/AiObservatory.Web/src/components/ModelBreakdown.tsx`
- Create: `src/AiObservatory.Web/src/components/ModelBreakdown.test.tsx`
- Create: `src/AiObservatory.Web/src/components/SourceStatusPanel.tsx`
- Create: `src/AiObservatory.Web/src/components/SourceStatusPanel.test.tsx`
- Modify: `src/AiObservatory.Web/src/pages/Dashboard.tsx`
- Modify: `src/AiObservatory.Web/src/index.css`

**Interfaces:**
- Consumes: `DailyAggregate` and `SourceStatusResponse` from Task 2.
- Produces: provider/source display metadata from `getProvider` and `getSource`.
- Preserves: unknown provider color/name fallbacks.

- [ ] **Step 1: Write registry, chart-grain, and status-panel tests**

```typescript
expect(getProvider('new-oss-provider')).toBeUndefined()
expect(providerDisplayName('new-oss-provider')).toBe('New oss provider')

const series = buildUsageSeries([
  aggregate({ provider: 'openai', sourceId: 'openai-usage-api', usageScope: 'api', costBasis: 'listPriceEstimate' }),
  aggregate({ provider: 'openai', sourceId: 'codex-local', usageScope: 'subscription', costBasis: 'notional' }),
])
expect(series.map(x => x.label)).toEqual([
  'OpenAI · Usage API · API · List-price estimate',
  'OpenAI · Codex local · Subscription · Notional',
])
```

Test that `SourceStatusPanel` renders all six statuses, relative/absolute last-success text, sanitized error text, and a setup link for not-configured sources. Test that unknown source/provider strings remain readable.

- [ ] **Step 2: Run tests and confirm current grouping merges the rows**

```powershell
npm --prefix src/AiObservatory.Web test -- --run src/config/providers.test.ts src/components/SpendChart.test.tsx src/components/ProviderSplit.test.tsx src/components/ModelBreakdown.test.tsx src/components/SourceStatusPanel.test.tsx
```

- [ ] **Step 3: Remove prices and add source metadata to the existing registry**

Delete `cacheSavingsPerToken`. Add `sources: { id: string; displayName: string; setupHref: string }[]` to each provider config and flatten it for `getSource`. Include usage, cost, local, report, and pricing source IDs introduced by the preceding plans. Keep `PROVIDER_KEYS` for known ordering only; API types and components must accept arbitrary strings.

- [ ] **Step 4: Make visual aggregations honest**

`SpendChart` becomes `UsageValueChart`: cost modes explicitly select one of list-price estimate, provider estimate, or notional, and series keys include provider/source/scope/basis. Its tooltip prints all four labels. Token mode also groups by source and scope. Never chart billed ledger rows as provider-token cost; the Spend page already owns the ledger time series.

`ProviderSplit` becomes token/activity share so it cannot merge unlike costs. `ModelBreakdown` groups by provider, model, source, scope, and basis; add visible `Source` and `Basis` cells and render `Not reported` for unknown cost rather than `£0.00`.

- [ ] **Step 5: Add the compact source panel**

Merge `SourceStatusResponse[]` with every source in the frontend registry, synthesizing `notConfigured` only for registry sources absent from the API. Render one row per source with existing `StatusBadge`, display name, status, last success, and failure count. Expand only rows with an error; do not expose response bodies or URLs. Put the panel below summary cards on Overview and link not-configured rows to `docs/provider-setup.md` through the repository's public documentation URL.

- [ ] **Step 6: Run focused and full frontend tests**

```powershell
npm --prefix src/AiObservatory.Web test -- --run src/config/providers.test.ts src/components/SpendChart.test.tsx src/components/ProviderSplit.test.tsx src/components/ModelBreakdown.test.tsx src/components/SourceStatusPanel.test.tsx
```

```powershell
npm --prefix src/AiObservatory.Web test -- --run
```

- [ ] **Step 7: Commit**

```powershell
git add src/AiObservatory.Web
```

```powershell
git commit -m "feat(web): show usage source and freshness truth"
```

### Task 4: Publish provider setup, pricing provenance, and extension guidance

**Files:**
- Modify: `README.md`
- Create: `docs/provider-setup.md`
- Create: `docs/truth-and-pricing.md`
- Create: `docs/adding-a-provider.md`
- Modify: `clients/README.md`
- Modify: `docs/ai-observatory.postman_collection.json`
- Modify: `src/AiObservatory.Ingest/appsettings.json`

**Interfaces:**
- Documents: every supported source ID, required plan/credential, truth classification, cadence, and known limitation.
- Documents: the exact compile-time provider extension checklist.
- Produces: runnable environment-variable examples without real credentials.

- [ ] **Step 1: Add the source/capability matrix**

`docs/provider-setup.md` contains this information as a table, with official setup links beside each row:

| Source | Capability | Required access | Scope / basis | When absent |
| --- | --- | --- | --- | --- |
| OpenAI Usage API | API tokens | organization Admin key | API / list-price estimate | Not configured |
| OpenAI Costs API | billed cost | organization Admin key | API / billed | Not configured |
| Codex local | local tokens | filesystem + Observatory API key | subscription / notional | Not configured |
| Anthropic Messages Usage | API tokens | organization Admin API key | API / list-price estimate | Unavailable for individual accounts |
| Anthropic Cost Report | billed cost | organization Admin API key | API / billed | Unavailable for individual accounts |
| Claude Code Usage | coding activity | eligible Team/Enterprise org + opt-in | subscription or API / provider estimate or none | Disabled by default |
| Claude local | local transcript telemetry | filesystem + Observatory API key | subscription / notional | Not configured |
| Copilot organization report | org activity | org token with Copilot metrics permission | subscription / none | Not configured |
| Copilot local | local tokens | filesystem + Observatory API key | subscription / notional | Not configured |
| Google billing export | billed cost | BigQuery job user + billing-export data viewer | mixed / billed | Not configured |
| Kimi local | local `usage.record` telemetry | filesystem + Observatory API key | subscription / notional | Not configured |

State clearly that local telemetry is best-effort, machine-local, and not an invoice. Document the exact Codex, Claude, Copilot, and Kimi paths and the duplicate/cumulative safeguards from the sweeper tests. Document `OBSERVATORY_LOCAL_SOURCES` and require excluding `claude` when `claude-code-usage-api` covers the same account.

- [ ] **Step 2: Document truth and automatic pricing behavior**

`docs/truth-and-pricing.md` defines every `SourceKind`, `UsageScope`, and `CostBasis`; explains occurrence versus observation time; describes daily first-party renewal, content hashes, raw evidence, future-effective rows, last-known-good fallback, security limits, null-on-unknown resolution, and eligible-only repricing. Link the four official pricing sources and state that BenchLM is discovery/cross-check evidence only.

- [ ] **Step 3: Document adding a provider with exact files and checks**

`docs/adding-a-provider.md` gives this sequence:

1. Add the usage `Provider` enum member only if the provider emits usage; persistence remains string-backed.
2. Add stable source ID constants and frontend display/source metadata.
3. Implement `IUsageSource`, `IPricingSource`, or both; a billing-only adapter still implements the scheduled `IUsageSource` acquisition boundary and writes `BillingObservation`.
4. Register `SourceDefinition` and the implementation in DI; do not edit either worker.
5. Add client/parser contract fixtures, incomplete-pagination rejection, source-status, and unknown-dimension tests.
6. Add setup requirements and limitations to the matrix.

Include the exact focused commands:

```powershell
dotnet test tests/AiObservatory.Ingest.Tests/AiObservatory.Ingest.Tests.csproj
```

```powershell
npm --prefix src/AiObservatory.Web test -- --run src/config/providers.test.ts
```

- [ ] **Step 4: Update examples and API collection**

Add all new environment keys to `appsettings.json` with empty values, update the local sweeper payload example with explicit provenance, add `/api/sources/status` and source-aware event examples to Postman, and link the three focused docs from the README. Never put credential-bearing URLs or sample secrets in committed files.

- [ ] **Step 5: Check docs for stale claims and hand-maintained prices**

```powershell
rg -n "reports|cacheSavingsPerToken|OPENAI_PRICING|COPILOT_PRICING|Sonnet 5|2026-09-01|5000|zero-token" README.md docs clients src/AiObservatory.Web/src src/AiObservatory.Ingest
```

Expected: hits are only historical design records, explicit statements that the retired route/fake rows were removed, or current tests; no live setup or production price table remains.

- [ ] **Step 6: Commit**

```powershell
git add README.md docs clients/README.md src/AiObservatory.Ingest/appsettings.json
```

```powershell
git commit -m "docs: publish provider and pricing setup"
```

### Task 5: Run the release gate and migration rehearsal

**Files:** all files changed by the four implementation plans.

**Interfaces:**
- Verifies: backend, frontend, client, migrations, architecture rules, and production build.
- Produces: one clean branch ready for review; does not push or open a PR in this task.

- [ ] **Step 1: Restore and check C# formatting**

```powershell
dotnet restore AiObservatory.slnx
```

```powershell
dotnet csharpier check .
```

- [ ] **Step 2: Build once before running the no-build test gate**

```powershell
dotnet build AiObservatory.slnx --configuration Release --no-restore
```

Expected: zero errors and zero warnings. Do not trust a stale MTP runner result after a failed build.

- [ ] **Step 3: Run the complete backend and client suites**

```powershell
dotnet test --solution AiObservatory.slnx --configuration Release --no-build --report-xunit-trx --results-directory ./TestResults --timeout 5m
```

```powershell
node --test clients/observatory-sweep.test.mjs
```

- [ ] **Step 4: Rehearse all migrations against representative legacy rows**

```powershell
dotnet test tests/AiObservatory.Data.Tests/AiObservatory.Data.Tests.csproj --configuration Release --no-build --filter "FullyQualifiedName~UsageMigrationTests"
```

Expected with `TEST_DB_CONNECTION`: legacy usage, aggregates, and spend remain present; provenance is honest; new indexes and constraints are active. If a migration was just generated, rebuild before any `--no-build` database command.

- [ ] **Step 5: Run the complete frontend gate**

```powershell
npm --prefix src/AiObservatory.Web run lint
```

```powershell
npm --prefix src/AiObservatory.Web test -- --run
```

```powershell
npm --prefix src/AiObservatory.Web run build
```

- [ ] **Step 6: Prove extension seams are central-worker-free**

```powershell
rg -n "Anthropic|Copilot|Google|OpenAi|Moonshot" src/AiObservatory.Ingest/ProviderPollingWorkerService.cs src/AiObservatory.Ingest/Pricing/PricingRefreshWorkerService.cs
```

Expected: no concrete provider type or switch in either worker.

- [ ] **Step 7: Review the final diff and commit any gate-only correction**

```powershell
git status --short
```

```powershell
git diff --check
```

If the gate required a correction, commit only those corrected files with a precise message. Otherwise leave the already committed task history intact.
