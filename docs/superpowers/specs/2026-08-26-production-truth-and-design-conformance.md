# Production truth and FixPortal design conformance

**Status: implementation complete on `reviewer-findings-batch10`; PR review and deployment verification in progress.**

## Purpose

Bring the live AI Observatory to release-candidate quality in one pull request before the
first-principles product walkthrough. The pass repairs the financial claims found during the
live audit, removes broken optional configuration, bounds the Overview feed, and brings every
frontend surface back to the established FixPortal visual language.

The application remains a dense observability console. This is a conformance pass, not a
redesign into a generic SaaS dashboard.

## Goals

- Make every financial total and alert derive from the billed GBP spend ledger.
- Keep usage-derived value visible only when it is explicitly described as notional.
- Make the Reporting tab a coherent billed-finance view rather than a mixture of incompatible
  evidence.
- Limit the default Overview insight feed without hiding older insights.
- Stop deployments from creating references to optional secrets that were not supplied.
- Sync the vendored FixPortal design layer with the canonical package and document the small
  Observatory-specific visual vocabulary.
- Normalize all six tabs in light and dark themes at desktop and mobile widths.
- Deliver the design record, implementation, tests, deployment changes, and verification in
  one `reviewer-findings-batch10` pull request.

## Non-goals

- Supplying provider credentials or enabling provider products the operator has not bought.
- Merging billed, estimated, and notional amounts into one total or chart.
- Replacing the existing React application, chart library, or CSS architecture.
- Adding a runtime dependency on the private GitHub Packages design package.
- Broad React maintainability cleanup unrelated to a visible defect or this pass.
- Changing the intentional `noindex` posture before the public launch decision.
- Running the deferred cross-vendor adversarial review before provider quotas recover.

## Canonical visual source

The source of truth is `@fixportal/design` 0.8.1 at tag `v0.8.1` / commit `6b3e3e0` in the FixPortal assets repository. Its design
sentence remains:

> trading-floor precise — terminal aesthetic, dense readouts, monospace where data lives,
> status legible at a glance.

Directly consuming the package would make a clean OSS install depend on a private GitHub
Packages token. The Observatory will therefore keep its vendored design copy, but replace the
stale copy with the current canonical tokens, component CSS, and only the primitives the app
actually uses. No second component framework or compatibility wrapper is introduced.

The pass also adds a local `system.md` beside the frontend styles. It records:

- the canonical version and source of the vendored files;
- the Observatory's evidence/provenance signature;
- provider colours as a chart and provider-identity vocabulary only;
- the rule that provider colours never communicate health, severity, or interaction state.

## Visual contract

The conformance target applies to Overview, Adversarial Review, Reporting, Activity, GitHub,
and Spend in both themes and at desktop and mobile widths.

### Colour

- The approved canonical blue accent is the interaction colour.
- Green, amber, and red are the only status colours.
- The orange identity accent is reserved for the FixPortal wordmark.
- Provider colours may distinguish provider series, badges, and identities, but never imply
  success, warning, failure, selection, or affordance.
- Text uses the appropriate foreground token; brand fills do not double as text colours.
- The deprecated information hue is removed from active semantics.

### Shape and depth

- The 4 px spacing scale and canonical 4/6/8 px radii replace local one-off values where they
  express the same role.
- Borders define panels, controls, tables, and groups.
- Shadows remain limited to floating surfaces such as dialogs, toasts, and popovers, plus the
  documented destructive halo.
- Focus, hover, active, disabled, loading, empty, and error states use canonical tokens and are
  visibly distinct in both themes.
- Dedicated header/footer surfaces, brand contrast/background/ring roles, canonical radius
  tokens, and the 80 ms interaction duration come from the 0.8.1 snapshot rather than local
  values.

### Typography and density

- IBM Plex remains the interface family and monospace remains the data family.
- Type is normalized to the canonical dense scale rather than arbitrary local sizes.
- Monospace is used for values, identifiers, timestamps, and machine evidence, not ordinary
  explanatory prose.
- Existing information density is preserved; mobile rearranges hierarchy instead of making
  every surface oversized.

### Shared chrome and primitives

- App header, navigation, wordmark, theme control, buttons, badges, cards, tables, dialogs,
  tooltips, and footer conform to their canonical primitives.
- Native elements remain the default where they already solve the interaction. The insight
  disclosure uses `<details>`/`<summary>` rather than new state machinery.
- Existing accessibility behaviour is preserved or improved: keyboard reachability, visible
  focus, labelled controls, semantic headings, table semantics, and reduced-motion support.
- Newly exported primitives are vendored only when an existing Observatory control can adopt
  them without losing behaviour; unused package primitives remain outside the vendored layer.

## Financial truth

The prior billed-spend design remains authoritative: billed and usage-derived values have two
visual languages and are never summed.

### Reporting

Reporting becomes a billed-GBP view backed by `SpendEntry.AmountGbp` and the spend vendor
catalogue.

- Summary cards show billed spend, billed daily average, and a billed projection for the
  selected range.
- The daily chart groups ledger entries by `OccurredOn` and displays GBP.
- The split groups ledger entries by spend vendor and displays GBP.
- Refunds and credits remain signed and reduce the relevant totals.
- An empty billed ledger reads as no billed spend reported, not as a zero-cost claim.
- Usage token and activity analysis remains available on Overview and Activity; Reporting does
  not reuse usage aggregates as money.

The API exposes the smallest read model needed by the current Reporting page rather than a new
general reporting subsystem. The frontend reuses the installed chart library and existing
date-range behaviour.

### Budget alerts

Budget rules and notifications move from estimated USD to billed GBP:

- `BudgetRule.ThresholdUsd`, request fields, response fields, payloads, and labels become
  `ThresholdGbp`.
- The consolidated database migration renames the existing column, adds the persisted
  evaluation boundary and durable alert-claim schema, and preserves existing numeric values. A
  stored threshold of `1000` therefore becomes GBP 1000; it is not converted at a live exchange
  rate.
- `BudgetAlertService` compares the rule only with billed ledger entries in the rule's period.
- Daily rules reconsider every completed day from their immutable evaluation boundary so late
  ledger corrections are not lost; weekly and monthly windows are clamped to that boundary.
- A provider-scoped rule includes billed entries only for spend vendors mapped to that
  provider. Unmapped vendor spend contributes only to an all-provider rule.
- The existing `IUsageRepository` is extended with one no-tracking projected billed-sum query;
  a second repository abstraction is not introduced.
- Alert titles, bodies, serialized metadata, and email copy use pounds and say billed spend.
- One durable claim per rule and period atomically owns the alert insight and email-delivery
  state. Replays read that claim without writing; concurrent creators converge through the
  database uniqueness constraint, and delivery uses a recoverable lease with stable message ID.
- The injected clock behaviour remains unchanged.

This is a trust-boundary correction. Legacy daily aggregates, regardless of their `CostBasis`,
cannot trigger a financial alert.

### Subscription comparison

Subscription cards retain the useful comparison but name it accurately:

- `Period spend` becomes `Notional usage value`.
- Only rows explicitly classified with `CostBasis.Notional` are included.
- Copy explains that the amount is an API-list-price comparison, not money charged.
- Subscription price and notional usage value remain separate values; they are not presented as
  savings, return, or a billing reconciliation.

## Overview insight feed

The default Overview renders the five newest insights. If more exist, a native disclosure shows
the remaining insights in their existing order. The count, unread state, mark-read behaviour,
and individual insight actions remain unchanged. With five or fewer insights, no disclosure is
rendered.

## Optional provider configuration

The deployment templates create a Key Vault reference only when the corresponding secret value
or explicitly configured secret name is supplied. Optional Anthropic billing and Copilot
organisation settings therefore stay absent rather than entering a permanent `SecretNotFound`
state.

The currently broken optional settings are removed from the live App Service configuration as
part of deployment. Required settings and already configured providers are untouched. No
existing secret is repurposed merely because its name looks similar.

## Data flow

```text
SpendEntry.AmountGbp ──> billed reporting API ──> Reporting cards/charts
                    └──> billed sum query ──────> durable alert claim + insight
                                                   └──> bounded leased email delivery

DailyAggregate[Notional] ──────────────────────> subscription comparison

DailyAggregate[other bases] ───────────────────> usage/activity evidence only
```

There is no path from a usage aggregate into a component or notification labelled billed
spend.

## Migration and compatibility

- In one migration, rename the budget threshold column in place, add `EvaluationStartsOn`, and
  create `BudgetAlertClaims` with its unique rule-period/insight indexes, constraints, and
  restrictive insight relationship. Existing rule identifiers, periods, providers, trigger
  history, and numeric thresholds survive.
- The reverse migration drops the claim table and evaluation boundary before renaming
  `ThresholdGbp` back to `ThresholdUsd`; the threshold number remains unchanged in either
  direction.
- Change the public request/response field to `thresholdGbp`; the API is pre-release and does
  not retain the misleading USD alias.
- Preserve all spend ledger and usage history.
- Keep the existing provider-to-vendor relationship as the only provider filter for billed
  spend.
- Keep the vendored design import paths stable so application components need no package-source
  conditional logic.

## Error and empty-state behaviour

- A reporting query failure uses the existing error boundary and never falls back to estimates.
- No ledger rows produces an explicit unavailable/empty financial state.
- An unmapped vendor is still visible in all-provider reporting and is not silently discarded.
- Provider-scoped alerts do not guess a provider for an unmapped vendor.
- Optional missing credentials remain visible as not configured in source status, not as a
  deployment-health failure.

## Verification

Implementation follows the existing test framework and repository conventions. Minimum durable
coverage includes:

### Money paths

- Reporting cards, daily series, and vendor split use signed GBP ledger entries only.
- Usage aggregates cannot affect billed Reporting values.
- GBP budget rules trigger above the billed threshold and not below it.
- Provider-scoped rules include mapped vendors and exclude unrelated or unmapped vendors.
- Alert payloads and email copy use GBP and billed-spend language.
- The migration preserves existing threshold numbers in both directions and creates/removes the
  durable boundary/claim schema as one deployment unit.
- Existing claim replays avoid a write transaction; concurrent attempts still converge on one
  durable claim.
- Pending email reads are oldest-first, terminal/actively leased rows are excluded, and each
  worker pass is bounded to 50 claims.
- Clearing insights removes dependent claims and insights atomically; a failed insight delete
  restores the claims.
- The notional subscription comparison includes only `Notional` aggregates.

The consequential billed-sum query is exercised against the repository's real test database
provider. `DbContext` and `DbSet` are not mocked.

### UI behaviour

- Five insights render by default; the native disclosure reveals the remainder.
- Five or fewer insights render without an empty disclosure.
- Relevant financial empty, loading, and error states remain truthful.
- Empty billed ledgers render unavailable values, while a non-empty ledger that nets to zero
  renders £0.00.
- Existing keyboard and accessible-name tests continue to pass.
- Canonical design files are proven by matching Git blob hashes; rendered light/dark desktop and
  mobile states are authoritative for CSS conformance and clipping.

### Full gate

- CSharpier and .NET analyzer formatting.
- Release build and the complete backend test suite.
- Frontend lint, unit tests, type checking, and production build.
- Client collector tests.
- Dependency vulnerability checks.
- Desktop and mobile visual inspection of all six tabs in light and dark themes.
- Lighthouse regression check and live authenticated smoke test after deployment.

## Deployment and rollback

The schema migration and application deploy travel together through the existing workflow. The
deployment also removes the two broken optional App Service settings and verifies that required
Key Vault references still resolve.

Rollback is the previous application image plus the reverse threshold-column rename. Spend and
usage data are otherwise untouched. The UI has no runtime dependency on the canonical design
package, so package-registry availability cannot affect the deployed app.

## Acceptance criteria

1. Reporting contains only billed GBP amounts from the spend ledger.
2. Budget rules and all alert surfaces use billed GBP, never usage estimates.
3. Subscription comparisons are explicitly notional and include only notional evidence.
4. No visible amount collapses billed, estimated, notional, and unknown evidence into one sum.
5. Overview shows five insights by default and exposes the rest accessibly.
6. Optional missing provider settings do not create broken Key Vault references.
7. Every tab conforms to the canonical FixPortal tokens and component language in both themes
   and at desktop and mobile widths.
8. Provider colours identify providers only; status and interaction colours remain semantic.
9. A clean OSS install still works without access to private GitHub Packages.
10. The complete local gate, deployed smoke test, and policy-required PR review are green.
