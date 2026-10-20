# Spend

The Spend tab is the ledger of billed charges. It lists every charge and refund, lets an administrator add entries and manage the vendor and category catalogs, and shows the totals and trend for the current filter. This page covers the ledger, how entries are created and where feed entries come from, currency conversion, and the catalogs.

The ledger is the source of "billed spend" in Observatory, and of the figures on the [Reporting](/docs/reporting) tab. It holds charges that have actually been billed. It never holds estimated costs or subscription notional value; see [Concepts](/docs/concepts).

The Spend tab is hidden in read-only mode, and the server restricts the row-level ledger endpoint to a signed-in user or the admin API key (the read-only key is refused), because the ledger lists individual charges. This page calls those callers "administrators". The aggregate figures on the Reporting tab are available to read-only viewers. See [Access](/docs/access) and [Authentication](/docs/authentication).

## The page at a glance

From top to bottom the tab shows:

1. The period controls (selected and comparison periods).
2. The filter bar (category, vendor) and, for administrators, the **Manage catalog** and **Add entry** buttons.
3. Four summary cards: Filtered total, Entries, Largest category, and a comparison card.
4. The **Billed spend over time** chart.
5. The ledger table.

## Period controls

The controls are the same as on the Reporting tab and are described in full under [Choosing periods](/docs/reporting#choosing-periods). In short: presets for 7, 31 and 90 days, This month, Last month and This quarter; editable start and end dates; and a comparison period that is the **Previous period** by default or any range you enter. The default is the last 31 days, ending today, in your browser's local time.

## Filters

| Filter   | Effect                                                                                 |
| -------- | -------------------------------------------------------------------------------------- |
| Category | Limits the totals, the chart and the ledger to one category. Default `All categories`. |
| Vendor   | Limits them to one vendor. Default `All vendors`.                                      |

The filters apply to the selected period and to the comparison period alike, so the comparison stays like-for-like. The pickers list only active (not archived) categories and vendors. If you archive the category or vendor you are filtering on, the filter quietly resets to "All".

## Summary cards

Every figure here describes the current filter and period, not the calendar month.

| Card                                 | Meaning                                                                                                                                                                                                                                                                                                  |
| ------------------------------------ | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Filtered total                       | Net billed amount in GBP for the period and filter.                                                                                                                                                                                                                                                      |
| Entries                              | Number of entries counted in that total.                                                                                                                                                                                                                                                                 |
| Largest category                     | The category with the largest net amount, or `—` when there are no entries.                                                                                                                                                                                                                              |
| vs `<previous or comparison> period` | Shown beside the three above. Reads `£X higher`, `£X lower` or `No change`, with a percentage of the comparison total to one decimal. When the comparison total is zero it reads `No prior spend`, and when the comparison total is negative it reads `Compared with <amount>` in place of a percentage. |

While the figures load, a placeholder is shown. If there are no entries for either period, the chart reads `No billed spend reported for either period.`

## Billed spend over time

The chart is the same paired-bar chart used on the Reporting tab: selected and comparison periods side by side, aligned by position rather than by date, with weekly bars when either period is longer than 92 days. See [Billed spend chart](/docs/reporting#billed-spend-chart) for details.

## The ledger table

The ledger table lists the entries that match the period and filters. Its caption for assistive technology is "Every billed spend entry matching the current filter".

| Column      | Meaning                                                                                                                                                                                                                       |
| ----------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Date        | The charge date (when the charge occurred, not when it was recorded).                                                                                                                                                         |
| Vendor      | The vendor's display name. Shows `—` if the vendor cannot be resolved.                                                                                                                                                        |
| Category    | The category's display name.                                                                                                                                                                                                  |
| Description | Free text, up to 200 characters, or empty.                                                                                                                                                                                    |
| Amount      | The amount in GBP. A refund or credit is negative, drawn in a distinct colour and announced to screen readers with the word `Refund:`. For a non-GBP entry the original amount follows in brackets, for example `(US$12.00)`. |
| Source      | How the entry arrived: `manual`, `csv`, `portal` or `api`. See [Where entries come from](#where-entries-come-from).                                                                                                           |

Click the Date, Vendor, Category or Amount header to sort. The default is Date, newest first. A new column sorts descending first; clicking the active column reverses it. Vendor and category sort by display name.

Archived vendors and categories still show their names on historical entries, so retiring a catalog item never blanks the history.

An empty table reads `No spend recorded for this filter.`

### Row limit

The ledger endpoint returns at most 5,000 entries, newest first. The totals and chart come from a separate aggregate query that has no such limit. When the table holds fewer rows than the card says, a note appears: `Showing the newest N of M entries — narrow the date range to see older ones.` Narrow the period to see the rest.

### Editing an entry

Administrators see an **Edit** button beside **Delete** on each row. It opens the **Edit spend entry** dialog, filled from the entry. The fields are those of the Add dialog: date, vendor, category, amount with the Charge or Refund choice, currency and description. A refund shows as a positive amount with Refund selected.

- Only what you change is sent, so fixing a description never touches the stored exchange rate.
- Changing the amount, currency or date recalculates the exchange rate and GBP amount at the (possibly new) charge date. See [Correcting an entry](#correcting-an-entry).
- Changing the vendor does not change the category. The category follows the vendor's default only when you add an entry.
- A date you leave alone is accepted even if it now falls outside the selected period. A date you change is held to the period, as when adding.
- Clearing the description removes it.
- A vendor or category that has since been archived stays selectable on an entry that already uses it, so the entry can be saved without being moved.
- The entry's source and entry key cannot be changed, so an imported entry stays marked as imported.
- Saving with no change closes the dialog without a request. A failure shows the server's message and leaves the dialog open.

### Deleting an entry

Administrators see a **Delete** button on each row. Select it, then **Confirm** (or **Cancel**). Deletion is permanent and removes the entry from every total. A failure shows `Failed to delete entry: <reason>`. While a delete is in progress the buttons are disabled so a double click cannot send a second request.

## Adding an entry

Administrators select **Add entry** to open the **Add spend entry** dialog.

| Field       | Rules                                                                                                                                                                                                                                                                                                |
| ----------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Date        | Defaults to today, or to the nearest edge of the selected period if today falls outside it. Limited to the currently selected period: a date outside it is rejected with `Date must be between <from> and <to>`, so that you can see the entry you add. Widen the period first to back-date further. |
| Vendor      | Active vendors only. Choosing a vendor sets the category to that vendor's default category, when the default is active.                                                                                                                                                                              |
| Category    | Active categories only.                                                                                                                                                                                                                                                                              |
| Amount      | A positive number. Zero and negative values are rejected in the form with `Amount must be a positive number — use Refund to record money back`.                                                                                                                                                      |
| Direction   | **Charge** or **Refund**. Refund stores the amount as negative.                                                                                                                                                                                                                                      |
| Currency    | `GBP` or `USD` in the form.                                                                                                                                                                                                                                                                          |
| Description | Optional, up to 200 characters.                                                                                                                                                                                                                                                                      |

Select **Save**. Errors appear in the dialog: a missing vendor or category reads `Pick a vendor and a category`, and a row the server rejects shows the reason. On success the dialog closes and all spend figures refresh.

Manual entries are deliberately not de-duplicated. Entering the same charge twice creates two rows so that you notice.

## Where entries come from

The `Source` column records the route an entry took. The same ledger and totals serve all of them.

| Source value | Route                                                                                           | Notes                                                                                                                    |
| ------------ | ----------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------ |
| `manual`     | The **Add entry** dialog, or an API post with source `manual`.                                  | No idempotency key.                                                                                                      |
| `csv`        | An API post with source `csv`, typically a statement import.                                    | Deduplicated when the post carries an `entryKey`, so re-importing an overlapping file adds nothing twice.                |
| `portal`     | An API post with source `portal`, from an external expense feed.                                | Deduplicated when the post carries an `entryKey`. The feed can also report which expenses it could not post (see below). |
| `api`        | Entries written by Observatory itself from a vendor's billing API, or posted with source `api`. | Billing-feed entries are keyed by Observatory and kept in step with the vendor's own figures.                            |

### Billing sources that write to the ledger

When the matching credentials are configured, Observatory pulls billed figures from vendor billing APIs and writes them to the ledger as `api` entries, in the vendor's currency, converted to GBP when written. Each is stored together with the provider's own billing record. Vendor and category for each feed:

| Feed                        | Vendor                    | Category                               | Dating and amounts                                                    |
| --------------------------- | ------------------------- | -------------------------------------- | --------------------------------------------------------------------- |
| Anthropic cost report       | Anthropic                 | API Usage                              | One entry per cost bucket, dated by the bucket's start (UTC), in USD. |
| OpenAI costs                | OpenAI                    | API Usage                              | One entry per cost bucket, dated by the bucket's start (UTC).         |
| Google Cloud Billing export | Google                    | Cloud                                  | Per service and SKU per usage day, net of credits.                    |
| GitHub billing              | GitHub Actions, or GitHub | CI, Cloud, Code Review or Subscription | See below.                                                            |

The GitHub billing sync runs once a day with the other background jobs, and only when both `GITHUB_TOKEN` and `GITHUB_BILLING_ORG` are configured. It reads the current and previous calendar years and groups lines by month, product and SKU. Each group becomes one entry in USD, dated the first of the month, so the open month's entry is updated with a rising amount as the month progresses. Products map as follows:

| GitHub product    | Vendor         | Category     |
| ----------------- | -------------- | ------------ |
| Actions           | GitHub Actions | CI           |
| Packages          | GitHub         | Cloud        |
| `code_quality`    | GitHub         | Code Review  |
| `ghas`            | GitHub         | Subscription |
| Any other product | GitHub         | Subscription |

For provider setup see [Providers](/docs/providers). Each of these rows is corrected in place when the vendor revises a figure, and removed if the net amount becomes zero. They are billed evidence, which is why they appear here and not as estimates.

The billing feeds find their vendor and category by key (`anthropic`, `openai`, `google`, `github`, `github-actions`, `api-usage`, `ci`, `cloud`, `code-review`, `subscription`). If a feed's vendor or category key does not exist, that write fails and the source reports an error. Keys cannot be edited and items cannot be hard-deleted, so this only happens on a catalog that never had the seeded items.

### Portal feed status

An external expense feed can post a status report to `POST /api/spend/feed-status` (admin key or signed-in user) after each run, listing the expenses it could not post, each with a reason: `no-vendor`, `no-category`, `aged-out` or `foreign-unreviewed`. Observatory stores only counts, never expense text or amounts. A report with skips marks the source `portal-spend-feed` as unavailable with a message like `3 expenses skipped: 2 no-vendor, 1 no-category`. A clean report marks it fresh. The source is expected daily and reads stale after two days. See [Data sources](/docs/data-sources) for how states appear.

## Currency and exchange rates

The ledger keeps three figures per entry: the amount as charged with its currency, the GBP amount, and the exchange rate used.

- The GBP amount is calculated once, when the entry is recorded, from the rate on the entry's charge date, and is not recalculated afterwards unless the entry itself is corrected (a `PATCH`, or a vendor revising a billing-feed figure). A historical total therefore does not drift with the market.
- Totals always sum the GBP amount. Sign is kept: a refund is negative in both.
- Rates are daily ECB reference rates from the Frankfurter service. The GBP amount is rounded to four decimal places using banker's rounding.
- GBP entries use a rate of 1.
- If the rate cannot be fetched for USD, a built-in fallback rate of 0.79 is used and not cached. For any other currency, the entry is rejected rather than stored at an unverifiable rate.
- An entry whose converted amount rounds to zero is rejected: `Amount converts to zero GBP at the stored 4-decimal scale — the entry is too small to record`.
- Any three-letter ISO 4217 code is accepted by the API; the dialog offers GBP and USD.

## Entry rules enforced by the API

`POST /api/spend/entries` always takes an array, so the manual form, a file import and a feed share one route. Up to 1,000 entries per request. Each row gets its own verdict in the response, so one bad row does not stop the rest:

| Status      | Meaning                                                                        |
| ----------- | ------------------------------------------------------------------------------ |
| `created`   | The entry was stored.                                                          |
| `duplicate` | An entry with the same source and entry key already exists; nothing was added. |
| `rejected`  | The row failed validation or conversion; a reason is returned.                 |

Validation rules:

| Field                    | Rule                                                                                                         |
| ------------------------ | ------------------------------------------------------------------------------------------------------------ |
| `occurredOn`             | Required date.                                                                                               |
| `vendorId`, `categoryId` | Must be existing ids.                                                                                        |
| `amount`                 | Signed; negative is a refund or credit. Zero is rejected.                                                    |
| `currency`               | Three letters, stored upper-case; defaults to `GBP`.                                                         |
| `description`            | At most 200 characters.                                                                                      |
| `entryKey`               | At most 200 characters. Unique per source when present. Entries with no key are never treated as duplicates. |
| `source`                 | `manual`, `csv`, `portal` or `api` (case-insensitive).                                                       |

Entries posted with source `csv` or `portal` carry the provenance of manual evidence; entries with source `api` are recorded as coming from a provider API. Every entry posted through this route is recorded with a billed cost basis.

### Correcting an entry

`PATCH /api/spend/entries/{id}` changes any of amount, currency, date, vendor, category and description. Changing the amount, currency or date recalculates the exchange rate and GBP amount at the (possibly new) date. The same validations apply, and unknown vendors or categories return `400`. `DELETE /api/spend/entries/{id}` removes one entry, returning `404` if it does not exist.

### Reading the ledger over the API

| Route                      | Purpose                                                                                                                                                                                             | Access               |
| -------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | -------------------- |
| `GET /api/spend/entries`   | Rows, newest first. Optional `from`, `to` (`yyyy-MM-dd`), `vendorId`, `categoryId`, and `limit` (1 to 5000, default 5000).                                                                          | Administrators only. |
| `GET /api/spend/reporting` | Aggregates for a period: entry count, total, daily average, 30-day run rate, top vendor, daily, vendor and category series. `from` and `to` are required; `vendorId` and `categoryId` are optional. | Read key or above.   |

The row response omits the stored provider payload. See [API](/docs/api) for the full route list and [Authentication](/docs/authentication) for keys.

## Vendor and category catalog

Select **Manage catalog** (administrators only) to open the **Manage spend catalog** dialog. It has a **Categories** tab and a **Vendors** tab. Vendors are suppliers; categories describe what was purchased, so one vendor can span several categories.

Both lists include archived items, shown with an `Archived` badge, so that you can un-archive them.

### Keys and names

- A **key** is a short identifier set when the item is created and never edited. Imports and feeds refer to items by key. Keys are lower-cased and may contain only letters, digits, hyphens and underscores, up to 60 characters.
- A **display name** is what the interface shows. It is required, up to 100 characters, and can be renamed.
- Creating an item with a key that already exists returns a conflict error, including when two requests race.

### Categories

Columns are the key, display name, an `Archived` badge when relevant, and **Rename** and **Archive**/**Unarchive** buttons. To add a category complete **Key** (up to 60 characters), **Display name** (up to 100) and **Sort order** (a number, default 0), then select **Add category**. Categories are listed by sort order, then name.

Seeded categories: Code Review, Credits, CI, Subscription, Cloud and API Usage.

### Vendors

Each vendor row shows its key, display name, its provider (or `No provider`), and a **default category** selector. Changing the selector shows **Save** and **Cancel** buttons; choosing `No default category` clears the default.

To add a vendor complete **Key**, **Display name**, **Provider** and **Default category**, then select **Add vendor**.

| Field            | Meaning                                                                                                                                                                                                                                                                                                                                               |
| ---------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Provider         | `No provider (not token-metered)` or one of the providers Observatory knows. The provider links a vendor's spend to usage for that provider, and decides whether the vendor's spend counts towards a provider-specific [budget rule](/docs/reporting#which-spend-counts). Use no provider for tools that are not token-metered, such as a CI service. |
| Default category | The category pre-selected in the **Add entry** dialog for this vendor.                                                                                                                                                                                                                                                                                |

Seeded vendors: Anthropic, GitHub Actions, CodeRabbit, Gitar, Moonshot, OpenAI, Google, Microsoft, OpenRouter, Blacksmith, GitHub Copilot and GitHub.

The provider and default category can also be changed over the API (`PATCH /api/spend/vendors/{id}`), where an omitted field means "leave unchanged" and an explicit `null` means "clear". The dialog supports renaming, archiving and the default category, but not changing a vendor's provider after creation.

### Archiving

Archiving is a soft delete. Archived items disappear from the pickers on the filter bar and the entry dialog, but historical entries keep resolving their names and remain in all totals. Un-archive to make an item selectable again. There is no hard delete.

Errors in the dialog appear inline (for example `Key and display name are required`, or the server's conflict message).

## What changes elsewhere when you edit the ledger

Adding, deleting or correcting an entry changes the Overview's billed-spend card, the Reporting cards and charts, the budget rules' current spend, and the Spend tab itself. Renaming or archiving a vendor or category refreshes the reporting series so names update. Overview figures reconcile to the ledger; see [Overview](/docs/overview).

## References

- `src/AiObservatory.Web/src/pages/SpendPage.tsx`
- `src/AiObservatory.Web/src/components/SpendFilterBar.tsx`
- `src/AiObservatory.Web/src/components/SpendTotals.tsx`
- `src/AiObservatory.Web/src/components/SpendLedgerTable.tsx`
- `src/AiObservatory.Web/src/components/SpendEntryModal.tsx`
- `src/AiObservatory.Web/src/components/SpendCatalogModal.tsx`
- `src/AiObservatory.Web/src/components/SpendCategoryCatalog.tsx`
- `src/AiObservatory.Web/src/components/SpendVendorCatalog.tsx`
- `src/AiObservatory.Api/Endpoints/SpendEntriesEndpoints.cs`
- `src/AiObservatory.Api/Endpoints/SpendCatalogEndpoints.cs`
- `src/AiObservatory.Api/Endpoints/SpendFeedStatusEndpoints.cs`
- `src/AiObservatory.Api/Services/GitHub/GitHubBillingSyncService.cs`
- `src/AiObservatory.Data/Spend/BillingObservationWriter.cs`
- `src/AiObservatory.Data/Spend/FxRateProvider.cs`
- `src/AiObservatory.Data/Entities/SpendEntry.cs`
