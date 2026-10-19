# Subscriptions

The Subscriptions panel on the Overview lists your active plans and compares the price of each with the notional value of the usage it covered. This page covers the panel, the management dialog, billing periods and currencies, and how the notional comparison is derived.

## What the panel is for

A subscription is a flat fee. The panel answers one question: how much usage did the plan cover, valued as if you had paid API list prices? That value is called notional usage value. It is a comparison, not money charged. A plan whose notional value is far below its price is a candidate for a cheaper tier; a plan far above its price is good value. Observatory records subscriptions by hand, so the panel is only as accurate as the plans you enter.

## The panel

The panel is titled `Subscriptions`. The `i` button beside the title explains the notional comparison in the page. The `Manage subscriptions` button opens the management dialog; it is hidden in [read-only mode](/docs/access#read-only-mode).

| State                    | What you see                                                                                                |
| ------------------------ | ----------------------------------------------------------------------------------------------------------- |
| Loading                  | Nothing until the plans arrive.                                                                             |
| Error                    | `Failed to load subscriptions.`                                                                             |
| No active plan           | `No subscriptions — add one to start tracking.` and an `Add subscription` button (hidden in read-only mode) |
| One or more active plans | One card per plan group                                                                                     |

### Which plans appear

A plan appears when today is on or after its `Active from` date and either it has no `Active to` date or today is on or before it. Today is the date in your browser's time zone.

Plans that have ended or have not started yet do not appear in the panel. They stay in the management dialog.

### Cards that merge

Active plans that share the same provider, currency, billing interval, renewal month and billing day are shown as one card. For a merged card:

- the prices add up,
- the extra-usage amounts add up,
- the plan names are joined with `+`,
- the earliest `Active from` date is used, and
- the extra-usage chip becomes read-only with a tooltip, because an edit could only change the first plan. Edit each plan from `Manage subscriptions`.

### What a card shows

| Element                | Meaning                                                                                      |
| ---------------------- | -------------------------------------------------------------------------------------------- |
| Provider name          | The provider, in its colour. The card's left border uses the same colour.                    |
| Plan name              | The `Plan name` you entered.                                                                 |
| Price                  | The plan price in its own currency, followed by `/mo` or `/yr`.                              |
| `Extra:` chip          | Extra usage cost for this period. See [Extra usage](/docs/subscriptions#extra-usage).        |
| Renewal line           | `Renews on the 15th` for a monthly plan, or `Renews annually on 15 March` for an annual one. |
| `Notional usage value` | The notional value of this provider's usage in the current billing period, in pounds.        |
| Progress bar           | That value as a share of the plan's total price. The bar stops at 100 percent.               |
| Bar label              | `P% of <price> subscription price`, plus a note if some requests had no cost.                |

When the value is above the price, the value and the bar change colour to mark the plan as over its price.

## Billing periods

The comparison covers the current billing period, which starts on the most recent renewal day on or before today.

- **Monthly plan.** The period starts on the latest occurrence of the billing day. If the billing day is later than today's date, the period began in the previous month.
- **Annual plan.** The period starts on the latest occurrence of the renewal month and day. The period can be up to a year long, and the plan price is the annual price.
- **Short months.** If the billing day does not exist in a month, the last day of that month is used. A billing day of 31 renews on 28 or 29 February, 30 April and so on.
- **New plans.** If a plan became active inside the period, the period starts at its `Active from` date instead.

Example (illustrative): a monthly plan with billing day 15, checked on 3 March, has a period that began on 15 February. Checked on 20 March, the period began on 15 March.

## How the notional value is derived

1. The panel loads daily usage from the start of the earliest current period among the plans, up to today. The data is reused for five minutes before it is fetched again.
2. For each card, it takes aggregates for the card's provider, dated on or after that card's period start, whose basis is `Notional`.
3. It sums the cost of the aggregates that have a known cost, in US dollars, and converts to pounds at the live rate. See [Concepts](/docs/concepts#currency).
4. It divides by the plan's total price in pounds. A USD plan is converted at the same live rate.

What the figure includes:

- **Provider level, not plan level.** Usage is counted for the whole provider. If you have two plans with different billing cycles for the same provider, each card counts all of the provider's notional usage since its own period start. The panel cannot split usage between plans.
- **Notional usage only.** Usage with another basis, such as billed API usage, is not part of this value.
- **Price assumptions.** The panel's information popover notes that, where subscription logs omit API-only routing flags, standard or global pricing is assumed. Google uses the published short-context rate, and an unknown Claude cache-write duration uses the published five-minute rate.
- **Missing costs.** If some requests had no cost, the card shows the known subtotal and adds `N requests not reported` to the bar label. If every request in the period had no cost, the value reads `Not reported` and the label reads `<N> requests recorded · value not reported`. If there is no usage at all, the value is `£0.00` and the label reads `0% of <price> subscription price`.

## Extra usage

The `Extra:` chip holds an amount you paid beyond the plan, such as overage.

- It is optional. `— add` means none is recorded; click the chip to enter an amount.
- The amount is added to the plan price before the comparison, so the bar measures usage against price plus extra.
- Press `Enter` to save or `Escape` to cancel. Clearing the field removes the amount.
- A value that is not a number, or is negative, is ignored and the edit closes without saving. The API also rejects a negative amount.
- If saving fails, a browser alert reads `Failed to save extra usage cost: <reason>`.
- In read-only mode the chip shows the amount without an edit control, or `—`.

## Managing subscriptions

`Manage subscriptions` opens a dialog titled `Manage subscriptions`. It lists every plan, including ended and future ones. Each row shows the provider badge, the plan name and the price with `/mo` or `/yr`.

Row actions:

- `Edit` opens the form with the plan's values.
- `Delete` asks `Delete?` with `Yes` and `No`. Deleting is permanent.

Below the list, `+ Add subscription` opens an empty form. The form starts with provider `Anthropic`, currency `GBP` and interval `Monthly`.

### Fields

| Field                           | Required    | Rules                                                                                                           |
| ------------------------------- | ----------- | --------------------------------------------------------------------------------------------------------------- |
| `Provider`                      | Yes         | One of the providers Observatory knows: Anthropic, Copilot, Google, OpenAI, Moonshot, xAI, Meta, z.AI, DeepSeek |
| `Plan name`                     | Yes         | Up to 200 characters                                                                                            |
| `Monthly cost` or `Annual cost` | Yes         | Zero or more. The label follows the billing interval.                                                           |
| `Billing interval`              | Yes         | `Monthly` or `Annual`                                                                                           |
| `Renewal month`                 | Annual only | A month from January to December                                                                                |
| `Currency`                      | Yes         | `GBP (£)` or `USD ($)` only                                                                                     |
| `Billing day`                   | Yes         | A whole number from 1 to 31                                                                                     |
| `Active from`                   | Yes         | A date                                                                                                          |
| `Active to (optional)`          | No          | A date. Leave blank for a plan with no end.                                                                     |

For a monthly plan the renewal month is ignored and stored as empty.

### Messages

The form checks its values first and shows one of these messages:

- `Plan name is required.`
- `Active from date is required.`
- `Cost must be zero or more.`
- `Renewal month is required.`
- `Billing day must be between 1 and 31.`
- `Currency must be GBP or USD.`

If the API refuses a save, the dialog shows `Failed to save — please try again.` For a failed delete it shows `Failed to delete — please try again.` The API repeats the same checks, so a request that skips the form gets the matching reason in its 400 response. See [API](/docs/api).

### Ending or replacing a plan

There is no `active` switch. To end a plan, set `Active to`. From the next day it leaves the panel but stays in the dialog. To change a price, edit the plan, or end it and add a new one. The panel uses only the plan values currently stored, so an edit applies to the whole current billing period.

Editing a plan keeps its extra-usage amount. A new plan starts with none.

## How plans feed other features

- The [Suggestions](/docs/suggestions) plan rules read the same plans. They compare a provider's notional usage with the summed monthly price of its active plans.
- The insight generator includes active plans in its prompt, with annual plans divided by twelve for a monthly total. See [Usage and insights](/docs/usage-insights#how-insights-are-generated).

## References

- `src/AiObservatory.Web/src/components/SubscriptionPanel.tsx` for grouping, periods and the comparison.
- `src/AiObservatory.Web/src/components/SubscriptionModal.tsx` for the form and its messages.
- `src/AiObservatory.Web/src/lib/subscriptions.ts` for the billing-period calculation and the notional sum.
- `src/AiObservatory.Api/Endpoints/SubscriptionsEndpoints.cs` for the server-side validation.
