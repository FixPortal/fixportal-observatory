# Source-aware observability and automatic pricing renewal

**Status: design approved 2026-08-24; implementation not started.**

## Purpose

Make AI Observatory a robust OSS candidate without pretending that every provider exposes
the same quality of usage and billing data.

The existing application has a sound core: raw usage events, daily aggregates, a separate
billed-spend ledger, transactional writes, provider-isolated polling, and candid limitations.
Its weakness is semantic rather than cosmetic. Provider API usage, provider billing,
subscription telemetry, local transcript estimates, and unsupported metrics are flattened
into totals that look equally authoritative. Stable provider corrections are also discarded
as duplicates, pricing is manually maintained, and process health does not say whether any
source is fresh.

This design preserves every useful collection route, repairs the data semantics, and disables
a function only where the upstream provider genuinely cannot supply the information.

## Goals

- Preserve all historical observations without inventing precision.
- Make origin, scope, and cost meaning first-class and queryable.
- Accept corrected provider snapshots without aggregate drift.
- Keep billed cost, list-price estimates, and subscription notional value visibly separate.
- Replace retired or imaginary provider calls with the strongest currently supported source.
- Refresh first-party pricing automatically every day with a last-known-good safety model.
- Make source freshness and failure visible without turning liveness into a dependency alarm.
- Let an OSS contributor add another provider without editing the central polling loop,
  database schema, or dashboard components.

## Non-goals

- Claiming invoice accuracy for transcript-derived estimates.
- Deriving token counts from billing records that contain no token dimensions.
- Runtime-loaded provider plugins or an out-of-process plugin protocol.
- A universal pricing rules language. Provider pricing shapes are materially different.
- Runtime dependence on BenchLM or another third-party pricing aggregator.
- Retroactively guessing provenance or pricing dimensions for ambiguous legacy rows.
- Replacing the existing billed-spend ledger.

## Truth model

Every observation carries enough metadata for the API and UI to say what it actually means.

### `SourceId`

A stable lower-case slug identifies the concrete origin, not merely the company:

- `openai-usage-api`
- `openai-costs-api`
- `codex-local`
- `anthropic-usage-api`
- `anthropic-cost-report`
- `claude-code-usage-api`
- `claude-local`
- `copilot-org-report`
- `copilot-local`
- `google-cloud-billing-export`
- `kimi-local`
- `legacy-api`

`EventKey` is unique within `SourceId`, not across the entire provider. Two legitimate sources
for one provider can therefore report independently without suppressing each other.

### `SourceKind`

- `ProviderApi` — a provider-operated usage, cost, or reporting API.
- `LocalTelemetry` — locally observed CLI/session/transcript data.
- `Manual` — a user-entered observation.
- `Legacy` — retained data whose original source cannot be recovered safely.

### `UsageScope`

- `Api` — pay-as-you-go or contracted API consumption.
- `Subscription` — consumer/team coding-product allowance or activity.
- `Mixed` — an upstream source explicitly combines scopes.
- `Unknown` — the scope cannot be established.

API and subscription observations must never be merged merely because they use the same
provider or model name.

### `CostBasis`

- `Billed` — provider-reported financial cost or a ledger entry.
- `ProviderEstimated` — a provider-produced estimate that is not yet an invoice.
- `ListPriceEstimate` — tokens rated using an observed public catalog.
- `Notional` — an API-list-price comparison applied to subscription/local activity where no
  corresponding money changed hands.
- `None` — the source reports usage but no price applies.
- `Unknown` — legacy or insufficiently described cost.

There is deliberately no numeric confidence score. Confidence would be subjective precision;
the source, scope, basis, timestamps, and freshness are the auditable facts.

### Observation time

`ObservedAt` records when Observatory obtained the value. The existing usage timestamp/date
continues to describe when consumption occurred. Both are needed to distinguish late-arriving
provider data from fresh collection of old usage.

### Aggregate grain

Daily usage aggregates retain:

- date
- provider
- model
- `SourceId`
- `SourceKind`
- `UsageScope`
- `CostBasis`

This prevents the aggregation layer from erasing the truth metadata added to events. Pricing
dimensions such as service tier and inference region remain on the raw event unless a report
needs them; their calculated costs can be summed safely within an already-labelled cost basis.

## Correctable usage snapshots

Provider reports and cumulative local snapshots are replaceable facts when they have a stable
key.

Within one database transaction:

1. Find the existing event by `SourceId + EventKey`.
2. If none exists, insert it and add its values to the matching aggregate bucket.
3. If every canonical field is identical, perform no write.
4. If it changed, subtract the old event from its old aggregate bucket, replace the event, and
   add the new values to its new bucket.
5. Permit the correction to move between dates, models, scopes, or cost bases.

An event with no key remains append-only. The API should reserve null keys for genuinely
independent manual observations; shipped collectors must generate stable keys.

Local collectors submit cumulative daily/model snapshots. Their local state files are scan
optimisations only, never the system of record. Losing local state may cause a resubmission,
but the server-side key makes that harmless.

Partial provider pages are worse than a failed poll. A paginated source is committed only
after every page and signed download has completed and validated. Otherwise the complete
attempt is rejected and the previous observation remains current.

## Provider acquisition design

### OpenAI

- The Usage API supplies API token activity.
- The Costs API supplies the financial total and is the source for billed API spend.
- Usage-derived public-list pricing may be retained for analysis, but it is not added to billed
  spend.
- Codex local telemetry is a separate subscription/notional source and cannot be reconciled to
  API billing.
- Service tier, context lane, and processing region are retained when available.

OpenAI explicitly documents that Usage and Costs can have different grouping and reconciliation
semantics. Finance views therefore use Costs; activity views use Usage.

### Anthropic and Claude

- The Messages Usage Report supplies API token activity.
- The Cost Report supplies billed API cost and non-token charges.
- Claude Code Usage supplies Team/Enterprise subscription activity where the account permits
  it.
- Local Claude transcript telemetry remains useful for Pro/Max and other accounts without the
  administrative report, but is labelled subscription/notional.
- Sources with overlapping account scope must be configured to avoid double counting.
- Cache creation is split into 5-minute and 1-hour buckets when the upstream payload exposes
  them; cache reads remain separate.
- Batch, fast mode, and inference geography are applied only when explicitly observed.

### GitHub Copilot

The retired direct metrics endpoint is removed. The current Copilot reporting flow returns a
report descriptor and signed NDJSON download rather than ordinary usage rows.

- Organisation report facts are stored in a Copilot-specific daily report entity.
- Fields that are not tokens are never converted into zero-token `UsageEvent` rows.
- Local session telemetry remains the token/activity source.
- Subscription price belongs in the spend/subscription ledger, not in a fabricated usage cost.

### Google

Google cost collection uses Cloud Billing BigQuery export and preserves service, SKU, credits,
currency, and billing period. The nonexistent Cloud Billing `/reports` route is removed.

The supplied Gemini pricing page is specifically Agent Platform pricing, not a universal Gemini
API table. A Google list-price estimate is produced only when the product, service tier, region,
modality, caching lane, and context threshold are identifiable. Otherwise the billed export is
shown and the token estimate remains unknown.

### Moonshot/Kimi

Kimi local coding telemetry remains a subscription/notional source. Direct API observations can
be added independently if a supported usage feed becomes available. The pricing feed follows
Moonshot's official documentation and distinguishes cache hit, cache miss, output, HighSpeed,
and eligible Batch usage.

The first-party rates verified during design were:

| Model | Cache hit | Cache miss | Output |
| --- | ---: | ---: | ---: |
| Kimi K3 | $0.30 | $3.00 | $15.00 |
| Kimi K2.7 Code | $0.19 | $0.95 | $4.00 |
| Kimi K2.7 Code HighSpeed | $0.38 | $1.90 | $8.00 |
| Kimi K2.6 | $0.16 | $0.95 | $4.00 |
| Kimi K2.5 | $0.10 | $0.60 | $3.00 |

Rates are USD per million tokens. Batch is 60% of standard price for K2.7 Code, K2.6, and
K2.5. BenchLM was useful for discovery but omitted HighSpeed and did not expose the official
K2.6/K2.5 cache-hit rates accurately enough to become an import source.

## Pricing catalogs

Each provider owns a small normalized catalog suited to its published shape. Shared concepts
are limited to provider, model identifier/alias, currency, source URL, observation time, and
effective date. OpenAI's service/context lanes, Claude's cache-write durations, Kimi's
HighSpeed variant, and Google's SKU/modality rules do not get forced into one nullable schema.

Unknown models or required dimensions produce a null estimate plus a visible warning. There is
no generic fallback rate. Token/activity facts are still stored.

The OpenAI local notional lane may use a documented standard, short-context, global assumption
only when the source is incapable of reporting billing dimensions. That assumption is attached
to the event and never presented as billed cost.

### Automatic renewal

Pricing is an observed source, not application configuration.

- Refresh daily and shortly after startup if no successful refresh exists within the previous
  day.
- Use first-party machine-readable sources:
  - OpenAI pricing Markdown: <https://developers.openai.com/api/docs/pricing.md>
  - Claude pricing Markdown: <https://platform.claude.com/docs/en/about-claude/pricing.md>
  - Kimi documentation index and per-model Markdown: <https://platform.kimi.ai/docs/llms.txt>
  - Google Cloud Billing Catalog/Pricing API:
    <https://docs.cloud.google.com/billing/v1/how-tos/catalog-api>
- Keep a bundled current catalog for cold start, offline development, and upstream outages.
- Parse each provider separately with strict required headings, columns, uniqueness, positive
  rate, currency, and effective-window validation.
- Store a normalized snapshot with provider, retrieval time, source URL, content hash, and raw
  evidence.
- Discard identical snapshots.
- Atomically activate a changed, valid snapshot while retaining all earlier snapshots.
- Store explicitly future-effective prices immediately and select them by usage date.
- Where a source publishes no effective date, use first successful observation as the boundary
  and mark it as observed rather than provider-declared.
- A timeout, malformed document, partial catalog, or changed page shape never replaces the
  last-known-good catalog.

Fixed HTTPS allowlists, redirect-host validation, response size limits, request timeouts, and
non-executable parsing protect the network trust boundary. Third-party pricing sites are never
an automatic fallback.

### Repricing

When a new catalog becomes active, reprice only `ListPriceEstimate` and `Notional` events for
that provider whose dimensions are sufficient. Use the event's usage date and the appropriate
effective catalog entry, then rebuild only affected daily aggregate costs transactionally.

`Billed`, `ProviderEstimated`, `Legacy/Unknown`, and dimension-incomplete events are not
rewritten. Price changes are infrequent and Observatory's data volume is modest, so a full
affected-provider scan is the deliberate initial ceiling. It can become a date/model-targeted
query if measured scale requires it.

## Source status and health

Each ingestion and pricing source maintains a `SourceSyncState` containing:

- stable source ID
- last attempt
- last successful refresh
- latest source observation time, where supplied
- consecutive failure count
- sanitized last error
- configured/unconfigured state

The poller records state on both success and failure. Provider failures remain isolated so one
outage cannot suppress unrelated sources.

`/healthz` remains process liveness: the worker loop is running and requests can be served. A
separate readiness/freshness API supplies source state to the dashboard. Stale pricing or one
failed provider does not make the process unhealthy, but it is visible. Secrets, response
bodies, signed URLs, and credential-bearing query strings must not enter stored errors.

## Provider extension boundary

The current `ProviderPollingWorkerService` names every concrete provider. Replace that list with
two small capabilities:

```csharp
public interface IUsageSource
{
    string SourceId { get; }
    Task IngestAsync(LocalDate from, LocalDate through, CancellationToken cancellationToken);
}

public interface IPricingSource
{
    string SourceId { get; }
    Task<PricingSnapshot?> FetchAsync(CancellationToken cancellationToken);
}
```

The exact result types may be refined during planning, but the boundary is fixed: workers
consume registered collections and do not switch on provider types. A source handles its own
daily/range granularity and pagination internally.

Adding a compile-time provider requires:

1. A provider identifier and display metadata.
2. One or both capability implementations.
3. Dependency-injection registration.
4. Parser/ingestion contract tests and setup documentation.

`Provider` is already persisted as text, so adding an enum member requires no database schema
migration. `SourceId` distinguishes multiple acquisitions for that provider.

The frontend's remaining hard-coded provider arrays and prose move to its existing provider
registry. Unknown providers retain the existing visual fallback. Full presentation support
therefore needs one registry entry rather than component changes.

Runtime plugin discovery is intentionally excluded. Compile-time adapters are easier for an
OSS project to review, test, and secure. A plugin protocol should be designed only after a real
out-of-process provider requires it.

## Dashboard presentation

The dashboard makes uncertainty visible without becoming an operations console.

- `Billed spend` contains only provider-reported or ledger financial data.
- `Estimated cost` contains API usage rated from an observed public catalog.
- `Subscription notional value` is a separate secondary comparison.
- Missing information reads `Not reported`, never `$0.00` or zero tokens.
- Charts and tooltips retain source, scope, and cost-basis labels.
- A compact source panel shows configured, fresh, stale, failing, unavailable, or not
  configured, together with last success and a sanitized error.
- An unconfigured integration remains discoverable with setup guidance.
- A function is disabled only when the upstream provider genuinely cannot support it.

Existing components, visual language, and accessibility behaviour remain. No new UI framework
or general configuration system is introduced.

## Migration and compatibility

The database migration is additive and lossless:

1. Add provenance columns with safe legacy defaults.
2. Add source sync and pricing snapshot storage.
3. Add provider-specific report storage where generic usage events would misrepresent facts.
4. Backfill source/scope/basis only when the existing provider, raw payload, or known ingest
   route proves the classification.
5. Leave ambiguous events and aggregates as `Legacy/Unknown`.
6. Replace provider-wide event-key uniqueness with a filtered `SourceId + EventKey` unique
   index for non-null keys.
7. Preserve every historical aggregate. Split one only where exact source reconstruction is
   possible; never invent a distribution.

> **Superseded 2026-08-26:** production evidence showed that accepting provenance-free writes
> silently recreated misleading legacy aggregates. `POST /api/events` now requires `sourceId`,
> `sourceKind`, `usageScope`, and `costBasis`; historical legacy rows remain migration-compatible.

Historical estimates are not bulk-repriced unless their source and pricing dimensions are
complete. Billed history is immutable through this path.

## Error handling

- One source failure cannot abort other sources in the cycle.
- Cancellation is always propagated.
- Pagination/download failure rejects the whole source attempt.
- Event correction and aggregate movement are one transaction.
- Pricing activation and affected aggregate repricing are transactional from the reader's point
  of view.
- Last-known-good pricing survives upstream outage and parser failure.
- Unknown model/tier/region records usage and emits one rate-limited warning rather than
  guessing.
- Source status persists failures so a process restart cannot make an outage appear healthy.

## Verification

Implementation follows test-driven development. The minimum durable suite covers:

### Usage and aggregation

- New keyed event inserts once.
- Identical keyed snapshot is a no-op.
- Corrected snapshot changes token/cost totals once.
- Correction can move date, model, source metadata, and aggregate bucket.
- Null-key observations remain append-only.
- Two sources may use the same event key.
- A failed transaction cannot leave event and aggregate totals inconsistent.

### Provider acquisition

- Every paginated source rejects incomplete results.
- Copilot signed-report download is validated before commit.
- Copilot report facts do not become fake zero-token usage.
- Google billing rows retain service/SKU/currency/credit facts.
- API and subscription sources remain separate in queries.

### Pricing

- Current OpenAI, Claude, Moonshot, and relevant Google fixture shapes normalize correctly.
- Claude 5-minute/1-hour writes, Batch, fast, and geography are resolved only when observed.
- Kimi cache, HighSpeed, and Batch rates are distinct.
- Google product/tier/region/modality/context distinctions cannot cross-match.
- Effective and future-dated prices activate on the correct usage date.
- Duplicate/overlapping entries, malformed tables, partial downloads, and non-positive rates
  are rejected.
- An unchanged content hash creates no snapshot.
- A failed refresh retains the last-known-good snapshot.
- Unknown dimensions yield null cost, not fallback cost.
- Repricing changes only estimate/notional rows with sufficient dimensions.
- Billed and ambiguous legacy rows remain unchanged.

### Operations and UI

- Source state records attempts, success, staleness, and sanitized failures.
- Liveness remains independent of individual source freshness.
- Billed, estimated, and notional totals never merge.
- Missing data renders as `Not reported`.
- A newly registered provider is polled without editing either worker.
- An unknown frontend provider remains readable through the fallback style.
- Old ingest payloads remain accepted and are visibly legacy.

Run the complete backend test suite, frontend unit tests, type checking, linting, production
build, and existing architecture checks before release. Exercise the migration against
representative legacy rows in the integration database path.

## OSS documentation deliverables

- A source/capability setup matrix with required account plan and credentials.
- The truth model and cost-basis definitions.
- Pricing provenance, renewal, last-known-good, and staleness behaviour.
- Known limitations for local telemetry and unavailable provider metrics.
- A concise `Adding a provider` guide following the capability boundary above.
- Deployment notes for Google Catalog API enablement and billing-export access.

## Source evidence reviewed for this design

- OpenAI API pricing: <https://developers.openai.com/api/docs/pricing>
- OpenAI AI-readable pricing: <https://developers.openai.com/api/docs/pricing.md>
- Claude pricing: <https://platform.claude.com/docs/en/about-claude/pricing>
- Claude AI-readable pricing: <https://platform.claude.com/docs/en/about-claude/pricing.md>
- Kimi K3: <https://platform.kimi.ai/docs/pricing/chat-k3>
- Kimi K2.7 Code: <https://platform.kimi.ai/docs/pricing/chat-k27-code>
- Kimi K2.6: <https://platform.kimi.ai/docs/pricing/chat-k26>
- Kimi K2.5: <https://platform.kimi.ai/docs/pricing/chat-k25>
- Kimi Batch: <https://platform.kimi.ai/docs/pricing/batch>
- Google Agent Platform pricing:
  <https://cloud.google.com/gemini-enterprise-agent-platform/generative-ai/pricing>
- Google Cloud Billing Catalog API:
  <https://docs.cloud.google.com/billing/v1/how-tos/catalog-api>
- BenchLM Moonshot cross-check: <https://benchlm.ai/moonshot/api-pricing>

## Acceptance criteria

The design is complete when all of the following are true:

1. Every displayed cost is labelled billed, estimated, notional, or unknown.
2. Every new observation has source, scope, basis, and observation time.
3. Stable provider corrections repair stored events and aggregates idempotently.
4. No provider commits an incomplete paginated/report response.
5. Current supported provider acquisition paths work or clearly report why they cannot.
6. Daily first-party pricing renewal safely updates estimates without manual catalog checks.
7. A broken pricing source cannot replace good rates.
8. Source freshness and failures are visible independently of liveness.
9. All legacy data remains present and honestly classified.
10. Adding another provider does not require changing the central workers, schema, or dashboard
    components.
