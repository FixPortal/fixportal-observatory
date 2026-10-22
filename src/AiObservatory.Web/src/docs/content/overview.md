# Overview

The Overview tab shows the last 31 days of usage and spend on one page. This page explains every card and chart on it, how each figure is computed, and what the loading, error and empty states mean.

## The 31-day window

Every figure on the Overview uses the same rolling window: 31 calendar days, ending today and including today. The start date is 30 days before the end date.

- Today is the date in your browser's time zone when the page loads. The window does not move while the page stays open.
- The Overview sends these dates to the API as calendar dates. Usage is stored by day, so a day is the unit; there is no partial day.
- Changing the window is not possible on the Overview. For other periods, use [Reporting](/docs/reporting) or [Spend](/docs/spend).
- The Spend tab opens on the same window by default, so its entry count and total reconcile with the `Billed spend` card for the same day.

## Summary cards

Six cards sit at the top. A card shows `…` while its data loads.

### Billed spend

The card is labelled `Billed spend · 31 days`.

- **What it is:** the sum of signed ledger amounts in GBP for entries dated inside the window. Charges add, refunds and credits subtract.
- **Where it comes from:** the spend ledger, through the billed reporting endpoint. It does not include token-rate estimates, provider estimates or subscription notional value.
- **GBP amounts are frozen.** Each ledger entry stores its GBP value and the exchange rate used, converted once when the entry is written, using the rate on the entry's date. A foreign-currency entry is not revalued later.
- **When it reads `Not reported`:** when the ledger has no entries in the window. A window with entries that net to zero reads `£0.00`.
- **When it fails:** the value reads `Unavailable`, with the line `Couldn’t load billed spend — try refreshing`.

The `i` button beside the label explains it in the page.

### List-price estimate

API usage valued at public list prices. It sums the cost of daily aggregates whose basis is `List-price estimate`. It is an estimate, not billed spend.

### Provider estimate

Cost figures that a provider or a local tool reported itself and that are not invoices. It sums aggregates whose basis is `Provider estimate`.

### Subscription notional

What subscription or local activity would have cost at API list prices. No money changed hands. It sums aggregates whose basis is `Notional`.

### Reading the three estimate cards

The three cards above share the same rules.

| Element                                 | Meaning                                                                                     |
| --------------------------------------- | ------------------------------------------------------------------------------------------- |
| Value                                   | The sum of known costs for that basis, converted from USD to GBP at the current rate.       |
| `Not reported`                          | No aggregate of that basis has a known cost in the window.                                  |
| `USD basis; shown in GBP when reported` | The stored values are US dollars; the card converts them for display.                       |
| `N observation(s) not reported`         | N usage records in that basis had no cost. The value above still includes records that did. |

An aggregate contributes only when it has more records than unknown-cost records. A partly priced aggregate contributes its known subtotal, and the qualifier line tells you it is incomplete.

The conversion uses the live USD to GBP rate in your browser, cached for about 12 hours, with a fixed fallback of 0.79 if the rate service is unreachable. The `Billed spend` card does not use this rate; its GBP values are already stored. See [Concepts](/docs/concepts#currency).

### Tokens

| Element                                 | Meaning                                                                                                                                                                    |
| --------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Large value                             | Total observed tokens in millions, for example `12.4M`. It is observed input plus output. `0` if there are aggregates but no tokens.                                       |
| `Not reported`                          | There are no aggregates in the window.                                                                                                                                     |
| `N in / M out`                          | Observed input tokens and output tokens.                                                                                                                                   |
| `P% cache hit`                          | Cache-read tokens divided by observed input tokens, rounded to a whole percent. Shown only when cache-read tokens exist.                                                   |
| `Cache savings`                         | The sum of server-reported cache savings, converted to GBP and labelled `server-reported, USD-derived`. `Not reported` if no bucket with cache traffic had a known saving. |
| `N savings observation(s) not reported` | Usage records with cache traffic whose saving was not reported.                                                                                                            |

Observed input tokens are plain input tokens plus cache-read tokens plus cache-write tokens. The cache hit rate is therefore the share of all input that came from the cache. The `i` button beside the cache lines gives the same explanation.

### New insights

The count of insights you have not dismissed. It counts every unread insight the API returns, up to 500. See [Usage and insights](/docs/usage-insights).

### Unclassified cost note

Under the cards, a note can appear:

`£X reported under a cost basis the cards above don’t categorize — see the Model breakdown table below for the full figure.`

It appears when aggregates with a known, non-zero cost sit under a basis that none of the three estimate cards covers. Examples are `Unknown`, and a `Billed` aggregate that came from a usage source rather than the ledger. A basis of `None` never triggers it, because that basis always carries a zero cost. The note exists so that such a cost is not silently left out.

## Suggestions card

Below the cards is the Suggestions card. It lists rule-based advice about review-panel seats and subscription plans. See [Suggestions](/docs/suggestions).

## Usage value chart

The panel is titled `Usage value · last 31 days`. It shows one stacked bar per day.

The toggle labelled `Usage value` chooses what the bars measure:

| Option                | Measures                                  | Default |
| --------------------- | ----------------------------------------- | ------- |
| `Tokens`              | Observed tokens (input, cache and output) | No      |
| `List-price estimate` | Cost of `List-price estimate` aggregates  | No      |
| `Provider estimate`   | Cost of `Provider estimate` aggregates    | No      |
| `Notional`            | Cost of `Notional` aggregates             | Yes     |

- **Segments.** Each segment is one series: provider, source, scope and basis together. The legend lists them as `Provider · Source · Scope · Basis`. Colour follows the provider.
- **Money modes.** Only aggregates with a known cost are drawn. Values are converted to GBP at the current rate; the tooltip shows three decimals, for example `£0.123`.
- **Token mode.** The axis shows millions of tokens (`1.2M`), the tooltip shows the full count. A series whose tokens are all zero is left out.
- **One value at a time.** The chart never stacks different bases. Switching the toggle replaces the data.
- **Empty states.** `No usage data for this period.` means there are no aggregates at all. `Not reported for this period.` means there are aggregates but none with a known value in the chosen mode.

The chart does not show its own error. If the aggregates request fails, the chart shows the empty text and the page banner carries the failure. See [Loading and error banners](/docs/overview#loading-and-error-banners).

## Provider usage share

The panel is titled `Provider usage share` and draws a donut chart. The toggle labelled `Provider share metric` chooses the measure.

| Option     | Slice value                                     | Default |
| ---------- | ----------------------------------------------- | ------- |
| `Notional` | Notional cost with a known value, in US dollars | Yes     |
| `Tokens`   | Observed tokens                                 | No      |
| `Records`  | Usage record count                              | No      |

- A slice's share is its value divided by the total of all slices, as a percentage.
- The tooltip reads `<amount> · <share>%`, with the share to one decimal place. In `Notional` mode the amount is in US dollars; the other modes show tokens or usage records.
- Slices follow a fixed provider order, with unknown providers last.
- If the total is zero or nothing qualifies, the panel reads `Not reported for this period.`

## Caveman, Subscriptions, Model breakdown and Insights

These panels have their own chapters:

- The collapsed `Caveman` panel: [Usage and insights](/docs/usage-insights#caveman-stats).
- The `Subscriptions` panel: [Subscriptions](/docs/subscriptions).
- The `Model breakdown` table and the `Insights` feed: [Usage and insights](/docs/usage-insights).
- The collapsed `Data sources` panel at the foot of the page: [Data sources](/docs/data-sources).

## Loading and error banners

A banner can appear above the content of any tab. It reflects the three requests the Overview depends on: usage aggregates, insights and subscriptions.

| Banner        | Text                                                                 | Cause                                                |
| ------------- | -------------------------------------------------------------------- | ---------------------------------------------------- |
| Loading       | `Loading data — the API may take a moment to wake up on first load…` | The first requests have not returned yet.            |
| Connection    | `Couldn’t reach the API — data may be unavailable.` with `Retry`     | A request failed, or is being retried.               |
| Authorisation | `Your session has expired or you’re not authorised.`                 | A request returned 401 or 403, or sign-in timed out. |

Failed requests are retried up to three times. The details, the `Retry` button and the sign-in button are in [Access](/docs/access#the-connection-banner).

Other Overview panels report their own failures: the Suggestions card reads `Couldn’t load suggestions — try refreshing`, the Subscriptions panel reads `Failed to load subscriptions.`, and the Data sources panel reads `Couldn’t load collection status`.

## References

- `docs/overview-metrics-audit.md` for the billed spend definition and the reconciliation procedure.
- `src/AiObservatory.Web/src/components/SummaryCards.tsx` and `src/AiObservatory.Web/src/lib/costSummary.ts` for the card rules.
- `src/AiObservatory.Web/src/components/SpendChart.tsx` and `src/AiObservatory.Web/src/components/ProviderSplit.tsx` for the charts.
- `src/AiObservatory.Web/src/lib/dateRange.ts` for the window.
