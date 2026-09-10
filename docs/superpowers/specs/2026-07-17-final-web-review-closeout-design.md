---
title: Final Web Review Closeout
date: 2026-07-17
status: accepted
---

# Final Web Review Closeout

> A conservative final pass over the web audit backlog. Fix findings only when
> the change improves accessibility, determinism, or maintainability; do not
> rewrite working code to satisfy analyzer preferences.

## Decision

Ship one final review PR that:

1. Replaces the dashboard's `<nav role="tablist">` with a neutral tablist
   container, preserving the existing keyboard and ARIA tab behavior.
2. Makes the adversarial-review test fixture IDs deterministic.
3. Disables ESLint rules whose current findings are TypeScript/JSX style noise,
   then fixes any remaining warnings only where the result is clearer code.
4. Closes the three open AI Findings and the remaining React Doctor findings as
   fixed or dismissed with evidence.

## Finding disposition

| Finding group | Disposition | Why |
|---|---|---|
| Dashboard tablist on `<nav>` | Fix | `tablist` is a widget role, not a navigation landmark; a neutral container has cleaner semantics. |
| Random test fixture IDs | Fix | Deterministic fixtures are simpler and reproducible. |
| ESLint duplicate-string, property-ordering, nested-conditional, union-size, and exhaustive-`else` style rules | Disable | Literal rewrites would add constants, branches, or indirection without improving behavior or clarity. |
| ESLint warnings retained after calibration | Fix selectively | Keep a warning only when its correction makes the code clearer; otherwise document and disable the noisy rule. |
| Repeated `find()` calls while grouping review participants | Dismiss | Each run has a bounded panel of roughly four participants, so the scan remains linear in total records; a combined accumulator would be more complex. |
| JSX passed to `CollapsiblePanel.summary` | Dismiss | `CollapsiblePanel` is not memoized; changing its natural `ReactNode` API would not avoid a render. |
| Spend chart recomputes aggregation on mode change | Dismiss | The dataset is small and splitting the memo would duplicate or complicate the aggregation shape. |
| Subscription spend filters aggregates per card | Dismiss | Provider cardinality is at most five; an index adds state and code without a measurable benefit. |
| `.sort()` on newly created arrays | Dismiss | The arrays are not shared, so mutation is local and safe; `.toSorted()` would be style-only churn. |

## Constraints

- No visual change beyond the semantically equivalent tablist container.
- No new abstractions, dependencies, or speculative optimisation.
- Do not weaken correctness, accessibility, React Hooks, or unused-code rules.
- Update the findings ledger so no item remains ambiguously open.
- Open the PR ready for review, not as a draft.

## Verification

Run frontend type checking, lint, tests, production build, and React Doctor.
Lint should finish without warnings after calibration. React Doctor may continue
to report explicitly dismissed heuristics; the ledger must explain each one.
