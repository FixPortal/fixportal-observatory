# Observatory

![Build](https://github.com/FixPortal/fixportal-observatory/actions/workflows/ci.yml/badge.svg)
![License](https://img.shields.io/github/license/FixPortal/fixportal-observatory)
[![Release](https://img.shields.io/github/v/release/FixPortal/fixportal-observatory)](https://github.com/FixPortal/fixportal-observatory/releases/latest)

![Observatory — one clear view of AI usage, spend, subscriptions, and budget signals](docs/images/observatory-hero-oss.png)

> OSS observability for AI usage and cost evidence. It keeps billed spend, public-list estimates, subscription notional values, and missing data distinct.

Observatory is a .NET 10 and React 19 dashboard with a PostgreSQL store. Provider sources, local CLI telemetry, and manual entries retain provenance so unlike evidence is never silently merged.

## Read these first

- [Provider setup](docs/provider-setup.md) — every source, access requirement, and known unavailable capability.
- [Truth and pricing](docs/truth-and-pricing.md) — source/scope/basis meanings and safe catalog refresh.
- [Self-audit user guide](docs/self-audit-user-guide.md) — run an audit in the UI and interpret its results.
- [Self-audit architecture](docs/self-audit-architecture.md) — evidence flow, trust boundaries, verifier and replay.
- [Self-audit operator guide](docs/self-audit.md) — opt-in capture, credentials, retention and offline commands.
- [Adding a provider](docs/adding-a-provider.md) — the compile-time adapter seam.
- [Local producers](clients/README.md) — Codex, Copilot, Claude, Kimi, Gemini review, Antigravity, Grok, and PI sweeper setup.
- [Postman collection](docs/observatory.postman_collection.json) — representative authenticated API requests.
- [OSS qualification](docs/oss-qualification.md) — validation evidence and remaining limitations.

This source is version [v1.2.1](CHANGELOG.md). The previous
`v1.1.0` release began a fresh public Git history; earlier history is archived by the
maintainer. If you have an older pre-cutover checkout, preserve local work and clone again.

## Local development

### Quick start

Want to look around without installing anything? Try the [public demo](https://observatory-demo.fixportal.org/): read-only, synthetic usage, five complete adversarial-review panels with finding-level Seats data, and a billed-reporting example. No provider accounts are connected. The [demo guide](docs/demo.md) lists what each visible view contains.

Restore uses public feeds; no GitHub Packages token is required.

```powershell
docker compose up --build
```

This starts PostgreSQL, the API, provider/pricing ingest, and the frontend at [http://localhost:4173](http://localhost:4173). The compose seed populates synthetic sample data; it is labelled `demo-seed` and is not a representation of provider billing. Optional GitHub billing uses `GITHUB_TOKEN` plus `GITHUB_BILLING_ORG`; the token needs the broader access listed in [provider setup](docs/provider-setup.md#compatibility-github-and-manual-sources).

For a manual run, install .NET SDK 10, Node `^22.22.2`, `^24.15.0`, or `>=26.0.0`, and PostgreSQL 16. The sweeper client needs Node 24 or later; see [Local producers](clients/README.md). The commands below reuse the Compose database and its local development credentials. Use `127.0.0.1`, not `localhost`: Compose publishes the port on IPv4 only, and on Windows `localhost` tries `::1` first, adding about 2 seconds to every new connection. The same applies to a `TEST_DB_CONNECTION` pointed at this database.

The API cannot start without `DB_CONNECTION`: it throws `DB_CONNECTION configuration is missing.` at startup (`src/AiObservatory.Api/Program.cs:41-43`), and the ingest worker throws `DB_CONNECTION is required` (`src/AiObservatory.Ingest/Program.cs:34`). Set it in each shell that runs one of them.

```powershell
docker compose up -d --wait db
```

```powershell
$env:DB_CONNECTION = 'Host=127.0.0.1;Port=5433;Database=aiobservatory;Username=aiobs;Password=aiobs'
```

```powershell
$env:OBSERVATORY_API_KEY = 'change-me'
```

```powershell
dotnet restore AiObservatory.slnx
```

```powershell
npm --prefix src/AiObservatory.Web ci
```

```powershell
dotnet run --project src/AiObservatory.Api
```

The API applies pending EF Core migrations when it starts. In a separate shell, give the frontend the same local development key, then start it:

```powershell
$env:VITE_API_KEY = 'change-me'
```

```powershell
npm --prefix src/AiObservatory.Web run dev
```

For a manual run, start the ingest worker in another shell; it only activates sources whose required settings are present:

```powershell
$env:DB_CONNECTION = 'Host=127.0.0.1;Port=5433;Database=aiobservatory;Username=aiobs;Password=aiobs'
```

```powershell
dotnet run --project src/AiObservatory.Ingest
```

### Docker Compose environment

`docker-compose.yml` forwards only a fixed set of variables, each read from your shell or a `.env` file. Anything else must be added by hand to the matching service's `environment` block, or it never reaches the container.

- `api` forwards `DB_CONNECTION`, `OBSERVATORY_API_KEY`, `GITHUB_TOKEN`, `GITHUB_BILLING_ORG`, and `Activity__ProjectOwners` (from `OBSERVATORY_PROJECT_OWNERS`) (`docker-compose.yml:22-34`).
- `ingest` forwards `DB_CONNECTION`, `ANTHROPIC_BILLING_KEY`, `CLAUDE_CODE_USAGE_ENABLED`, `OPENAI_ADMIN_KEY`, `GITHUB_TOKEN`, `COPILOT_ORG`, the six `GOOGLE_*` settings, `Ingest__GitHubRepoAllowlist` (from `INGEST_GITHUB_REPO_ALLOWLIST`), `Ingest__PollingIntervalMinutes` (from `INGEST_POLLING_INTERVAL_MINUTES`, default 60) and `Ingest__LookbackDays` (from `INGEST_LOOKBACK_DAYS`, default 3) (`docker-compose.yml:58-75`).
- Not forwarded; add to `ingest` to use: `GITHUB_ACTIVITY_ORG` (org discovery for `github-activity-api`; `GITHUB_BILLING_ORG` is its fallback and is also not forwarded to `ingest`, so under Compose GitHub activity needs `INGEST_GITHUB_REPO_ALLOWLIST` unless you add one of them).
- Not forwarded; add to `api` to use: `Audit__Enabled` (Self-audit), `BUDGET_ALERT_SMTP_HOST`, `BUDGET_ALERT_SMTP_PORT`, `BUDGET_ALERT_SMTP_USER`, `BUDGET_ALERT_SMTP_PASS`, `BUDGET_ALERT_EMAIL_FROM`, `BUDGET_ALERT_MESSAGE_ID_DOMAIN`, and `SLACK_WEBHOOK_PROTECTION_KEY`. These are read by API code (`src/AiObservatory.Api/Services/SmtpMailSender.cs:31-34`, `EmailAlertNotifier.cs:26`, `src/AiObservatory.Data/Security/SlackWebhookProtector.cs:22`).
- `OPENAI_ADMIN_KEY` is the only `OPENAI_*` setting the code reads (`src/AiObservatory.Ingest/Program.cs:98`), so nothing else is missing for OpenAI.

### Optional settings

| Setting | Effect when unset |
| --- | --- |
| `Activity__ProjectOwners` | The Activity and GitHub tabs show every project and repository that was ingested. Set it to a comma-separated list of GitHub account names to narrow them to those owners. |
| `VITE_ATTRIBUTION_NAME`, `VITE_ATTRIBUTION_URL` | The footer shows only a link to this project. Set both to credit whoever runs your instance. |
| `Suggestions__*` | Suggestion rules use their built-in thresholds and the daily worker runs. Every threshold and the worker switch are listed in [Suggestions](docs/suggestions.md#settings). |
| `Ingest__PollingIntervalMinutes` | Provider polling cycles run every 60 minutes (`IngestOptions.cs:9`). |
| `Ingest__LookbackDays` | Each polling cycle re-requests the trailing 3 days, ending yesterday, so a missed day is filled on a later cycle; values below 1 are treated as 1 (`IngestOptions.cs:12-16`, `ProviderPollingWorkerService.cs:36`). |
| `BUDGET_ALERT_MESSAGE_ID_DOMAIN` | Budget alert `Message-Id` headers use the domain of your configured sender address, or `observatory.local` if no sender is configured. |

Use neutral placeholders such as `<observatory-api-key>` outside your secret store. See [Provider setup](docs/provider-setup.md) for acquisition settings; public pricing catalogs require no credentials. Google Cloud SKU pricing remains unavailable until verified mappings exist, independently of the bundled Gemini Developer API rates.

## Dashboard truth

![Observatory Overview showing billed spend, list-price estimate, provider estimate, and subscription notional as separate cards](docs/dashboard.png)

The Overview above runs on the Compose demo seed. Note that provider estimate reads `Not reported` rather than zero, because the seed carries no provider-reported cost — the distinction the cards exist to preserve:

- Billed spend contains provider-reported financial or ledger evidence only.
- Estimated cost is API usage rated from an observed public catalog.
- Subscription notional value is a separate comparison, not spend.
- Missing money or tokens read `Not reported`, never zero.
- Source status shows configuration, freshness, failure, and unavailability separately from process liveness.

Supported acquisition includes OpenAI usage/costs, Anthropic usage/cost reports and optional Claude Code analytics, GitHub Copilot organization engagement, Google Cloud Billing BigQuery export, GitHub activity/billing, and eight local transcript collectors. The [provider matrix](docs/provider-setup.md) is the authoritative capability list.

## Self-audit

An administrator can enable the Self-audit tab to compare a selected UTC range of
retained Claude or Codex usage evidence with frozen Observatory figures. It reports
coverage and findings for source accounting, daily attribution, event aggregation,
pricing and displayed totals, then offers a readable report and private bundle for
independent offline replay. The [user guide](docs/self-audit-user-guide.md) explains
the workflow; the [operator guide](docs/self-audit.md) covers the opt-in companion,
retention and verifier archives. The verifier identity is 1.1.0/rules 2, separate
from the Observatory product release number.
The other six local collectors can ingest usage but do not yet have qualified
independent audit extractors; seeing their figures on the dashboard is not an audit verdict.

The historical October audit is **Closed — historical evidence unavailable** with an
immutable **Incomplete** verifier verdict. Fresh prospective Claude and Codex samples
matched their observed usage and displayed figures. Neither result establishes
complete historical source coverage or invoice agreement. Live Google billing export
remains unassessed; see [OSS qualification](docs/oss-qualification.md).

## API

Requests use `X-Observatory-Key` when API keys are configured. The most useful entry points are:

| Method | Route | What it provides |
| --- | --- | --- |
| `GET` | `/api/aggregates` | Daily source-aware usage and cost aggregates |
| `GET` | `/api/sources/status` | Source configuration, freshness, and sanitized status |
| `POST` | `/api/events` | A provenance-labelled usage event |
| `POST` | `/api/spend/feed-status` | Admin key only. Receives the tax portal's spend-feed report of expense ids it skipped, by reason code; only counts are kept. Nothing posts to it yet: the portal has not been changed to call it |
| `GET` | `/api/subscriptions` | Subscription ledger records |
| `GET` | `/api/insights` | Generated insights |
| `GET` | `/api/suggestions` | Rule-based suggestions about review-panel seats and subscription plans, with their evidence. See [Suggestions](docs/suggestions.md) |

Import the [Postman collection](docs/observatory.postman_collection.json), set `base_url` and `api_key`, and use its source-aware event examples. It is a representative collection, not an exhaustive API specification.

## Bringing your own data

Observatory accepts private evidence through three supported paths, none of which requires publishing anything to this repository:

- **Usage events.** `POST /api/events` accepts a provenance-labelled snapshot (`sourceId`, `sourceKind`, `usageScope`, `costBasis`); the [Postman collection](docs/observatory.postman_collection.json) carries source-aware examples. Events are source-scoped, so re-posting a corrected snapshot replaces its previous contribution.
- **Spend entries.** The Spend page records manual ledger entries under the `manual-ledger` source; they behave like provider-billed rows in every total and reconciliation.
- **A private adapter.** For recurring feeds — a custom expense source, an internal billing export — [adding a provider](docs/adding-a-provider.md) describes the compile-time adapter seam. An adapter can live in your own fork or deployment; it never needs to be upstreamed.

## Contributing

Run the focused local-producer check:

```powershell
node --test clients/observatory-sweep.test.mjs
```

Run the solution checks before a pull request:

```powershell
dotnet test --solution AiObservatory.slnx --configuration Release
```

Run the opt-in PostgreSQL repricing qualification workload separately; it reports the median of three 1,000-event pricing activations and does not enforce a machine-specific timing threshold:

```powershell
dotnet run --project tests/AiObservatory.Data.Tests/AiObservatory.Data.Tests.csproj --configuration Release -- --explicit only --filter-method AiObservatory.Data.Tests.Pricing.PricingRepricingServiceTests.QualificationRepricesEveryEligibleEventAndItsAggregate --show-live-output on --output Detailed
```

```powershell
npm --prefix src/AiObservatory.Web test -- --run
```

The repository is [Apache-2.0](LICENSE) licensed. Keep provider changes source-aware and update the setup matrix with any new capability or limitation.

- [Contributing](CONTRIBUTING.md)
- [Code of Conduct](CODE_OF_CONDUCT.md)
- [Security policy](SECURITY.md)

## Troubleshooting

| Symptom | Cause and next step |
| --- | --- |
| A provider is `Not configured` | Supply the exact required settings and upstream access in [Provider setup](docs/provider-setup.md). |
| Google Cloud catalog stays unavailable | Cloud SKU mappings remain unavailable; Gemini Developer API list pricing and billed BigQuery export are independent. |
| Claude activity appears twice | Exclude local Claude telemetry when Claude Code Analytics covers the same activity. |
| A cost is missing | Required model dimensions may be unknown; Observatory intentionally returns no guessed fallback price. |
