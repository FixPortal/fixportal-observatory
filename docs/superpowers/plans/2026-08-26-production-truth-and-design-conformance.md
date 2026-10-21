# Production Truth and FixPortal Design Conformance Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the deployed AI Observatory financially truthful and visually conformant with the FixPortal design language in one pull request.

**Architecture:** The billed GBP ledger becomes the sole source for Reporting and budget alerts, while usage aggregates remain activity, estimate, and explicitly notional evidence. The frontend keeps its tokenless-OSS-safe vendored design layer, synchronizes it to `@fixportal/design` 0.8.1 at tag `v0.8.1` / commit `6b3e3e0`, and applies app-local provider/project palettes only for identity and chart series.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, EF Core 10 with PostgreSQL and NodaTime, React 19, TypeScript 6, TanStack Query, Recharts, Vitest/Testing Library, Bicep, Azure App Service and Static Web Apps.

**Spec:** `docs/superpowers/specs/2026-08-26-production-truth-and-design-conformance.md`

## Global Constraints

- Work only in `.claude/worktrees/reviewer-passes` on `reviewer-findings-batch10`.
- Keep the design record, implementation, migration, infrastructure change, and tests in one PR.
- Make `SpendEntry.AmountGbp` the only money input to Reporting and budget alerts.
- Preserve usage aggregates for activity, estimates, and notional comparisons; never sum them into billed spend.
- Rename the existing threshold column and preserve its numeric values; do not perform FX conversion during migration.
- Keep NodaTime at the domain/data boundary and the existing injected `IClock` in alert evaluation.
- Use the existing repository, query hooks, Recharts dependency, CSS architecture, and native `<details>` disclosure.
- Keep the vendored design copy; do not add `@fixportal/design` or another UI dependency to `package.json`.
- Provider colours identify providers or chart series only. The canonical blue accent is interaction; green/amber/red are status.
- Preserve accessibility basics and test keyboard/semantic behaviour where the interaction changes.
- Commit locally by task, but push the finished branch only once after the full gate is green.

---

### Task 1: Move budget rules and alerts to billed GBP

**Files:**
- Modify: `src/AiObservatory.Data/Entities/BudgetRule.cs`
- Create: `src/AiObservatory.Data/Entities/BudgetAlertClaim.cs`
- Modify: `src/AiObservatory.Data/AiObservatoryDbContext.cs`
- Modify: `src/AiObservatory.Data/Repositories/IUsageRepository.cs`
- Modify: `src/AiObservatory.Data/Repositories/UsageRepository.cs`
- Create: EF-generated migration `20260826130157_AddBudgetAlertsAndRenameThresholdToGbp` under `src/AiObservatory.Data/Migrations/`
- Modify: `src/AiObservatory.Data/Migrations/AiObservatoryDbContextModelSnapshot.cs`
- Modify: `src/AiObservatory.Api/Endpoints/BudgetRulesEndpoints.cs`
- Modify: `src/AiObservatory.Api/Services/BudgetAlertService.cs`
- Modify: `src/AiObservatory.Api/Services/IAlertNotifier.cs`
- Modify: `src/AiObservatory.Api/Services/EmailAlertNotifier.cs`
- Modify: `src/AiObservatory.Api/Program.cs`
- Modify: `src/AiObservatory.Web/src/api/client.ts`
- Modify: `src/AiObservatory.Web/src/components/BudgetRulesPanel.tsx`
- Modify: `tests/AiObservatory.Data.Tests/Repositories/UsageRepositoryTests.cs`
- Modify: `tests/AiObservatory.Data.Tests/Repositories/UsageMigrationTests.cs`
- Modify: `tests/AiObservatory.Api.Tests/Services/BudgetAlertServiceTests.cs`
- Modify: `tests/AiObservatory.Api.Tests/Services/EmailAlertNotifierTests.cs`
- Modify: `tests/AiObservatory.Api.IntegrationTests/BudgetRulesEndpointsWafTests.cs`
- Modify: `tests/AiObservatory.Api.IntegrationTests/DevSeedEndpointTests.cs`

**Interfaces:**
- Produces: `Task<decimal> GetBilledSpendGbpAsync(LocalDate from, LocalDate to, Provider? provider = null, CancellationToken ct = default)` on `IUsageRepository`.
- Produces: one grouped `GetDailyBilledSpendGbpAsync` read from the persisted evaluation boundary,
  one durable claim per rule-period, and a bounded oldest-first pending-email read with leases.
- Produces: `BudgetRule.ThresholdGbp`, JSON field `thresholdGbp`, and `BudgetAlertPayload.ThresholdGbp` / `ActualSpendGbp`.
- Consumes: signed `SpendEntry.AmountGbp` and `SpendVendor.Provider`; unmapped vendors count only when `provider` is null.

- [ ] **Step 1: Add a real-PostgreSQL repository test for all-provider and provider-scoped billed totals**

Add one test to `UsageRepositoryTests` that uses seeded vendors/categories, signed ledger rows, an out-of-range row, and an unmapped vendor:

```csharp
[Fact]
public async Task GetBilledSpendGbpAsync_sums_signed_in_range_entries_and_filters_by_mapped_provider()
{
    var ct = TestContext.Current.CancellationToken;
    var anthropic = await _ctx.SpendVendors.SingleAsync(v => v.Provider == Provider.Anthropic, ct);
    var openAi = await _ctx.SpendVendors.SingleAsync(v => v.Provider == Provider.OpenAI, ct);
    var unmapped = await _ctx.SpendVendors.SingleAsync(v => v.Key == "coderabbit", ct);
    var categoryId = await _ctx.SpendCategories.Select(c => c.Id).FirstAsync(ct);

    _ctx.SpendEntries.AddRange(
        Spend(anthropic.Id, categoryId, new LocalDate(2026, 8, 1), 20m),
        Spend(anthropic.Id, categoryId, new LocalDate(2026, 8, 2), -5m),
        Spend(openAi.Id, categoryId, new LocalDate(2026, 8, 2), 7m),
        Spend(unmapped.Id, categoryId, new LocalDate(2026, 8, 2), 3m),
        Spend(anthropic.Id, categoryId, new LocalDate(2026, 7, 31), 100m)
    );
    await _ctx.SaveChangesAsync(ct);

    (await _repo.GetBilledSpendGbpAsync(new LocalDate(2026, 8, 1), new LocalDate(2026, 8, 2), null, ct))
        .Should().Be(25m);
    (await _repo.GetBilledSpendGbpAsync(new LocalDate(2026, 8, 1), new LocalDate(2026, 8, 2), Provider.Anthropic, ct))
        .Should().Be(15m);
}
```

Use a local `Spend(...)` helper in the test class that fills required `SpendEntry` fields with `Amount = AmountGbp = amountGbp`, `Currency = "GBP"`, `FxRate = 1m`, `RecordedAt`/`ObservedAt` set to a fixed `Instant`, and `CostBasis = CostBasis.Billed`.

- [ ] **Step 2: Run the repository test and verify the missing method fails compilation**

Run:

```powershell
dotnet test tests/AiObservatory.Data.Tests/AiObservatory.Data.Tests.csproj --filter "FullyQualifiedName~GetBilledSpendGbpAsync_sums_signed" --no-restore
```

Expected: FAIL because `IUsageRepository.GetBilledSpendGbpAsync` does not exist.

- [ ] **Step 3: Add the minimal projected billed-sum query**

Add the declared interface method and implement it with `AsNoTracking`, inclusive dates, and a join only for provider-scoped rules:

```csharp
public async Task<decimal> GetBilledSpendGbpAsync(
    LocalDate from,
    LocalDate to,
    Provider? provider = null,
    CancellationToken ct = default
)
{
    var entries = ctx.SpendEntries.AsNoTracking().Where(e => e.OccurredOn >= from && e.OccurredOn <= to);
    if (provider is not null)
    {
        entries =
            from entry in entries
            join vendor in ctx.SpendVendors.AsNoTracking() on entry.VendorId equals vendor.Id
            where vendor.Provider == provider
            select entry;
    }

    return await entries.SumAsync(e => (decimal?)e.AmountGbp, ct) ?? 0m;
}
```

Run the same filtered test. Expected: PASS.

- [ ] **Step 4: Change the service tests before changing alert production code**

Update existing `BudgetAlertServiceTests` rules and payload assertions to GBP names. Replace aggregate stubs with the billed query:

```csharp
repository
    .GetBilledSpendGbpAsync(from, to, rule.Provider, Arg.Any<CancellationToken>())
    .Returns(rule.ThresholdGbp + 0.01m);

await notifier.Received(1).NotifyAsync(
    Arg.Is<BudgetAlertPayload>(p =>
        p.ThresholdGbp == rule.ThresholdGbp &&
        p.ActualSpendGbp == rule.ThresholdGbp + 0.01m),
    Arg.Any<CancellationToken>()
);
```

Add an assertion that `GetAggregatesAsync` is never called. Retain the existing daily/weekly/monthly window, de-duplication, cancellation, failed-delivery retry, and per-rule isolation cases.

Run:

```powershell
dotnet test tests/AiObservatory.Api.Tests/AiObservatory.Api.Tests.csproj --filter "FullyQualifiedName~BudgetAlertServiceTests" --no-restore
```

Expected: FAIL on the old USD properties and aggregate query.

- [ ] **Step 5: Rename the budget contract and switch alert evaluation to the billed query**

Apply these exact semantic changes:

```csharp
public decimal ThresholdGbp { get; init; }

public record BudgetAlertPayload(
    string Provider,
    string Period,
    decimal ThresholdGbp,
    decimal ActualSpendGbp,
    DateTimeOffset TriggeredAt
);
```

In `CheckRuleAsync`, replace aggregate loading/filtering/summing with:

```csharp
var totalSpendGbp = await repository.GetBilledSpendGbpAsync(from, to, rule.Provider, ct);
if (totalSpendGbp <= rule.ThresholdGbp)
{
    return;
}
```

Use `£{value:F2}` and the phrase `billed spend` in insight titles/bodies and email copy. Serialize `{ thresholdGbp = rule.ThresholdGbp, actualSpendGbp = totalSpendGbp }`. Rename the minimal API request property and validation message to `ThresholdGbp`. Rename the two development seed properties without changing their numeric values.

Persist `EvaluationStartsOn` as the immutable alert lifetime boundary. Daily evaluation uses one
grouped billed-spend query for every completed day from that boundary so late/corrected ledger
entries remain eligible; weekly and monthly windows clamp their start to the same boundary.

Creating an alert writes its insight, unique rule-period `BudgetAlertClaim`, and rule trigger time
in one transaction. Read an existing claim before opening that write transaction; retain the
unique-violation fallback for concurrent creators. Deliver pending email from durable claims in
oldest-first batches of 50, with the existing recoverable lease and stable message ID. Clearing
all insights explicitly deletes claims then insights in one transaction because the claim-to-
insight FK is restrictive.

Cover the evaluation boundary, replay fast path, concurrent convergence, pending-email bound and
terminal filters, claim-aware insight purge, and purge rollback against real PostgreSQL where
provider behavior is consequential.

- [ ] **Step 6: Generate and verify the consolidated durable-alert migration**

Run:

```powershell
dotnet ef migrations add AddBudgetAlertsAndRenameThresholdToGbp --project src/AiObservatory.Data --startup-project src/AiObservatory.Api
```

The generated `Up` must:

- rename `BudgetRules.ThresholdUsd` to `ThresholdGbp` in place;
- add non-null `EvaluationStartsOn` with a database-UTC-date default for existing and new rules;
- create `BudgetAlertClaims` with rule-period and lease check constraints;
- add the unique rule-period and insight indexes plus the filtered oldest-first delivery index;
- cascade rule deletion to its claims while restricting insight deletion until the claim is
  explicitly removed.

The generated `Down` must drop `BudgetAlertClaims`, drop `EvaluationStartsOn`, then rename
`ThresholdGbp` back to `ThresholdUsd`. Reject any drop/add threshold pair because it would lose
numeric values.

Add a real-PostgreSQL migration test that migrates to
`20260825220510_TrackPendingSourceWindows`, inserts a rule with `"ThresholdUsd" = 123.45`,
migrates to latest, and asserts the value, database evaluation default, indexes, constraints,
and relationships. Migrate back and assert `ThresholdUsd == 123.45`, the claim table is gone,
and `EvaluationStartsOn` is gone.

- [ ] **Step 7: Update the web budget contract and panel copy**

Change `BudgetRule.thresholdUsd` and create payloads to `thresholdGbp`. Change the headings and input label to `Threshold (GBP)` and render with the existing `gbp(rule.thresholdGbp)` formatter. Move the panel's static inline layout styles to the CSS classes planned in Task 5; keep only data-driven styles out of CSS.

- [ ] **Step 8: Run focused backend and frontend checks**

Run:

```powershell
dotnet test tests/AiObservatory.Data.Tests/AiObservatory.Data.Tests.csproj --filter "FullyQualifiedName~UsageRepositoryTests|FullyQualifiedName~UsageMigrationTests" --no-restore
```

```powershell
dotnet test tests/AiObservatory.Api.Tests/AiObservatory.Api.Tests.csproj --filter "FullyQualifiedName~BudgetAlertServiceTests|FullyQualifiedName~EmailAlertNotifierTests" --no-restore
```

```powershell
dotnet test tests/AiObservatory.Api.IntegrationTests/AiObservatory.Api.IntegrationTests.csproj --filter "FullyQualifiedName~BudgetRulesEndpointsWafTests|FullyQualifiedName~DevSeedEndpointTests" --no-restore
```

```powershell
npm test -- --run src/api/client.test.ts
```

Run the frontend command from `src/AiObservatory.Web`. Expected: all selected tests PASS.

- [ ] **Step 9: Prove the old money contract is gone from active code and commit**

Run:

```powershell
rg --pcre2 -n "ThresholdUsd|thresholdUsd|ActualSpend(?!Gbp)" src tests --glob "!src/AiObservatory.Data/Migrations/*.Designer.cs" --glob "!src/AiObservatory.Data/Migrations/20260602143243_InitialSchema.cs"
```

Expected: no matches. Historical migration metadata is intentionally unchanged.

Commit:

```powershell
git add src/AiObservatory.Data src/AiObservatory.Api src/AiObservatory.Web/src/api/client.ts src/AiObservatory.Web/src/components/BudgetRulesPanel.tsx tests/AiObservatory.Data.Tests tests/AiObservatory.Api.Tests tests/AiObservatory.Api.IntegrationTests
```

```powershell
git commit -m "Use billed GBP for budget alerts"
```

---

### Task 2: Replace Reporting estimates with billed-ledger views

**Files:**
- Create: `src/AiObservatory.Web/src/lib/billedReporting.ts`
- Create: `src/AiObservatory.Web/src/lib/billedReporting.test.ts`
- Modify: `src/AiObservatory.Web/src/components/ReportingCards.tsx`
- Create: `src/AiObservatory.Web/src/components/BilledSpendChart.tsx`
- Create: `src/AiObservatory.Web/src/components/BilledVendorSplit.tsx`
- Modify: `src/AiObservatory.Web/src/pages/ReportingPage.tsx`
- Delete: `src/AiObservatory.Web/src/lib/velocity.ts`
- Delete: `src/AiObservatory.Web/src/lib/velocity.test.ts`

**Interfaces:**
- Produces: `summarizeBilledReporting(entries, vendors, daysInRange)`, `buildBilledDailySeries(entries)`, and `buildBilledVendorSeries(entries, vendors)`.
- Consumes: existing `useSpendEntries(from, to)`, `useAllSpendVendors()`, `SpendEntry.amountGbp`, and `SpendVendor.displayName`.
- Produces: truthful empty state when the ledger has no rows; no aggregate fallback.

- [ ] **Step 1: Write pure tests for signed GBP reporting**

Define structural input types in `billedReporting.ts`; tests must cover these exact expectations:

```ts
const entries = [
  { occurredOn: '2026-08-01', vendorId: 'anthropic', amountGbp: 20 },
  { occurredOn: '2026-08-01', vendorId: 'anthropic', amountGbp: -5 },
  { occurredOn: '2026-08-02', vendorId: 'openai', amountGbp: 9 },
]
const vendors = [
  { id: 'anthropic', displayName: 'Anthropic' },
  { id: 'openai', displayName: 'OpenAI' },
]

expect(summarizeBilledReporting(entries, vendors, 2)).toEqual({
  totalGbp: 24,
  dailyAverageGbp: 12,
  projectedMonthlyGbp: 360,
  topVendorName: 'Anthropic',
  topVendorGbp: 15,
})
expect(buildBilledDailySeries(entries)).toEqual([
  { date: '2026-08-01', amountGbp: 15 },
  { date: '2026-08-02', amountGbp: 9 },
])
expect(buildBilledVendorSeries(entries, vendors)).toEqual([
  { vendorId: 'anthropic', name: 'Anthropic', amountGbp: 15 },
  { vendorId: 'openai', name: 'OpenAI', amountGbp: 9 },
])
```

Also assert that empty entries return `null` from `summarizeBilledReporting` and an unknown vendor renders as `Unknown vendor`, rather than disappearing.

- [ ] **Step 2: Run the new helper test and verify it fails**

Run from `src/AiObservatory.Web`:

```powershell
npm test -- --run src/lib/billedReporting.test.ts
```

Expected: FAIL because the module does not exist.

- [ ] **Step 3: Implement the three single-pass grouping helpers**

Use plain `Map` accumulation and the existing signed frozen `amountGbp`. `summarizeBilledReporting` returns `null` only when `entries.length === 0`; a ledger containing refunds that net to zero is still reported as £0.00. Projection remains the existing 30-day convention:

```ts
const dailyAverageGbp = totalGbp / daysInRange
const projectedMonthlyGbp = dailyAverageGbp * 30
```

Do not import API-layer types into `lib`; accept structural shapes as `costSummary.ts` and `spendFilters.ts` already do.

- [ ] **Step 4: Wire Reporting to existing ledger hooks**

In `ReportingPage`, replace `useAggregates` with:

```ts
const { entries, isLoading, isError } = useSpendEntries(from, to)
const vendors = useAllSpendVendors()
```

Pass `entries`, `vendors`, and `daysInRange` to `ReportingCards`; pass the derived daily/vendor series to the two new chart components. Render the existing error-banner language for query failure, a chart skeleton while loading, and `No billed spend reported for this period.` when `entries.length === 0`.

`ReportingCards` must label its values `Billed spend`, `Daily average`, `Projected / month`, and `Top vendor`, format all amounts with `gbp`, and render em dashes when its summary is null.

- [ ] **Step 5: Add the billed charts without changing the Overview usage charts**

`BilledSpendChart` uses the installed Recharts `BarChart` with one `amountGbp` series, the canonical brand fill, the existing short-date formatter, and GBP tooltip/axis formatting.

`BilledVendorSplit` uses a horizontal Recharts `BarChart`, not a pie: signed refunds/credits are valid ledger values and a pie cannot represent negatives truthfully. Use `--brand` for positive bars and `--bad-border` for negative bars. The accessible surrounding panel title remains `Billed spend by vendor`.

Keep `SpendChart` and `ProviderSplit` unchanged on Overview, where they are already labelled usage value/share. Delete `velocity.ts` and its obsolete USD-estimate tests after no imports remain.

- [ ] **Step 6: Run focused Reporting tests and build**

Run from `src/AiObservatory.Web`:

```powershell
npm test -- --run src/lib/billedReporting.test.ts src/pages/Dashboard.test.tsx
```

```powershell
npm run build
```

Expected: PASS; TypeScript has no references to `computeBurnRate`.

- [ ] **Step 7: Commit the billed Reporting slice**

```powershell
git add src/AiObservatory.Web/src/lib src/AiObservatory.Web/src/components/ReportingCards.tsx src/AiObservatory.Web/src/components/BilledSpendChart.tsx src/AiObservatory.Web/src/components/BilledVendorSplit.tsx src/AiObservatory.Web/src/pages/ReportingPage.tsx
```

```powershell
git commit -m "Report billed ledger spend"
```

---

### Task 3: Make subscription value explicitly notional and bound the insight feed

**Files:**
- Modify: `src/AiObservatory.Web/src/lib/subscriptions.ts`
- Modify: `src/AiObservatory.Web/src/lib/subscriptions.test.ts`
- Modify: `src/AiObservatory.Web/src/components/SubscriptionPanel.tsx`
- Modify: `src/AiObservatory.Web/src/components/InsightsFeed.tsx`
- Create: `src/AiObservatory.Web/src/components/InsightsFeed.test.tsx`

**Interfaces:**
- Produces: `notionalValueUsd(aggregates, provider, from)` in the existing subscriptions helper.
- Consumes: only aggregates where `costBasis === 'notional'`, provider matches, date is in range, and cost is reported.
- Produces: five default insight rows plus one native disclosure for any remainder.

- [ ] **Step 1: Add a failing notional-filter test**

Add a structural helper test:

```ts
expect(notionalValueUsd([
  { provider: 'anthropic', date: '2026-08-01', costBasis: 'notional', costUsd: 10, requestCount: 1, unknownCostCount: 0 },
  { provider: 'anthropic', date: '2026-08-02', costBasis: 'listPriceEstimate', costUsd: 1000, requestCount: 1, unknownCostCount: 0 },
  { provider: 'anthropic', date: '2026-08-03', costBasis: 'notional', costUsd: 8, requestCount: 1, unknownCostCount: 1 },
  { provider: 'openai', date: '2026-08-02', costBasis: 'notional', costUsd: 7, requestCount: 1, unknownCostCount: 0 },
], 'anthropic', '2026-08-01')).toBe(10)
```

Run:

```powershell
npm test -- --run src/lib/subscriptions.test.ts
```

Expected: FAIL because `notionalValueUsd` does not exist.

- [ ] **Step 2: Implement and use the notional helper**

Implement one filter/reduce in `subscriptions.ts`. Replace `periodSpendUsd` in `SubscriptionPanel` with `periodNotionalUsd = notionalValueUsd(...)`; keep the existing USD-to-GBP display conversion because the notional catalog is USD.

Change UI copy:

- `Period spend` -> `Notional usage value`
- `Period spend is API-tracked usage...` -> `Notional usage value applies public API list prices to eligible subscription activity. It is a comparison, not money charged.`
- Progress text -> `{percent}% of {subscription total} subscription price`

Provider colour may remain on the card's identity edge/provider name, but the progress fill becomes `--brand`; the over-threshold state remains semantic bad red. This prevents provider identity from masquerading as progress/status.

- [ ] **Step 3: Add a failing component test for five-plus-remainder insights**

Mock `useInsights` with seven unacknowledged records, render inside a `QueryClientProvider`, and assert:

```ts
expect(screen.getAllByText(/Insight [1-5]/)).toHaveLength(5)
expect(screen.queryByText('Insight 6')).not.toBeInTheDocument()
await user.click(screen.getByText('Show 2 older insights'))
expect(screen.getByText('Insight 6')).toBeVisible()
expect(screen.getByText('Insight 7')).toBeVisible()
```

Add a second case with five insights and assert no `summary` exists.

Run:

```powershell
npm test -- --run src/components/InsightsFeed.test.tsx
```

Expected: FAIL because all seven rows currently render at once.

- [ ] **Step 4: Implement the native disclosure**

Keep ordering from `useInsights`; split after unread filtering:

```tsx
const visible = unread.slice(0, 5)
const older = unread.slice(5)
```

Render `visible` in the existing feed. When `older.length > 0`, append:

```tsx
<details className="insights-older">
  <summary>Show {older.length} older insight{older.length === 1 ? '' : 's'}</summary>
  <div className="insights-feed insights-feed--older">
    {older.map(insight => <InsightRow key={insight.id} insight={insight} />)}
  </div>
</details>
```

Do not add React state for open/closed behaviour.

- [ ] **Step 5: Run focused tests and commit**

```powershell
npm test -- --run src/lib/subscriptions.test.ts src/components/InsightsFeed.test.tsx
```

Expected: PASS.

```powershell
git add src/AiObservatory.Web/src/lib/subscriptions.ts src/AiObservatory.Web/src/lib/subscriptions.test.ts src/AiObservatory.Web/src/components/SubscriptionPanel.tsx src/AiObservatory.Web/src/components/InsightsFeed.tsx src/AiObservatory.Web/src/components/InsightsFeed.test.tsx
```

```powershell
git commit -m "Clarify notional value and bound insights"
```

---

### Task 4: Synchronize the vendored FixPortal design layer

**Files:**
- Replace from canonical: `src/AiObservatory.Web/src/design/tokens.css`
- Replace from canonical: `src/AiObservatory.Web/src/design/components.css`
- Replace from canonical: `src/AiObservatory.Web/src/design/Button.tsx`
- Replace from canonical: `src/AiObservatory.Web/src/design/Card.tsx`
- Replace from canonical: `src/AiObservatory.Web/src/design/StatusBadge.tsx`
- Replace from canonical: `src/AiObservatory.Web/src/design/ThemeToggle.tsx`
- Create: `src/AiObservatory.Web/src/system.md`

**Interfaces:**
- Consumes: canonical `fixportal-assets/packages/design` version 0.8.1 at tag `v0.8.1` / commit `6b3e3e0`.
- Produces: stable existing Observatory import paths with canonical primitive behaviour.
- Preserves: `BrandWordmark.tsx` and `SearchIcon.tsx`, which do not need synchronization.

- [ ] **Step 1: Record the canonical Git blob hashes**

Record the clean `v0.8.1` Git blob hash for each of the six source files. Those immutable blobs,
not assertions about selected CSS strings, define the vendored snapshot.

- [ ] **Step 2: Compare the existing vendored hashes with the canonical blobs**

Record which vendored files differ before copying. Do not add CSS/source-string change-detector
tests: they pin private implementation text while missing rendered regressions.

- [ ] **Step 3: Copy only the six used canonical files**

Copy byte-for-byte from these source paths:

```text
fixportal-assets/packages/design/tokens.css
fixportal-assets/packages/design/components.css
fixportal-assets/packages/design/primitives/Button.tsx
fixportal-assets/packages/design/primitives/Card.tsx
fixportal-assets/packages/design/primitives/StatusBadge.tsx
fixportal-assets/packages/design/primitives/ThemeToggle.tsx
```

Do not copy unused primitives, build configuration, tests, or package metadata. Keep the local filenames/import paths listed above.

- [ ] **Step 4: Add the Observatory system note**

Create `system.md` with these exact sections and rules:

```markdown
# AI Observatory visual system

Canonical base: `@fixportal/design` 0.8.1 at tag `v0.8.1` / commit `6b3e3e0`, vendored from the FixPortal assets repository so public installs require no private package token.

## Product signature

AI Observatory is evidence-first: values carry source, scope, basis, freshness, and observation time. Billed GBP, estimates, and subscription notional value never share a total or visual claim.

## App-local palettes

Provider colours identify providers in charts, swatches, and provider badges only. Project colours identify project series only. Neither palette communicates status, selection, progress, or interaction. The canonical blue accent is interaction; green, amber, and red are status.

## Conformance

Use canonical surface, text, brand, status, spacing, radius, typography, focus, and motion rules. Borders provide depth; shadows are reserved for floating surfaces. Monospace is for values, identifiers, timestamps, and machine evidence.
```

- [ ] **Step 5: Verify blob identity, rendered behaviour, and the production build, then commit**

Recompute all six vendored Git blob hashes and require exact equality with the clean-tag sources.
Then run frontend lint, the full test suite, and the production build. The Task 5 six-tab ×
desktop/mobile × light/dark render matrix is the authoritative CSS/interaction evidence.

```powershell
npm run build
```

Expected: PASS.

```powershell
git add src/AiObservatory.Web/src/design src/AiObservatory.Web/src/system.md
```

```powershell
git commit -m "Sync the FixPortal design layer"
```

---

### Task 5: Apply the visual language across all six tabs

**Files:**
- Modify: `src/AiObservatory.Web/src/index.css`
- Modify: `src/AiObservatory.Web/src/config/providers.ts`
- Modify: `src/AiObservatory.Web/src/config/providers.test.ts`
- Modify: `src/AiObservatory.Web/src/components/BudgetRulesPanel.tsx`
- Modify: `src/AiObservatory.Web/src/components/DateRangePicker.tsx`
- Modify: `src/AiObservatory.Web/src/components/ProjectTreemap.tsx`
- Modify: `src/AiObservatory.Web/src/pages/Dashboard.tsx`
- Modify: affected frontend snapshots/tests only where copy or accessible names intentionally changed.

**Interfaces:**
- Consumes: canonical design tokens/primitives from Task 4 and the financial UI from Tasks 1-3.
- Produces: one coherent desktop/mobile and light/dark presentation for Overview, Adversarial Review, Reporting, Activity, GitHub, and Spend.
- Preserves: dynamic inline widths and Recharts style objects where values are data-driven or library API inputs.

- [ ] **Step 1: Add provider-palette tests before changing the registry**

Update `providers.test.ts` to assert every provider badge background derives from its own CSS variable rather than a duplicated RGB literal:

```ts
for (const provider of PROVIDERS) {
  expect(provider.badgeStyle.color).toBe(provider.colorVar)
  expect(provider.badgeStyle.background).toBe(`color-mix(in srgb, ${provider.colorVar} 12%, transparent)`)
}
```

Run:

```powershell
npm test -- --run src/config/providers.test.ts
```

Expected: FAIL because the registry currently hard-codes `rgba(...)` values.

- [ ] **Step 2: Normalize the app-local colour vocabulary**

Change each badge background to `color-mix(in srgb, <colorVar> 12%, transparent)`. Move `ProjectTreemap`'s eight raw colours into `index.css` as `--project-1` through `--project-8`, add an app-local `--project-other` in both themes, and consume only those variables in the component.

Replace the adversarial badge literals with semantic tokens:

```css
.adv-run__badge--ok { background: var(--ok-bg); color: var(--ok-text); }
.adv-run__badge--warn { background: var(--warn-bg); color: var(--warn-text); }
```

Remove fallback hex values from active component CSS where the canonical token is guaranteed to exist. SVG identity colours in `BrandWordmark` are explicitly exempt.

- [ ] **Step 3: Remove obsolete token bridges and normalize CSS values**

Because canonical 0.8.1 supplies font, dedicated chrome surfaces, brand contrast/background/ring roles, radius/motion roles, stronger borders, sidebar, flow, code, and corrected dark warning tokens:

- remove app-local redefinitions of canonical tokens;
- keep only Observatory spacing, motion, radius aliases, provider colours, project colours, and spend-category colours;
- replace brand-coloured text with `--brand-text`;
- replace one-off radii with `--r-control`, `--r-chip`, or `--r-panel`;
- map interface type to the fixed 10/11/12/13/14/16/18/20/24 px scale;
- keep 8/9 px only for existing micro labels that remain legible and are not interactive;
- keep borders as depth; retain shadows only for the info/date popovers and existing modal surfaces;
- add `::placeholder { color: var(--text-faint); opacity: 1; }` through the canonical rule rather than a local override.

Run these audits after editing:

```powershell
rg -n "var\(--brand\)" src/AiObservatory.Web/src/index.css
```

Review each match: fills/borders may remain; text colour must use `--brand-text`.

```powershell
rg -n "box-shadow" src/AiObservatory.Web/src
```

Expected: only floating popover/modal rules and the documented destructive halo.

- [ ] **Step 4: Convert static inline layout styles into named classes**

Move the static styles in `BudgetRulesPanel` and `DateRangePicker` into these selectors in `index.css`:

```text
.budget-rules__header
.budget-rules__title-row
.budget-rules__channel
.budget-rules__body
.budget-rules__table
.budget-rules__actions
.budget-rules__form-grid
.budget-rules__field
.budget-rules__control
.budget-rules__history
.date-range
.date-range__popover
.date-range__field
.date-range__input
```

Use the canonical control border/focus treatment and dense type scale. Leave only Recharts style objects, screen-reader-only objects, data-driven bar widths/colours, and computed popover placement inline.

- [ ] **Step 5: Normalize shell and page hierarchy**

Apply the following concrete layout rules in `index.css` and matching class names in `Dashboard.tsx`:

- Header/footer use the canonical app-header/site-footer border and typography language while preserving the current wordmark, descriptor, and theme toggle.
- Tabs remain a semantic roving `tablist`; active uses brand text/border, inactive uses muted text, and overflow scrolls horizontally on mobile.
- All page content shares one `1200px` max-width container and the same horizontal gutters.
- Panels use `--card-bg`, one `--border`, `--r-panel`, and the same internal padding.
- Panel headings use the 12 px semibold/uppercase tracking tier; primary figures remain mono.
- Tables share header, row-divider, numeric-alignment, hover, overflow, and mobile-scroll rules.
- Charts share one 160/200 px plotting rhythm, tokenized tooltip surface, and compact legends.
- Dialogs use the canonical overlay, panel radius, focus treatment, and one floating shadow.
- Empty/loading/error states use neutral, brand, and bad semantics respectively.
- `insights-older > summary` is keyboard-visible, uses brand-text on hover/focus, and adds no card shadow.

- [ ] **Step 6: Exercise all six tabs at four visual states**

Start the API and Vite app using the existing development commands. Inspect each tab at:

```text
Desktop light: 1440 × 1000
Desktop dark:  1440 × 1000
Mobile light:   390 × 844
Mobile dark:    390 × 844
```

For each state verify no horizontal page overflow, clipped controls, unreadable muted text, provider colour used as status/progress, inconsistent panel radius, stray shadow, or inaccessible focus ring. Record one screenshot per tab/theme/width under the external audit directory, not in Git:

```text
web-audit-output/fixportal-observatory/screenshots/batch10/
```

- [ ] **Step 7: Run frontend checks and commit**

Run from `src/AiObservatory.Web`:

```powershell
npm run lint
```

```powershell
npm test
```

```powershell
npm run build
```

Expected: all PASS.

```powershell
git add src/AiObservatory.Web/src
```

```powershell
git commit -m "Conform the Observatory UI to FixPortal design"
```

---

### Task 5b: Resynchronize the vendored UI with `@fixportal/design` 0.8.1

The 0.8.1 snapshot supersedes the 0.7.0 provenance in Tasks 4-5 without changing
their product semantics. Replace `tokens.css`, `components.css`, and `Button.tsx`
byte-for-byte from clean tag `v0.8.1` / commit `6b3e3e0`; hash-check the other
already-vendored primitives. Consume the new header/footer surfaces,
`--brand-contrast`, `--brand-ring`, canonical radius roles, and
`--transition-base` through app-local role aliases rather than copied values.

Review newly exported primitives against real call sites under the Ponytail rule:
vendor only one that removes an existing implementation while preserving its full
behaviour and accessibility. In particular, the Dashboard's current tablist accepts
ArrowUp/ArrowDown aliases; canonical 0.8.1 `Tabs` handles horizontal arrows plus
Home/End but omits those existing aliases, so adopting it would lose keyboard
behaviour. Keep the local tablist and do not vendor unused primitives.

The three Observatory edit dialogs also remain app-local native `<dialog>` implementations.
Canonical 0.8.1 `Modal` improves native-close synchronization, but its fixed 420 px panel,
unbounded viewport height, and non-sticky header would regress the existing 560 px,
viewport-bounded, sticky-header subscription and spend forms. Do not vendor it until the
canonical primitive can preserve those live behaviours without app-specific overrides.

No CSS/source-string change-detector test is added. Prove the resync with canonical
blob hashes, frontend lint/full tests/build, `git diff --check`, and the full
six-tab × desktop/mobile × light/dark rendered matrix because the canonical canvas,
chrome, brand, text, focus, radius, and motion values affect the entire shell.

---

### Task 6: Make optional Key Vault references genuinely optional

**Files:**
- Modify: `infra/main.bicep`
- Modify: `infra/modules/ingest.bicep`
- Modify: `docs/provider-setup.md`

**Interfaces:**
- Produces: optional `anthropicBillingSecretName` and `copilotOrgSecretName` Bicep parameters, default `''`.
- Consumes: a Key Vault secret name only when explicitly supplied; required DB/GitHub/activity settings remain unchanged.

- [ ] **Step 1: Add empty-default parameters and conditional app settings**

Add to `main.bicep` and pass through to the ingest module:

```bicep
param anthropicBillingSecretName string = ''
param copilotOrgSecretName string = ''
```

Add the same parameters to `ingest.bicep`, then build the setting list from required and optional arrays:

```bicep
var requiredAppSettings = [
  { name: 'DB_CONNECTION', value: '@Microsoft.KeyVault(VaultName=${kvName};SecretName=db-connection)' }
  { name: 'GITHUB_TOKEN', value: '@Microsoft.KeyVault(VaultName=${kvName};SecretName=github-token)' }
  { name: 'Ingest__GitHubRepoAllowlist', value: '@Microsoft.KeyVault(VaultName=${kvName};SecretName=github-repo-allowlist)' }
  { name: 'GOOGLE_BILLING_ACCOUNT_ID', value: '@Microsoft.KeyVault(VaultName=${kvName};SecretName=google-billing-account-id)' }
  { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: aiConnectionString }
]

var optionalAppSettings = concat(
  empty(anthropicBillingSecretName) ? [] : [
    { name: 'ANTHROPIC_BILLING_KEY', value: '@Microsoft.KeyVault(VaultName=${kvName};SecretName=${anthropicBillingSecretName})' }
  ],
  empty(copilotOrgSecretName) ? [] : [
    { name: 'COPILOT_ORG', value: '@Microsoft.KeyVault(VaultName=${kvName};SecretName=${copilotOrgSecretName})' }
  ]
)
```

Set `siteConfig.appSettings` to `concat(requiredAppSettings, optionalAppSettings)`. Default deployment therefore omits both currently broken references and Azure replaces the app-setting collection without touching the real Key Vault secrets.

- [ ] **Step 2: Document exact enablement commands without credentials**

In `provider-setup.md`, state that the default FixPortal Bicep deployment omits the two optional references. Document parameter names and secret-name examples:

```powershell
az deployment group create -g fpaiobs-rg -f infra/main.bicep -p anthropicBillingSecretName=anthropic-billing-key
```

```powershell
az deployment group create -g fpaiobs-rg -f infra/main.bicep -p copilotOrgSecretName=copilot-org
```

Do not document secret values or suggest reusing `anthropic-api-key` / `github-billing-org`.

- [ ] **Step 3: Compile the infrastructure and inspect emitted settings**

Run:

```powershell
az bicep build --file infra/main.bicep --outfile infra/main.json
```

Inspect `infra/main.json` to confirm both parameters default empty and both app-setting objects sit behind conditions. Remove the generated `infra/main.json` after inspection; it is a build artifact and is not committed.

Run:

```powershell
git diff --check
```

Expected: no whitespace errors.

- [ ] **Step 4: Commit the optional-configuration fix**

```powershell
git add infra/main.bicep infra/modules/ingest.bicep docs/provider-setup.md
```

```powershell
git commit -m "Omit unconfigured provider secret references"
```

---

### Task 7: Run the complete release gate and one-push PR workflow

**Files:**
- Modify only files required to fix failures caused by Tasks 1-6.
- Do not fold unrelated React Doctor maintainability suggestions into this PR.

**Interfaces:**
- Consumes: the completed batch-10 commits.
- Produces: one pushed branch and one HIGH-tier PR with CI, Gitar, and CodeRabbit evidence.

- [ ] **Step 1: Restore and format-check backend code**

```powershell
dotnet tool restore
```

```powershell
dotnet csharpier check .
```

```powershell
dotnet format AiObservatory.slnx analyzers --verify-no-changes --no-restore
```

Expected: all PASS. If CSharpier reports files, run `dotnet csharpier format .`, rerun the check, and commit only that mechanical formatting with the task whose code it formats.

- [ ] **Step 2: Build and run the complete .NET suite against PostgreSQL**

Ensure the existing local PostgreSQL test service is running and `TEST_DB_CONNECTION` points to it, then run:

```powershell
dotnet build AiObservatory.slnx --configuration Release
```

```powershell
dotnet test --solution AiObservatory.slnx --configuration Release --no-build --timeout 5m
```

Expected: build has 0 warnings/errors and every test passes.

- [ ] **Step 3: Run the complete frontend and client gates**

From `src/AiObservatory.Web`:

```powershell
npm run lint
```

```powershell
npm test
```

```powershell
npm run build
```

From the repository root:

```powershell
node --test clients/observatory-sweep.test.mjs
```

Expected: all 38 collector tests continue to pass.

- [ ] **Step 4: Run vulnerability and generated-diff checks**

```powershell
npm audit --audit-level=high
```

Run from `src/AiObservatory.Web`; expected: 0 vulnerabilities.

```powershell
dotnet list AiObservatory.slnx package --vulnerable --include-transitive
```

Expected: no vulnerable packages.

```powershell
git diff --check origin/main...HEAD
```

```powershell
git status --short
```

Expected: no whitespace errors and no uncommitted files.

- [ ] **Step 5: Confirm risk tier from committed policy and push once**

The changed migration and `infra/**` paths match `.claude/review-policy.json` HIGH globs, so this PR requires CI, Gitar, and CodeRabbit. Re-read the committed policy before pushing; do not override it.

```powershell
git push -u origin reviewer-findings-batch10
```

This is the branch's only pre-PR push.

- [ ] **Step 6: Open the single PR and request routine review**

Create one PR whose body lists FAO-009, FAO-010, and FAO-011; the local/backend/frontend gates; the 24-state visual matrix; the GBP migration semantics; and the optional-secret deployment effect.

Immediately request Gitar once:

```powershell
$batch10Pr = gh pr view reviewer-findings-batch10 --json number --jq .number
```

```powershell
gh pr comment $batch10Pr --body "Gitar review"
```

CodeRabbit should run from the HIGH-tier PR. Do not request another CodeRabbit review. If review fixes are required, commit them locally, rerun the full affected gates, push once more only after the branch is finished, then request Gitar again. Resolve CodeRabbit threads with dispositions rather than spending a manual re-review.

- [ ] **Step 7: Merge only when every required gate is satisfied**

Confirm CI Gate succeeds, Gitar has an actual verdict, CodeRabbit's comment has been read, and `reviewDecision` is not `CHANGES_REQUESTED`. Rebase-merge the PR; never squash or create a merge commit.

---

### Task 8: Deploy infrastructure/application and verify production

**Files:**
- No repository changes unless production exposes a regression caused by this PR.

**Interfaces:**
- Consumes: rebased batch-10 commits on `main`.
- Produces: healthy API, ingest worker, web app, resolved required references, absent optional broken references, and truthful live UI.

- [ ] **Step 1: Refresh the primary checkout after rebase merge**

From `<repo root>`:

```powershell
git switch main
```

```powershell
git pull --ff-only
```

Verify the rebased commit titles match the batch-10 titles before deleting the local feature branch after its remote auto-deletes.

- [ ] **Step 2: Apply the Bicep default that removes broken optional references**

Dispatch the `Infra` workflow on `main` once and wait for success:

```powershell
gh workflow run Infra --ref main
```

Do not pass optional secret-name parameters: production does not currently contain those two secrets.

- [ ] **Step 3: Wait for the normal main deployment and health gates**

Monitor the main-branch CI and `Deploy` workflows. The API migration runs with ingest stopped; the workflow must restart ingest and receive HTTP 200 from `/healthz`. Do not manually deploy around a failed workflow.

- [ ] **Step 4: Verify optional and required App Service settings without returning values**

Query only names/statuses. Confirm `ANTHROPIC_BILLING_KEY` and `COPILOT_ORG` are absent from `fpaiobs-ingest`; confirm `DB_CONNECTION`, `GITHUB_TOKEN`, and `Ingest__GitHubRepoAllowlist` remain present and their Key Vault reference statuses are resolved. Never print setting values or credential-bearing URLs.

- [ ] **Step 5: Run the live authenticated product smoke**

Using the rotated read-only viewer key without exposing it in logs, verify:

- Overview billed spend remains near the ledger value and five insights render before disclosure.
- Reporting shows billed GBP cards/chart/vendor split and contains no legacy aggregate legend.
- Subscription cards say `Notional usage value` and explain that it is not money charged.
- Budget rules and alert history say GBP/billed spend.
- Adversarial Review, Activity, GitHub, and Spend still load.
- Light/dark and 1440×1000 / 390×844 states retain the approved layout.

Run Lighthouse against the production shell and require no regression from the baseline: Performance at least 95, Accessibility 100, Best Practices 100. SEO remains intentionally excluded by `noindex`.

- [ ] **Step 6: Close the audit findings and record evidence**

Update the external audit report and tracker:

```text
web-audit-output/fixportal-observatory/report.md
WEB-AUDIT-TRACKER.md
```

Mark FAO-009, FAO-010, and FAO-011 resolved only with the deployed commit, workflow run links, live screenshots, and the relevant automated test evidence.

The next product task is the requested first-principles walkthrough. The final cross-vendor adversarial review remains deferred until provider quotas recover and must use the dedicated `adversarial-review` skill.
