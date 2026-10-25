# Changelog

Notable changes to Observatory. Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project uses [semantic versioning](https://semver.org/spec/v2.0.0.html).

Dates are the release date, in UTC.

## [Unreleased]

## [1.2.0] — 2026-10-25

### Added

- **Self-audit in the dashboard.** Administrators can select a UTC range and configured
  Claude or Codex source, inspect readiness, request bounded local capture, run an
  independent comparison, and review check-level coverage and findings. Retained
  reports, private evidence export, offline replay, displayed-total verification,
  retention and deletion controls are documented in the [user guide](docs/self-audit-user-guide.md)
  and [operator guide](docs/self-audit.md).
- **Portable verifier archives.** Windows x64, Linux x64 and macOS arm64 runners carry
  source identity, payload hashes, `LICENSE` and `NOTICE`. The verifier remains at
  identity 1.1.0/rules 2; the product release version is independent.
- **Selected capture and operational inventory.** An exact, bounded one-shot capture
  path supports controlled operator runs. A guarded administrator inventory reports
  current storage and capacity counts without exposing private evidence.

### Changed

- Codex cumulative usage is assigned to the observed UTC day and model instead of
  attributing the whole session to its final day and model. Unsafe or missing model
  evidence remains incomplete rather than being guessed.
- Claude prompt-context bands and historical model families receive qualified price
  treatment; missing catalog evidence remains visible as a gap.
- Completed incomplete audits display **Closed · Incomplete**. An operator can close
  permanently lost history as **Closed — historical evidence unavailable** without
  changing the immutable verifier verdict.

### Fixed

- Restored the credential-free Docker Compose quick start after the audit project
  dependency was added. Its web, API, ingest and 70 synthetic aggregates passed the
  local and Linux smoke checks.
- Included the repository's `NOTICE` attribution in every verifier archive and its
  hashed payload manifest.
- Resolved the release's open CodeQL finding and updated the vulnerable development
  dependency; the final npm audit and GitHub security alert checks were clear.

### Qualification

- Fresh Claude and Codex production samples matched independently observed token
  counts, UTC day, model, source, list-price notional and rendered figures; repeat
  collection did not double-count. This does not certify invoices or all past usage.
- The completed historical audit remains **Incomplete** because original evidence was
  lost. Its closed status is an operational disposition, not a Matched verdict.
- Live Google Cloud Billing BigQuery export remains **Not assessed** without a suitable
  export. See [OSS qualification](docs/oss-qualification.md) for the release boundary.

## [1.1.0] — 2026-10-21

### Added

- **Suggestions engine.** Rule-based suggestions about review-panel seats and subscription
  plans, with their evidence, a lifecycle (open, dismissed, resolved), a daily worker and an
  overview card. See [Suggestions](docs/suggestions.md).
- **Panel finding capture and seat influence.** Adversarial review runs record their findings,
  and a seat influence tab shows each seat's catches, cost per unique catch and false-positive
  rate.
- **Public demo instance.** `OBSERVATORY_DEMO_MODE` runs a read-only build on synthetic data,
  with a seeder, a transactional reset, a write block, an on-screen banner and a bootstrap
  script. See [Demo](docs/demo.md).
- **In-app documentation reader** with chapters covering every tab and the settings.
- **Providers and pricing.** xAI, Meta, z.AI and DeepSeek as first-class providers with
  bundled catalogs, Grok 4.6 in the model corpus, and vendor-to-provider linking on spend
  entries.
- **Source health.** A daily digest of degraded ingest sources, delivered through every
  configured alert channel, and a spend-feed health endpoint.
- **Organization-wide GitHub activity.** Repositories are discovered from the organization
  instead of a static list, with a cap on the walk.
- **Spend ledger editing.** A spend entry can be edited from the ledger; provider facts on
  imported rows are rejected.
- **Local sweep.** Unplaced sessions are reported, dormant vendor lanes default off, and a
  retired local source namespace can be purged by exact source id.
- **Web tooling.** Prettier, strict TypeScript flags and `strictTypeChecked` lint for the web
  app, with named exports throughout.

### Changed

- An unknown adversarial-review panel cost is recorded and shown as `Unknown`, never `0.00`,
  and is excluded from averages.
- DeepSeek estimates are rated at the standard tier and presented as an upper bound.
- `GET /api/insights` returns every unread insight, not only the newest 50.
- Google billing export queries are pruned on ingestion date and capped on bytes billed.
- The demo App Service plan runs on the Free (F1) tier.

### Fixed

- The partner-ready message is posted before the Entra gate in the IDE embed.
- A share link now outranks a stale Entra account in the web app.
- The Compose admin key is kept out of the JavaScript bundle and sent only on same-origin
  proxy requests; the Static Web Apps shell is served for client-side routes.
- Polling startup resolution failures are contained, and null pages from the billing, review,
  commit, repository and pull-request clients are rejected instead of treated as empty.
- Codex sessions are attributed by model provider, and misfiled Codex snapshots are replaced
  when the provider changes.
- Dashboard API failures are surfaced, with clearer retry announcements and a larger sign-in
  touch target.

### Security

- Pinned `System.Security.Cryptography.Xml` and `Pkcs` to 10.0.13 for the October 2026
  advisories, and replaced a vulnerable PlantUML glob dependency chain.
- The demo database no longer gets a `0.0.0.0` firewall rule.
- Removed client and machine-specific names from public content.

### History

- Starts a fresh public Git history again: the repository is a single root commit and the
  v1.0.1 tag and release are retired. The maintainer has archived the previous history, tags,
  release metadata and every PR and issue record. If you have an older checkout, preserve
  local work and clone again rather than merge the old and new histories.

## [1.0.1] — 2026-09-10 (archived, tag retired)

### Changed

- Published the OSS qualification record, including test evidence, the unassessed live
  BigQuery integration, and the deferred Gitar review coverage gap.
- Starts a fresh public Git history from the qualified source snapshot. The maintainer
  has archived the pre-cutover history, tags, release metadata, and PR review records.
  Existing contributors should preserve local work and clone again rather than merge
  the old and new histories.

### Fixed

- Strengthened billing aggregation tests and qualified serial mutation runs.
- Repaired local Docker action test fixtures and synchronized the canonical CI policy checks.

## 1.0.0 — archived pre-cutover release

The first release intended for anyone other than its author. Everything before this was
developed in the open but never announced, so this entry describes what the project *is*
rather than what changed since a previous version.

### Added

- **Provenance-aware cost model.** Billed spend, list-price estimates, provider estimates and
  subscription notional values are kept as separate quantities and never silently merged.
  Missing money or tokens read `Not reported`, never zero, and no fallback price is guessed.
- **Providers.** OpenAI usage and costs, Anthropic usage and cost reports with optional Claude
  Code analytics, GitHub Copilot organization engagement, Google Cloud Billing BigQuery export,
  GitHub activity and billing, and six local transcript collectors. The
  [provider matrix](docs/provider-setup.md) is the authoritative capability list.
- **Self-hosting.** `docker compose up --build` brings up PostgreSQL, the API, the ingest
  worker and the frontend with a synthetic demo seed labelled `demo-seed`. Restore runs from
  public feeds and needs no token.
- **Budget alerts** by email and Slack, with a single-delivery lease so an alert is not sent
  twice.
- **HTTP API** with a [Postman collection](docs/observatory.postman_collection.json) covering
  the representative authenticated requests.

### Changed

- Dropped the `AI` prefix from the product name; it is now simply Observatory.
- The Activity and GitHub tabs no longer filter to a hardcoded set of GitHub account owners.
  The owner allowlist is configuration (`Activity__ProjectOwners`), and **leaving it unset
  shows everything** — the previous hardcoded list left every deployment other than the
  author's with two blank tabs and nothing to explain why.
- Footer attribution is configuration (`VITE_ATTRIBUTION_NAME` / `VITE_ATTRIBUTION_URL`).
  Unset shows only a link to the project.
- Budget alert `Message-Id` domain is configuration
  (`BUDGET_ALERT_MESSAGE_ID_DOMAIN`), defaulting to the domain of the configured sender
  rather than to a domain belonging to this project's maintainer.

### Security

- `GET /api/spend/entries` no longer returns `RawPayload`, the provider billing response stored
  verbatim and unredacted, and now requires the admin key rather than admitting the read-only
  viewer key. `GET /api/spend/reporting` is deliberately unchanged: it returns aggregates and
  the Reporting tab is meant to be visible to share-link viewers.

[Unreleased]: https://github.com/FixPortal/fixportal-observatory/compare/v1.2.0...HEAD
[1.2.0]: https://github.com/FixPortal/fixportal-observatory/releases/tag/v1.2.0
[1.1.0]: https://github.com/FixPortal/fixportal-observatory/releases/tag/v1.1.0
