# Final Web Review Closeout Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the remaining web-audit backlog without adding complexity or changing application behavior or appearance.

**Architecture:** Preserve the existing React component structure. Make one semantic HTML correction, remove nondeterminism from a test fixture, tune SonarJS to retain only useful rules, and record evidence-backed dismissals in the existing findings ledger.

**Tech Stack:** React 19, TypeScript 6, ESLint 10 with SonarJS, Vitest 4, React Doctor 0.5.1, Vite 8.

## Global Constraints

- No visual change beyond the semantically equivalent tablist container.
- No new abstractions, dependencies, or speculative optimisation.
- Do not weaken correctness, accessibility, React Hooks, or unused-code rules.
- Update the findings ledger so no item remains ambiguously open.
- Open the PR ready for review, not as a draft.

---

### Task 1: Correct the dashboard tablist semantics

**Files:**
- Modify: `src/AiObservatory.Web/src/pages/Dashboard.tsx:83-101`

**Interfaces:**
- Consumes: the existing `page-nav` CSS class, button-based tabs, roving `tabIndex`, and `aria-controls`/`aria-labelledby` wiring.
- Produces: the same rendered tab widget with a neutral `<div role="tablist">` container instead of a navigation landmark.

- [ ] **Step 1: Install the locked frontend dependencies in the review worktree**

Run:

```powershell
npm ci
```

Working directory: `src/AiObservatory.Web`

Expected: exit 0 with dependencies matching `package-lock.json`.

- [ ] **Step 2: Capture the React Doctor baseline**

Run:

```powershell
npx --no-install react-doctor --verbose
```

Expected: five findings, including `Dashboard.tsx` reporting a noninteractive `<nav>` with `role="tablist"`.

- [ ] **Step 3: Replace only the tablist container element**

In `Dashboard.tsx`, change the opening tag:

```tsx
<nav className="page-nav" role="tablist" aria-label="Dashboard sections">
```

to:

```tsx
<div className="page-nav" role="tablist" aria-label="Dashboard sections">
```

Change the matching closing tag from `</nav>` to `</div>`.

Do not change CSS, tab buttons, keyboard handling, focus management, or panel ARIA attributes.

- [ ] **Step 4: Verify the accessibility finding is gone**

Run:

```powershell
npx --no-install react-doctor --verbose
```

Expected: four findings remain; no finding references `Dashboard.tsx` or the tablist container.

- [ ] **Step 5: Commit the semantic fix**

```powershell
git add src/AiObservatory.Web/src/pages/Dashboard.tsx
```

```powershell
git commit -m "fix(web): correct dashboard tablist semantics"
```

---

### Task 2: Calibrate lint and remove worthwhile residual warnings

**Files:**
- Modify: `src/AiObservatory.Web/eslint.config.js`
- Modify: `src/AiObservatory.Web/src/components/DateRangePicker.tsx:59`
- Modify: `src/AiObservatory.Web/src/components/InfoPopover.tsx:14`
- Modify: `src/AiObservatory.Web/src/components/adversarialReviewGrouping.test.ts:5-8`
- Modify: `src/AiObservatory.Web/src/lib/dateRange.test.ts:14-16`

**Interfaces:**
- Consumes: the current SonarJS recommended rules downgraded to warnings.
- Produces: zero lint warnings while retaining correctness, accessibility, Hooks, and unused-code checks.

- [ ] **Step 1: Confirm the lint baseline**

Run:

```powershell
npm run lint
```

Expected: exit 0 with 48 warnings and no errors.

- [ ] **Step 2: Disable only the rejected style rules**

Add these entries beside the existing SonarJS exceptions in `eslint.config.js`:

```js
'sonarjs/no-duplicate-string': 'off',          // local UI copy and test fixtures are clearer inline
'sonarjs/shorthand-property-grouping': 'off',  // property ordering is formatting, not correctness
'sonarjs/no-nested-conditional': 'off',        // concise JSX state rendering is idiomatic here
'sonarjs/max-union-size': 'off',               // literal unions are the intended TypeScript model
'sonarjs/elseif-without-else': 'off',          // guard-style keyboard handlers are already exhaustive
```

Do not disable `react-hooks/*`, `@typescript-eslint/no-unused-vars`, accessibility diagnostics, or the remaining SonarJS correctness rules.

- [ ] **Step 3: Make effect callbacks explicitly consistent**

In `DateRangePicker.tsx`, change:

```ts
if (!popoverOpen) return
```

to:

```ts
if (!popoverOpen) return undefined
```

In `InfoPopover.tsx`, change:

```ts
if (!open) return
```

to:

```ts
if (!open) return undefined
```

These retain the existing effect behavior while satisfying the useful inconsistent-return check.

- [ ] **Step 4: Make review fixture IDs deterministic**

Replace `Math.random()` with a fixed fixture value in `adversarialReviewGrouping.test.ts`:

```ts
function run(p: Partial<AdversarialReviewRun>): AdversarialReviewRun {
  return {
    id: 'test-run',
```

Keep the rest of the fixture unchanged.

- [ ] **Step 5: Use destructuring where it improves the date-range test**

In the first `useDateRange` test, replace the two property reads with:

```ts
const { to, from } = result.current
const diffDays = Math.round((to.getTime() - from.getTime()) / 86400000)
```

- [ ] **Step 6: Verify lint and focused tests**

Run:

```powershell
npm run lint
```

Expected: exit 0 with zero warnings and zero errors.

Run:

```powershell
npx vitest run src/components/adversarialReviewGrouping.test.ts src/lib/dateRange.test.ts
```

Expected: all focused tests pass.

- [ ] **Step 7: Commit the lint calibration**

```powershell
git add src/AiObservatory.Web/eslint.config.js src/AiObservatory.Web/src/components/DateRangePicker.tsx src/AiObservatory.Web/src/components/InfoPopover.tsx src/AiObservatory.Web/src/components/adversarialReviewGrouping.test.ts src/AiObservatory.Web/src/lib/dateRange.test.ts
```

```powershell
git commit -m "chore(web): calibrate static analysis"
```

---

### Task 3: Close the findings ledger and run the full gate

**Files:**
- Modify: `docs/ai-findings.md`

**Interfaces:**
- Consumes: the three existing `open` rows and the current React Doctor/lint results.
- Produces: a durable ledger with no open rows and explicit evidence for every dismissal.

- [ ] **Step 1: Close the three open AI Findings**

Change each existing `open` row to `dismissed` with reason `by-design` and retain the measured ceiling:

```markdown
| `src/AiObservatory.Web/src/components/SpendChart.tsx` — byDate useMemo includes mode dep: full reduce re-runs on chart toggle | dismissed | by-design | Data volume is small; splitting the memo would complicate or duplicate the aggregation shape without a measurable benefit. Reassess only if the chart dataset grows materially (batch4). | 2026-06-23 |
| `src/AiObservatory.Web/src/components/SubscriptionPanel.tsx:178` — aggregates.filter() inside .map(): O(n*m) | dismissed | by-design | Provider cardinality is bounded at five; indexing the data adds state and code without a measurable benefit. Reassess if provider cardinality grows materially (batch4). | 2026-06-23 |
| `src/AiObservatory.Web/src/components/*.tsx` — .sort() on fresh arrays vs .toSorted() idiom (BudgetRulesPanel, ModelBreakdown, SpendChart, SubscriptionPanel) | dismissed | by-design | Every flagged array is newly created and unshared, so local mutation is safe. A conversion would be style-only churn (batch4). | 2026-06-23 |
```

- [ ] **Step 2: Record the final React Doctor and lint dispositions**

Append ledger rows for:

```markdown
| `src/AiObservatory.Web/src/pages/Dashboard.tsx` — react-doctor/noninteractive-element-to-interactive-role: `<nav role="tablist">` | fixed | | Replaced the navigation landmark with a neutral `<div role="tablist">`; existing tab keyboard and ARIA wiring are unchanged (batch4). | 2026-07-17 |
| `src/AiObservatory.Web/src/components/adversarialReviewGrouping.ts:82-96` — react-doctor/no-find-in-loop (3 occurrences) | dismissed | by-design | Each run contains a bounded review panel of roughly four participants, so the inner scans keep total work linear in input records. A combined accumulator would make the grouping code harder to read for no practical gain (batch4). | 2026-07-17 |
| `src/AiObservatory.Web/src/components/AdversarialReviewPanel.tsx:114` — react-doctor/jsx-no-jsx-as-prop | dismissed | false-positive | `CollapsiblePanel` is not memoized and intentionally accepts `ReactNode`; changing the API or hoisting per-run JSX would not avoid a render (batch4). | 2026-07-17 |
| `src/AiObservatory.Web` — 48 SonarJS lint warnings | fixed | | Disabled five style-only rules that encouraged constants, branches, or indirection; fixed the remaining deterministic-fixture, destructuring, and effect-return warnings. Lint now reports zero warnings without weakening correctness, Hooks, accessibility, or unused-code checks (batch4). | 2026-07-17 |
```

- [ ] **Step 3: Verify the ledger has no open rows**

Run:

```powershell
rg -n "\| open \|" docs/ai-findings.md
```

Expected: exit 1 with no matches.

- [ ] **Step 4: Run the complete frontend gate**

Run each command separately from `src/AiObservatory.Web`:

```powershell
npm run lint
```

Expected: zero warnings and zero errors.

```powershell
npm test
```

Expected: all tests pass.

```powershell
npm run build
```

Expected: TypeScript and Vite production build succeed.

```powershell
npx --no-install react-doctor --verbose
```

Expected: four documented findings remain: three bounded `find()` scans and one non-actionable JSX-prop heuristic. No accessibility finding remains.

- [ ] **Step 5: Check the final diff**

Run:

```powershell
git diff --check origin/main...HEAD
```

Expected: exit 0 with no whitespace errors.

Run:

```powershell
git status --short
```

Expected: only `docs/ai-findings.md` is uncommitted.

- [ ] **Step 6: Commit the closeout ledger**

```powershell
git add docs/ai-findings.md
```

```powershell
git commit -m "docs(web): close final audit findings"
```

- [ ] **Step 7: Re-run the final status check**

Run:

```powershell
git status --short --branch
```

Expected: clean `reviewer-findings-batch4` worktree ahead of `origin/main` by five commits, including the committed design specification and implementation plan.

---

### Task 4: Review and publish the ready PR

**Files:**
- No source changes expected; any reviewer-approved correction must repeat the relevant focused and full verification from Tasks 1-3.

**Interfaces:**
- Consumes: the clean, verified `reviewer-findings-batch4` branch.
- Produces: one non-draft GitHub PR targeting `main`, with the required Gitar review request.

- [ ] **Step 1: Review the complete branch diff**

Run:

```powershell
git diff --stat origin/main...HEAD
```

Expected: only the approved specification, implementation plan, five frontend files, dashboard markup, and findings ledger are present.

Run:

```powershell
git diff origin/main...HEAD
```

Expected: no behavior, visual styling, dependency, or unrelated changes.

- [ ] **Step 2: Run the required code-review and quality-gate workflows**

Use `superpowers:requesting-code-review` for the branch diff, address only technically valid findings, then use `quality-gate-review` for the merge verdict. If any code changes, re-run the complete frontend gate from Task 3 before continuing.

- [ ] **Step 3: Push the review branch**

```powershell
git push -u origin reviewer-findings-batch4
```

Expected: branch publishes successfully and tracks `origin/reviewer-findings-batch4`.

- [ ] **Step 4: Open the PR ready for review**

```powershell
gh pr create --base main --head reviewer-findings-batch4 --title "chore(web): close final audit findings" --body "## Summary`n- correct the dashboard tablist semantics`n- calibrate noisy SonarJS rules and make residual fixes`n- close remaining AI and React Doctor findings with evidence`n`n## Verification`n- npm run lint`n- npm test`n- npm run build`n- npx --no-install react-doctor --verbose"
```

Expected: a PR URL. The command deliberately omits `--draft`.

- [ ] **Step 5: Request the required Gitar review**

```powershell
gh pr comment --body "Gitar review"
```

Expected: comment body is exactly `Gitar review`.

- [ ] **Step 6: Verify PR state**

```powershell
gh pr view --json isDraft,state,url
```

Expected: `isDraft` is `false`, `state` is `OPEN`, and `url` is populated.
