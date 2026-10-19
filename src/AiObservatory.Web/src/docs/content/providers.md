# Providers

A source is one way Observatory obtains evidence: a provider API, a repository API, a pricing page, a script on a developer machine, or a person typing. This page lists every source, what it needs, what it provides and what it cannot, and how to tell whether it is working.

## How sources work

Every source has an id such as `openai-costs-api`. The id appears in the Data sources panel, in the `Source` column of the model breakdown and in `GET /api/sources/status`. See [Data sources](/docs/data-sources) for what the panel shows.

Each observation a source writes carries three labels: its **source kind** (provider API, local telemetry, manual, synthetic, legacy), its **usage scope** (API, subscription, mixed, unknown) and its **cost basis** (billed, provider estimated, list-price estimate, notional, none, unknown). Totals never mix bases. The meaning of each label is in [Concepts](/docs/concepts).

### Where sources run

| Process             | Runs                                                     | Sources                                                                                         |
| ------------------- | -------------------------------------------------------- | ----------------------------------------------------------------------------------------------- |
| Ingest worker       | Polls provider APIs on a timer; refreshes pricing daily. | Anthropic, OpenAI, Copilot, Google, GitHub activity, and the pricing sources.                   |
| API                 | Serves requests and one daily background cycle.          | GitHub billing (daily), the health-only sources, manual entries, and every local producer post. |
| A developer machine | You schedule it.                                         | Local producers. See [Local producers](/docs/local-producers).                                  |

The ingest worker starts a polling cycle immediately and then repeats every `Ingest__PollingIntervalMinutes` (default 60). Each cycle re-reads the trailing `Ingest__LookbackDays` days ending yesterday (default 3), so a missed day is filled on a later cycle. A source also reaches back to the date of its last success if that is earlier, and to any date left pending by a failed cycle.

A source runs only when its settings are present. A missing setting leaves the source visible as not connected; it never turns into zero. A value that is the unresolved text `@Microsoft.KeyVault(...)` counts as unset.

Some bad combinations stop the ingest worker at start instead of being ignored:

- `CLAUDE_CODE_USAGE_ENABLED=true` without `ANTHROPIC_BILLING_KEY`.
- `GOOGLE_CLOUD_PROJECT_ID` without `GOOGLE_BILLING_EXPORT_TABLE`, or the reverse.
- A `GOOGLE_BILLING_EXPORT_TABLE` that is not `project.dataset.table` with safe identifier characters.
- A `GOOGLE_BILLING_EXPORT_MAX_BYTES_BILLED` that is not a positive whole number.
- No `DB_CONNECTION`.

### How health is judged

The status of each source comes from one rule, used by the panel and by the daily digest:

| Status          | Meaning                                                                                                                                    |
| --------------- | ------------------------------------------------------------------------------------------------------------------------------------------ |
| `notConfigured` | The source is switched off or lacks a setting.                                                                                             |
| `unavailable`   | The provider or the credential says the data cannot be read (a rate limit, a refused scope, an unavailable report). It outranks `failing`. |
| `failing`       | The last attempts failed, and the count of consecutive failures is above zero.                                                             |
| `configured`    | Enabled, but no attempt has succeeded yet.                                                                                                 |
| `stale`         | The last success is more than twice the source's expected interval ago.                                                                    |
| `fresh`         | Within that limit.                                                                                                                         |

The expected interval is the polling interval for provider sources and one day for pricing, GitHub billing, local producers and the health-only sources. A failure is also logged; from the third consecutive failure the log escalates to a message that the source may be misconfigured or unavailable. Error messages are sanitised: query strings are removed and text is cut to 500 characters.

A daily digest by email or Slack lists sources that are `unavailable` or `failing`. A source that is only `stale` is listed when something else justifies the message but never triggers it, because a laptop that has not run its sweep is routine. Set up the channels as described in [Reporting](/docs/reporting).

## Source matrix

Costs are USD unless a source says otherwise.

### Provider usage and cost APIs

| Source id                     | Provides                                                                                                                        | Needs                                                                                                              | Scope and basis                                                                                                    |
| ----------------------------- | ------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------ | ------------------------------------------------------------------------------------------------------------------ |
| `anthropic-usage-api`         | Daily token usage by model, service tier, inference geography and speed.                                                        | `ANTHROPIC_BILLING_KEY`, an organization Admin API key.                                                            | API; list-price estimate (rated by Observatory).                                                                   |
| `anthropic-cost-report`       | Daily billed cost by workspace and description.                                                                                 | The same key.                                                                                                      | API; billed.                                                                                                       |
| `claude-code-usage-api`       | Daily Claude Code usage per actor and model, with the provider's cost estimate.                                                 | The same key and `CLAUDE_CODE_USAGE_ENABLED=true`.                                                                 | API or subscription, from the report's customer type; provider estimated, or none when the report has no estimate. |
| `openai-usage-api`            | Token usage from the completions usage endpoint, by model, batch flag, service tier and processing lane.                        | `OPENAI_ADMIN_KEY`, an organization admin key.                                                                     | API; list-price estimate.                                                                                          |
| `openai-costs-api`            | Billed cost by line item and project.                                                                                           | The same key.                                                                                                      | API; billed.                                                                                                       |
| `copilot-org-report`          | GitHub Copilot organization engagement: active users and interaction, generation and acceptance counts. No tokens and no money. | `GITHUB_TOKEN` and `COPILOT_ORG`.                                                                                  | Subscription; none.                                                                                                |
| `google-cloud-billing-export` | Billed cloud spend by day, service and SKU, with credits and invoice month.                                                     | `GOOGLE_CLOUD_PROJECT_ID` and `GOOGLE_BILLING_EXPORT_TABLE` together, plus Google application default credentials. | API; billed, in the export's native currency.                                                                      |

### Repository sources

| Source id             | Provides                                                                              | Needs                                                              |
| --------------------- | ------------------------------------------------------------------------------------- | ------------------------------------------------------------------ |
| `github-activity-api` | Pull requests, reviews, commits and workflow runs for the [GitHub](/docs/github) tab. | `GITHUB_TOKEN` and an allowlist or organisation.                   |
| `github-billing-api`  | GitHub's billed usage as spend ledger entries (scope mixed, basis billed).            | `GITHUB_TOKEN` and `GITHUB_BILLING_ORG`, in the API's environment. |

### Pricing sources

These supply the catalogs that rate list-price estimates and notional values. They provide no usage. Details are in [Pricing](/docs/pricing).

| Source id                      | Catalog                               | Fetched?                                          |
| ------------------------------ | ------------------------------------- | ------------------------------------------------- |
| `openai-pricing`               | OpenAI                                | Daily.                                            |
| `claude-pricing`               | Anthropic                             | Daily.                                            |
| `kimi-pricing`                 | Moonshot                              | Only with `PRICING_RETIRED_SOURCES_ENABLED=true`. |
| `xai-pricing`                  | xAI                                   | Only with `PRICING_RETIRED_SOURCES_ENABLED=true`. |
| `meta-openrouter-pricing`      | Meta, through OpenRouter's model list | Only with `PRICING_RETIRED_SOURCES_ENABLED=true`. |
| `google-cloud-catalog`         | Google Cloud SKUs                     | Never in this release; see below.                 |
| `gemini-developer-api-pricing` | Gemini Developer API                  | Bundled with the release; not fetched.            |
| `zai-pricing`                  | z.AI                                  | Bundled; not fetched.                             |
| `deepseek-pricing`             | DeepSeek                              | Bundled; not fetched.                             |

The last three bundled catalogs have no status row.

### Local telemetry

Written by the sweeper in `clients/`, one source per tool and machine. All use source kind local telemetry.

| Source id             | Tool                   | Basis                                                                 |
| --------------------- | ---------------------- | --------------------------------------------------------------------- |
| `codex-local`         | Codex                  | Notional for OpenAI and z.AI; list-price estimate (API) for DeepSeek. |
| `copilot-local`       | GitHub Copilot CLI     | Notional.                                                             |
| `claude-local`        | Claude Code            | Notional.                                                             |
| `kimi-local`          | Kimi                   | Notional. Off by default.                                             |
| `gemini-review-local` | Gemini review sessions | List-price estimate (API).                                            |
| `antigravity-local`   | Antigravity            | Notional.                                                             |
| `grok-local`          | Grok                   | Provider estimated. Off by default.                                   |
| `pi-local`            | PI, Meta models        | Provider estimated. Off by default.                                   |

Ids carry `@<machine>` in practice. See [Local producers](/docs/local-producers).

### Health-only and manual sources

| Source id                    | Purpose                                                                                                                                                                                                                                                                                                                                           |
| ---------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `sweep-placement@<machine>`  | Counts of sessions a sweeper could not attribute to a provider. Unavailable while any exist. No usage or cost rows.                                                                                                                                                                                                                               |
| `portal-spend-feed`          | Health of an external expense feed. `POST /api/spend/feed-status` takes counts of skipped expenses by reason (`no-vendor`, `no-category`, `aged-out`, `foreign-unreviewed`). A report with skips makes the source `unavailable`; a clean report makes it `fresh`. Expense ids are counted and not stored. Nothing in this repository posts to it. |
| `manual-ledger`              | Spend entries typed on the [Spend](/docs/spend) tab.                                                                                                                                                                                                                                                                                              |
| `demo-seed`                  | Synthetic sample data from the demo seed. Never provider billing.                                                                                                                                                                                                                                                                                 |
| `legacy-api`, `legacy-spend` | Older rows whose source could not be recovered. Kept and labelled; not polled. New writes without provenance are rejected.                                                                                                                                                                                                                        |

## Per-provider notes

### Anthropic

All three Anthropic sources use one Admin API key, sent as `x-api-key`.

- **Usage.** Daily buckets grouped by model, service tier, inference geography and speed. Rows are written with no cost and rated from the catalog. Cache writes carry the five-minute and one-hour split that the report provides.
- **Costs.** Billed amounts in USD only; any other currency makes the cycle fail. They land in the spend ledger under vendor `anthropic`, category `api-usage`, with the report's description as the SKU.
- **Claude Code.** Requested one day at a time. A row's cost is the provider's estimate, shown as `Provider estimate`; when any row of a group has no estimate the group has no cost. The scope is API when the report marks the customer type as `api`, and subscription otherwise.
- **Unavailable reports.** If Anthropic answers `403` with a `permission_error` whose message contains "not available", "not enabled" or "ineligible", the source becomes `unavailable` rather than `failing`. Any other error counts as a failure.
- **Not supported.** Claude Enterprise Analytics uses a different API and key and is not read by these adapters. Anthropic's Admin APIs are for organizations; check Anthropic's documentation for your account type [source](#references).

### OpenAI

One admin key enables both lanes.

- **Usage** reads only the completions usage endpoint, grouped by model, batch, service tier and processing lane. Other usage categories are not read.
- **Costs** is the financial source. Use it, not Usage, when reconciling money. Currency must be USD.
- Usage rows are rated from the catalog and need the processing, context and region dimensions to price; see [Pricing](/docs/pricing#what-a-price-needs).

### GitHub Copilot

The organization report is the latest 28-day report. Observatory stores each day's active-user and activity counts. No screen or route currently displays them: the source exists to show that the report is being collected. It never produces tokens or cost rows, so nothing is estimated. The token needs `read:org` (classic) or the Organization Copilot metrics read permission (fine-grained). Per-developer Copilot usage on a laptop arrives through the `copilot-local` producer instead.

### Google

The Cloud Billing export reads a BigQuery table or stable view (the Standard or Detailed export shape).

- **Cost to you.** Each cycle runs two billed BigQuery jobs. `GOOGLE_BILLING_EXPORT_MAX_BYTES_BILLED` caps the bytes one job may bill; the default is 1,073,741,824 (1 GiB). A job over the cap fails without charge and the cycle reports the failure. `GOOGLE_BILLING_EXPORT_LOCATION` (for example `EU`) pins the job region to the dataset's.
- **Access.** Application default credentials with BigQuery Job User on the execution project and BigQuery Data Viewer on the table or view.
- **Corrections.** Late corrections to a day, service and SKU are folded in by re-aggregating that group. The scan has a floor: usage older than 31 days before the changes-since point cannot be refreshed. If a correction for older usage is detected, the worker logs a warning and does not backfill. Check those warnings before treating the export as reconciled.
- **Currency.** Rows keep the export's native currency; GBP views follow the historical FX path.
- This is billed evidence, not token telemetry.

### GitHub

Both GitHub sources are covered on the [GitHub](/docs/github) page, including token scopes, limits and the billing mapping. The two tokens are the same secret but need different scopes: activity needs contents, pull-requests and actions read; billing needs organization billing read.

### Providers with no usage API

- **xAI** and **Meta.** Observatory has no xAI or Meta usage API source. Their usage arrives from the Grok and PI local producers, which post the cost the CLI recorded. The catalogs still gate what can be priced from tokens alone: a model that appears in no catalog, such as an unlisted `grok-4.6-build`, stays unpriced rather than borrowing another model's rates.
- **z.AI** and **DeepSeek.** No API source. Usage arrives through Codex sessions on the Codex local producer.
- **Moonshot (Kimi).** No API source. Usage arrives from the Kimi local producer, which is off by default.

## Known unavailable capabilities

| Capability                                         | State                                                                                                                                                                                     |
| -------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Google Cloud SKU pricing (`google-cloud-catalog`)  | Unavailable. The release ships no verified SKU mappings, so the source stays `notConfigured` even with credentials. This does not affect Gemini Developer API rates or the billed export. |
| Claude Enterprise analytics                        | Not supported by these adapters.                                                                                                                                                          |
| Copilot tokens or cost                             | The organization report carries engagement counts only.                                                                                                                                   |
| Real-time usage                                    | Provider sources are polled; the freshest data is one polling interval old, and providers settle daily totals later.                                                                      |
| Per-request xAI or Meta pricing for unknown models | Unpriced until a catalog entry exists.                                                                                                                                                    |

## Checking a source

1. Open the Data sources panel on the Overview, or call `GET /api/sources/status`.
2. Read the status and the last error. Errors have query strings removed.
3. For a `notConfigured` row, the `Setup` link opens the provider setup guide.
4. For `unavailable`, fix the access named in the error; for `failing`, read the ingest worker's log.
5. After fixing, wait for the next cycle, or restart the worker to start one immediately.

Common causes are in [Troubleshooting](/docs/troubleshooting).

## References

- `src/AiObservatory.Ingest/Program.cs` (which settings enable which source)
- `src/AiObservatory.Ingest/ProviderPollingWorkerService.cs`
- `src/AiObservatory.Ingest/Services/` (Anthropic, OpenAi, Copilot, Google, GitHub)
- `src/AiObservatory.Api/Services/SourceHealthClassifier.cs`, `SourceHealthDigest.cs`
- `src/AiObservatory.Api/Endpoints/SourceStatusEndpoints.cs`, `SweepStatusEndpoints.cs`, `SpendFeedStatusEndpoints.cs`
- `src/AiObservatory.Data/Entities/ObservationProvenance.cs`
- `docs/provider-setup.md`
- Anthropic Usage and Cost API: https://platform.claude.com/docs/en/manage-claude/usage-cost-api
- Anthropic Claude Code Analytics API: https://platform.claude.com/docs/en/manage-claude/claude-code-analytics-api
- OpenAI usage and costs: https://developers.openai.com/api/reference/python/resources/admin/subresources/organization/subresources/usage
- GitHub Copilot usage metrics: https://docs.github.com/en/rest/copilot/copilot-usage-metrics?apiVersion=2026-03-10
- Google Cloud Billing export setup: https://docs.cloud.google.com/billing/docs/how-to/export-data-bigquery-setup
