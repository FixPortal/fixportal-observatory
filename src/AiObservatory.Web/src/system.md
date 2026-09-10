# Observatory visual system

Canonical base: `@fixportal/design` 0.8.1 at tag `v0.8.1` / commit `6b3e3e0`, vendored from the FixPortal assets repository so public installs require no private package token.

## Product signature

Observatory is evidence-first: values carry source, scope, basis, freshness, and observation time. Billed GBP, estimates, and subscription notional value never share a total or visual claim.

## Overview hierarchy

Keep headline usage and valuation followed by analytical evidence as the primary flow. Put operational collection mechanics at the bottom in a quiet, collapsed `Data sources` panel; do not create a settings area for a single panel. Its summary counts reporting and not-connected sources, plus attention only when non-zero. Use `Not connected`, never `Not configured`. Explain that collection health covers optional APIs and local telemetry and does not indicate missing subscription usage.

Collapsed disclosures containing controls use native `inert` alongside `aria-hidden` so hidden controls are not keyboard-focusable. Reuse the existing `CollapsiblePanel`, canonical tokens, border-only depth, and 4 px spacing.

## App-local palettes

Provider colours identify providers in charts, swatches, and provider badges only. Project colours identify project series only. Neither palette communicates status, selection, progress, or interaction. The canonical blue accent is interaction; green, amber, and red are status.

Bar-chart hover bands use the theme-aware `--brand-pill-bg` interaction tint in both themes. Override Recharts' hard-coded `#ccc` cursor rather than allowing a white highlight; retain the tooltip as the detailed hover evidence.

## Conformance

Use canonical surface, text, brand, status, spacing, radius, typography, focus, and motion rules. Version 0.8.1 supplies dedicated header/footer surfaces, brand contrast/background/ring roles, the 4/6/8 px radius ladder, and the 80 ms interaction duration; app CSS aliases those roles rather than restating their values. Borders provide depth; shadows are reserved for floating surfaces. Monospace is for values, identifiers, timestamps, and machine evidence.

Dashboard navigation retains its app-local roving tablist because it supports ArrowUp/ArrowDown aliases in addition to horizontal arrow navigation. Canonical 0.8.1 `Tabs` omits those existing keys, so adopting it would lose keyboard behaviour; its unused source is not vendored.

## Spend controls

Keep the page-wide category, vendor, and edit controls in one border-only filter row: card surface, standard border, panel radius, 12 px padding, and 16 px gap. The same filter state must continue to drive totals, charts, and the ledger.

Use the shared `Button` `danger` variant for destructive actions: transparent surface with semantic danger text and border, danger tint on hover/active, and the small 4 px × 8 px padding in table rows. Never leave destructive actions on the browser's native button styling.

## GitHub evidence

Keep period and repo selection in one border-only filter row. Repo is the shared evidence axis: one selection filters pull requests, commits, and CI together.

Stack commit and CI panels at full width. GitHub tables reserve at least 14 rem for repo identifiers and never wrap them; narrow viewports scroll the table instead. PR titles are human prose and use the sans-serif face, while repo names, dates, and metrics remain monospace.

## Reporting evidence

Reuse the shared selected-versus-comparison period rail from Spend, Activity, and GitHub. `Previous period` is the default comparison; custom dates remain explicit. The same ranges drive summary cards and the billed-spend chart.

Lead with billed spend, followed by daily average, 30-day run rate, and top vendor. A 30-day run rate is exactly `daily average × 30`; never label it as a calendar-month projection. Show absolute GBP comparison deltas with `higher` or `lower`, and show the previous period's top vendor and amount as evidence rather than forcing a percentage comparison.

Every Reporting figure must reconcile to the same signed ledger aggregate: headline total, daily-series sum, and vendor-series sum agree. A non-empty ledger that nets to zero displays `£0.00`; an empty ledger displays an em dash. Fill missing chart dates with zero-valued slots so sparse periods retain their true time scale, and collapse duplicate chart empty states into one message when both periods are empty.

Use provider identity colours and end labels in the vendor chart; keep refunds semantic red only where their negative direction needs emphasis. Budget rules show `current / limit` plus `Within limit` or `Over limit`; over-limit status is strictly `current > threshold`, matching alert evaluation. Destructive rule actions use the shared danger button. At narrow widths, render each rule as a stacked two-column card with current spend, status, last-fired time, and action visible without horizontal scrolling.
