# Suggestions

The Overview tab shows a Suggestions card: a short list of things the Observatory has noticed about your review panel and your subscription plans. Every suggestion comes from a fixed rule and shows the figures behind it. No model writes them. The rules run once at startup and then daily at 00:00 UTC, independently of the insights job, so they work with the insights job switched off.

A suggestion is advice to look at something, not an instruction. The Observatory never changes a plan or a panel.

## The rules

Seat rules judge a panel seat, meaning one reviewer model, over the seat window. They only count runs that have finding rows. History that holds counts only never satisfies a minimum.

| Rule | Fires when | Severity |
| --- | --- | --- |
| `seat.no-unique-catch` | The seat has at least `MinDetailedRuns` runs with finding rows and no accepted finding that only it raised. | attention |
| `seat.high-false-positive` | At least `MinSettledFindings` of its findings were settled, and the share that were refuted is above `FalsePositiveThreshold`. | attention |
| `seat.confirmation-only` | At least `MinCatchesForConfirmationOnly` accepted catches, every one also raised by another seat. | info |
| `seat.cost-outlier` | The seat's cost per unique catch is more than `CostOutlierMultiple` times the median of its peers. It needs a cost per unique catch itself, at least `CostOutlierMinPeerRuns` runs and at least `CostOutlierMinPeers` peers that qualify the same way. | attention |

Plan rules judge one provider at a time, summing all its active subscriptions, over the trailing plan window.

| Rule | Fires when | Severity |
| --- | --- | --- |
| `plan.costs-more-than-use` | The notional value of the usage, scaled to a 30-day month, is below `PlanUsageRatio` of the monthly price. Annual prices are spread over twelve months and USD prices are converted at the live rate. The provider needs at least `PlanMinKnownCostRequests` requests with a known cost. | attention |
| `plan.unused` | The provider has an active plan and no requests at all in the plan window. | attention |

If any active plan of a provider is younger than `PlanMinActiveDays`, or priced in a currency other than GBP or USD, the whole provider is skipped by both plan rules, because usage cannot be split between plans. If the exchange rate cannot be fetched, `plan.costs-more-than-use` is reported as not evaluated with the reason "FX unavailable" rather than compared at a made-up rate.

### Not evaluated

A rule that has too little data to judge any subject is listed under "rules not evaluated" at the foot of the card, with its reason, for example "No seat has 5 runs with finding rows in the last 60 days yet". Until a panel run writes a `findings` array, all four seat rules read this way. That is expected. A rule that was not evaluated never changes its existing suggestions.

A rule that judged some subjects but not others only changes the subjects it judged. A seat below a minimum keeps its suggestion and any snooze.

## Dismiss and snooze

| State | Meaning |
| --- | --- |
| open | Shown on the card. |
| dismissed | Hidden. It comes back only if its evidence moves by more than `ReopenChangeFraction` against what you saw when you dismissed it, or its sample size doubles. |
| snoozed | Hidden for 7 or 30 days, then open again if the condition still holds. |
| resolved | The condition stopped holding. Kept for history. A dismissed or snoozed suggestion resolves too when its condition clears, so a problem that clears and later returns is shown again. |

A resolved suggestion cannot be dismissed or snoozed.

## Settings

Set these in the `Suggestions` configuration section, or as environment variables with a double underscore, for example `Suggestions__SeatWindowDays=90`. Every value is checked at startup. A bad value stops the app and the message names the key.

| Key | Default | Meaning |
| --- | --- | --- |
| `WorkerEnabled` | `true` | Whether the daily worker runs. Refresh from the API still works when it is off. |
| `SeatWindowDays` | `60` | Days of panel history the seat rules read, ending yesterday (UTC). |
| `MinDetailedRuns` | `5` | Runs with finding rows a seat needs before `seat.no-unique-catch` judges it. |
| `MinSettledFindings` | `10` | Settled findings a seat needs before `seat.high-false-positive` judges it. |
| `FalsePositiveThreshold` | `0.5` | Refuted share above which a seat fires. Greater than 0, at most 1. |
| `MinCatchesForConfirmationOnly` | `10` | Accepted catches a seat needs before `seat.confirmation-only` judges it. |
| `CostOutlierMultiple` | `3` | Multiple of the peer median cost per unique catch that makes a seat an outlier. |
| `CostOutlierMinPeerRuns` | `3` | Runs a seat needs to take part in the cost comparison, as subject or peer. |
| `CostOutlierMinPeers` | `3` | Peers needed to form a median. |
| `PlanWindowDays` | `30` | Days of usage the plan rules read, ending yesterday (UTC). |
| `PlanUsageRatio` | `0.5` | Usage-to-price ratio below which `plan.costs-more-than-use` fires. Greater than 0, at most 1. |
| `PlanMinKnownCostRequests` | `10` | Requests with a known cost a provider needs before `plan.costs-more-than-use` judges it. |
| `PlanMinActiveDays` | `14` | Days a plan must have been active before the plan rules judge it. |
| `ReopenChangeFraction` | `0.25` | Move in the main figure that reopens a dismissed suggestion. Greater than 0, at most 1. |

## API

| Method | Route | What it does |
| --- | --- | --- |
| `GET` | `/api/suggestions?status=open` | Suggestions in one state (`open`, `dismissed`, `snoozed` or `resolved`; default `open`) with their evidence, plus the rules that were not evaluated and why. |
| `POST` | `/api/suggestions/{id}/dismiss` | Dismiss. Answers 409 for a resolved suggestion. |
| `POST` | `/api/suggestions/{id}/snooze?days=` | Snooze for 1 to 90 days. Answers 409 for a resolved suggestion. |
| `POST` | `/api/suggestions/refresh` | Run every rule now and return what each one found. |

Reading needs the read or admin key, or a signed-in user. Dismiss, snooze and refresh need the admin key or a signed-in user.
