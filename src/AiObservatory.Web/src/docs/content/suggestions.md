# Suggestions

The Suggestions card on the Overview lists things Observatory has noticed about your review panel and your subscription plans. This page covers the card, the six rules, the dismiss and snooze lifecycle, every setting and the API.

## What suggestions are

A suggestion is advice to look at something, not an instruction. Each one comes from a fixed rule and shows the figures behind it. No language model writes them, and Observatory never changes a plan or a panel for you.

The rules run once when the API starts and then daily at 00:00 UTC. They are independent of the insights job, so they work when insights are switched off or have no `ANTHROPIC_API_KEY`. A manual refresh is available through the API. Neither the daily run nor the startup run happens in a demo instance. See [Usage and insights](/docs/usage-insights#how-insights-are-generated) for the separate insights job.

## The card

The card is titled `Suggestions` and sits under the summary cards.

| State            | What you see                                 |
| ---------------- | -------------------------------------------- |
| Loading          | `Loading suggestions...`                     |
| Error            | `Couldn’t load suggestions — try refreshing` |
| Nothing open     | `No open suggestions.`                       |
| Suggestions open | One row per open suggestion                  |

### Rows

Each row shows:

- a severity badge, `Attention` or `Info`,
- the title, which states the finding in a sentence,
- the rule id in small text, for example `seat.no-unique-catch`, and
- a `Show evidence` button (`Hide evidence` when open).

Rows are ordered with `Attention` before `Info`, and within a severity the longest-standing first.

Evidence, when expanded, shows:

- the figures behind the finding, each with a label, a value and a unit,
- `Window: <from> to <to>`, the dates the rule read,
- `Sample size: N`, and
- `Run ids: ...` for seat rules, the most recent runs of that seat, up to 20.

Plan rules that compare costs include two descriptive figures, labelled `fxRate` and `fxSource`, which record the exchange rate used and where it came from.

### Actions

Under each row, unless you are in [read-only mode](/docs/access#read-only-mode):

- `Dismiss` hides the suggestion.
- `Snooze 7 days` and `Snooze 30 days` hide it for that long.

A failed action shows no message. The buttons become available again.

### Rules not evaluated

If any rule had too little data to judge any subject, the foot of the card shows a disclosure: `N rule(s) not evaluated`. Opening it lists each rule id and its reason, for example `No seat has 5 runs with finding rows in the last 60 days yet`. This is expected until you have enough data. Until a panel run records findings, all four seat rules read this way.

A rule that was not evaluated never changes its existing suggestions.

## The rules

Rule ids are fixed. Seat rules judge a **seat**, which is one reviewer model. A seat made of several models joined by `+` is labelled `mixed: <models>`. Plan rules judge one **provider** at a time.

All windows end on the last complete UTC day, which is yesterday.

### Seat rules

Seat rules read the panel runs recorded in the seat window. They use only runs that have finding rows. History that holds counts only never satisfies a minimum. A seat's counts come from findings:

- **Unique catch:** an accepted finding that exactly one reviewer raised.
- **Corroborated catch:** an accepted finding that more than one reviewer raised.
- **Refuted:** a finding whose outcome was refuted.
- **Settled:** unique, corroborated and refuted findings together.

| Rule                       | Fires when                                                                                                               | Severity  | Figure used to reopen           |
| -------------------------- | ------------------------------------------------------------------------------------------------------------------------ | --------- | ------------------------------- |
| `seat.no-unique-catch`     | The seat has at least `MinDetailedRuns` runs with finding rows and no unique catch                                       | Attention | `Detailed runs`                 |
| `seat.high-false-positive` | The seat has at least `MinSettledFindings` settled findings and the refuted share is above `FalsePositiveThreshold`      | Attention | `False-positive rate`           |
| `seat.confirmation-only`   | The seat has at least `MinCatchesForConfirmationOnly` accepted catches and every one was also raised by another reviewer | Info      | `Corroborated accepted catches` |
| `seat.cost-outlier`        | The seat's cost per unique catch is more than `CostOutlierMultiple` times the median of its peers                        | Attention | `Cost per unique catch`         |

For `seat.high-false-positive`, the rate is refuted findings divided by settled findings.

For `seat.cost-outlier`:

- A seat takes part, as a subject or as a peer, only if it has at least `CostOutlierMinPeerRuns` runs with finding rows and a cost per unique catch.
- Cost per unique catch is the known cost of the seat's runs with finding rows divided by its unique catches. A seat with no unique catches, or no known cost, has none.
- A seat is judged only when at least `CostOutlierMinPeers` other seats take part. The comparison value is the median of those peers.

### Plan rules

Plan rules sum all active subscriptions of a provider over the plan window. See [Subscriptions](/docs/subscriptions).

| Rule                       | Fires when                                                                                                    | Severity  | Figure used to reopen  |
| -------------------------- | ------------------------------------------------------------------------------------------------------------- | --------- | ---------------------- |
| `plan.costs-more-than-use` | The provider's notional usage value, scaled to a 30-day month, is below `PlanUsageRatio` of the monthly price | Attention | `Usage to price ratio` |
| `plan.unused`              | The provider has an active plan and no requests at all in the plan window                                     | Attention | `Requests in window`   |

Details for `plan.costs-more-than-use`:

- The notional value is the cost of the provider's notional aggregates with a known cost, converted to pounds, then multiplied by 30 and divided by `PlanWindowDays`.
- The monthly price is the sum of the provider's plans. Annual prices are spread over twelve months. USD prices are converted at the same live rate.
- The provider needs at least `PlanMinKnownCostRequests` requests with a known cost in the window.

Details for `plan.unused`: requests are counted across all bases, not only notional.

### Providers that are skipped

Both plan rules skip a provider entirely when any of its active plans:

- has been active for fewer than `PlanMinActiveDays` days, or
- is priced in a currency other than GBP or USD.

Usage cannot be split between plans, so one unusable plan makes the whole provider unjudgeable. If the exchange rate cannot be fetched, `plan.costs-more-than-use` is reported as not evaluated with the reason `FX unavailable`. It does not compare at a made-up rate.

If a rule throws an error, it is recorded as not evaluated with the reason `Rule failed: <error type>`. The other rules still run.

## Lifecycle: open, dismissed, snoozed, resolved

Every suggestion has one state.

| State       | Meaning                                                                    |
| ----------- | -------------------------------------------------------------------------- |
| `open`      | Shown on the card.                                                         |
| `dismissed` | Hidden. See the reopen rule below.                                         |
| `snoozed`   | Hidden until the snooze ends.                                              |
| `resolved`  | The condition stopped holding. Kept for history and not shown on the card. |

A suggestion is identified by its rule and its subject. The same condition on the same seat or provider is one suggestion that is refreshed on each run. Its evidence updates every time the rule evaluates.

### Dismissing

Dismissing records the figure you were looking at. A dismissed suggestion returns to `open` on a later evaluation only if, for the same rule and subject:

- its main figure has moved by more than `ReopenChangeFraction` of the recorded value, or
- its sample size is at least double the recorded one.

If the recorded main figure was zero, any non-zero figure counts as a move.

### Snoozing

Snoozing hides a suggestion for the chosen number of days. When the snooze ends, the suggestion becomes open again at the next evaluation in which its condition still holds. That is the next daily run or the next manual refresh, not the moment the snooze expires. The card offers 7 and 30 days; the API accepts 1 to 90.

### Resolving and returning

When a rule evaluates and the condition no longer holds for a subject it judged, that subject's suggestion becomes `resolved`. This applies to open, dismissed and snoozed suggestions alike, so a problem that clears and later returns is shown again as a new open suggestion. When it returns, its first-seen date and recorded baseline start over.

A resolved suggestion cannot be dismissed or snoozed.

### What a partial evaluation does

A rule that judged some subjects but not others only changes the subjects it judged. A seat that falls below a minimum, for example because old runs left the window, keeps its suggestion and any snooze.

## Settings

Set these in the `Suggestions` configuration section, or as environment variables with a double underscore, for example `Suggestions__SeatWindowDays=90`. Every value is checked at startup. A bad value stops the application, and the error names the key as `Suggestions:<Key>`.

| Key                             | Default | Rule                                                                                                        |
| ------------------------------- | ------- | ----------------------------------------------------------------------------------------------------------- |
| `WorkerEnabled`                 | `true`  | Whether the daily worker runs. A refresh through the API still works when it is `false`.                    |
| `SeatWindowDays`                | `60`    | Days of panel history the seat rules read, ending yesterday (UTC). Greater than zero.                       |
| `MinDetailedRuns`               | `5`     | Runs with finding rows a seat needs before `seat.no-unique-catch` judges it. Greater than zero.             |
| `MinSettledFindings`            | `10`    | Settled findings a seat needs before `seat.high-false-positive` judges it. Greater than zero.               |
| `FalsePositiveThreshold`        | `0.5`   | Refuted share above which a seat fires. Greater than 0 and at most 1.                                       |
| `MinCatchesForConfirmationOnly` | `10`    | Accepted catches a seat needs before `seat.confirmation-only` judges it. Greater than zero.                 |
| `CostOutlierMultiple`           | `3`     | Multiple of the peer median cost per unique catch that makes a seat an outlier. Greater than zero.          |
| `CostOutlierMinPeerRuns`        | `3`     | Runs with finding rows a seat needs to take part in the cost comparison. Greater than zero.                 |
| `CostOutlierMinPeers`           | `3`     | Peers needed to form a median. Greater than zero.                                                           |
| `PlanWindowDays`                | `30`    | Days of usage the plan rules read, ending yesterday (UTC). Greater than zero.                               |
| `PlanUsageRatio`                | `0.5`   | Usage-to-price ratio below which `plan.costs-more-than-use` fires. Greater than 0 and at most 1.            |
| `PlanMinKnownCostRequests`      | `10`    | Requests with a known cost a provider needs before `plan.costs-more-than-use` judges it. Greater than zero. |
| `PlanMinActiveDays`             | `14`    | Days a plan must have been active before the plan rules judge it. Greater than zero.                        |
| `ReopenChangeFraction`          | `0.25`  | Move in the main figure that reopens a dismissed suggestion. Greater than 0 and at most 1.                  |

The full list of settings is in [Configuration](/docs/configuration).

## API

| Method | Route                                | What it does                                                                                                       |
| ------ | ------------------------------------ | ------------------------------------------------------------------------------------------------------------------ |
| `GET`  | `/api/suggestions?status=open`       | Suggestions in one state, plus the rules that were not evaluated and why.                                          |
| `POST` | `/api/suggestions/{id}/dismiss`      | Dismiss. Answers 204, 404 for an unknown id, or 409 for a resolved suggestion.                                     |
| `POST` | `/api/suggestions/{id}/snooze?days=` | Snooze for 1 to 90 days. Answers 204, 400 for a missing or out-of-range `days`, 404 or 409 as above.               |
| `POST` | `/api/suggestions/refresh`           | Run every rule now. Returns, for each rule, `ruleId`, `evaluated`, `reason` and the number of `suggestions` found. |

- `status` is `open`, `dismissed`, `snoozed` or `resolved`. It defaults to `open`. Any other value answers 400.
- A suggestion in the response has `id`, `key` (the rule id and subject joined by a colon), `ruleId`, `subject`, `severity` (`info` or `attention`), `title`, `status`, `snoozedUntil`, `firstSeenAt`, `lastSeenAt` and `evidence`. The `evidence` object holds `window`, `sampleSize`, `figures` and `runIds`. Results are ordered oldest first-seen first.
- The response also has `notEvaluated`: a list of `ruleId` and `reason`.
- Reading needs the read-only or admin key, or a signed-in user. Dismiss, snooze and refresh need the admin key or a signed-in user. See [Authentication](/docs/authentication) and [API](/docs/api).

## References

- `docs/suggestions.md` for the original rules text, checked against the code for this page.
- `src/AiObservatory.Api/Services/Suggestions/` for the rules, lifecycle, store and worker.
- `src/AiObservatory.Api/Endpoints/SuggestionsEndpoints.cs` for the routes.
- `src/AiObservatory.Web/src/components/SuggestionsCard.tsx` for the card.
