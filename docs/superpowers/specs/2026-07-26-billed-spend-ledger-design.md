# Billed spend ledger — design

Date: 2026-07-26
Status: approved, not implemented
Supersedes: the `ActualCost` proposal in the 2026-07-25 handoff brief (item 4)

## 1. Why

The observatory records **estimated** spend, derived from token counts and
published rates. It cannot record what was actually **billed**, and it cannot
record spend that has no tokens behind it at all.

`Provider` is an enum of token providers — Anthropic, Copilot, Google, OpenAI,
Moonshot. Real AI spend is wider than that: GitHub Actions minutes, Anthropic
credit top-ups, CodeRabbit, Gitar. None can be entered today.

The estimate is now trustworthy — a preceding workstream corrected a ~4x
overstatement and proved the corrected figure two independent ways
(`docs/pricing-plan.md`). That is what makes an estimate-vs-billed comparison
worth building: before it, the variance would only have measured our own bug.

## 2. What this is

A **spend ledger**: one row per charge, queryable — filter, sort, group — with
the total always reflecting the current filter, plus a time series over a
configurable period.

The primary lens is **category**, not vendor. The questions to answer are
"how much on Code Review", "how much on Credits", "how much on CI", "how much on
everything". Categories cut across vendors and vendors span categories:

| Vendor | Categories it spans |
|---|---|
| Anthropic | Subscription + Credits |
| GitHub | CI + Copilot subscription |
| CodeRabbit, Gitar | Code Review |
| Moonshot | Subscription |

### Explicitly not this

- Not aggregate-only. The brief proposed `(provider, periodStart, periodEnd,
  amount)`; that cannot express either axis above.
- Not per-model. Anthropic bills a total, not a figure per model, so per-model
  variance could only be allocated pro-rata from the estimate — inventing
  precision that was never measured. Estimates stay per-model; actuals are
  per-charge.
- Not a bank connector, receipt parser, or scheduled sync. See §4.

## 3. Privacy boundary

The repository is public; the deployed data is behind auth. The constraint is
**linkage, not amounts**: nothing may tie spend to a bank, card, invoice or
counterparty.

`SpendEntry` therefore carries no account, card, counterparty, invoice number, or
bank transaction id. Its `Source` column is provenance only — `manual` / `csv` /
`portal`.

This is enforced as a test, not a convention (§8), so a future change cannot
quietly reintroduce it.

A read-only share-link holder does **not** see the Spend tab. The shipped tab is
`readonlyHidden: true`, matching Activity and GitHub — the share link exists to show
token usage, not household spend. (This corrects the design's original statement that
a read-only holder could see spend figures; the human partner ruled, during the phase-1
final whole-branch review, that the tab stays hidden and the spec is amended rather than
the code changed.)

## 4. Architecture

**Approach: a separate ledger, joined to the token pipeline only for variance.**

Considered and rejected:

- *Actuals as a kind of `UsageEvent`.* Every existing chart and budget rule sums
  `CostUsd`; introducing a second row type means each either double-counts or
  needs a filter, and the failure mode is a silently wrong total. `UsageEvent` is
  also immutable and idempotent by design, while ledger rows must be edited and
  deleted.
- *Ledger plus a pre-aggregated `SpendDaily` rollup.* The rollup is the part that
  drifts. Volume is a few hundred rows a year — a direct `GROUP BY` is instant.
  Revisit only if charts ever become slow, which manual and monthly-invoice entry
  will not cause.

The two datasets differ structurally, which is why they stay apart:

| | `UsageEvent` (estimate) | `SpendEntry` (actual) |
|---|---|---|
| Origin | machine-derived from transcripts | human / bank-derived |
| Mutability | immutable, idempotent | edited and deleted |
| Volume | ~4,700 / month | tens / month |
| Truth status | inferred from published rates | what was charged |

Keeping hand-entered money physically out of the token pipeline means a bad
ledger row can never corrupt a token figure.

## 5. Data model

Three tables in `AiObservatory.Data/Entities/`, following existing conventions
(`Guid` id, NodaTime, `Provider` value-converted to string).

### `SpendCategory` — user-managed

```
Id, Key (unique slug: "code-review"), DisplayName ("Code Review"),
ColorVar, SortOrder, ArchivedAt (Instant?)
```

`Key` is the stable handle imports and the portal feed reference, so renaming the
display name never breaks a feed. `ArchivedAt` is a soft delete — a retired
category must still resolve for historical rows.

User-managed rather than a C# enum: a new spend type should be data entry, not a
migration and a deploy. The alternative reliably produces an `Other` dumping
ground.

### `SpendVendor` — user-managed

```
Id, Key (unique slug: "coderabbit"), DisplayName,
Provider (Provider? — nullable link to the token enum),
DefaultCategoryId (Guid?), ArchivedAt (Instant?)
```

`Provider` is non-null for the five token providers and null for CodeRabbit,
Gitar and GitHub Actions. It is the **only** join between actual and estimate, so
variance is available exactly where an estimate exists and is structurally
impossible where it does not.

This keeps `Provider` meaning what it means today — "a provider whose tokens we
can meter" — rather than stretching it to cover CI minutes.

`DefaultCategoryId` pre-fills the entry form and lets a CSV omit the category
column.

### `SpendEntry` — the ledger

```
Id, OccurredOn (LocalDate), VendorId, CategoryId,
Amount (decimal), Currency (ISO 4217),
AmountGbp (decimal), FxRate (decimal),
Description (string?, <= 200), Source (Manual|Csv|Portal),
EntryKey (string?), RecordedAt (Instant)
```

Unique index on `(Source, EntryKey)`.

Three decisions worth their reasons:

- **Category sits on the entry, not the vendor.** Anthropic spend lands in both
  Subscription and Credits. The vendor default is a convenience, not a
  constraint.
- **`AmountGbp` and `FxRate` are frozen at write**, using the rate on
  `OccurredOn`. Totals sum `AmountGbp`. This diverges from the convert-at-render
  convention used for token costs — deliberately. That convention is right for an
  estimate and wrong for a record of what was paid: converting at render makes a
  March top-up show a different figure every day and an annual total drift with
  the exchange rate. The stored rate keeps every conversion auditable.
- **`EntryKey` is the duplicate defence**, reusing the `UsageEvent.EventKey`
  pattern that already works in this codebase.
- **`Amount` is signed** — positive is a charge, negative a refund or credit;
  only zero is barred (`CK_SpendEntry_Amount_NonZero`). Added 2026-07-27 by
  migration `AllowNegativeSpendAmounts`, replacing the original pair of
  non-negative constraints. A refund flag was considered and rejected: keeping
  the sign on the amount means `AmountGbp` stays the one column every aggregate
  sums *unconditionally*, whereas a flag would oblige every total, breakdown and
  time series to remember to subtract — and the one that forgot would silently
  overstate. Phase 1 shipped without this because the CSV load was debits-only;
  real data (three credits totalling £492.80) disproved that within a day. Full
  reasoning in
  [`2026-07-27-tax-portal-spend-feed-design.md`](2026-07-27-tax-portal-spend-feed-design.md) §3.

## 6. API

All routes under `/api`, so the existing `ApiKeyEndpointFilter` applies without
new work: **GET = readonly-or-admin, any write = admin only**, and an Entra
bearer token grants full access.

```
GET    /api/spend/entries    ?from&to&vendor&category&sort&limit
GET    /api/spend/summary    ?from&to&groupBy=category|vendor|month
POST   /api/spend/entries    always an array
PATCH  /api/spend/entries/{id}
DELETE /api/spend/entries/{id}
GET|POST|PATCH  /api/spend/categories , /api/spend/vendors
```

**`POST` always takes an array, never a bare object.** The manual form posts an
array of one. This is what lets the three entry paths — form, CSV import, tax
portal — share one endpoint with one contract, one code path and one set of
tests, instead of a second batch route or a polymorphic body.

It returns a **per-row verdict** — `created` / `duplicate` / `rejected` with a
reason — rather than being all-or-nothing. A 200-row CSV with one bad date should
land 199 rows and report the one.

### Idempotency

The portal supplies its own stable `EntryKey`. A CSV row derives one from a hash
of `occurredOn + vendor + amount + currency + description`, **plus an occurrence
index** counted across the rows of the file being imported that share those same
inputs.

The index is load-bearing. Without it, two genuine identical charges on the same
day collide and the second silently vanishes — a quiet under-count. With it, both
land, and re-importing the same file reproduces identical indices, so the import
is still a no-op.

**Manual entries carry a null `EntryKey`.** PostgreSQL permits multiple nulls in
a unique index, so hand-entered rows are never deduplicated — correct, because a
person typing the same charge twice is a mistake to show them, not one to silence.
The ledger's own filtered view is where that gets spotted.

**Known limit.** The index is scoped to a single file. Two identical charges
arriving in *separate* imports are indistinguishable by content, so the second is
reported `duplicate`. The preview surfaces this before commit, and the escape
hatch is to differentiate the `description`. Accepted rather than solved: making
it stricter would break the far more common case of re-importing an overlapping
statement.

### FX

`FxRateProvider` gains `GetRateAsync(from, to, LocalDate)` against Frankfurter's
dated endpoint (the same free ECB service already in use; only the `latest` path
exists today). Historical rates are immutable, so they cache indefinitely, unlike
the 12-hour cache on `latest`. `Currency == "GBP"` short-circuits to rate `1`,
which will be most rows.

### Out of scope, by design

No bank or statement connector, no PDF or receipt parsing, no scheduled sync. The
private tax portal owns transaction ingest, receipt parsing and vendor matching,
and pushes finished spend lines — the same direction of flow as
`observe-sweep.ps1` for tokens.

## 7. UI

A new **Spend** page, analysis-led. Six regions, top to bottom:

1. **Filter bar** — date range (reuse `DateRangePicker`), category, vendor,
   granularity, plus Add entry / Import CSV.
2. **Totals** — filtered total, vs previous period, entry count, largest
   category. Always the total of what is on screen, never the calendar month.
3. **Billed spend over time** — stacked by category. Legend entries toggle series
   in and out, and the totals in region 2 follow.
4. **Breakdown** — by category and by vendor; clicking a row filters the page.
5. **Estimate vs billed** — see below.
6. **The ledger** — every transaction, sortable on any column (reuse the
   `GitHubSortableHeader` pattern).

One filter state drives every region. That is what makes "the filtered aggregate"
unambiguous.

### Keeping estimated and billed apart

This is the one real cost of a separate ledger, and it is a UI problem.

**Two visual languages, never one chart**: dashed = estimated from published
rates, solid = actually billed. They are never combined into a single series.

Vendors with no token link are **excluded** from region 5 entirely, rather than
shown as a 100% variance against an estimate that was never possible.

### CSV import

Parsing happens **client-side**: the browser parses, the user maps columns once,
and a preview table shows verdicts before anything commits. The server only ever
receives the same JSON array the form sends — no multipart endpoint, no
server-side CSV parser, no upload storage. The preview needs client-side parsing
regardless, so a server parser would be a second implementation of the same
thing.

## 8. Failure modes

| Failure | Behaviour |
|---|---|
| CSV names an unknown vendor or category | Flagged in preview; created inline or mapped to an existing one. Never auto-created silently — that is how `CI` and `CI/CD` become separate categories. |
| CSV row has a bad date or amount | That row rejected with a reason; others still land. |
| Same charge imported twice | Reported `duplicate`, not an error. Totals unchanged. |
| Two genuine identical charges, same day | Both land, distinguished by occurrence index. |
| FX service unreachable | Write succeeds at the latest rate; `FxRate` records what was used. |
| A refund or credit is recorded | Lands as a negative `Amount`/`AmountGbp` and nets off the total through the ordinary `SUM`. The ledger row is colour-coded and announced as a refund so it cannot be misread as a charge. A zero amount is rejected. |
| Vendor or category retired | Soft-archived: hidden from pickers, still resolves for history. |
| Read-only viewer | Sees figures, no edit affordance — existing `isReadonly` pattern. |

## 9. Testing

- **Double-count guard (WAF).** Post an array, re-post it identically, assert
  every row returns `duplicate` and the period total is unchanged. The single
  most important test here: it is the failure this project has already been
  burned by.
- **Mixed-verdict batch (WAF).** One payload with a good row, a duplicate and a
  malformed row returns three distinct verdicts and lands exactly one.
- **Key derivation (unit).** Two identical same-day charges produce different
  keys; re-deriving from the same file reproduces both. Pure function, no
  database — runs in the pre-push gate without PostgreSQL, like
  `AnthropicPricingResolverTests`.
- **Dated FX (unit).** A substituted provider proves `OccurredOn` drives the
  rate, not "now", and that `GBP` short-circuits to 1.
- **Privacy boundary (architecture).** Extend the existing
  `ArchitectureTests.cs`: assert `SpendEntry` exposes no property matching
  `account|card|counterparty|iban|sortcode|transactionid`. This makes §3 a
  build-time guarantee rather than something a future change must remember.
- **Frontend (vitest).** Filter state drives every region — toggling a legend
  entry changes the headline total, not only the chart.

## 10. Phasing

This is too large for one implementation plan. Three phases, each independently
shippable and useful on its own:

**Phase 1 — the ledger.** Three tables and their migration, the CRUD endpoints,
dated FX, the manual entry form, and the ledger table with filters and the
filtered total (regions 1, 2, 6). Ends with a usable feature: you can record and
query spend by hand.

**Phase 2 — the analysis.** The time series and breakdowns with legend inclusion
toggles and click-to-filter (regions 3, 4). Purely additive; the phase-1 query
endpoints already return what it needs via `groupBy`.

**Phase 3 — bulk in, variance out.** CSV import with column mapping and preview,
and the estimate-vs-billed region (region 5). Grouped because both depend on
phase 1 being proven with real data: import is worth building once the shape is
known to be right, and variance is only meaningful once enough actuals exist to
compare.

The tax-portal feed needs no phase of its own — it posts to the phase-1 endpoint.

## 11. Decisions taken, for the record

| Decision | Choice | Why |
|---|---|---|
| Privacy line | Linkage, not amounts | Confirmed with Chris, 2026-07-26 |
| Granularity | Per charge, not monthly aggregate | "All transactions, filtered and sorted on demand" |
| Primary lens | Category, user-managed | Questions asked are category questions |
| Vendor axis | User-managed, optional `Provider` link | Non-token vendors are core, `Provider` keeps its meaning |
| Coexistence | Separate ledger (approach A) | Protects the just-corrected token pipeline |
| Entry paths | Form + CSV + portal, one endpoint | All three post the same array |
| Currency | Frozen at write, rate on charge date | Historical totals must not drift |
| Page layout | Analysis-led (layout A) | It is read more than it is fed |
| Per-model variance | No | No per-model actual exists to compare |

## 12. Open

- Whether region 5 also warrants a card on the main Dashboard, or stays only on
  the Spend page. Cosmetic; decide during implementation.
- Whether `Subscription.CostAmount` / `ExtraUsageCost` should eventually be
  superseded by ledger rows. Deliberately untouched here: this design adds a
  ledger without disturbing the existing subscription concept, and merging them
  is a separate change with its own migration story.
