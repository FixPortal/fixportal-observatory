# Usage and insights

This page covers the Overview panels that break usage down and interpret it: the Model breakdown table, the Provider usage share chart, the Insights feed and the Caveman stats panel. It explains how insights are generated, how repeats are suppressed, and what needs an API key.

## Model breakdown

The `Model breakdown` table lists usage for the same 31-day window as the rest of the Overview. See [Overview](/docs/overview#the-31-day-window).

### Rows

One row is one combination of provider, model, source, scope and basis. The same model under two bases, or from two sources, is two rows. Rows are never merged across bases, so each cost keeps its meaning. See [Concepts](/docs/concepts#why-totals-never-mix).

| Column      | Meaning                                                                                    |
| ----------- | ------------------------------------------------------------------------------------------ |
| `Model`     | The model name, with a provider-coloured dot and the provider name under it.               |
| `Source`    | The data source's display name.                                                            |
| `Scope`     | `API`, `Subscription`, `Mixed` or `Unknown`.                                               |
| `Basis`     | `Billed`, `Provider estimate`, `List-price estimate`, `Notional`, `None` or `Unknown`.     |
| `Requests`  | Total requests in the window.                                                              |
| `Cost`      | The row's cost in pounds, with a bar sized against the largest reported cost in the table. |
| `Cost / 1M` | Cost per million observed tokens.                                                          |

### How Cost and Cost / 1M are computed

- **Cost** is the sum of the row's daily costs in US dollars, converted to pounds at the live rate. See [Concepts](/docs/concepts#currency).
- A row has a reported cost when it has more requests than unknown-cost requests. Otherwise `Cost` reads `Not reported`.
- A row with some unreported requests still shows its known cost, with a line such as `2 observations not reported`.
- **Cost / 1M** is cost divided by observed tokens, multiplied by one million. Observed tokens are input, cache-read, cache-write and output tokens together. It is shown only when every request in the row has a cost and the row has tokens. Otherwise it reads `Not reported`.
- The tooltip on the `Cost / 1M` header says "Cost per 1 million input and output tokens". The calculation also includes cache-read and cache-write tokens in the divisor.
- A row with basis `None` has a known cost of zero and shows `£0.00`.

### Search, filter and sort

- The `Search evidence...` box matches the model name, provider, source, scope and basis labels. It ignores case.
- The provider chips, starting with `All`, filter to one provider. The chips list providers in a fixed order, with unknown providers last.
- Click a column header to sort by `Model`, `Requests`, `Cost` or `Cost / 1M`; click again to reverse. The default is `Cost` descending. A first click on `Model` sorts ascending; the other columns sort descending first.
- Rows with no reported cost count as lower than any reported cost, so they come last when sorting `Cost` or `Cost / 1M` descending (the default for those columns) and first when ascending.
- `No matching models found.` means the filters hide every row. `No usage data for this period.` means there were no aggregates at all.

## Provider usage share

The `Provider usage share` donut shows each provider's share of one measure. Use the `Provider share metric` toggle to choose:

| Option     | Measure                                         |
| ---------- | ----------------------------------------------- |
| `Notional` | Notional cost with a known value, in US dollars |
| `Tokens`   | Observed tokens                                 |
| `Activity` | Request count                                   |

The chart is described in [Overview](/docs/overview#provider-usage-share). The tooltip shows the amount and the share together, with the share to one decimal place.

## Insights

The `Insights` panel is a feed of observations written about your usage. Most come from a language model; budget alerts come from the alerting code.

### Reading the feed

Each insight shows a type, a title and a body written in Markdown. The body is sanitised before it is shown.

| Type             | Label            | Badge      |
| ---------------- | ---------------- | ---------- |
| `summary`        | `Summary`        | Plain text |
| `efficiency`     | `Efficiency`     | Green      |
| `anomaly`        | `Anomaly`        | Red        |
| `recommendation` | `Recommendation` | Plain text |
| `budgetalert`    | `Budget alert`   | Amber      |

Only unread insights are shown. The five newest appear first, and a `Show N older insight(s)` disclosure holds the rest. `No unread insights.` means there are none. The feed reads the latest 50 insights the API returns, so older ones are not visible in the dashboard even if unread.

### Actions

The two buttons are hidden in [read-only mode](/docs/access#read-only-mode).

- **`Dismiss`** marks the insight as acknowledged. It leaves the feed and stops counting toward the `New insights` card. The API still returns it, with `acknowledged` true. If the request fails, nothing is shown and the button simply becomes available again.
- **`How to implement`** appears on `Efficiency` and `Recommendation` insights. It asks the model for guidance on acting on the insight and shows the answer inline under the label `How to implement`. The answer is not stored. Reloading the page loses it. It needs `ANTHROPIC_API_KEY`; without it, or if the request fails, the panel shows `Failed to load guidance. Try again.`

## How insights are generated

Generated insights come from the intelligence worker that runs inside the API process. It does not run in a demo instance.

### Schedule

1. The worker runs once when the API starts.
2. After each cycle it sleeps until the next 00:00 UTC and runs again.
3. In each cycle it analyses every missing day, from the day after the latest analysed day up to yesterday, going back no further than seven days. On a first run it analyses yesterday.
4. For each analysis date it covers the 30 days ending on that date.

The same cycle also checks budget rules, syncs GitHub billing if configured, and sends the daily source-health digest. See [Reporting](/docs/reporting) and [Data sources](/docs/data-sources#the-daily-digest).

You can also generate insights on demand with `POST /api/insights/generate`, which analyses the 30 days ending today (UTC) and returns how many insights were stored. It needs the admin key.

### What needs an API key

Generation and `How to implement` both call the Anthropic API and need `ANTHROPIC_API_KEY` set for the API process. When it is unset or blank:

- the API logs `ANTHROPIC_API_KEY not set — AI insights and explanations are disabled.`,
- generation does nothing and stores no insights, and
- `POST /api/insights/{id}/explain` answers 503.

Budget alert insights do not need the key. The model is a fixed Claude Sonnet model chosen in code; it is not configurable. See [Configuration](/docs/configuration).

### What is sent to the model

The prompt is built from the analysed window. It contains:

- reported usage value totals, one line per cost basis, each tagged `[BILLED]`, `[PROVIDER ESTIMATE]`, `[LIST-PRICE ESTIMATE]`, `[NOTIONAL]`, `[NO COST]` or `[UNCLASSIFIED]`,
- a breakdown by provider and a breakdown by model, with requests, token ratios and cache tokens,
- the active subscriptions: plan name, price and equivalent monthly total, with USD plans converted to pounds,
- a yesterday-versus-30-day-average comparison, but only when every row in the window shares one cost basis and no cost is missing.

All money is given in pounds. Missing costs are labelled `Not reported`, and the prompt tells the model never to describe them as zero. It also tells the model not to sum figures of different bases and not to call notional value spend.

It asks for three to five insights, with Markdown bodies under 200 words. That length is a request, not a check. The model must answer through a structured tool call, so the result is a list of typed insights with an optional `costBasis` field in `data`.

Raw events, project names and repository names are not in the prompt. Provider names, model names, plan names and aggregate figures are.

### What is stored

The response is parsed as a list. An item without a title is dropped. An unknown type becomes `summary`. A `data` value that is not an object is replaced with an empty object. If the response is not valid JSON, the run fails and is logged; the worker carries on with the next day.

### Deduplication

The daily pass rebuilds its prompt from a rolling 30-day window, so an ongoing story would otherwise return every day with only the numbers changed. Before storing, each new insight is checked against unread insights, and skipped if one already covers the same subject. The rules are:

| Rule             | Detail                                                                                                                                                                                                                                                                              |
| ---------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Types checked    | `anomaly`, `efficiency` and `recommendation`. `summary` and `budgetalert` are never suppressed.                                                                                                                                                                                     |
| Age              | The existing insight must have been generated within the last 4 days.                                                                                                                                                                                                               |
| Same type        | The existing insight must have the same type.                                                                                                                                                                                                                                       |
| Same basis       | If both declare a `costBasis` in `data`, they must match. If either declares none, this check passes.                                                                                                                                                                               |
| Same subject     | They must mention a model in common. If neither mentions a known model, they must mention a provider in common.                                                                                                                                                                     |
| Subject matching | Known models are the model names in the analysed window that are at least 3 characters long. Provider names come from the usage data and the subscriptions. Matching is on whole words in the title and body, ignoring case. A shorter model id inside a longer one does not match. |
| No subject       | An insight that mentions no known model or provider is stored.                                                                                                                                                                                                                      |

Insights created earlier in the same run count as existing, so two repeats in one batch are also caught. Dismissing an insight removes it from the comparison, so a story you dismissed can return. A suppressed insight is logged on the server and not shown anywhere.

Storing is serialised per date by a database lock, and the unread insights are read after the lock is taken. The daily worker and a manual request for the same date therefore cannot both store the same batch.

### Budget alert insights

When a budget rule fires, the alert code stores an insight of type `budgetalert` and notifies the configured channels. Those insights are not generated by the model. See [Reporting](/docs/reporting).

## Caveman stats

The `Caveman` panel is a collapsible panel on the Overview. It starts collapsed and remembers whether you open it. Its header shows a one-line summary: `N sessions · £X saved`, `No sessions yet`, `Loading stats…` or `Couldn’t load stats`.

The panel reports Caveman sessions. A session is a record that an external tool sends to the API. It carries a mode, a model, the output tokens and the tool's own estimate of the tokens and money saved.

| Card                      | Meaning                                                                                                                                     |
| ------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------- |
| `Caveman sessions`        | The number of recorded sessions.                                                                                                            |
| `Tokens saved (est.)`     | The estimated tokens saved, with `of N total output`. Counts above a thousand show as `k`, above a million as `M`.                          |
| `Compression rate (est.)` | Estimated saved tokens divided by output plus estimated saved tokens, rounded to a whole percent. The sub-line reads `full mode benchmark`. |
| `Est. value saved`        | The estimated money saved, converted from dollars to pounds at the live rate.                                                               |

Facts that matter when reading it:

- **Estimates are supplied, not measured.** Observatory adds up the numbers it receives. It does not compute the savings.
- **All time.** The panel totals every recorded session, not the 31-day window.
- **Sessions arrive by `POST /api/caveman-sessions`**, which needs the admin key. The client scripts in this repository's `clients/` folder do not send Caveman sessions; another hook or script has to call the endpoint.
- **Posting rules.** A request holds up to 1000 sessions. Each has a session id of up to 200 characters, non-negative token counts and value, and a time no more than five minutes in the future. Duplicate session ids inside one request are rejected. Reposting a session id updates it only if the new time is the same or later than the stored one.

## References

- `src/AiObservatory.Web/src/components/ModelBreakdown.tsx`, `InsightsFeed.tsx` and `CavemanStatsPanel.tsx` for the panels.
- `src/AiObservatory.Api/Services/Intelligence/` for the worker, prompt, parser and deduplicator.
- `src/AiObservatory.Api/Endpoints/InsightsEndpoints.cs` and `CavemanEndpoints.cs` for the routes and their limits.
