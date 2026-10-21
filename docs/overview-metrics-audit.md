# Overview metrics audit

This is the canonical definition and reconciliation record for the Overview cards. Update it when a card's calculation, source authority, or audit result changes. Never include credentials, private raw payloads, or personal financial figures.

## Billed spend

### Definition

`Billed spend` is the sum of signed `SpendEntries.AmountGbp` values whose `OccurredOn` date falls inside the Overview's rolling 31-calendar-day window, including both endpoints.

- The end date is the browser's local calendar date when the Overview mounts.
- The start date is 30 calendar days before the end date.
- A charge contributes positively; a refund or credit contributes negatively.
- Foreign-currency rows use the GBP amount and FX rate frozen when the ledger row was recorded or corrected.
- The card includes rows whose cost basis is billed. It excludes token-rate estimates, provider estimates, and subscription notional value.
- The reporting endpoint groups directly over the complete ledger query. It does not use the capped ledger-list response.

Code lineage:

- Window: [`src/AiObservatory.Web/src/api/queries.ts`](../src/AiObservatory.Web/src/api/queries.ts)
- Card: [`src/AiObservatory.Web/src/components/SummaryCards.tsx`](../src/AiObservatory.Web/src/components/SummaryCards.tsx)
- Aggregate: [`src/AiObservatory.Api/Endpoints/SpendEntriesEndpoints.cs`](../src/AiObservatory.Api/Endpoints/SpendEntriesEndpoints.cs)
- Frozen GBP values: [`src/AiObservatory.Data/Entities/SpendEntry.cs`](../src/AiObservatory.Data/Entities/SpendEntry.cs)

### Reconciliation procedure

For the dates displayed by the card:

1. Read `GET /api/spend/reporting?from=yyyy-MM-dd&to=yyyy-MM-dd`.
2. Read `GET /api/spend/entries?from=yyyy-MM-dd&to=yyyy-MM-dd&limit=5000`. If `entryCount` exceeds the returned row count, retrieve smaller date slices; never reconcile a capped response as though it were complete.
3. Confirm all four values agree at ledger precision:
   - `reporting.totalGbp`;
   - the sum of raw `amountGbp` rows;
   - the sum of `dailySeries.amountGbp`;
   - the sum of `vendorSeries.amountGbp`;
   - the sum of `categorySeries.amountGbp`.
4. Group raw rows by `sourceId`, vendor, category, currency, and entry-key identity. Arithmetic agreement proves only internal consistency; it does not prove that two rows are not the same supplier charge.
5. Reconcile each acquisition lane to its authority:
   - custom or private feeds: the feed's own source records and basis;
   - `github-billing-api`: GitHub enhanced billing usage, using net amounts;
   - other provider sources: their retained `BillingObservation` identities and upstream billed export or API.
6. Record unresolved overlaps, freshness differences, missing source access, and whether supplier invoices or receipts were inspected.

### Audit — 2026-08-28

Window: **2026-07-29 through 2026-08-28**, inclusive. This audit ran against a private deployment; amounts are withheld as personal financial figures, and the record keeps the shape of the finding rather than the numbers.

The card's displayed total, its unrounded value, raw-row sum, daily-series sum, and vendor-series sum all agreed exactly, with a zero arithmetic delta — internally consistent, but financially overstated.

Source composition:

| Source | Rows | Reconciliation |
| --- | ---: | --- |
| A private expense feed | 8 | Every entry matched the feed's source records and basis. |
| Canonical GitHub provider observations | 9 | Matched the retained provider-feed snapshot. |
| Migrated legacy GitHub snapshots | 8 | Every row had a canonical counterpart and was counted a second time. |
| **Displayed total** | **25** | Arithmetically consistent but financially overstated. |

The internally corrected value at the same Observatory snapshot was roughly a sixth lower. A direct GitHub API check later in the audit had advanced slightly at the same stored FX rate; that difference was source freshness, not another ledger discrepancy.

The duplicate lineage was:

1. The original GitHub billing sync wrote deterministic `github:<month>:<product>:<sku>` API ledger rows.
2. `20260824172007_AddObservationProvenance` conservatively labelled every pre-existing spend row `legacy-spend` rather than guessing its origin.
3. The retained-observation writer introduced canonical `github-billing-api` rows but originally adopted an old key only when its row already carried the new source ID. The migrated production rows therefore did not match and new rows were inserted.
4. Production held 18 legacy GitHub rows from May through August. All 18 had canonical counterparts; no unpaired legacy GitHub row was found. Closed months May, June, and July matched their canonical counterparts exactly. August's stale snapshots differed because the canonical month continued accruing.

Remediation:

- [`20260828082356_RemovePairedLegacyGitHubSpend.cs`](../src/AiObservatory.Data/Migrations/20260828082356_RemovePairedLegacyGitHubSpend.cs) deletes only API/`legacy-spend` GitHub rows whose canonical `github-billing-api` counterpart exists.
- [`BillingObservationWriter.cs`](../src/AiObservatory.Data/Spend/BillingObservationWriter.cs) adopts an unpaired migrated GitHub row on first retained observation, preventing recurrence when an older database is upgraded.
- Custom feed and genuinely unpaired legacy spend are retained.
- This audit reconciled the Observatory to the private feed's source records and GitHub's billing API. It did not independently inspect every supplier invoice or receipt.

### Post-remediation verification — 2026-08-28

After the duplicate-removal migration deployed, the same inclusive window contained **17 rows**: 8 from the private expense feed, 9 canonical GitHub provider observations, and zero migrated legacy GitHub rows.

The raw ledger sum, `reporting.totalGbp`, daily series, and vendor series all agreed exactly. The small movement from figures recorded earlier in the audit was additional canonical GitHub source activity, not reintroduced legacy duplication.

## Spend page relationship

Spend is the ledger drill-down for Overview. Its default unfiltered date range is therefore the same rolling 31-calendar-day inclusive window as `Billed spend`; for the same mounted end date, its entry count and total must reconcile exactly with the Overview card.

Before 2026-08-28, Spend silently used a separate 90-day window. On that date it showed 54 rows for 2026-05-30 through 2026-08-28, while the Overview window contained 17 rows. The additional 37 rows were valid older spend, not an arithmetic discrepancy. Spend now takes its default from the shared `dashboardDateRange` and displays its dates explicitly.

### Spend range and comparison semantics

- The initial selected range is the Overview's rolling 31-calendar-day inclusive window. Changing Spend's range does not change Overview.
- Rolling 7-, 31-, and 90-day presets end on the current local calendar date. `This month` runs from the first of the current month through today; `Last month` is the complete preceding calendar month; `This quarter` runs from the first day of the current calendar quarter through today. Native From/To inputs accept any other inclusive range.
- Comparison is enabled by default. Rolling and custom ranges compare with the immediately preceding range of the same inclusive day count. Calendar month presets compare with the complete preceding calendar month; `This quarter` compares with the complete preceding calendar quarter.
- Editing either comparison date switches the comparison to an arbitrary custom range. `Previous period` restores the automatic rule for the currently selected range. Inverted From/To inputs are normalised rather than sent as an invalid range.
- Totals, entry count, largest category, and chart data come from `GET /api/spend/reporting`. Optional `vendorId` and `categoryId` filters are applied to `SpendEntries` before the database aggregate, and both selected and comparison requests receive the same filters. These are `AsNoTracking` reads; range and comparison controls do not create, update, or delete ledger rows.
- The reporting response is not subject to the ledger list endpoint's 5,000-row ceiling. The table remains a drill-down of the selected period only; the comparison period is not mixed into it. Its vendor/category filters are sent to the entries endpoint and applied before that ceiling, so a filtered drill-down cannot lose matching rows behind unrelated entries.
- The headline change is `selected total - comparison total`. A lower signed total is presented as lower spend and a higher signed total as higher spend. A percentage is shown only when the comparison total is positive; zero or negative baselines retain the absolute GBP change without a mathematically misleading percentage.
- The comparison chart aligns periods by ordinal day, using signed frozen GBP values and filling days without entries with zero. Ranges longer than 92 days are grouped into consecutive seven-day buckets to keep the chart legible. The chart is evidential only and does not feed any stored calculation.

Catalog semantics:

- A **vendor** is the supplier identity used for ledger grouping, not a distinct billing lane or purchase type.
- A **category** describes what was purchased and sits on each entry. One vendor can span several categories.
- A vendor therefore appears once in the vendor catalog even when its entries span several categories — the 90-day audit above included a single AI vendor whose rows comprised both Credits and Subscription entries.
- `SpendVendor.Provider` is only an optional link to a token-metered provider; it does not split the supplier into separate vendors.
- `SpendCategory.ColorVar` is an internal compatibility field retained by the API/data model. The current Spend UI does not consume it, so catalog users are not asked to enter CSS variables.
- Renames and default-category edits require an explicit row-level Save; Cancel restores the persisted value without writing. Archive/unarchive remains an explicit reversible action.
