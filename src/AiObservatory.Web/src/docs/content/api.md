# API

A readable reference for the Observatory HTTP API: every route group, its methods, who may call it, the parameters that matter and the shape of the response. It is derived from the endpoint code. A Postman collection of representative requests also ships in the repository documentation; it is not exhaustive.

## Conventions

### Base path and credentials

All routes are under `/api`. The IDE integration has its own group under `/api/ide/v1`. Credentials are described in [Authentication](/docs/authentication); in the tables below the **Auth** column uses these labels:

| Label      | Meaning                                                                      |
| ---------- | ---------------------------------------------------------------------------- |
| Read       | Entra token, the read-only key, or the admin key. For `GET` routes.          |
| Admin      | Entra token or the admin key. For every non-`GET` route.                     |
| Admin read | A `GET` route that refuses the read-only key: Entra token or admin key only. |
| IDE        | The IDE key in `X-Observatory-IDE-Key`.                                      |

Send keys as `X-Observatory-Key: <key>`. Never paste a real key into a shared document.

### Requests and responses

- Bodies and responses are JSON. Property names are camelCase on output; on input they match case-insensitively.
- Enumerations are strings (camelCase on output, for example `providerApi`, `listPriceEstimate`), never numbers. On input the name is matched case-insensitively. Provider names are `anthropic`, `copilot`, `google`, `openai`, `moonshot`, `xai`, `meta`, `zai` and `deepseek`.
- Dates are `yyyy-MM-dd`. Timestamps are ISO 8601 UTC instants such as `2026-10-19T08:30:00Z`.
- Money in the usage data is USD (`costUsd`). Money in the ledger, budget rules and reporting is GBP (`amountGbp`, `thresholdGbp`), converted from the entry currency at the rate on the charge date.
- Errors from validation are usually a JSON string (for example `"Unknown provider: foo"`) or a standard problem-details object, with status 400.
- Date-range queries (`from`, `to`) on the aggregates, activity and GitHub routes default to the last 30 calendar days including today. `GET /api/spend/entries` has no default window (both are optional), and `GET /api/spend/reporting` requires both. All of them answer 400 for a malformed date or `from` after `to`.

### Limits

| Limit                                               | Value                                                                                                             |
| --------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------- |
| Rate limit                                          | 120 requests per client IP per minute by default (setting `RateLimiting:ApiPermitLimit`); excess requests get 429 |
| Batch upserts (activity sessions, caveman sessions) | At most 1000 items per request                                                                                    |
| Spend entries per `POST`                            | At most 1000                                                                                                      |
| `GET /api/events` rows                              | `limit` clamped to 1 to 10000                                                                                     |
| `GET /api/spend/entries` rows                       | `limit` clamped to 1 to 5000                                                                                      |
| IDE event body                                      | At most 65536 bytes                                                                                               |
| Adversarial findings per request                    | At most 2000 findings, at most 12 participants (raisers plus assessors) per finding                               |

## Usage events

| Method  | Route                         | Auth  | Purpose                                                                  |
| ------- | ----------------------------- | ----- | ------------------------------------------------------------------------ |
| `POST`  | `/api/events`                 | Admin | Record one provenance-labelled usage event.                              |
| `GET`   | `/api/events`                 | Read  | List events for one provider.                                            |
| `GET`   | `/api/events/{id}`            | Read  | Fetch one event by id.                                                   |
| `GET`   | `/api/events/local-snapshots` | Read  | List what a local source has already sent, so a collector can reconcile. |
| `PATCH` | `/api/events/{eventKey}/cost` | Admin | Correct the cost of one stored event.                                    |

### Record an event

`POST /api/events` takes these fields.

| Field                                                                        | Required    | Notes                                                                                                                                                                                                                   |
| ---------------------------------------------------------------------------- | ----------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `provider`                                                                   | yes         | One of the provider names above.                                                                                                                                                                                        |
| `sourceId`                                                                   | yes         | Up to 100 characters; trimmed and lower-cased. Identifies the source, for example a producer id.                                                                                                                        |
| `sourceKind`                                                                 | yes         | `providerApi`, `localTelemetry`, `manual`, `synthetic` or `legacy`.                                                                                                                                                     |
| `usageScope`                                                                 | yes         | `api`, `subscription`, `mixed` or `unknown`.                                                                                                                                                                            |
| `costBasis`                                                                  | yes         | `providerEstimated`, `listPriceEstimate`, `notional`, `none` or `unknown`. `billed` is refused: billed money goes through the spend path.                                                                               |
| `inputTokens`, `outputTokens`                                                | recommended | Non-negative integers. The endpoint rejects negatives; it has no explicit presence check.                                                                                                                               |
| `cacheReadTokens`, `cacheWriteTokens`, `cacheWrite1hTokens`, `thoughtTokens` | no          | Non-negative. `cacheWrite1hTokens` is a subset of `cacheWriteTokens` and may not exceed it.                                                                                                                             |
| `costUsd`                                                                    | no          | Non-negative. Not stored when `costBasis` is `listPriceEstimate` or `notional`: those events go through the estimate path and the supplied figure is discarded. A non-zero value with `costBasis` of `none` is refused. |
| `model`, `runtime`, `sessionId`, `agentId`                                   | no          | `runtime` up to 100 characters, `sessionId` and `agentId` up to 200.                                                                                                                                                    |
| `eventKey`                                                                   | no          | Up to 200 characters. The idempotency key within a source.                                                                                                                                                              |
| `occurredAtUtc`                                                              | no          | When the usage happened. Defaults to now. Not more than 5 minutes in the future.                                                                                                                                        |
| `observedAtUtc`                                                              | no          | When it was observed. Defaults to now. Not more than 5 minutes in the future.                                                                                                                                           |
| `rawPayload`                                                                 | no          | A JSON string, default `{}`. Must be valid JSON without duplicate property names.                                                                                                                                       |

Missing any of the four provenance fields answers 400 with `SourceId, SourceKind, UsageScope, and CostBasis provenance are required.`

Responses:

- `201 Created` with `{ id }` for a new event.
- `200 OK` with `{ id, duplicate, corrected }` when the same source and key already exists: `duplicate` is true if nothing changed, `corrected` is true if the stored figures were updated. Events are source-scoped, so re-posting a corrected snapshot replaces its earlier contribution.
- `400` for any validation failure.

### List and fetch

- `GET /api/events?provider=<name>&from=<instant>&to=<instant>&limit=<n>`: `provider` is required; `from` and `to` filter on the time the usage occurred. Returns cost-correction records, oldest first (id, source id, event key, runtime, session, agent, model, input, output, cache-write and thought tokens, cost).
- `GET /api/events/{id}`: the stored event, including its raw payload. `id` must be a GUID.
- `GET /api/events/local-snapshots?sourceId=<id>`: `sourceId` is required. Returns what is stored for that source, limited to local-telemetry events that have an event key and are not sweeper tombstones (provider, time, model, whether a cost is present, runtime, provenance, event key). Costs and raw evidence are left out.

### Correct a cost

`PATCH /api/events/{eventKey}/cost?provider=<name>&sourceId=<id>` with `{ "costUsd": 1.23, "cacheSavingsUsd": 0.1 }`. Both must be non-negative; an omitted `cacheSavingsUsd` is stored as zero. An omitted `sourceId` targets legacy rows only (`legacy-api`), so a correction for any other source needs its `sourceId`. Returns `{ eventId, oldCostUsd, newCostUsd }`, or 404 when no such row exists.

## Aggregates

| Method   | Route             | Auth  | Purpose                                                             |
| -------- | ----------------- | ----- | ------------------------------------------------------------------- |
| `GET`    | `/api/aggregates` | Read  | Daily usage and cost, split by provider, model, source and basis.   |
| `DELETE` | `/api/aggregates` | Admin | Irreversible reset of one provider, or of one retired local source. |

`GET /api/aggregates?from=&to=` returns an array ordered by date, provider and model. Each element has `date`, `provider`, `model`, `sourceId`, `sourceKind`, `usageScope`, `costBasis`, `inputTokens`, `outputTokens`, `cacheReadTokens`, `cacheWriteTokens`, `cacheWrite1hTokens` (a subset of the cache-write total, not an addition), `costUsd`, `unknownCostCount`, `cacheSavingsUsd`, `unknownCacheSavingsCount` and `requestCount`. `requestCount` counts stored usage records, including records corrected to zero, rather than provider requests; a record may summarize many provider requests. Unknown cost counts say how many records had no price; see [Concepts](/docs/concepts) and [Pricing](/docs/pricing).

`DELETE /api/aggregates` takes either `provider=<name>` (deletes the provider's events and daily rows) or `sourceId=<id>` (only a legacy un-suffixed local id matching `*-local`, such as `claude-local`; per-machine ids are refused) with optional `dryRun=true` to preview. Passing both is a 400. `dryRun` applies to `sourceId` only. Returns the deleted counts.

## Sources and health

| Method | Route                       | Auth  | Purpose                                            |
| ------ | --------------------------- | ----- | -------------------------------------------------- |
| `GET`  | `/api/sources/status`       | Read  | Configuration and freshness of every source.       |
| `POST` | `/api/sources/sweep-status` | Admin | A sweeper reports sessions it could not attribute. |
| `POST` | `/api/spend/feed-status`    | Admin | A spend feed reports expenses it skipped.          |

`GET /api/sources/status` returns one row per source, ordered by id: `sourceId`, `status`, `isConfigured`, `lastAttemptAt`, `lastSuccessAt`, `latestObservationAt`, `consecutiveFailureCount`, `lastError`. `status` is the first that applies of `notConfigured`, `unavailable`, `failing` (one or more consecutive failures), `configured` (configured, no success yet), `stale` (the last success is older than twice the expected refresh interval) or `fresh`. See [Data sources](/docs/data-sources).

`POST /api/sources/sweep-status` takes `{ machine, unplaced: [{ tool, model, count }] }`. `machine` is 1 to 64 characters of `a-z`, `0-9`, `-`; `tool` is up to 40 characters of the same set; `model` up to 80 characters of letters, digits and `. _ : / -`; `count` is positive; at most 200 entries. An empty `unplaced` marks source `sweep-placement@<machine>` fresh; otherwise it is marked unavailable with a counts-only message. Returns 204. Only counts are stored.

`POST /api/spend/feed-status` takes `{ attempted, posted, skipped: [{ expenseId, reason }], reportedAt? }` with `posted` between 0 and `attempted`, at most 5000 skipped entries, `expenseId` positive and `reason` one of `no-vendor`, `no-category`, `aged-out`, `foreign-unreviewed`. A clean report marks source `portal-spend-feed` fresh; any skip marks it unavailable with a counts-only message. `reportedAt` lets a delayed report carry its own time and must not be more than 5 minutes ahead. Returns 204. Expense ids are counted and not stored.

## Spend ledger

See [Spend](/docs/spend) for how the Spend tab uses these routes.

| Method   | Route                                                   | Auth       | Purpose                                                          |
| -------- | ------------------------------------------------------- | ---------- | ---------------------------------------------------------------- |
| `GET`    | `/api/spend/entries`                                    | Admin read | Row-level ledger.                                                |
| `GET`    | `/api/spend/reporting`                                  | Read       | Aggregated billed spend for a range.                             |
| `POST`   | `/api/spend/entries`                                    | Admin      | Record one or more entries (always an array).                    |
| `PATCH`  | `/api/spend/entries/{id}`                               | Admin      | Edit an entry.                                                   |
| `DELETE` | `/api/spend/entries/{id}`                               | Admin      | Delete an entry (204, or 404 if absent).                         |
| `GET`    | `/api/spend/categories`, `/api/spend/vendors`           | Read       | List catalog items (`includeArchived=true` to include archived). |
| `GET`    | `/api/spend/categories/{id}`, `/api/spend/vendors/{id}` | Read       | One item.                                                        |
| `POST`   | `/api/spend/categories`, `/api/spend/vendors`           | Admin      | Create.                                                          |
| `PATCH`  | `/api/spend/categories/{id}`, `/api/spend/vendors/{id}` | Admin      | Edit, archive or restore.                                        |

### Record entries

`POST /api/spend/entries` takes an array of 1 to 1000 entries (an empty or longer array is a 400). Each entry:

| Field                    | Required | Notes                                                                                                                                           |
| ------------------------ | -------- | ----------------------------------------------------------------------------------------------------------------------------------------------- |
| `occurredOn`             | yes      | Charge date.                                                                                                                                    |
| `vendorId`, `categoryId` | yes      | Must exist.                                                                                                                                     |
| `amount`                 | yes      | Signed, in the entry currency. Negative is a refund or credit. Zero is refused.                                                                 |
| `currency`               | no       | Three-letter ISO code, default `GBP`.                                                                                                           |
| `description`            | no       | Up to 200 characters.                                                                                                                           |
| `source`                 | yes      | `manual`, `csv`, `portal` or `api`. Sets the provenance stored with the row (`manual-ledger`, `csv-import`, `portal-feed`, `vendor-api-entry`). |
| `entryKey`               | no       | Up to 200 characters. Within one `source`, the same key is a duplicate.                                                                         |

The response is always `200` with one result per entry, in order: `{ id, status, reason }` where `status` is `created`, `duplicate` (the row already exists; `id` is the existing one) or `rejected` (with a `reason`). A bad row never fails the batch. Reasons include an unknown vendor or category, a zero amount, a bad currency, an exchange rate that is unavailable for a non-USD, non-GBP currency, and an amount that rounds to zero GBP.

The GBP amount is `amount` times the exchange rate on the charge date, rounded to 4 decimals, and is frozen when the row is written so a historical total does not drift. All ledger rows are stored with a `billed` cost basis.

### Read the ledger

`GET /api/spend/entries?from=&to=&vendorId=&categoryId=&limit=` returns rows newest first, each with `id`, `occurredOn`, `vendorId`, `categoryId`, `amount`, `currency`, `amountGbp`, `fxRate`, `description`, `source`, `entryKey`, `recordedAt`, `sourceId`, `sourceKind`, `usageScope`, `costBasis`, `observedAt`. The raw provider payload is never returned.

`GET /api/spend/reporting?from=&to=` requires both dates; `vendorId` and `categoryId` are optional filters. It returns `entryCount`, `totalGbp`, `dailyAverageGbp` (total divided by the calendar days in the range), `projectedMonthlyGbp` (daily average times 30), `topVendorName`, `topVendorGbp`, `dailySeries` (`date`, `amountGbp`), `vendorSeries` (`vendorId`, `name`, `provider`, `amountGbp`, largest first) and `categorySeries` (`categoryId`, `name`, `amountGbp`, largest first).

### Edit an entry

`PATCH /api/spend/entries/{id}` accepts any of `occurredOn`, `vendorId`, `categoryId`, `amount`, `currency`, `description`. An omitted field is unchanged. Changing the amount, currency or date re-resolves the exchange rate at the (new) charge date and recalculates `amountGbp`. The entry's `source` and `entryKey` cannot be changed. Returns the updated row, 404 if absent, or 400 for the same kinds of failure as creation.

### Catalogs

- Create answers `201` with the new item; a duplicate `key` answers 409.
- **Categories:** `POST` with `{ key, displayName, colorVar?, sortOrder }`. `key` is a slug of up to 60 characters using letters, digits, `-` and `_`, stored lower-case; a duplicate answers 409. `displayName` is required, up to 100 characters. `colorVar` is up to 60 characters. `PATCH` accepts `displayName`, `colorVar`, `sortOrder`, `archived`.
- **Vendors:** `POST` with `{ key, displayName, provider?, defaultCategoryId? }`. `provider` is optional (vendors such as CI or review tools have no token estimate); an unknown name is a 400. `PATCH` accepts `displayName`, `defaultCategoryId`, `provider` and `archived`; for `defaultCategoryId` and `provider`, an omitted property leaves the value alone and an explicit `null` clears it.
- There is no delete. Archiving (`archived: true`) is a soft delete that keeps historical entries resolvable, and `archived: false` restores.

## Subscriptions

| Method   | Route                                 | Auth  | Purpose                                           |
| -------- | ------------------------------------- | ----- | ------------------------------------------------- |
| `GET`    | `/api/subscriptions`                  | Read  | All subscriptions.                                |
| `GET`    | `/api/subscriptions/{id}`             | Read  | One subscription.                                 |
| `POST`   | `/api/subscriptions`                  | Admin | Create.                                           |
| `PUT`    | `/api/subscriptions/{id}`             | Admin | Replace.                                          |
| `PATCH`  | `/api/subscriptions/{id}/extra-usage` | Admin | Set the extra-usage amount.                       |
| `DELETE` | `/api/subscriptions/{id}`             | Admin | Delete. Answers 204 even if the id did not exist. |

Create and replace take `provider`, `name` (required, up to 200 characters), `costAmount` (non-negative), `currency` (`GBP` or `USD`), `billingInterval` (`monthly` default, or `annual`), `billingDay` (1 to 31), `billingMonth` (1 to 12, required for `annual` and ignored for `monthly`), `activeFrom` and optional `activeTo`. The response includes `id` and `extraUsageCost`. `PATCH .../extra-usage` takes `{ "amount": 12.5 }`, non-negative; `null` clears it. See [Subscriptions](/docs/subscriptions).

## Insights and suggestions

| Method   | Route                            | Auth  | Purpose                                         |
| -------- | -------------------------------- | ----- | ----------------------------------------------- |
| `GET`    | `/api/insights`                  | Read  | The 50 newest insights plus every unread one.   |
| `POST`   | `/api/insights/generate`         | Admin | Generate insights now for the current UTC date. |
| `POST`   | `/api/insights/{id}/acknowledge` | Admin | Mark an insight acknowledged (204).             |
| `POST`   | `/api/insights/{id}/explain`     | Admin | Ask the model for implementation guidance.      |
| `DELETE` | `/api/insights`                  | Admin | Delete all insights and budget-alert claims.    |
| `GET`    | `/api/suggestions`               | Read  | Suggestions in one state, with evidence.        |
| `POST`   | `/api/suggestions/{id}/dismiss`  | Admin | Dismiss.                                        |
| `POST`   | `/api/suggestions/{id}/snooze`   | Admin | Snooze.                                         |
| `POST`   | `/api/suggestions/refresh`       | Admin | Run every rule now.                             |

An insight has `id`, `generatedAt`, `insightType` (`summary`, `efficiency`, `anomaly`, `recommendation` or `budgetalert`), `title`, `body`, `data` and `acknowledged`. `GET /api/insights` returns them newest first: the 50 newest rows plus every unread one, at most 500 in all. `POST .../generate` returns `{ generated }`, the number persisted. `POST .../explain` returns `{ explanation }`, 404 for an unknown id, or 503 if `ANTHROPIC_API_KEY` is not configured. `DELETE /api/insights` answers 409 while a budget-alert email delivery is in progress (retry after its lease expires) and otherwise returns `{ deleted }`. See [Usage and insights](/docs/usage-insights).

`GET /api/suggestions?status=` takes `open` (default), `dismissed`, `snoozed` or `resolved` (anything else is a 400) and returns `{ suggestions, notEvaluated }`. Each suggestion has `id`, `key`, `ruleId`, `subject`, `severity`, `title`, `status`, `snoozedUntil`, `firstSeenAt`, `lastSeenAt` and `evidence` (rule-specific JSON). `notEvaluated` lists rules that could not run, each with `ruleId` and `reason`. `snooze` needs `?days=` from 1 to 90 (otherwise 400). Dismiss and snooze answer 204, 404 for an unknown id, or 409 for a resolved suggestion. `refresh` returns `{ rules: [{ ruleId, evaluated, reason, suggestions }] }`. See [Suggestions](/docs/suggestions).

## Reporting: budget rules and notifications

| Method   | Route                        | Auth  | Purpose                                         |
| -------- | ---------------------------- | ----- | ----------------------------------------------- |
| `GET`    | `/api/budget-rules`          | Read  | Rules with current spend and window.            |
| `GET`    | `/api/budget-rules/{id}`     | Read  | One rule.                                       |
| `POST`   | `/api/budget-rules`          | Admin | Create a rule.                                  |
| `DELETE` | `/api/budget-rules/{id}`     | Admin | Delete a rule (204, or 404).                    |
| `GET`    | `/api/notification-settings` | Read  | Masked alert recipient and Slack webhook state. |
| `PUT`    | `/api/notification-settings` | Admin | Set or clear the recipient and webhook.         |

`POST /api/budget-rules` takes `{ provider?, period, thresholdGbp }`. `period` is `daily`, `weekly` or `monthly`; `provider` is optional (omit for all providers); `thresholdGbp` must be greater than zero. It answers `201` with the stored rule; `GET /api/budget-rules/{id}` returns that stored rule without the computed spend fields. The rule starts evaluating on the day it is created (UTC); earlier spend never triggers it. The list response adds `currentSpendGbp`, `windowStart` and `windowEnd`: the daily window is yesterday, the weekly window is the last 7 days including today, and the monthly window is the current month to date, each clamped to the rule's start. See [Reporting](/docs/reporting).

`GET /api/notification-settings` returns `{ emailConfigured, emailMasked, slackConfigured, slackMasked }`. Real values are never returned: the email is masked after its first two local-part characters, and the webhook is shown as `https://hooks.slack.com/services/***`. `PUT` takes `alertEmailTo` and `slackWebhookUrl`. An omitted field is unchanged; `null` or an empty string clears it. The email must be a valid address of at most 320 characters. The webhook must be an `https://hooks.slack.com/services/...` URL of at most 2048 characters. The body must be a JSON object. `PUT` answers 200 with the same masked state as `GET`.

## Activity and GitHub

| Method   | Route                                        | Auth       | Purpose                                                                     |
| -------- | -------------------------------------------- | ---------- | --------------------------------------------------------------------------- |
| `POST`   | `/api/activity/sessions`                     | Admin      | Upsert Claude activity sessions.                                            |
| `GET`    | `/api/activity/daily`                        | Admin read | Active and wall-clock seconds per day.                                      |
| `GET`    | `/api/activity/by-project`                   | Admin read | Time per project.                                                           |
| `DELETE` | `/api/activity/sessions/disallowed-projects` | Admin      | Delete sessions outside the owner allowlist. Returns `{ deletedSessions }`. |
| `GET`    | `/api/github/prs`                            | Admin read | Pull requests.                                                              |
| `GET`    | `/api/github/reviews`                        | Admin read | Reviewer summary.                                                           |
| `GET`    | `/api/github/commits/summary`                | Admin read | Commits and line changes per repository.                                    |
| `GET`    | `/api/github/ci`                             | Admin read | Workflow-run totals and success rate.                                       |
| `POST`   | `/api/caveman-sessions`                      | Admin      | Upsert caveman savings sessions.                                            |
| `GET`    | `/api/caveman-stats`                         | Read       | Totals across caveman sessions.                                             |

The activity and GitHub reads all take `from` and `to`, and are filtered by `Activity:ProjectOwners` when it is set (see [Configuration](/docs/configuration#activity-and-github-tabs)).

- `POST /api/activity/sessions` takes `{ sessions: [{ sessionId, project, startedAtUtc, lastSeenAtUtc, activeSeconds }] }`. `sessionId` and `project` are up to 200 characters; `activeSeconds` is non-negative and may not exceed the elapsed time; no timestamp may be more than 5 minutes in the future; `sessionId` must be unique within the request. Re-sending a session only ever raises its active seconds and last-seen time. Returns `{ upserted }`, the number of sessions created or changed (an empty `sessions` list returns 0); a concurrent insert answers 409 and a retry converges.
- `GET /api/activity/daily` returns `[ { date, activeSeconds, wallClockSeconds } ]`. Sessions that cross midnight are split by elapsed time; `wallClockSeconds` merges overlapping sessions so parallel sessions count once.
- `GET /api/activity/by-project` returns `[ { project, sessionCount, activeSeconds, sharePercent } ]`, largest first. A session belongs to the day it started. See [Activity](/docs/activity).
- `GET /api/github/prs` returns pull requests created, merged or first reviewed in range: `repo`, `number`, `title`, `author`, `state`, `createdAt`, `mergedAt`, `reviewCount`, `turnaroundHours` (hours from open to first review, null if none), newest first.
- `GET /api/github/reviews` returns one row per repository and reviewer: `repo`, `reviewer`, `isBot`, `reviewCount`, `pullRequestCount`, `approvedCount`, `changesRequestedCount`, `avgFirstReviewHours`. Counts cover the range; turnaround is measured from the reviewer's first review of each pull request wherever it falls.
- `GET /api/github/commits/summary` returns `[ { repo, commitCount, additions, deletions } ]`, most commits first.
- `GET /api/github/ci` returns `[ { repo, workflowName, totalRuns, failedRuns, successRate } ]`. Failed means `failure`, `timed_out` or `startup_failure`; the success rate counts only `success` runs over all runs. See [GitHub](/docs/github).
- `POST /api/caveman-sessions` takes `{ sessions: [{ sessionId, occurredAtUtc, mode?, model?, outputTokens, estSavedTokens, estSavedUsd }] }` with the same limits (1000, unique ids, non-negative values, no future time). An existing session is replaced only when the new `occurredAtUtc` is the same or later. `GET /api/caveman-stats` returns `{ sessions, totalOutputTokens, totalEstSavedTokens, totalEstSavedUsd }`, zeros when empty.

## Adversarial review

| Method   | Route                                  | Auth  | Purpose                                          |
| -------- | -------------------------------------- | ----- | ------------------------------------------------ |
| `POST`   | `/api/adversarial-review/runs`         | Admin | Record one participant's run.                    |
| `GET`    | `/api/adversarial-review/runs`         | Read  | List participant rows (`runId` optional filter). |
| `POST`   | `/api/adversarial-review/findings`     | Admin | Replace the findings of a run.                   |
| `GET`    | `/api/adversarial-review/findings`     | Read  | List findings (`runId`, `since=YYYY-MM-DD`).     |
| `GET`    | `/api/adversarial-review/stats`        | Read  | Per reviewer and model statistics.               |
| `DELETE` | `/api/adversarial-review/runs`         | Admin | Delete all runs.                                 |
| `DELETE` | `/api/adversarial-review/runs/{runId}` | Admin | Delete one sweep.                                |

A run row has these fields on `POST`: `reviewer` (a slug: a lower-case letter then lower-case letters, digits or hyphens, up to 100 characters), `model` (up to 200), `runId` (up to 200), `role` (`reviewer` or `judge`), `inputTokens`, `outputTokens`, `costUsd`, `reviewDurationMs`, `issuesRaised`, `issuesAccepted` (all non-negative), and optional `repo` (up to 200), `summary` (trimmed, cut to 80 characters), `chunkCount` (positive) and `costUnknown`. Judge rows must have zero issue counts. A cost of zero is treated as unknown rather than free, and an unknown cost is stored as 0 with `costUnknown` true. Cost per accepted finding is `costUsd / issuesAccepted` when the cost is known and at least one finding was accepted. Posting an existing participant row corrects it in place: `201` with `{ id }` for new, `200` with `{ id, updated: true }` for an update.

A findings request is `{ runId, repo?, findings: [{ chunkId, findingId, severity, outcome, raisedBy: [{ reviewer, model }], assessments: [{ reviewer, model, verdict }] }] }`:

- `severity`: `critical`, `high`, `medium`, `low` or `unknown`.
- `outcome`: `accepted`, `refuted`, `dropped` or `indeterminate`.
- `verdict`: `agree`, `false-positive`, `needs-evidence`, `needs-repo` or `abstained`.
- `chunkId` and `findingId` are 1 to 40 characters of letters, digits, `.`, `_`, `-`; the pair must be unique in the request.
- `raisedBy` has at least one entry and may not repeat a reviewer and model pair.
- The run must already exist (404 otherwise), and `repo` must match the run.

The call replaces all stored findings for the run and returns `{ findings: <count> }`. `DELETE /api/adversarial-review/runs` returns `{ deleted }`; `DELETE /api/adversarial-review/runs/{runId}` returns `{ deleted }`, or 404 when no row has that run id.

`GET .../runs` returns `id`, `reviewer`, `model`, `role`, `repo`, `summary`, `inputTokens`, `outputTokens`, `costUsd`, `costUnknown`, `reviewDurationMs`, `issuesRaised`, `issuesAccepted`, `costPerAcceptedFinding`, `chunkCount`, `runId`, `recordedAt`. `GET .../findings` returns `runId`, `repo`, `chunkId`, `findingId`, `severity`, `outcome`, `raisedBy` and `assessments`. `GET .../stats` returns per reviewer, model and role: `runCount`, `avgCostPerRun`, `avgIssuesRaised`, `avgIssuesAccepted`, `avgCostPerAcceptedFinding`, `avgDurationMs`, `unknownCostRuns`. See [Adversarial Review](/docs/adversarial-review).

## IDE integration

| Method | Route                          | Auth | Purpose                             |
| ------ | ------------------------------ | ---- | ----------------------------------- |
| `GET`  | `/api/ide/v1/routing-snapshot` | IDE  | The current model routing snapshot. |
| `POST` | `/api/ide/v1/events`           | IDE  | Record an IDE event.                |

The snapshot carries an `ETag`; send `If-None-Match` to get `304` when it has not changed. An event body is at most 65536 bytes (413 above that) and must be a single JSON envelope with the expected partner id, classification 0, an idempotency key of up to 256 characters, a complete run identity, a time not more than 5 minutes in the future, and an event type of `run.completed`, `operator.intervened` or `routing.decided` with exactly that type's payload members. A valid new event answers `201`; a repeat with identical content answers `200`; a repeat of the same idempotency key with different content answers `409`; any invalid event answers `400`.

## Development and demo

| Method | Route                 | Auth  | Purpose                                                                                |
| ------ | --------------------- | ----- | -------------------------------------------------------------------------------------- |
| `POST` | `/api/dev/seed`       | Admin | Seed synthetic data into an empty database. Exists only in `Development` or demo mode. |
| `POST` | `/api/dev/reset-demo` | Admin | Reset the demo database to its seed. Exists only in demo mode.                         |

Seeding is idempotent: if any table the seed writes already holds data, it answers `Already seeded` and changes nothing. In `Development` the OpenAPI document is also served.

## Ingest worker

The ingest worker is not part of `/api`. It serves only `GET /healthz` with no authentication: 200 with `status`, `service`, `workerRunning`, `cyclesCompleted`, `lastCycleCompletedAt` while the polling loop runs, and 503 when it has stopped.

## References

- `src/AiObservatory.Api/Endpoints/*.cs` (one file per route group)
- `src/AiObservatory.Api/Services/AdversarialReviewService.cs`, `src/AiObservatory.Api/Services/AdversarialReviewFindingService.cs`
- `src/AiObservatory.Api/Program.cs`
- `src/AiObservatory.Ingest/Program.cs`
- `src/AiObservatory.Data/Entities/Provider.cs`, `src/AiObservatory.Data/Entities/ObservationProvenance.cs`
