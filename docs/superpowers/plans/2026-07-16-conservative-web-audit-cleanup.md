---
title: Conservative Web Audit Cleanup Implementation Plan
date: 2026-07-16
status: accepted
---

# Conservative Web Audit Cleanup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use
> `superpowers:subagent-driven-development` (recommended) or
> `superpowers:executing-plans` to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove proven-unused frontend CSS and one duplicate lookup without
changing rendered output or behavior.

**Architecture:** Keep the existing design system, interaction components,
architecture tests, build configuration, and public interfaces. Delete only
unreferenced CSS declarations, then route provider-color lookup through the
existing provider configuration helper.

**Tech Stack:** React 19, TypeScript 6, Vite 8, Vitest 4, CSS custom properties.

## Global Constraints

- Preserve all rendered markup, behavior, accessibility semantics, and visual output.
- Do not change dependencies, architecture tests, Vite configuration, or unrelated findings.
- Work only on `reviewer-findings-batch3` in `.claude/worktrees/reviewer-passes`.
- Do not push, merge, or deploy.

---

### Task 1: Remove proven-dead design tokens

**Files:**
- Modify: `src/AiObservatory.Web/src/design/tokens.css`

**Interfaces:**
- Consumes: CSS custom-property consumers under `src/AiObservatory.Web/src`.
- Produces: The same live token and `@keyframes fpds-spin` contract with unused declarations removed.

- [ ] **Step 1: Reconfirm the candidate custom properties have no consumers**

Run the repository search used by the audit. For every removed property, expect
zero `var(--property-name)` matches outside its declarations. Confirm
`animation: fpds-spin` remains live in `src/index.css`.

- [ ] **Step 2: Delete only the proven-unused declarations**

Remove these custom properties from `tokens.css`:

```text
bad-solid
brand-soft
brand-soft-rgb
brand-tint
code-bg
code-text
flow-out
flow-out-soft
new-bg
new-border
new-text
side-buy-bg
side-buy-border
side-buy-text
side-sell-bg
side-sell-border
side-sell-text
sidebar-active-bg
sidebar-active-text
sidebar-badge-bg
sidebar-badge-text
sidebar-bg
sidebar-border
sidebar-hover-bg
sidebar-selected-bg
sidebar-text
sidebar-text-faint
sidebar-text-muted
warn-fill-deep
warn-fill-soft
```

Keep all custom properties that have a `var(...)` consumer.

- [ ] **Step 3: Remove only the unused animation utility**

Delete `.fpds-spin` and its reduced-motion override. Keep:

```css
@keyframes fpds-spin {
  to { transform: rotate(360deg); }
}
```

Replace the stale BrandAvatar comment with a short comment describing the live
loading-indicator use.

- [ ] **Step 4: Verify the CSS reference contract**

Run the custom-property search again. Expected: none of the removed property
names have `var(...)` consumers, and `src/index.css` still contains
`animation: fpds-spin 0.8s linear infinite`.

### Task 2: Reuse the existing provider lookup

**Files:**
- Modify: `src/AiObservatory.Web/src/theme/providerColors.ts`

**Interfaces:**
- Consumes: `getProvider(key: string): ProviderConfig | undefined` from `config/providers.ts`.
- Produces: Existing `providerColor(provider: string): string` and
  `participantColor(reviewer: string, role: string): string` behavior unchanged.

- [ ] **Step 1: Replace the duplicate map**

Use the existing helper directly:

```ts
import { getProvider } from '../config/providers'

export function providerColor(provider: string): string {
  return getProvider(provider)?.colorVar ?? 'var(--provider-other)'
}
```

Retain `participantColor()` unchanged.

- [ ] **Step 2: Run focused static verification**

Run `npm run build`. Expected: TypeScript and Vite complete with exit code 0.

### Task 3: Run the full frontend verification gate

**Files:**
- Verify only; no additional source changes unless a scoped verification fails.

**Interfaces:**
- Consumes: The completed Task 1 and Task 2 changes.
- Produces: Evidence that the conservative cleanup preserves the working frontend.

- [ ] **Step 1: Run tests**

Run `npm test`. Expected: 15 test files and 101 tests pass.

- [ ] **Step 2: Run lint**

Run `npm run lint`. Expected: exit code 0 with the existing warning baseline and no errors.

- [ ] **Step 3: Run React Doctor**

Run `npx --no-install react-doctor --verbose`. Expected: no new findings relative
to the five-warning baseline on current `origin/main`.

- [ ] **Step 4: Inspect the final diff**

Run `git diff --check`, `git status --short`, and `git diff --stat`. Expected:
only `tokens.css`, `providerColors.ts`, and this plan are new or modified after
the already-committed design spec; no whitespace errors.

- [ ] **Step 5: Commit the implementation locally**

```powershell
git add src/AiObservatory.Web/src/design/tokens.css src/AiObservatory.Web/src/theme/providerColors.ts docs/superpowers/plans/2026-07-16-conservative-web-audit-cleanup.md
```

```powershell
git commit -m "refactor(web): remove proven audit dead weight"
```
