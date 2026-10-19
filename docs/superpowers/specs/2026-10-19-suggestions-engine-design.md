# Suggestions engine: design

Status: draft for owner approval. Project 3 of 4 (see the panel finding capture design's roadmap).

## Intent

The owner decides which models hold a panel seat, which plans to keep and where to spend.
The Observatory now holds the data to answer those questions (per-seat influence from
finding rows, per-provider usage by cost basis, subscription prices) but only shows it;
nothing says "act on this". This project adds a rules layer that reads that data and
surfaces concrete, checkable suggestions.

## Decision recorded: rules, not free-written LLM output

Every suggestion is computed by a fixed, versioned rule and carries the exact figures and
window behind it. The owner acts on these (dropping a seat, cancelling a plan), so each
number must be verifiable and reproducible. The existing daily LLM insights job
(`IntelligenceWorkerService`, `PromptBuilder`) stays as it is: it writes narrative from
aggregates and cannot see panel data, and its figures are not guaranteed to match the
database. (Owner delegated this call; the reasoning is the previous two sentences.)

## Goals

- A short list of open suggestions on the Overview, each with title, rule id, severity,
  the evidence behind it and a way to dismiss or snooze it.
- Rules for panel seats and for subscriptions in v1 (below). Each rule states its sample
  floor, and emits nothing, not a weak guess, when the data is below it.
- Stable identity: the same condition produces the same suggestion across days (updated,
  not duplicated), and a dismissed suggestion stays dismissed until its evidence changes
  materially.
- Thresholds are configuration with documented defaults, not constants in code.

## Non-goals and anti-goals

- No automatic action: nothing edits `reviewers.json`, cancels a plan or changes a route.
- No LLM in v1, no email or Slack alerting in v1.
- No task-to-model routing suggestions in v1: no task labels are captured, so a rule would
  be a guess. Listed as a follow-up that needs capture first.
- No suggestion from counts-only panel runs: a seat rule needs finding rows.
- The existing Insights table, feed and job are unchanged.

## Vocabulary

Seat group, unique accepted catch, corroborated accepted catch, settled findings and the
rates are exactly as defined in the panel finding capture design. A "window" is a count of
days ending on the evaluation date (UTC).

## Rules in v1

Each rule produces at most one suggestion per subject. Defaults in brackets are
configuration.

| Id | Subject | Fires when | Suggestion |
|---|---|---|---|
| `seat.no-unique-catch` | seat group | at least [5] runs with finding rows in the last [60] days and zero unique accepted catches | the seat adds no coverage the others lack; consider replacing it |
| `seat.high-false-positive` | seat group | at least [10] settled findings in the window and false-positive rate above [0.5] | the seat raises more refuted than accepted findings |
| `seat.confirmation-only` | seat group | at least [10] accepted catches, all corroborated (none unique) | its accepted findings were always also raised elsewhere |
| `seat.cost-outlier` | seat group | known-cost cost per unique catch above [3] times the median of the other seats, with at least [3] seats having a value | the seat costs far more per unique catch than its peers |
| `plan.costs-more-than-use` | subscription | the subscription's provider has notional usage value over the last billing period below [0.5] times the plan's price in the same currency, and at least [10] known-cost requests | the plan costs more than the usage it covered would at list price |
| `plan.unused` | subscription | active for more than [14] days and zero requests from its provider in the last [30] days | the plan shows no use |

`seat.*` rules read `computeSeatMetrics`-equivalent aggregates built server-side from
`AdversarialReviewRun` and `AdversarialReviewFinding` rows (same joins and definitions as
the Seats tab: `(runId, reviewer)` for cost, reviewer rows only, unknown cost excluded
from cost rules and counted). `plan.*` rules read `Subscription` and `DailyAggregate`
rows for the subscription's provider, notional basis only, FX per the existing
`useUsdToGbp` convention (server uses the configured rate source the insights job uses).

A rule that cannot evaluate (no panel findings yet, no FX rate) returns a "not evaluated"
record with the reason; the UI shows the count of rules not evaluated, never silence.

## Contract

### Storage

New table `Suggestion`:

- `Id` (guid), `Key` (string, unique: `<ruleId>:<subject>`), `RuleId`, `Subject`,
  `Severity` (`info | attention`), `Title`, `EvidenceJson` (jsonb, below), `Status`
  (`open | dismissed | snoozed | resolved`), `SnoozedUntil` (nullable), `FirstSeenAt`,
  `LastSeenAt`, `LastEvaluatedAt`, `BaselineFiguresJson` (jsonb, see ruling 6). A second
  table `SuggestionRuleRun` holds the latest per-rule result (ruling 9).

`EvidenceJson` is an object `{ window: { from, to }, sampleSize, figures: [ { label, value,
unit } ], runIds: [ ... ] }`. `figures` is what the UI prints; `runIds` lets the owner open
the run folder. No finding text.

### Lifecycle

Evaluation runs daily after the existing analysis catch-up in `IntelligenceWorkerService`
and on `POST /api/suggestions/refresh` (admin key). For each rule and subject:

- condition holds, no row: insert `open`.
- condition holds, row `open` or `snoozed` past its date: update evidence and
  `LastSeenAt`.
- condition holds, row `dismissed`: stay dismissed unless the evidence fingerprint changed
  materially (a figure moved by more than [25%] or the sample size doubled), then reopen.
- condition no longer holds, row `open` or `snoozed`: mark `resolved` (kept for history).

### Endpoints (under `/api`; GET needs the read key or a signed-in user, others per ruling 7)

- `GET /suggestions?status=open` returns suggestions plus `{ notEvaluated: [ { ruleId,
  reason } ] }`.
- `POST /suggestions/{id}/dismiss` and `POST /suggestions/{id}/snooze?days=`.
- `POST /suggestions/refresh`.

## UI

A "Suggestions" card on the Overview under the summary cards: open suggestions ordered by
severity then age, each row title, rule id, evidence figures on expand, dismiss and snooze
buttons, and a footer "n rules not evaluated" with the reasons on hover or expand. Empty
state says which rules ran and found nothing.

## Rulings (from the builder check; the owner delegated these)

1. **Several subscriptions per provider.** Plan rules use the provider as the subject
   (`plan.costs-more-than-use:<provider>`) and compare provider usage against the SUM of
   that provider's active subscription prices, because usage cannot be split between
   plans. `ExtraUsageCost` is not part of the plan price (it is overage, already visible
   as usage). `plan.unused` fires per provider when every active subscription of that
   provider is past its guard period.
2. **Definitions.** Usage value is the sum of `CostUsd` over `notional` rows with
   `RequestCount > UnknownCostCount`. Known-cost requests are the sum of
   `RequestCount - UnknownCostCount` over those rows. `plan.unused` counts requests of any
   basis and scope for the provider. `UsageScope` is not filtered.
3. **Billing period.** The current period start comes from a C# port of the web app's
   `currentBillingPeriodStart` (month-end clamping included), clamped to `ActiveFrom`.
   `plan.costs-more-than-use` waits until at least half of the period has elapsed, so a
   partial period is never compared with a full price; before that it is "not evaluated"
   with the reason. Annual plans use the trailing 30 days against price / 12 and need at
   least [14] active days, the same guard as `plan.unused`.
4. **Seat identity.** A seat group is the run row's model string, exactly as the Seats tab
   defines it. The C# implementation is a port, so both implementations run the same
   shared JSON fixture (`docs/superpowers/contracts/seat-metrics/`, input rows and
   expected metrics) in their own test suites. A seat that is absent from some runs is
   counted only for the runs where it has a reviewer row. The "others" set for
   `seat.cost-outlier` is every other seat group with at least [3] runs in the window.
5. **Windows.** One setting, `Suggestions:SeatWindowDays` (default 60), applies to all seat
   rules. The evaluation date is UTC today and the window ends on the last complete day.
   Rates follow the panel finding capture design (false-positive rate is refuted over
   accepted plus refuted).
6. **Reopen rules.** `Suggestion` stores `BaselineFiguresJson`, the evidence figures at the
   moment of creation or of the last dismiss or snooze. A dismissed suggestion reopens
   when its primary figure moved more than [25%] against that baseline or its sample size
   is at least double (a zero baseline with a non-zero figure counts as moved). A snooze
   that expires onto an unchanged condition reopens. A `resolved` row whose condition
   returns reopens as `open` and resets `FirstSeenAt`; otherwise `FirstSeenAt` is kept.
   (This replaces `EvidenceFingerprint` in the storage list.)
7. **Authentication.** A signed-in browser user has full access, as for subscription
   edits. For machine callers every non-GET route needs the admin key (the existing
   `ApiKeyEndpointFilter` rule). Dismiss, snooze and refresh are therefore ordinary
   non-GET routes in the `api` group; there is no separate write key.
8. **Scheduling.** A dedicated `SuggestionWorkerService` (its own `BackgroundService`,
   daily, isolated error handling) runs the evaluation, so it does not depend on the LLM
   job being enabled or healthy. A Postgres advisory lock serialises the daily run, the
   refresh endpoint and any concurrent caller.
9. **Not evaluated.** The latest result of each rule is persisted in a small table
   `SuggestionRuleRun` (`RuleId` primary key, `EvaluatedAt`, `Status` as `evaluated` or
   `notEvaluated`, `Reason`, `SubjectsEvaluated`, `SuggestionsEmitted`). `GET` reads it. A
   rule is `notEvaluated` when its inputs are missing or when no subject reaches the
   sample floor (reason says which); it is `evaluated` with zero suggestions when at least
   one subject reached the floor and none fired.
10. **Severity** is fixed per rule: `info` for `seat.confirmation-only`; `attention` for
    every other v1 rule.
11. **Evidence.** The same shape for both rule families. `runIds` lists the 20 most recent
    contributing runs for seat rules and is empty for plan rules.
12. **Currency.** GBP and USD only. A subscription in any other currency makes that
    subscription's plan rules `notEvaluated` with the reason. Notional USD converts at one
    rate, the rate on the evaluation date from the same source the insights job uses
    (`fx.GetUsdToGbpAsync`).

## Constraints

- Server-side C#, NodaTime for dates, `IClock` for time, no static now.
- Rules are pure functions over an immutable input snapshot (`SuggestionInputs`) so they
  are unit-testable without a database; one thin service loads inputs and persists output.
- Thresholds bind from configuration (`Suggestions:*`), validated at startup.
- Matches the repository's conventions; house gates per repo.

## Work packages

| # | Package | Owner |
|---|---|---|
| A | Contract fixtures: sample `GET /suggestions` response, evidence shape | Claude |
| B | Rules, inputs loader, persistence, lifecycle, endpoints, tests | Claude |
| C | Overview Suggestions card against the fixtures | GLM |
| D | Documentation for the rules (feeds the docs project) | later |

## Open points for the builder

- Seat identity when a vendor ran two models in one run follows the Seats tab (the run
  row's model string, labelled mixed) until per-seat run rows exist.
- UNVERIFIED: that `fx.GetUsdToGbpAsync` is usable outside the insights generator's scope
  without extra wiring. Refuted if it can be injected into a second service as is.
