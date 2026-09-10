# Reporting Tab Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a "Reporting" third tab to the AI Observatory dashboard with date-range spend exploration, burn-rate projection, and budget rule management UI.

**Architecture:** The `/api/aggregates?from=&to=` endpoint already handles date ranges; this is mostly frontend. Backend gaps: wire BudgetAlertService into the worker, add webhook delivery, add webhook-status endpoint. Frontend: new ReportingPage + supporting components/hooks.

**Tech Stack:** ASP.NET Core .NET 10 Minimal APIs, EF Core + PostgreSQL, NodaTime; React 19 + Vite + TypeScript, react-query, recharts (lazy-loaded), xUnit v3 + NSubstitute + AwesomeAssertions

## Global Constraints

- Tab label: exactly `'reporting'` (DashboardTab union type)
- `'Reporting'` in the nav button text (capitalised, no emoji)
- Custom date range UI: Calendar popover (absolutely-positioned div containing two `<input type="date">` — no date-picker library)
- Alert history: filter existing Anomaly insights by title prefix `'Budget alert:'` — no new DB table
- Webhook URL never returned from API — only `{ configured: bool }` from `GET /api/budget-rules/webhook-status`
- `IAlertNotifier` interface + `WebhookAlertNotifier` — no-op when `BUDGET_ALERT_WEBHOOK_URL` env var absent
- Overview tab and its 31-day window unchanged
- SpendChart and ProviderSplit get optional `from`/`to` props, defaulting to existing 31-day behaviour when absent
- Burn-rate: `(totalSpend / daysInRange) * 30`; suppress (return null) when fewer than 3 data days
- xUnit v3, NSubstitute, AwesomeAssertions (namespace `AwesomeAssertions`, NOT `FluentAssertions`)
- No emoji anywhere — not in UI copy, comments, or commit messages
- Feature branch: `feat/reporting-tab`; commit frequently

---

### Task 1: Backend — IAlertNotifier + WebhookAlertNotifier

**Files:**
- Create: `src/AiObservatory.Api/Services/IAlertNotifier.cs`
- Create: `src/AiObservatory.Api/Services/WebhookAlertNotifier.cs`
- Modify: `src/AiObservatory.Api/Services/BudgetAlertService.cs`
- Modify: `src/AiObservatory.Api/Program.cs`
- Create: `tests/AiObservatory.Api.Tests/Services/WebhookAlertNotifierTests.cs`
- Modify: `tests/AiObservatory.Api.Tests/Services/BudgetAlertServiceTests.cs`

**Interfaces:**
- Produces: `IAlertNotifier` with `Task NotifyAsync(BudgetAlertPayload payload, CancellationToken ct)` — used by Task 2
- Produces: `BudgetAlertPayload` record: `string Provider, string Period, decimal ThresholdUsd, decimal ActualSpend, DateTimeOffset TriggeredAt`

**Context:**
- `BudgetAlertService` in `src/AiObservatory.Api/Services/BudgetAlertService.cs` — already creates Anomaly insight when rule fires, but has no notifier. Read this file first.
- `Program.cs` uses `builder.Services.Add*` pattern. `IHttpClientFactory` is already registered (used elsewhere).
- `WebhookAlertNotifier` reads `BUDGET_ALERT_WEBHOOK_URL` from `IConfiguration` in its constructor. If absent or empty, `NotifyAsync` is a no-op. Otherwise it POSTs JSON to that URL using `IHttpClientFactory`.
- Test project is at `tests/AiObservatory.Api.Tests/`. Existing test for `BudgetAlertService` is at `tests/AiObservatory.Api.Tests/Services/BudgetAlertServiceTests.cs` — read it to understand the test pattern.

- [ ] **Step 1: Read BudgetAlertService.cs and BudgetAlertServiceTests.cs**
  Read both files to understand existing structure before writing anything.

- [ ] **Step 2: Create IAlertNotifier.cs**
  ```csharp
  namespace AiObservatory.Api.Services;

  public record BudgetAlertPayload(
      string Provider,
      string Period,
      decimal ThresholdUsd,
      decimal ActualSpend,
      DateTimeOffset TriggeredAt);

  public interface IAlertNotifier
  {
      Task NotifyAsync(BudgetAlertPayload payload, CancellationToken ct = default);
  }
  ```

- [ ] **Step 3: Write failing test for WebhookAlertNotifier (no-op case)**
  In `tests/AiObservatory.Api.Tests/Services/WebhookAlertNotifierTests.cs`:
  - Test: when `BUDGET_ALERT_WEBHOOK_URL` config is absent, `NotifyAsync` completes without making any HTTP call (verify via mock `IHttpMessageHandler` / NSubstitute on `HttpMessageHandler`).

- [ ] **Step 4: Create WebhookAlertNotifier.cs**
  ```csharp
  namespace AiObservatory.Api.Services;

  public sealed class WebhookAlertNotifier(IHttpClientFactory httpClientFactory, IConfiguration config)
      : IAlertNotifier
  {
      public async Task NotifyAsync(BudgetAlertPayload payload, CancellationToken ct = default)
      {
          var url = config["BUDGET_ALERT_WEBHOOK_URL"];
          if (string.IsNullOrEmpty(url)) return;
          var client = httpClientFactory.CreateClient();
          await client.PostAsJsonAsync(url, payload, ct);
      }
  }
  ```

- [ ] **Step 5: Write second test — POST fires when URL is configured**
  Create a fake `HttpMessageHandler` that captures the request. Assert it's called with the right URL and that the JSON body contains the provider, period, threshold.

- [ ] **Step 6: Run new tests to verify they pass**
  Run: `dotnet test tests/AiObservatory.Api.Tests/AiObservatory.Api.Tests.csproj --filter "FullyQualifiedName~WebhookAlertNotifier" -v n`
  Expected: 2 passing

- [ ] **Step 7: Inject IAlertNotifier into BudgetAlertService**
  Read `BudgetAlertService.cs`. Add `IAlertNotifier _notifier` constructor parameter. After creating the insight (the existing call to `_usageRepo.SaveInsightAsync` or similar), call:
  ```csharp
  await _notifier.NotifyAsync(new BudgetAlertPayload(
      rule.Provider ?? "all", rule.Period, rule.ThresholdUsd, actualSpend, DateTimeOffset.UtcNow), ct);
  ```

- [ ] **Step 8: Update BudgetAlertServiceTests.cs**
  Add `var notifier = Substitute.For<IAlertNotifier>();` and pass it to the constructor. Add assertion: `await notifier.Received(1).NotifyAsync(Arg.Is<BudgetAlertPayload>(p => p.ThresholdUsd == rule.ThresholdUsd), Arg.Any<CancellationToken>())`.

- [ ] **Step 9: Run BudgetAlertService tests**
  Run: `dotnet test tests/AiObservatory.Api.Tests/AiObservatory.Api.Tests.csproj --filter "FullyQualifiedName~BudgetAlertService" -v n`
  Expected: all pass

- [ ] **Step 10: Register in Program.cs**
  Add `builder.Services.AddSingleton<IAlertNotifier, WebhookAlertNotifier>();` in the services section.

- [ ] **Step 11: Build to verify**
  Run: `dotnet build src/AiObservatory.Api/AiObservatory.Api.csproj -c Release --no-restore`
  Expected: Build succeeded, 0 errors

- [ ] **Step 12: Commit**
  ```
  feat: add IAlertNotifier + WebhookAlertNotifier, wire into BudgetAlertService
  ```

---

### Task 2: Backend — Wire BudgetAlertService into IntelligenceWorkerService + webhook-status endpoint

**Files:**
- Modify: `src/AiObservatory.Api/Services/Intelligence/IntelligenceWorkerService.cs`
- Modify: `src/AiObservatory.Api/Endpoints/BudgetRulesEndpoints.cs`

**Interfaces:**
- Consumes: `BudgetAlertService.CheckAndAlertAsync(CancellationToken)` (from Task 1 — read the service to find the exact method name)

**Context:**
- `IntelligenceWorkerService` is a BackgroundService. Read it to understand where to add the budget check call (after the insights loop, before sleeping until next midnight).
- Use `IServiceScopeFactory` to resolve `BudgetAlertService` (it's scoped). The pattern is already used in the worker — look for existing scope usage.
- `BudgetRulesEndpoints.cs` already has endpoints for CRUD. Add one more:
  `GET /api/budget-rules/webhook-status` → returns `Results.Ok(new { configured = !string.IsNullOrEmpty(config["BUDGET_ALERT_WEBHOOK_URL"]) })`
- No tests needed for the worker wiring (it's integration-level); the endpoint is simple enough to verify by build.

- [ ] **Step 1: Read IntelligenceWorkerService.cs and BudgetRulesEndpoints.cs**
  Understand the existing structure before modifying.

- [ ] **Step 2: Add RunBudgetCheckAsync to IntelligenceWorkerService**
  ```csharp
  private async Task RunBudgetCheckAsync(CancellationToken ct)
  {
      await using var scope = _scopeFactory.CreateAsyncScope();
      var svc = scope.ServiceProvider.GetRequiredService<BudgetAlertService>();
      await svc.CheckAndAlertAsync(ct);
  }
  ```
  Call `await RunBudgetCheckAsync(ct);` after the insights catch-up loop runs.

- [ ] **Step 3: Add webhook-status endpoint to BudgetRulesEndpoints.cs**
  After the existing endpoint registrations:
  ```csharp
  group.MapGet("/webhook-status", (IConfiguration config) =>
      Results.Ok(new { configured = !string.IsNullOrEmpty(config["BUDGET_ALERT_WEBHOOK_URL"]) }));
  ```

- [ ] **Step 4: Build to verify**
  Run: `dotnet build src/AiObservatory.Api/AiObservatory.Api.csproj -c Release --no-restore`
  Expected: Build succeeded, 0 errors

- [ ] **Step 5: Commit**
  ```
  feat: wire BudgetAlertService into worker, add webhook-status endpoint
  ```

---

### Task 3: Frontend — velocity.ts + dateRange.ts utilities with tests

**Files:**
- Create: `src/AiObservatory.Web/src/lib/velocity.ts`
- Create: `src/AiObservatory.Web/src/lib/velocity.test.ts`
- Create: `src/AiObservatory.Web/src/lib/dateRange.ts`
- Create: `src/AiObservatory.Web/src/lib/dateRange.test.ts`

**Interfaces:**
- Produces: `computeBurnRate(aggregates: DailyAggregate[], daysInRange: number): { dailyAvgUsd: number; projectedMonthlyUsd: number } | null`
  - Returns null if fewer than 3 distinct dates have data (i.e., if the number of unique dates with totalSpend > 0 is < 3)
  - `projectedMonthlyUsd = (totalSpend / daysInRange) * 30`
- Produces: `useDateRange()` hook returning `{ from: Date; to: Date; preset: 7 | 31 | 90 | 'custom'; setPreset: (days: 7 | 31 | 90) => void; setCustom: (from: Date, to: Date) => void }`
  - Default preset: 31 (same as Overview's window)
  - Preset: to = today, from = today - (preset - 1) days

**Context:**
- `DailyAggregate` type is in `src/AiObservatory.Web/src/api/client.ts` — import from there
- Test files use Vitest (already in the project — check `package.json` for the test runner/setup)
- Tests run with: `npx vitest run src/AiObservatory.Web/src/lib/velocity.test.ts`

- [ ] **Step 1: Write velocity.test.ts (failing)**
  ```typescript
  import { describe, it, expect } from 'vitest'
  import { computeBurnRate } from './velocity'
  import type { DailyAggregate } from '../api/client'

  function makeAgg(date: string, costUsd: number): DailyAggregate {
    return { date, provider: 'anthropic', model: 'claude', inputTokens: 0, outputTokens: 0,
      cacheReadTokens: 0, cacheWriteTokens: 0, costUsd, requestCount: 1 }
  }

  describe('computeBurnRate', () => {
    it('returns null when fewer than 3 days have data', () => {
      expect(computeBurnRate([makeAgg('2026-06-01', 5)], 31)).toBeNull()
      expect(computeBurnRate([makeAgg('2026-06-01', 5), makeAgg('2026-06-02', 3)], 31)).toBeNull()
    })

    it('returns null for empty array', () => {
      expect(computeBurnRate([], 31)).toBeNull()
    })

    it('computes correctly for >= 3 data days', () => {
      const aggs = [
        makeAgg('2026-06-01', 4),
        makeAgg('2026-06-02', 6),
        makeAgg('2026-06-03', 2),
      ]
      const result = computeBurnRate(aggs, 31)
      expect(result).not.toBeNull()
      // totalSpend = 12, dailyAvg = 12/31
      expect(result!.dailyAvgUsd).toBeCloseTo(12 / 31)
      expect(result!.projectedMonthlyUsd).toBeCloseTo((12 / 31) * 30)
    })

    it('handles zero spend', () => {
      const aggs = [
        makeAgg('2026-06-01', 0),
        makeAgg('2026-06-02', 0),
        makeAgg('2026-06-03', 0),
      ]
      const result = computeBurnRate(aggs, 31)
      expect(result).not.toBeNull()
      expect(result!.dailyAvgUsd).toBe(0)
    })
  })
  ```

- [ ] **Step 2: Create velocity.ts**
  ```typescript
  import type { DailyAggregate } from '../api/client'

  export function computeBurnRate(
    aggregates: DailyAggregate[],
    daysInRange: number
  ): { dailyAvgUsd: number; projectedMonthlyUsd: number } | null {
    const datesWithData = new Set(aggregates.filter(a => a.costUsd > 0).map(a => a.date))
    if (datesWithData.size < 3) return null
    const totalSpend = aggregates.reduce((sum, a) => sum + a.costUsd, 0)
    const dailyAvgUsd = totalSpend / daysInRange
    return { dailyAvgUsd, projectedMonthlyUsd: dailyAvgUsd * 30 }
  }
  ```

- [ ] **Step 3: Run velocity tests**
  Run: `npx vitest run src/AiObservatory.Web/src/lib/velocity.test.ts` (from `src/AiObservatory.Web/`)
  Expected: 4 passing

- [ ] **Step 4: Write dateRange.test.ts (failing)**
  ```typescript
  import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
  import { renderHook, act } from '@testing-library/react'
  import { useDateRange } from './dateRange'

  describe('useDateRange', () => {
    const FIXED = new Date('2026-06-21T12:00:00Z')
    beforeEach(() => { vi.useFakeTimers(); vi.setSystemTime(FIXED) })
    afterEach(() => { vi.useRealTimers() })

    it('defaults to 31-day preset', () => {
      const { result } = renderHook(() => useDateRange())
      expect(result.current.preset).toBe(31)
      const to = result.current.to
      const from = result.current.from
      const diffDays = Math.round((to.getTime() - from.getTime()) / 86400000)
      expect(diffDays).toBe(30) // 31 days inclusive = 30 day difference
    })

    it('setPreset(7) sets a 7-day window', () => {
      const { result } = renderHook(() => useDateRange())
      act(() => result.current.setPreset(7))
      expect(result.current.preset).toBe(7)
      const diffDays = Math.round((result.current.to.getTime() - result.current.from.getTime()) / 86400000)
      expect(diffDays).toBe(6)
    })

    it('setCustom changes to custom preset', () => {
      const { result } = renderHook(() => useDateRange())
      const from = new Date('2026-05-01')
      const to = new Date('2026-05-31')
      act(() => result.current.setCustom(from, to))
      expect(result.current.preset).toBe('custom')
      expect(result.current.from).toEqual(from)
      expect(result.current.to).toEqual(to)
    })
  })
  ```

- [ ] **Step 5: Create dateRange.ts**
  ```typescript
  import { useState, useCallback } from 'react'

  type Preset = 7 | 31 | 90

  function presetRange(days: Preset): { from: Date; to: Date } {
    const to = new Date()
    const from = new Date(to.getTime() - (days - 1) * 24 * 60 * 60 * 1000)
    return { from, to }
  }

  export function useDateRange() {
    const [preset, setPresetState] = useState<Preset | 'custom'>(31)
    const initial = presetRange(31)
    const [from, setFrom] = useState<Date>(initial.from)
    const [to, setTo] = useState<Date>(initial.to)

    const setPreset = useCallback((days: Preset) => {
      const range = presetRange(days)
      setPresetState(days)
      setFrom(range.from)
      setTo(range.to)
    }, [])

    const setCustom = useCallback((f: Date, t: Date) => {
      setPresetState('custom')
      setFrom(f)
      setTo(t)
    }, [])

    return { from, to, preset, setPreset, setCustom }
  }
  ```

- [ ] **Step 6: Run dateRange tests**
  Run: `npx vitest run src/AiObservatory.Web/src/lib/dateRange.test.ts` (from `src/AiObservatory.Web/`)
  Expected: 3 passing

- [ ] **Step 7: Run full test suite to check no regressions**
  Run: `npx vitest run` (from `src/AiObservatory.Web/`)
  Expected: all passing

- [ ] **Step 8: Commit**
  ```
  feat: add velocity and dateRange utility libs with tests
  ```

---

### Task 4: Frontend — API client + query hooks extensions

**Files:**
- Modify: `src/AiObservatory.Web/src/api/client.ts`
- Modify: `src/AiObservatory.Web/src/api/queries.ts`

**Interfaces:**
- Consumes: existing `getJson`, `request`, `jsonHeaders` helpers in `client.ts`
- Produces:
  - `BudgetRule`: `{ id: string; provider: string | null; period: 'daily' | 'weekly' | 'monthly'; thresholdUsd: number; lastTriggeredAt: string | null }`
  - `getBudgetRules(): Promise<BudgetRule[]>`
  - `createBudgetRule(body: Omit<BudgetRule, 'id' | 'lastTriggeredAt'>): Promise<BudgetRule>`
  - `deleteBudgetRule(id: string): Promise<void>`
  - `getWebhookStatus(): Promise<{ configured: boolean }>`
  - `useAggregates(from?: Date, to?: Date): DailyAggregate[]` — when args provided, uses that range; when absent, uses existing 31-day default (preserving existing Overview behaviour)
  - `useBudgetRules(): { rules: BudgetRule[]; isLoading: boolean; isError: boolean }`
  - `useWebhookStatus(): { configured: boolean | undefined }`

**Context:**
- Read `client.ts` and `queries.ts` before modifying — understand the existing `getJson`, `request`, `localDate` helpers
- `useAggregates` currently takes no args. Add an overload: when `from`/`to` are provided, pass them; when absent, use the existing `AGGREGATES_DAYS_RANGE` logic. Query key must include the dates so react-query caches them separately: `['aggregates', localDate(from), localDate(to)]` vs `['aggregates']` for the default.
- `useDashboardStatus` currently subscribes to `['aggregates']` — leave that unchanged so Overview still works.

- [ ] **Step 1: Read client.ts and queries.ts in full**

- [ ] **Step 2: Add BudgetRule type and client functions to client.ts**
  Add after the existing exports:
  ```typescript
  export interface BudgetRule {
    id: string
    provider: string | null
    period: 'daily' | 'weekly' | 'monthly'
    thresholdUsd: number
    lastTriggeredAt: string | null
  }

  export const getBudgetRules = () => getJson<BudgetRule[]>('/budget-rules')

  export const createBudgetRule = async (body: Omit<BudgetRule, 'id' | 'lastTriggeredAt'>): Promise<BudgetRule> => {
    const res = await request('/budget-rules', { method: 'POST', headers: jsonHeaders, body: JSON.stringify(body) })
    return res.json() as Promise<BudgetRule>
  }

  export const deleteBudgetRule = async (id: string): Promise<void> => {
    await request(`/budget-rules/${id}`, { method: 'DELETE' })
  }

  export const getWebhookStatus = () => getJson<{ configured: boolean }>('/budget-rules/webhook-status')
  ```

- [ ] **Step 3: Update useAggregates in queries.ts to accept optional from/to**
  Change the signature to `useAggregates(from?: Date, to?: Date): DailyAggregate[]`.
  When `from` and `to` are provided, use them and key as `['aggregates', localDate(from), localDate(to)]`.
  When absent, use the existing logic with key `['aggregates']`.

  ```typescript
  export function useAggregates(from?: Date, to?: Date): DailyAggregate[] {
    const hasRange = from != null && to != null
    const { data = [] } = useQuery({
      queryKey: hasRange ? ['aggregates', localDate(from!), localDate(to!)] : ['aggregates'],
      queryFn: hasRange
        ? () => getAggregates(localDate(from!), localDate(to!))
        : aggregatesQueryFn,
    })
    return data
  }
  ```

- [ ] **Step 4: Add useBudgetRules and useWebhookStatus to queries.ts**
  ```typescript
  export function useBudgetRules(): { rules: BudgetRule[]; isLoading: boolean; isError: boolean } {
    const { data = [], isPending, isError } = useQuery({ queryKey: ['budget-rules'], queryFn: getBudgetRules })
    return { rules: data, isLoading: isPending, isError }
  }

  export function useWebhookStatus(): { configured: boolean | undefined } {
    const { data } = useQuery({ queryKey: ['webhook-status'], queryFn: getWebhookStatus })
    return { configured: data?.configured }
  }
  ```

- [ ] **Step 5: Typecheck**
  Run: `npx tsc -b --noEmit` (from `src/AiObservatory.Web/`)
  Expected: 0 errors

- [ ] **Step 6: Commit**
  ```
  feat: extend API client and query hooks for budget rules, date ranges
  ```

---

### Task 5: Frontend — DateRangePicker + ReportingCards components

**Files:**
- Create: `src/AiObservatory.Web/src/components/DateRangePicker.tsx`
- Create: `src/AiObservatory.Web/src/components/ReportingCards.tsx`

**Interfaces:**
- Consumes: `useDateRange` hook from Task 3 (passed as props, not called internally — the page owns the hook)
- `DateRangePicker` props: `{ from: Date; to: Date; preset: 7 | 31 | 90 | 'custom'; onPreset: (days: 7 | 31 | 90) => void; onCustom: (from: Date, to: Date) => void }`
- `ReportingCards` props: `{ aggregates: DailyAggregate[]; daysInRange: number }`
- Consumes: `computeBurnRate` from `lib/velocity.ts`
- Consumes: `useUsdToGbp`, `formatGbp` from `lib/currency.ts` (check that these exist)
- Consumes: `formatInt` from `lib/format.ts`

**Context:**
- Look at `SummaryCards.tsx` for the card layout pattern (`.summary-cards`, `.card-label`, `.card-value`, `Card` component from `../design`)
- The "Custom" button opens a popover: a `<div>` positioned absolutely with two `<input type="date">` fields + Apply button. Use a `useState` for `open: boolean`. Clicking outside closes it (useEffect + document click listener, or just `onBlur` on the container).
- The 4 Reporting cards are distinct from Overview's 4 cards: Period spend, Daily avg, Projected/month, Top provider.
- For "Top provider": group aggregates by provider, sum costUsd, find max.

- [ ] **Step 1: Read SummaryCards.tsx and index.css (or relevant CSS file) for existing card class names and patterns**

- [ ] **Step 2: Create DateRangePicker.tsx**
  Preset pill buttons for 7, 31, 90. A "Custom" pill that toggles `popoverOpen`. When `popoverOpen`, render an absolutely-positioned div (positioned relative to the picker container) containing:
  - `<input type="date">` for From (value = from.toISOString().slice(0,10))
  - `<input type="date">` for To (value = to.toISOString().slice(0,10))
  - "Apply" button that calls `onCustom(new Date(fromStr), new Date(toStr))` and closes popover
  - Close popover on Escape key or click outside (add event listener in useEffect, remove on cleanup)

- [ ] **Step 3: Create ReportingCards.tsx**
  ```tsx
  import { useMemo } from 'react'
  import { Card } from '../design'
  import { computeBurnRate } from '../lib/velocity'
  import { useUsdToGbp, formatGbp } from '../lib/currency'
  import type { DailyAggregate } from '../api/client'

  interface Props { aggregates: DailyAggregate[]; daysInRange: number }

  export default function ReportingCards({ aggregates, daysInRange }: Props) {
    const rate = useUsdToGbp()
    const { totalSpend, topProvider } = useMemo(() => {
      let totalSpend = 0
      const providerSpend: Record<string, number> = {}
      for (const a of aggregates) {
        totalSpend += a.costUsd
        providerSpend[a.provider] = (providerSpend[a.provider] ?? 0) + a.costUsd
      }
      const topProvider = Object.entries(providerSpend).reduce<[string, number] | undefined>(
        (best, e) => best == null || e[1] > best[1] ? e : best, undefined)
      return { totalSpend, topProvider }
    }, [aggregates])

    const burnRate = computeBurnRate(aggregates, daysInRange)

    return (
      <div className="summary-cards">
        <Card>
          <div className="card-label">Period spend</div>
          <div className="card-value card-value--lead">{formatGbp(totalSpend, rate)}</div>
        </Card>
        <Card>
          <div className="card-label">Daily avg</div>
          <div className="card-value">{burnRate ? formatGbp(burnRate.dailyAvgUsd, rate) : '—'}</div>
        </Card>
        <Card>
          <div className="card-label">Projected / month</div>
          <div className="card-value">{burnRate ? formatGbp(burnRate.projectedMonthlyUsd, rate) : '—'}</div>
          {burnRate && <div className="card-sub">{formatGbp(burnRate.dailyAvgUsd, rate)}/day avg</div>}
        </Card>
        <Card>
          <div className="card-label">Top provider</div>
          <div className="card-value card-value--model">{topProvider?.[0] ?? '—'}</div>
          {topProvider && <div className="card-sub">{formatGbp(topProvider[1], rate)}</div>}
        </Card>
      </div>
    )
  }
  ```

- [ ] **Step 4: Typecheck**
  Run: `npx tsc -b --noEmit` (from `src/AiObservatory.Web/`)
  Expected: 0 errors

- [ ] **Step 5: Commit**
  ```
  feat: add DateRangePicker and ReportingCards components
  ```

---

### Task 6: Frontend — BudgetRulesPanel component

**Files:**
- Create: `src/AiObservatory.Web/src/components/BudgetRulesPanel.tsx`

**Interfaces:**
- Consumes: `useBudgetRules`, `useWebhookStatus`, `useInsights` from `api/queries.ts`
- Consumes: `createBudgetRule`, `deleteBudgetRule` from `api/client.ts`
- Consumes: `useQueryClient` from `@tanstack/react-query` for cache invalidation after mutations

**Context:**
- Alert history: filter insights where `insightType === 'anomaly'` AND `title.startsWith('Budget alert:')`. Show last 10, newest first. Read `InsightsFeed.tsx` for how insights are rendered for style reference.
- Add Rule panel: a `useState<boolean>` for `panelOpen`. When open, render a form panel (can be a sibling `<div>` styled as a side panel or inline form — keep it simple). Fields: provider (select: "All providers" = null, or "anthropic"/"copilot"/"google"/"openai"), period (select: "daily"/"weekly"/"monthly"), threshold (number input). On submit: call `createBudgetRule`, then `queryClient.invalidateQueries({ queryKey: ['budget-rules'] })`, close panel.
- Webhook status: show "Webhook: configured" (green) or "Webhook: not configured" (muted). Read from `useWebhookStatus()`.
- Delete: call `deleteBudgetRule(rule.id)`, then `queryClient.invalidateQueries({ queryKey: ['budget-rules'] })`.
- `BudgetRule.period` values are lowercase: `'daily'`, `'weekly'`, `'monthly'` — capitalise for display only.

- [ ] **Step 1: Read InsightsFeed.tsx for style reference, and look at SubscriptionPanel.tsx for the add/edit panel pattern**

- [ ] **Step 2: Create BudgetRulesPanel.tsx**
  Structure:
  ```
  <section>
    <div class="panel">
      <header row: "Budget Rules" title | webhook status pill | "+ Add rule" button>
      <table: rules (provider, period, threshold, last fired, delete)>
    </div>
    <div class="panel" (if panelOpen)>
      <form: provider select, period select, threshold input, Cancel/Add buttons>
    </div>
    <div class="panel">
      <header: "Alert History">
      <list: budget alert insights, newest first, max 10>
    </div>
  </section>
  ```
  Keep styling consistent with existing panels (`.panel`, `.panel-title` CSS classes).

- [ ] **Step 3: Typecheck**
  Run: `npx tsc -b --noEmit` (from `src/AiObservatory.Web/`)
  Expected: 0 errors

- [ ] **Step 4: Commit**
  ```
  feat: add BudgetRulesPanel component
  ```

---

### Task 7: Frontend — SpendChart + ProviderSplit optional from/to props

**Files:**
- Modify: `src/AiObservatory.Web/src/components/SpendChart.tsx`
- Modify: `src/AiObservatory.Web/src/components/ProviderSplit.tsx`

**Interfaces:**
- Both get new optional props: `from?: Date`, `to?: Date`
- When provided: call `useAggregates(from, to)`
- When absent: call `useAggregates()` (existing behaviour, zero changes to Overview)

**Context:**
- Read both files first. They call `useAggregates()` with no args today.
- The prop addition is the only change — all chart rendering logic stays identical.
- `daysInRange` for SpendChart's x-axis label might need updating: currently says "last 31 days". When from/to are provided, it can derive the range from the dates or just omit the label.

- [ ] **Step 1: Read SpendChart.tsx and ProviderSplit.tsx**

- [ ] **Step 2: Update SpendChart.tsx**
  Add `interface Props { from?: Date; to?: Date }`. Change `useAggregates()` call to `useAggregates(from, to)`. Update the "Daily spend — last 31 days" panel title label: if `from`/`to` present, show the date range; otherwise keep "last 31 days". (The panel title is in `Dashboard.tsx`, not in `SpendChart` itself — check and update appropriately.)

- [ ] **Step 3: Update ProviderSplit.tsx**
  Same pattern: add `Props`, pass to `useAggregates`.

- [ ] **Step 4: Typecheck**
  Run: `npx tsc -b --noEmit` (from `src/AiObservatory.Web/`)
  Expected: 0 errors

- [ ] **Step 5: Commit**
  ```
  feat: add optional from/to props to SpendChart and ProviderSplit
  ```

---

### Task 8: Frontend — ReportingPage + wire Dashboard.tsx

**Files:**
- Create: `src/AiObservatory.Web/src/pages/ReportingPage.tsx`
- Modify: `src/AiObservatory.Web/src/pages/Dashboard.tsx`

**Interfaces:**
- Consumes: `useDateRange` (Task 3), `useAggregates` (Task 4), `DateRangePicker` (Task 5), `ReportingCards` (Task 5), `BudgetRulesPanel` (Task 6), `SpendChart`/`ProviderSplit` (Task 7)
- `DashboardTab` type in Dashboard.tsx needs `'reporting'` added

**Context:**
- Read Dashboard.tsx. Current `DashboardTab = 'overview' | 'adversarial-review'`. Add `'reporting'`.
- Add third nav tab button with label "Reporting".
- Add `{tab === 'reporting' && <ReportingPage />}` branch after the adversarial-review branch.
- SpendChart and ProviderSplit are lazy-loaded in Dashboard — they can also be lazy in ReportingPage or just import directly (since they're already chunk-split by the lazy() calls in Dashboard; just import the existing lazy refs or create new ones).

- [ ] **Step 1: Create ReportingPage.tsx**
  ```tsx
  import { lazy, Suspense } from 'react'
  import DateRangePicker from '../components/DateRangePicker'
  import ReportingCards from '../components/ReportingCards'
  import BudgetRulesPanel from '../components/BudgetRulesPanel'
  import { useDateRange } from '../lib/dateRange'
  import { useAggregates, localDate } from '../api/queries'

  const SpendChart = lazy(() => import('../components/SpendChart'))
  const ProviderSplit = lazy(() => import('../components/ProviderSplit'))

  export default function ReportingPage() {
    const { from, to, preset, setPreset, setCustom } = useDateRange()
    const aggregates = useAggregates(from, to)
    const daysInRange = Math.max(1, Math.round((to.getTime() - from.getTime()) / 86400000) + 1)
    const rangeLabel = `${localDate(from)} to ${localDate(to)}`

    return (
      <div className="reporting-page">
        <div className="reporting-range-bar">
          <DateRangePicker from={from} to={to} preset={preset} onPreset={setPreset} onCustom={setCustom} />
          <span className="reporting-range-label">{rangeLabel}</span>
        </div>
        <ReportingCards aggregates={aggregates} daysInRange={daysInRange} />
        <div className="main-grid">
          <div className="panel">
            <div className="panel-title">Daily spend — {rangeLabel}</div>
            <Suspense fallback={<div className="chart-skeleton" />}>
              <SpendChart from={from} to={to} />
            </Suspense>
          </div>
          <div className="panel">
            <div className="panel-title">Provider split</div>
            <Suspense fallback={<div className="chart-skeleton" />}>
              <ProviderSplit from={from} to={to} />
            </Suspense>
          </div>
        </div>
        <BudgetRulesPanel />
      </div>
    )
  }
  ```

- [ ] **Step 2: Update Dashboard.tsx**
  - Change `type DashboardTab = 'overview' | 'adversarial-review'` to `type DashboardTab = 'overview' | 'adversarial-review' | 'reporting'`
  - Add nav button after "Adversarial Review":
    ```tsx
    <button
      type="button"
      className={`page-nav__tab${tab === 'reporting' ? ' page-nav__tab--active' : ''}`}
      onClick={() => setTab('reporting')}
    >
      Reporting
    </button>
    ```
  - Add branch after adversarial-review: `{tab === 'reporting' && <ReportingPage />}`
  - Add `import ReportingPage from './ReportingPage'`

- [ ] **Step 3: Add minimal CSS for reporting page layout**
  Check `index.css` for existing `.reporting-page`, `.reporting-range-bar` classes. If absent, add:
  ```css
  .reporting-page { display: flex; flex-direction: column; gap: var(--space-4); padding: 0 var(--page-pad); }
  .reporting-range-bar { display: flex; align-items: center; gap: var(--space-3); flex-wrap: wrap; padding: var(--space-3) 0; }
  .reporting-range-label { color: var(--text-muted); font-size: 0.85rem; }
  ```
  Add to the appropriate CSS file (check where `.main-grid` and `.page-nav` are defined — likely `index.css`).

- [ ] **Step 4: Typecheck**
  Run: `npx tsc -b --noEmit` (from `src/AiObservatory.Web/`)
  Expected: 0 errors

- [ ] **Step 5: Run full test suite**
  Run: `npx vitest run` (from `src/AiObservatory.Web/`)
  Expected: all passing

- [ ] **Step 6: Commit**
  ```
  feat: add ReportingPage and wire into Dashboard as third tab
  ```
