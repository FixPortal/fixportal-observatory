---
title: Conservative Web Audit Cleanup
date: 2026-07-16
status: accepted
---

# Conservative Web Audit Cleanup

> A behavior-preserving cleanup of the `AiObservatory.Web` frontend following
> the Ponytail audit. The governing constraint is that the working application
> must remain visually and functionally unchanged.

## Decision

Remove only code proven unused or duplicative by repository-wide reference
searches:

1. Delete unused design-token declarations and the unused `.fpds-spin` utility,
   retaining the live `fpds-spin` keyframe used by the loading indicator.
2. Make `providerColor()` reuse the existing `getProvider()` lookup instead of
   maintaining a second provider map.

## Explicit non-goals

- Do not replace the theme toggle, date picker, popovers, or collapsible panels.
- Do not remove the design-system components or architecture tests.
- Do not change Vite chunking or bundle-analysis tooling.
- Do not alter rendered markup, interaction behavior, accessibility semantics,
  public APIs, dependencies, or visual output.
- Do not fix unrelated React Doctor, ESLint, performance, or accessibility
  findings in this pass.

## Files

| File | Change | Why it is safe |
|---|---|---|
| `src/AiObservatory.Web/src/design/tokens.css` | Remove custom properties with no `var(...)` consumers and the unused `.fpds-spin` class/media rule | Repository-wide searches show no consumers; the separately used keyframe remains |
| `src/AiObservatory.Web/src/theme/providerColors.ts` | Import and call `getProvider()` | Four providers make the existing linear lookup trivial; fallback behavior remains unchanged |

## Verification

Run the frontend tests, lint, production build, and React Doctor. Compare the
production bundle's successful construction and confirm the worktree contains
only the two intended source-file changes plus this documentation.
