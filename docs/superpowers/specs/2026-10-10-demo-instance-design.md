# Public demo instance — design

Date: 2026-10-10. Status: approved; amended by the implementation plan.

Amendments found while reading the code are listed under "Deviations from the spec" in
`docs/superpowers/plans/2026-10-10-demo-instance.md`. Where the two disagree, the plan wins.

## Purpose

A public, one-click, read-only Observatory that shows only synthetic data, so the
project can be announced and linked without exposing the maintainer's real AI spend,
project names or Entra identity. The production instance (`fpaiobs-*`) is unchanged.

Success criteria:

- A visitor opens one URL and sees a populated dashboard, with no key and no sign-in.
- The demo has no network or credential path to production data, vaults or Entra app.
- Visitors cannot change demo data; any drift or defacement is undone by the next reset.
- With the new setting off, API behaviour is identical to today.

## Decisions (agreed in brainstorming)

| Decision | Choice |
| --- | --- |
| Hosting | Second Azure stack from `infra/main.bicep` (approach A) |
| Data | Existing `demo-seed` data, loaded through an API demo mode |
| Access | Open: the demo web build bakes in the demo read-only key; no `?key=` needed |
| Isolation | Own resource group, Key Vault, Postgres, API, static web app, GitHub environment, OIDC credential |

Rejected: a slim separate `demo.bicep` (templates drift apart); sharing production's
App Service plan or Postgres server (a demo fault could affect production).

## Architecture

### 1. Infrastructure

- Deploy `main.bicep` into a new resource group with `prefix = fpaiodemo` and blank
  `aadTenantId` / `aadClientId`, so JWT auth is off and API-key auth is used.
- Add parameter `deployIngest bool = true`. The demo passes `false`; production's
  default leaves its deployment unchanged. The demo runs no ingest app, so nothing polls
  real providers.
- The demo Key Vault holds `observatory-api-key` (admin) and
  `observatory-readonly-api-key`, both generated fresh. No production secret is copied.
- UNVERIFIED: that the API works with Entra IDs blank and API-key auth only on a real
  deployment. `main.bicep` comments say it does. Refuted if the first demo deploy rejects
  the read-only key on GET.

### 2. API demo mode

New setting `OBSERVATORY_DEMO_MODE=true` (absent or false is the default and changes nothing).

When on:

- The seed endpoint is registered outside Development.
- Every non-GET request returns 403, including with the admin key, except the single
  exempt route below.
- Startup guard: the API refuses to start unless `DB_CONNECTION` and the configured
  Key Vault name match the demo naming (`fpaiodemo-*`). This stops demo mode being
  switched on against production by mistake.

### 3. Reset

- `POST /api/dev/reset-demo`, registered only when demo mode is on and the startup
  guard has passed. Admin key only; the read-only key can never reach it.
- In one database transaction: delete all rows from every table the seed writes, then
  run the existing seed. A visitor never sees an empty dashboard; a seed failure rolls
  back and the previous data remains.
- The table list is derived from the seed's own list (single source), so the reset and
  the seed cannot drift.
- Context: the seed's guard (`Program.cs`, "No TRUNCATE") exists to avoid a data-loss
  vector. This route reintroduces a wipe deliberately, which is why it carries all three
  guards (demo mode, admin key, startup guard) and is absent from the route table
  otherwise.

### 4. Frontend

- Demo build sets `VITE_API_BASE` to the demo API, `VITE_API_KEY` to the demo read-only
  key, and `VITE_AAD_*` empty.
- A "synthetic data" banner is shown, driven by a build-time flag.

### 5. CI/CD

- `deploy-demo.yml`: separate workflow, `environment: demo`, its own OIDC federated
  credential (`repo:FixPortal/fixportal-observatory:environment:demo`). The production
  credential and the demo credential cannot act on each other's resources.
- `demo-reset.yml`: nightly and `workflow_dispatch`, `environment: demo`, calls
  `reset-demo` with the demo admin key read from the demo vault.
- One-time manual setup, not code: resource group, GitHub environment and variables,
  federated credential, DNS record for the demo hostname, repository README link.

## Error handling

- Seed failure: reset workflow fails visibly; prior data stays; nothing partial commits.
- Startup guard failure: API does not start; the deployment goes red.
- Demo mode off: none of the demo routes exist and the write filter is unchanged.

## Testing

- Filter tests: demo mode on, non-GET with admin key returns 403; non-GET with the
  read-only key is rejected; `reset-demo` succeeds with the admin key only; demo mode
  off, `reset-demo` returns 404.
- Startup guard test: demo mode with a non-demo connection string fails to start.
- Integration (Testcontainers, as the repo already does): seed, mutate rows, reset,
  assert the seed state is restored; assert a forced seed failure leaves prior rows.
- Smoke: extend `scripts/compose-smoke.mjs` with a demo-mode profile asserting the
  banner, the 403 on write, and 70 seeded aggregates.
- Mutation testing stays with the existing workflow; no new configuration.

## Out of scope

- Extending or restyling the seed data (can follow once the plumbing works).
- Scrubbing historical identifiers from public git history.
- Any change to the production instance or its keys.

## Risks

- Cost: a second Postgres and App Service. UNVERIFIED estimate; check the SKUs in
  `postgresql.bicep` and `appservice.bicep` against the Azure price calculator before
  deploying, and prefer the smallest tier.
- A public API accepts anonymous traffic; the existing `api` rate limiter applies and
  should be confirmed active in demo mode.
