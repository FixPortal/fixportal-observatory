# Provider setup

> Setup guide for maintainers configuring acquisition sources as of 2026-08-25. Costs are USD unless a billed export retains its native currency.

Configure only sources you can verify. A missing credential leaves a source visible as not configured; it does not turn a missing value into zero. Local telemetry is best-effort, machine-local subscription/notional evidence, never an invoice.

## Setup order

1. Choose only verifiable lanes from the matrix; avoid overlapping local and remote coverage for the same activity.
2. Provision the required upstream access and settings through your secret store.
3. Run the relevant API, Ingest worker, or local producer.
4. Check `/api/sources/status` for configured, fresh, or unavailable truth before relying on values.

## Dashboard sources

Provider polling sources start in the immediate loop and then run every 60 minutes by default; set `Ingest__PollingIntervalMinutes` to change that interval. Pricing refreshes at startup when due, then daily. Local producers are user-scheduled; this guide recommends every 15 minutes.

| Source ID / display name | What it provides | Required access or setting | Scope and cost basis | Cadence | Official/setup link | When absent or unavailable |
| --- | --- | --- | --- | --- | --- | --- |
| `anthropic-usage-api` — Messages usage | API token activity | `ANTHROPIC_BILLING_KEY`: organization Admin API key | API / list-price estimate | Startup/immediate loop, then default 60m | [Usage and Cost API](https://platform.claude.com/docs/en/manage-claude/usage-cost-api) | Not configured; unavailable to individual accounts and Claude Platform on AWS |
| `anthropic-cost-report` — Cost report | Billed API and non-token cost evidence | Same Admin API key | API / billed | Startup/immediate loop, then default 60m | [Usage and Cost API](https://platform.claude.com/docs/en/manage-claude/usage-cost-api) | Not configured; unavailable where the Admin API is unavailable |
| `claude-code-usage-api` — Claude Code usage | Daily Claude Code activity and provider estimates where supplied | `ANTHROPIC_BILLING_KEY` plus `CLAUDE_CODE_USAGE_ENABLED=true`; organization must have Admin API access | API or subscription / provider estimated or none | Startup/immediate loop, then default 60m | [Claude Code Analytics API](https://platform.claude.com/docs/en/manage-claude/claude-code-analytics-api) | Disabled by default; unavailable to individual accounts and Claude Platform on AWS. Claude Enterprise uses its separate Enterprise Analytics API/key, not this adapter |
| `claude-local` — Claude local | Transcript-derived token snapshots | Local files plus `OBSERVATORY_LOCAL_SOURCES`, `OBSERVATORY_URL`, and `OBSERVATORY_API_KEY` | Subscription / notional | User-scheduled; recommend every 15m | [Local producers](../clients/README.md) | Not configured on that machine |
| `claude-pricing` — Claude pricing | Refreshed public catalog | No credential | API catalog / list-price estimate input | Startup when due, then daily | [Claude pricing](https://platform.claude.com/docs/en/about-claude/pricing.md) | Bundled/last-known-good catalog remains; unknown dimensions stay null |
| `copilot-org-report` — Organization report | `organization-28-day/latest` engagement report descriptor and facts, not token or money rows | `GITHUB_TOKEN` and `COPILOT_ORG`; classic PAT `read:org`, fine-grained `Organization Copilot metrics` read | Subscription / none | Startup/immediate loop, then default 60m | [Copilot report](https://docs.github.com/en/rest/copilot/copilot-usage-metrics?apiVersion=2026-03-10) | Not configured; no fabricated token/cost rows |
| `copilot-local` — Copilot local | Session token snapshots | Local files plus local producer settings | Subscription / notional | User-scheduled; recommend every 15m | [Local producers](../clients/README.md) | Not configured on that machine |
| `google-cloud-billing-export` — Cloud Billing export | Billed cloud-spend evidence with service, SKU, credits, invoice month, and corrections | `GOOGLE_CLOUD_PROJECT_ID` and `GOOGLE_BILLING_EXPORT_TABLE` together; Standard/Detailed compatible table or stable view; ADC with BigQuery Job User on execution project and BigQuery Data Viewer on export table/view | API / billed | Startup/immediate loop, then default 60m | [Export setup](https://docs.cloud.google.com/billing/docs/how-to/export-data-bigquery-setup) | Not configured; export enablement needs separate billing/project privileges and queries incur charges |
| `google-cloud-catalog` — Cloud catalog | Potential Google list-price catalog | `GOOGLE_CLOUD_CATALOG_API_KEY` and `GOOGLE_CLOUD_CATALOG_SERVICE_ID`; Cloud Billing API enabled | API catalog / list-price estimate input | No fetch occurs now: marked not configured during startup/daily pricing checks until verified exact SKU mappings ship | [Catalog API](https://docs.cloud.google.com/billing/v1/how-tos/catalog-api) | Known unavailable: production has zero verified exact SKU mappings, so credentials alone do not enable estimates |
| `gemini-review-local` — Gemini review | Token snapshots from API-key-isolated adversarial review sessions | Local files plus local producer settings | API / list-price estimate | User-scheduled; recommend every 15m | [Local producers](../clients/README.md) | Only `gem-review-*` sessions are included; other Gemini auth history is not inferred |
| `gemini-developer-api-pricing` — Gemini API pricing | Bundled public Gemini Developer API rates | No credential | API catalog / list-price estimate input | Loaded at startup | [Gemini pricing](https://ai.google.dev/gemini-api/docs/pricing) | Exact model, tier, and context band required |
| `antigravity-local` — Antigravity local | Conversation token snapshots | Local files plus local producer settings | Subscription / notional | User-scheduled; recommend every 15m | [Local producers](../clients/README.md) | Missing transcript/model evidence remains unreported |
| `openai-usage-api` — Usage API | API token activity | `OPENAI_ADMIN_KEY`: organization Admin key | API / list-price estimate | Startup/immediate loop, then default 60m | [Usage and Costs](https://developers.openai.com/api/reference/python/resources/admin/subresources/organization/subresources/usage) | Not configured |
| `openai-costs-api` — Costs API | Financial API cost evidence | Same organization Admin key | API / billed | Startup/immediate loop, then default 60m | [Usage and Costs](https://developers.openai.com/api/reference/python/resources/admin/subresources/organization/subresources/usage) | Not configured; use Costs, not Usage, for financial reconciliation |
| `codex-local` — Codex local | Session token snapshots | Local files plus local producer settings | Subscription / notional | User-scheduled; recommend every 15m | [Local producers](../clients/README.md) | Not configured on that machine |
| `openai-pricing` — OpenAI pricing | Refreshed public catalog | No credential | API catalog / list-price estimate input | Startup when due, then daily | [OpenAI pricing](https://developers.openai.com/api/docs/pricing.md) | Bundled/last-known-good catalog remains |
| `kimi-local` — Kimi local | Session token snapshots | Local files plus local producer settings | Subscription / notional | User-scheduled; recommend every 15m | [Local producers](../clients/README.md) | Not configured on that machine |
| `kimi-pricing` — Kimi pricing | Refreshed public catalog | No credential | API catalog / list-price estimate input | Startup when due, then daily | [Kimi documentation](https://platform.kimi.ai/docs/llms.txt) | Bundled/last-known-good catalog remains |

`OPENAI_ADMIN_KEY` enables both OpenAI acquisition lanes. OpenAI documents different Usage and Costs reconciliation semantics; Costs is the financial source. Claude Admin API key choices are documented in [Anthropic analytics guidance](https://platform.claude.com/docs/en/manage-claude/analytics-api).

> [!IMPORTANT]
> `anthropic-usage-api`, `anthropic-cost-report`, and `claude-code-usage-api` are shipped adapters for Claude Platform organization Admin APIs and use `ANTHROPIC_BILLING_KEY`. Claude Enterprise Analytics uses a different API/key and is not supported by these adapters.

### Optional Key Vault references

The default FixPortal Bicep deployment omits `ANTHROPIC_BILLING_KEY` and `COPILOT_ORG` so an unconfigured provider does not create a broken Key Vault reference. After creating the corresponding Key Vault secret, enable either setting by passing its secret name:

```powershell
az deployment group create -g fpaiobs-rg -f infra/main.bicep -p anthropicBillingSecretName=anthropic-billing-key
```

```powershell
az deployment group create -g fpaiobs-rg -f infra/main.bicep -p copilotOrgSecretName=copilot-org
```

To enable both, pass both names in the same deployment because omitted optional parameters return to their empty defaults:

```powershell
az deployment group create -g fpaiobs-rg -f infra/main.bicep -p anthropicBillingSecretName=anthropic-billing-key copilotOrgSecretName=copilot-org
```

Google export rows preserve native export currency and credits. `export_time` advances when late corrections arrive, so affected stable groups are reaggregated. This is billed evidence, not token telemetry or an invoice clone; GBP display follows the existing historical FX path.

The current source scan has a usage-date floor of 31 days before `changes_since`.
Corrections for older usage cannot refresh stored observations: the companion
query detects affected keys and the worker logs a warning; it does not backfill
them. Check those warnings before treating the export as reconciled.

**UNVERIFIED: live BigQuery partition pruning and query cost (review finding M24).**
SQL-shape tests do not establish how a real export table or view is partitioned.
Before enabling this lane, inspect its partition metadata and dry-run both queries
from `GoogleBillingExportClient.cs` with representative parameters. Record bytes
processed and check recent, boundary-day, and older corrected usage against source
totals. A missing pruning benefit or omitted/mismatched billing group refutes the
qualification; do not claim full historical correction coverage with this scan floor.

> [!WARNING]
> Exclude `claude` from `OBSERVATORY_LOCAL_SOURCES` when `claude-code-usage-api` covers the same account/activity. The independent lanes do not cross-deduplicate.

## Compatibility, GitHub, and manual sources

| Source ID | What it provides | Required access | Cadence | Meaning when absent |
| --- | --- | --- | --- | --- |
| `demo-seed` | Synthetic local demonstration data | Development seed endpoint | On submission; only when the relevant tables are empty | No sample data; never treated as provider billing |
| `github-activity-api` | Repository activity acquisition | `GITHUB_TOKEN` plus `Ingest__GitHubRepoAllowlist`; token needs `contents:read`, `pull-requests:read`, and `actions:read` | Startup/immediate loop, then default 60m | Not configured; separate from Copilot metrics |
| `github-billing-api` | GitHub billed-ledger observations | `GITHUB_TOKEN` plus `GITHUB_BILLING_ORG`; fine-grained `Plan` read or classic `admin:org` billing access | API-worker startup, then daily UTC | Not configured; not a token source |
| `legacy-api` | Historical usage migrated without trusted provenance | None | Existing rows retained; no polling; new provenance-free writes rejected | Retained as legacy/unknown rather than guessed |
| `legacy-spend` | Older spend records without trusted provenance | None | Retained; no polling | Retained as legacy/unknown rather than guessed |
| `manual-ledger` | User-entered spend ledger evidence | Dashboard/API authorization | On submission | Manual billed record only when entered |

BenchLM is discovery/cross-check evidence only: it is never a runtime source, authority, fallback, or prerequisite.

## Local telemetry safeguards

| Tool | Local path and fact | Safeguard |
| --- | --- | --- |
| Codex | `~/.codex/sessions/**/rollout-*.jsonl`; final cumulative `token_count` | Stable cumulative day/model snapshots |
| Copilot | `~/.copilot/session-state/**/events.jsonl`; final `session.shutdown` per-model totals | Final cumulative totals win |
| Claude | `~/.claude/projects/**/*.jsonl`; assistant usage | Global `message.id` deduplication retains the richest copy |
| Kimi | `~/.kimi-code/sessions/**/wire.jsonl`; `usage.record` rows only | Mirrored `step.end` rows are ignored; turn and session scopes both count |

The sweeper uses source-scoped keys, server inventory, and zero corrections for removed/disabled snapshots. Its local state is a parse cache, not the system of record. See [clients/README.md](../clients/README.md) for home overrides and scheduling.
