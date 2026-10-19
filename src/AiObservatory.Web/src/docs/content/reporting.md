# Reporting

The Reporting tab answers "what was actually billed, and is it going up?" It compares billed spend for two periods and lets you set budget rules that raise an alert when billed spend passes a limit. This page covers the period controls, every card and chart, and how budget rules are evaluated and delivered.

Everything on this tab is billed spend in pounds sterling (GBP), taken from the [Spend ledger](/docs/spend). It never includes estimated costs or subscription notional value; see [Concepts](/docs/concepts) for why those are kept apart. The tab is visible in read-only mode, but the controls that change budget rules and notification settings are hidden there.

## Choosing periods

The strip at the top of the tab sets two periods: the **Selected** period and the period to **Compare** it with. Both are inclusive of their first and last day.

### Presets

| Preset       | Selected period                                               |
| ------------ | ------------------------------------------------------------- |
| 7 days       | The last 7 calendar days, ending today.                       |
| 31 days      | The last 31 calendar days, ending today. This is the default. |
| 90 days      | The last 90 calendar days, ending today.                      |
| This month   | The first of the current month to today.                      |
| Last month   | The whole of the previous calendar month.                     |
| This quarter | The first day of the current calendar quarter to today.       |

Quarters are the calendar quarters starting in January, April, July and October. "Today" and all dates in the period controls use your browser's local calendar date.

You can also type dates into **Selected from** and **Selected to**. Doing so switches the selection to a custom range. If you enter the dates in the wrong order, Observatory swaps them. A cleared or invalid date input is ignored.

### Comparison period

By default the comparison is the **Previous period**, worked out from the selected period:

| Selected period                     | Previous period                                                            |
| ----------------------------------- | -------------------------------------------------------------------------- |
| 7, 31 or 90 days, or a custom range | The same number of days, ending the day before the selected period starts. |
| This month or Last month            | The whole calendar month before the selected period's first day.           |
| This quarter                        | The whole calendar quarter before the selected quarter.                    |

Because "This month" and "This quarter" end today but the previous month or quarter is whole, those comparisons are month-to-date against a full period. Expect the selected total to look lower until late in the month or quarter. Use a custom range if you want a like-for-like comparison.

To compare against any other period, edit **Compare from** and **Compare to**. The comparison is then fixed ("custom") and does not follow later changes to the selected period. Select **Previous period** to return to the automatic comparison.

The period controls are shared in design with the [Spend](/docs/spend) tab, but each tab keeps its own selection.

## Summary cards

Four cards summarise the selected period. A card shows `—` when the selected period has no billed entries at all. A second line compares with the comparison period when that period also has entries.

| Card            | Meaning                                                                                                      |
| --------------- | ------------------------------------------------------------------------------------------------------------ |
| Billed spend    | The net sum of all billed ledger entries dated inside the period. Charges add, refunds and credits subtract. |
| Daily average   | Billed spend divided by the number of calendar days in the period, including days with no entries.           |
| 30-day run rate | Daily average multiplied by 30. This is a straight-line projection of the period's pace, not a forecast.     |
| Top vendor      | The vendor with the largest net billed amount in the period, with that amount below.                         |

Comparison lines read `£X higher vs <label>`, `£X lower vs <label>` or `No change vs <label>`, where the label is `previous period` or `comparison period`. They show the absolute difference in pounds, not a percentage. The Top vendor card instead adds `Previous: <vendor> · <amount>` for the comparison period.

How the figures are computed, by the server for each period:

- Entries are those with a charge date from the first to the last day inclusive. Sub-totals are grouped by day, vendor and category from the full ledger, so the cards are not affected by the ledger table's row limit.
- Daily average is the total divided by the inclusive day count, and is zero when there are no entries.
- Top vendor is the first vendor when vendors are ordered by net amount, highest first, then name. A vendor whose refunds exceed its charges can have a negative net amount.
- Amounts are the GBP values frozen when each entry was recorded, so a total for a past period does not change when exchange rates move.

## Billed spend chart

The chart titled **Billed spend — `<from>` to `<to>`** shows the selected and comparison periods as paired bars.

- Bars are aligned by position, not by calendar date: bar 1 of the selected period is plotted against bar 1 of the comparison period. The horizontal axis reads `Day 1`, `Day 2`, and so on.
- When either period is longer than 92 days, each bar becomes one week and the axis reads `Week 1`, `Week 2`, and so on. Weeks are counted from each period's first day.
- If the two periods differ in length, the shorter one has no bars for the extra positions.
- The legend names the two series `Selected period` and `Previous period` (or `Comparison period` for a custom comparison). Hover a bar for its position and amount.
- The vertical axis is in pounds.

If neither period has any entries the chart is replaced by `No billed spend reported for either period.`

## Billed spend by vendor

A horizontal bar chart of net billed spend per vendor in the selected period, largest first, with the amount written beside each bar. Bar colours follow the vendor's provider when it has one. A vendor whose net amount is negative is drawn in the warning colour. The panel reads `No billed spend reported for the selected period.` when the selected period is empty.

## Loading and error messages

- While data loads, the charts show a grey placeholder.
- `Couldn’t load billed reporting. Check the API service and try refreshing.` replaces the cards and charts when either period fails to load. The period controls and the budget panel stay usable, so you can correct a range the API rejected. The API rejects ranges where the start is after the end.
- The budget rules panel loads independently of billed reporting.

## Budget rules

Budget rules raise an alert when billed spend within a period exceeds a limit. They live in the **Budget Rules** panel below the charts.

### The rules table

| Column          | Meaning                                                                                                                                                                                                   |
| --------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Provider        | `All providers` or a single provider.                                                                                                                                                                     |
| Period          | Daily, Weekly or Monthly.                                                                                                                                                                                 |
| Current / limit | Billed spend in the rule's current window, then the limit, for example `£12.00 / £50.00`. The label beside it reads `Over limit` when spend is strictly greater than the limit, otherwise `Within limit`. |
| Last fired      | The local date and time the rule last raised an alert, or `Never`.                                                                                                                                        |

An empty list reads `No budget rules configured.`, and a load failure reads `Failed to load budget rules.`

### Adding and removing a rule

Select **+ Add rule** (hidden in read-only mode) and complete the form:

| Field           | Options and rules                                                                                                                    |
| --------------- | ------------------------------------------------------------------------------------------------------------------------------------ |
| Provider        | All providers, Anthropic, Copilot, Google or OpenAI.                                                                                 |
| Period          | Daily, Weekly or Monthly. The default is Monthly.                                                                                    |
| Threshold (GBP) | A number of at least 0.01. The server rejects a threshold of zero or less, because any spend would exceed it and alert every period. |

Select **Add rule** to save, or **Cancel**. Errors show as `Couldn’t add the rule: <reason>`. To remove a rule, select **Remove**, then **Confirm** (or **Cancel**). A rule cannot be edited; remove it and add a new one.

When you add a rule, Observatory records the current UTC date as the rule's evaluation start. Spend dated before that day never counts towards the rule, and the rule's window never reaches back past it.

### Which spend counts

A rule sums the GBP amount of ledger entries dated inside its window.

- **All providers** counts every ledger entry.
- A **single provider** counts only entries whose vendor is linked to that provider. Vendors that have no provider (a vendor created with "No provider (not token-metered)") count only towards All providers rules. Link a vendor to a provider in the [catalog](/docs/spend#vendor-and-category-catalog) if you want it included in a provider rule.
- Refunds reduce the sum.

### The window for each period

Windows use UTC dates, whereas the period pickers above use your local date, so the two can differ by a day around midnight.

| Period  | Window shown in the panel                | Window the alert check scans                                                                    |
| ------- | ---------------------------------------- | ----------------------------------------------------------------------------------------------- |
| Daily   | Yesterday, the last completed day.       | Every completed day from the rule's evaluation start through yesterday, each judged on its own. |
| Weekly  | The last 7 days, today included.         | The same last-7-days window.                                                                    |
| Monthly | The first of the current month to today. | The same month-to-date window.                                                                  |

All windows are clamped so they do not start before the rule's evaluation start.

### When alerts are checked

Alerts are not raised in real time. The check runs in the background service that also generates insights: once when the service starts, once after each insight generation run, and then every day at midnight UTC. In demo mode the background service is not started, so no alerts are raised. A rule can therefore show `Over limit` in the panel hours before it fires.

A check raises an alert when the window's billed spend is strictly greater than the threshold. To avoid repeats:

- **Daily**: one alert per day that exceeded the limit. Because the check rescans from the evaluation start each time, a day that later rises over the limit because of backdated entries still alerts once.
- **Weekly**: skipped when the rule last fired within the past 7 days.
- **Monthly**: skipped when the rule last fired this calendar month.
- Each alert is also protected by a unique record for the rule and window, so the same window can never alert twice, even if the check runs again.

When a check raises an alert it:

1. Creates an insight titled `Budget alert: <Period> billed spend exceeded £<threshold>` with a body stating the total and the threshold.
2. Updates the rule's **Last fired** time.
3. Queues the alert for delivery to email and Slack.

### Alert History

The **Alert History** panel lists budget-alert insights, newest first, up to ten, with title, body and the time generated. It reads `No budget alerts triggered.` when there are none. The panel is built from the insights list, which holds only the 50 most recent insights of all types, so older alerts can drop out of this list even though they were sent. Clearing all insights also clears the alert records, which allows the same windows to alert again.

## Notifications

The **Notifications** panel sets where alerts are delivered. It holds two optional channels, **Email** and **Slack**. Each shows `Not set` or a masked value. The stored value is never shown in full: an email appears as the first two characters of the name, then `***`, then the domain, and a webhook appears as `https://hooks.slack.com/services/***`.

| Action | Notes                                                                                                                                                              |
| ------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| Add    | Enter an address or webhook URL and select **Save**, or press Enter. A failed save keeps the field open and shows `Couldn’t save notification settings: <reason>`. |
| Edit   | Replaces the stored value.                                                                                                                                         |
| Remove | Asks for **Confirm**, then clears the channel.                                                                                                                     |

These actions are hidden in read-only mode. A load failure reads `Failed to load notification settings.`

Validation applied by the server:

- **Email**: must be a valid address with a domain, at most 320 characters.
- **Slack**: must be an `https://hooks.slack.com/services/...` URL on the default port with no embedded credentials, at most 2048 characters. A URL that merely begins with `https://hooks.slack.com` but points elsewhere is rejected.
- A request that omits one channel leaves it unchanged; sending an empty value or `null` clears it.

### How delivery works

Delivery is attempted for every alert that has not yet been sent, on each check, until a channel succeeds.

- **Slack** is sent first, as a plain message with the subject in bold. It is sent at most once per alert. A failure is logged and does not stop email.
- **Email** is sent as plain text from the configured sender. The subject reads `Budget alert: <provider> <Period> billed spend exceeded £<threshold>`, where the provider is `all` for an All providers rule. The body states the total, the threshold and the time the alert was created.
- An alert counts as delivered when at least one channel reports success.
- If no channel delivers, the alert stays pending and is retried on later checks for up to 30 days after it was created. After that it is no longer retried, so configure a channel promptly. An alert raised before any channel is configured is still delivered once you add one, within that window.
- A Slack rejection with a 4xx status (other than 429) is logged as a permanent rejection (for example a rotated webhook) and will keep failing until you correct the webhook; 5xx errors, timeouts and 429 are logged as temporary. In both cases the alert stays pending within the 30-day window.
- Email delivery is at-least-once: if the mail server accepts a message but the confirmation is lost, a duplicate can arrive. Retries reuse the same message identifier so mail systems can collapse them.
- A delivery claims a 15-minute lease on the alert so two workers do not send it twice at once.

### Operator settings for delivery

These are server environment variables (see [Configuration](/docs/configuration) for the full list):

| Setting                          | Default                                       | Effect                                                                                                                                                                                                                                              |
| -------------------------------- | --------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `BUDGET_ALERT_SMTP_HOST`         | `smtp.office365.com`                          | SMTP server. The connection uses STARTTLS.                                                                                                                                                                                                          |
| `BUDGET_ALERT_SMTP_PORT`         | `587`                                         | SMTP port. A non-numeric value falls back to 587.                                                                                                                                                                                                   |
| `BUDGET_ALERT_SMTP_USER`         | empty                                         | SMTP user. When empty, no authentication is attempted. Also the sender address if no `From` is set.                                                                                                                                                 |
| `BUDGET_ALERT_SMTP_PASS`         | empty                                         | SMTP password.                                                                                                                                                                                                                                      |
| `BUDGET_ALERT_EMAIL_FROM`        | the SMTP user                                 | Sender address. If neither this nor the user is a valid address, email is treated as not configured.                                                                                                                                                |
| `BUDGET_ALERT_MESSAGE_ID_DOMAIN` | the sender's domain, else `observatory.local` | Domain used in the message identifier.                                                                                                                                                                                                              |
| `BUDGET_ALERT_EMAIL_TO`          | none                                          | Legacy. Seeds the email recipient once, only if no notification settings exist yet. Afterwards the panel is authoritative.                                                                                                                          |
| `SLACK_WEBHOOK_PROTECTION_KEY`   | unset                                         | Passphrase used to encrypt the stored Slack webhook. When unset, the webhook is stored as given. If it is later unset or changed, the stored webhook cannot be decrypted and Slack delivery fails until you restore the key or replace the webhook. |

The recipient and webhook are stored in the database and edited from this panel; the other settings come from the environment.

## Troubleshooting

| Symptom                                                  | Likely cause                                                                                                                                                                    |
| -------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| A rule shows `Over limit` but no alert arrived           | The check has not run since the limit was crossed, no channel is configured, the rule fired earlier in its weekly or monthly window, or the SMTP sender is not a valid address. |
| A provider rule shows `£0.00` while spend exists         | The spending vendors have no provider linked. Link them in the vendor catalog, or use All providers.                                                                            |
| Cards show `—`                                           | The selected period has no billed entries. Check the [ledger](/docs/spend) and the date range.                                                                                  |
| Cards or charts are replaced by an error                 | The API could not be reached, or the date range was rejected.                                                                                                                   |
| Comparison looks too favourable in the middle of a month | "This month" is month-to-date but its previous period is a whole month.                                                                                                         |

More symptoms are listed in [Troubleshooting](/docs/troubleshooting).

## References

- `src/AiObservatory.Web/src/pages/ReportingPage.tsx`
- `src/AiObservatory.Web/src/components/ReportingCards.tsx`
- `src/AiObservatory.Web/src/components/BilledSpendChart.tsx`
- `src/AiObservatory.Web/src/components/BilledVendorSplit.tsx`
- `src/AiObservatory.Web/src/components/BudgetRulesPanel.tsx`
- `src/AiObservatory.Web/src/components/SpendRangeControls.tsx`
- `src/AiObservatory.Web/src/lib/dateRange.ts`
- `src/AiObservatory.Web/src/lib/comparisonSeries.ts`
- `src/AiObservatory.Api/Endpoints/SpendEntriesEndpoints.cs` (reporting aggregate)
- `src/AiObservatory.Api/Endpoints/BudgetRulesEndpoints.cs`
- `src/AiObservatory.Api/Endpoints/NotificationSettingsEndpoints.cs`
- `src/AiObservatory.Api/Services/BudgetAlertService.cs`
- `src/AiObservatory.Api/Services/CompositeAlertNotifier.cs`
- `src/AiObservatory.Api/Services/SmtpMailSender.cs`
