# Changelog

Notable changes to Observatory. Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project uses [semantic versioning](https://semver.org/spec/v2.0.0.html).

Dates are the release date, in UTC.

## [Unreleased]

## [1.0.1] — 2026-09-10

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

[Unreleased]: https://github.com/FixPortal/fixportal-observatory/compare/v1.0.1...HEAD
[1.0.1]: https://github.com/FixPortal/fixportal-observatory/releases/tag/v1.0.1
