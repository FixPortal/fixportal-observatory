# Scheduled ingest and intelligence jobs

**Status: NOT PROCEEDING — decided 2026-08-08.** Approved on the first draft, then dropped
once cross-vendor review established what the change actually costs. Retained as a record so
the option is not re-proposed and re-analysed from scratch.

**Goal (as proposed):** run both daily background workers as scheduled GitHub Actions jobs
instead of resident App Service background services, so `fpaiobs-api-plan` can drop from B1
to F1 Free.

## Why this was dropped

The saving is GBP 9.90/30d, about GBP 120/year. Against that, review turned up four costs the
first draft had not accounted for:

1. **No federated identity exists** (see Prerequisites). Creating an Entra app, granting Key
   Vault Secrets User and firewall-rule RBAC, is out-of-band Azure administration that must
   land before a single line of this runs.
2. **The F1 burst CPU quota**, 3 minutes per 5, is the binding one — not the 60 min/day
   ceiling the draft analysed. `MigrateAsync()` plus two cache rebuilds all fall on the same
   cold start, and tripping it suspends the app mid-request.
3. **A new FX failure mode.** `FxRateProvider`'s no-expiry cache is wiped by every F1 unload,
   so a cold start during a frankfurter.dev outage fails non-USD ledger writes. The resident
   process masks this today.
4. **A tighter operational obligation.** `LookbackDays` is 3 and writes are first-write-wins,
   so a red run must be acted on within two days. The current resident loop just retries
   hourly and needs nobody.

Plus a manual firewall cleanup on a live database, because incremental Bicep deploys leave
the stale `allow-app-N` rules behind.

GBP 120/year does not buy that. The observatory's actual cost problem was Log Analytics
ingestion, fixed separately in `9a8bce9` and `f1d0f2a`.

**What would change the decision:** a federated identity arriving for another reason, or the
plan needing to scale up anyway. Neither is true as of 2026-08-08.

The design below is unchanged from the reviewed version and remains sound if the economics
ever shift.

## Why

An Azure cost sweep on 2026-08-08 put `fpaiobs-rg` at roughly GBP 37/month, of which the
App Service plan is GBP 9.90/30d. The plan is B1 for exactly one reason: two
`BackgroundService` loops need the app resident, and **Always On is not offered below
Basic** (Microsoft's App Service limits table: the Always On row is blank for Free and
Shared). Removing both loops removes the only thing pinning the plan above Free.

The prize is small and stated plainly: about GBP 120/year. It was approved on the
understanding that the secondary benefits carry equal weight — each job becomes
independently runnable, a failure surfaces as a red workflow instead of silence, and two
processes stop being paid for to sleep 23 hours a day.

**Revised effort estimate, after review (2026-08-08).** The original "roughly a day's work"
assumed a GitHub OIDC federated identity already existed. It does not (see Prerequisites).
Creating and granting one is out-of-band Azure work that must happen before any of this can
run. Anyone weighing this change should re-check that GBP 120/year against the revised
scope before starting, not against the original estimate.

## Prerequisites — not yet satisfied

**There is no federated identity in this repository.** All three `azure/login` steps
(`deploy.yml:47`, `deploy.yml:126`, `infra.yml:28`) authenticate with
`creds: ${{ secrets.AZURE_CREDENTIALS }}` — legacy service-principal JSON. No workflow
requests `id-token: write`, and `infra/` declares no federated credential. An earlier draft
of this spec asserted the workflow could reuse "the existing OIDC identity deploy.yml already
uses"; that identity does not exist and the claim was wrong.

Two routes, neither free:

1. **Create an Entra app with a federated credential** for this repo, grant it Key Vault
   Secrets User on `fpaiobs-kv` and firewall-rule management on `fpaiobs-db`, and migrate the
   new workflow to `id-token: write`. Preferred — no long-lived secret — but it is Azure
   administration this repo cannot perform on its own.
2. **Reuse `AZURE_CREDENTIALS`.** Available today, but its scope has not been verified: it
   may or may not carry Key Vault data-plane access or permission to manage firewall rules.
   Confirm before assuming, and note it is a long-lived secret in a public repository's
   secret store.

Whichever is chosen, it is a blocking prerequisite, not an implementation detail.

## Current state

Observed on 2026-08-08, not assumed:

- `AiObservatory.Ingest` — `ProviderPollingWorkerService : BackgroundService`, hourly loop,
  each cycle ingesting a trailing `LookbackDays` window ending **yesterday**. It hosts a
  minimal Kestrel serving only `/healthz`, which exists solely to answer Linux App Service's
  startup probe (see the restart-loop trap in `deploy-and-ci-traps.md`).
- `AiObservatory.Api` — `IntelligenceWorkerService : BackgroundService`, which computes the
  next midnight UTC and sleeps until it, so it is **already daily**. On start it runs
  `RunAnalysisCatchupAsync`, capped at 7 days, so it already tolerates missed runs.
- Both apps share `fpaiobs-api-plan` (B1). The plan SKU is per plan, so one resident worker
  pins it regardless of the other app.
- `fpaiobs-db` firewall is the union of both apps' `possibleOutboundIpAddresses`
  (`infra/main.bicep`). There is no blanket allow-Azure-services rule.
- `IngestOptions.LookbackDays` defaults to **3** (`IngestOptions.cs:16`). The trailing window
  is three days wide, not indefinite.
- `AiObservatory.Api/Program.cs:163` runs `await db.Database.MigrateAsync()` at startup, so
  every cold start pays it.
- The API holds two in-memory caches that a cold start wipes: `IMemoryCache` via
  `AddMemoryCache()` (`Program.cs:89`), used by `FxRateProvider` which caches historical rates
  with **no expiry** (`FxRateProvider.cs:90` — "a past date's rate cannot change"); and
  `RoutingCatalog._cached` (`RoutingCatalog.cs:72`).

**F1 has two CPU quotas, not one**, and the burst quota is the one that bites:

```
| CPU time (5 minutes)6 | 3 minutes  | 3 minutes   | Unlimited, pay at standard rates | ...
| CPU time (day)6       | 60 minutes | 240 minutes | Unlimited, pay at standard rates | ...
```

Both are enforced **per app**, so the two apps' figures never needed adding together.
Measured over Aug 1-7: `fpaiobs-api` 12.7-17.8 min/day, `fpaiobs-ingest` 7-12.4 min/day.
Only the API's figure matters after this change, and it falls further once the intelligence
worker is removed. The daily ceiling is comfortable; the 3-minutes-per-5-minutes burst
ceiling is the real constraint, because a cold start runs `MigrateAsync()` and rebuilds both
caches at once. Tripping it suspends the app mid-request rather than merely slowing it.

Neither worker needs to be resident. Both are daily-cadence. Both tolerate a missed run, but
only within limits — see Error handling for what "self-heal" does and does not cover.

## Approach

Each app gains a run-once switch that reuses **its own existing composition root verbatim**.
No services move between projects.

- `AiObservatory.Ingest --once` — build the host without the Kestrel web host and without
  `AddHostedService`, resolve the worker, run one poll cycle, exit. The startup-probe Kestrel
  hack is deleted along with the resident loop.
- `AiObservatory.Api --run-jobs` — skip `app.Run()`, resolve `IntelligenceWorkerService`, run
  one pass, exit. Every service it needs is already registered.

`AddHostedService` is **removed from both apps**, not made conditional. Left in place, the
deployed API would keep running its loop resident on F1 with no signal that it was happening,
which is the failure this change exists to remove. One code path; one place the work runs.

### Rejected alternatives

- **Azure Container Apps job on a cron.** Needs a new ACA environment and a registry (GHCR,
  or ACR at GBP 3.80/month — 38% of the prize). Consumption egress IPs are not stable, so the
  Postgres allowlist would need a NAT gateway (about GBP 25/month, making the saving negative)
  or a blanket Azure-services rule, which is a security downgrade from the current per-IP
  allowlist.
- **Azure Functions on Consumption with timer triggers.** Executions are free at this volume,
  but it means porting two substantial DI composition roots onto the Functions host, and the
  egress IPs are equally dynamic, so it hits the identical firewall problem.
- **An HTTP trigger endpoint on the ingest app.** Forbidden by existing design intent:
  `AiObservatory.Ingest/Program.cs` states the worker must never grow an API surface, because
  it holds provider credentials the public-facing API does not.

Every Azure-hosted replacement founders on the same rock — unstable egress against an
IP-allowlisted database — and fixing it costs more than the saving. A GitHub runner can open
and close its own hole, which is why approach A wins.

## Components

### `ProviderPollingWorkerService`

Lift the body of `ExecuteAsync`'s `while` loop into:

```csharp
public async Task<PollOutcome> RunOnceAsync(CancellationToken ct)
```

`ExecuteAsync` becomes a thin wrapper for local development. The existing per-arm `try/catch`
blocks stay exactly as they are — each arm still isolated — but each arm's result is now
recorded in the returned `PollOutcome` rather than only logged.

### `IntelligenceWorkerService`

The same refactor: `RunOnceAsync(CancellationToken)` containing the current
`RunAnalysisCatchupAsync` / `RunBudgetCheckAsync` / `RunGitHubBillingSyncAsync` sequence,
returning an outcome. The midnight-delay arithmetic belongs to the wrapper and is not used by
the job.

### `PollOutcome`

One record per run: which arms were configured, which succeeded, which failed and with what
exception. It is what the entrypoint turns into an exit code, and what the tests assert on.

### Entrypoints

Both `Program.cs` files branch on the CLI argument **before** building. The no-argument path
must remain the existing web behaviour unchanged, because
`WebApplicationFactory<Program>` constructs the host with no args and every composition-root
test depends on it.

## Scheduling and data flow

**One workflow, not two.** Intelligence analyses what ingest has just written, so the two are
ordered, not independent — sequential steps in a single scheduled workflow, ingest first.
Today they are only loosely coupled because ingest runs hourly and intelligence at midnight;
making the dependency explicit is strictly better than inheriting that accident.

Cadence: once daily. Confirmed with the owner — every provider call ingests yesterday's
usage, so an hourly poll bought nothing.

```
schedule (daily, after 00:00 UTC so "yesterday" is complete)
  -> azure/login (OIDC, existing federated identity)
  -> prune leftover gha-* firewall rules
  -> resolve runner egress IP, create firewall rule gha-<run_id>
  -> read secrets from Key Vault, mask, export as env vars
  -> dotnet AiObservatory.Ingest.dll --once
  -> dotnet AiObservatory.Api.dll --run-jobs
  -> delete firewall rule            [if: always()]
```

## Error handling

**The exit code is the point of this change.** Both workers catch broadly by design, so one
bad arm cannot kill a loop that must survive to tomorrow. Transplanted unchanged into a
scheduled job, that same code produces a green workflow with no data written — the exact
silent failure the ingest app was rebuilt to eliminate.

Decided: **exit non-zero if any configured arm failed.** Not "all arms failed", which is
today's GitHub-repo rule and far too weak for a job. A provider outage will red the workflow
until it clears, and that is information rather than an incident.

**What self-heal actually covers.** Re-ingesting a date is idempotent, but the repair is
bounded and an earlier draft of this spec overstated it:

- `LookbackDays` is **3**, so up to two consecutive missed runs are repaired automatically.
  Three or more consecutive misses leave a permanent gap.
- Writes are **first-write-wins**, so a provider restating a day already recorded is silently
  discarded. Re-running does not correct an earlier bad figure.
- The GitHub arm's backfill never re-triggers once a repo has rows, so a gap there is
  permanent regardless of the lookback window.

A red run therefore needs acting on within two days, not merely noting. That is a stronger
obligation than a resident hourly loop imposed, and it is the main operational cost of this
change.

An arm that is not configured is not a failure. The existing "NOT CONFIGURED" logging stays,
because an unregistered arm and a registered arm that found nothing are otherwise
indistinguishable.

## Security

- Secrets are read from Key Vault at run time by whichever identity Prerequisites settles on,
  and exported under the names `Program.cs` already reads (`DB_CONNECTION`,
  `ANTHROPIC_BILLING_KEY`, `GITHUB_TOKEN`, `COPILOT_ORG`, `Ingest__GitHubRepoAllowlist`,
  `GOOGLE_BILLING_ACCOUNT_ID`). Nothing is duplicated into GitHub secrets. Each is masked with
  `::add-mask::` on read.
- That identity needs **Key Vault Secrets User** on `fpaiobs-kv` and permission to manage
  firewall rules on `fpaiobs-db`. Neither is granted today.
- **This repository is public.** The workflow carries `schedule` and `workflow_dispatch`
  triggers only — never `pull_request` or `pull_request_target`, either of which would expose
  live provider credentials to code from a fork — with `permissions: { id-token: write,
  contents: read }` and nothing else.
- Net firewall position improves **only if the stale rules are cleaned up by hand.** Bicep
  deploys are incremental, so shrinking the `allowedIps` list does not delete the
  `allow-app-N` rules already on `fpaiobs-db` — they persist, still allowing the deleted
  app's ranges. Deleting them is an explicit migration step; without it the allowlist gets
  strictly worse, not better, because the per-run rule is added on top of ranges that never
  went away.
- Per-run rules are pruned at the start of each run as well as deleted at the end, because a
  cancelled run's cleanup cannot be assumed to have executed.

## Infrastructure changes

- Delete `fpaiobs-ingest` from `infra/modules/ingest.bicep` and its `main.bicep` wiring,
  including its Key Vault role assignment and its contribution to the Postgres allowlist.
- **Delete the `deploy-ingest` job from `deploy.yml` (line 102) and the `/healthz` deploy gate
  that follows it** (lines 134-160). Left in place it deploys to an app that no longer exists
  and then fails the workflow waiting for a health check that can never pass.
- **Manually delete the stale `allow-app-N` firewall rules** on `fpaiobs-db` after the Bicep
  deploy, per the Security note above.
- `fpaiobs-api-plan`: `B1` -> `F1`; `alwaysOn: true` -> `false` on `fpaiobs-api`.
- Add the Key Vault role assignment for whichever identity Prerequisites settles on.

Accepted consequence: the API cold-starts after 20 minutes idle. Confirmed acceptable — this
is a single-user dashboard.

## Risks

- **Cold start turns an FX outage into a write failure.** `FxRateProvider` caches historical
  rates with no expiry, so today the process accumulates them and rarely calls out. Every F1
  unload wipes that cache, so a cold start during a frankfurter.dev outage makes non-USD
  spend-ledger writes throw `FxUnavailableException`. Today's resident process largely masks
  this. Cold start is not purely a latency question.
- **Burst CPU quota.** As above: `MigrateAsync()` plus two cache rebuilds on every cold start,
  against a 3-minutes-per-5-minutes ceiling. Worth measuring a real cold start before trusting
  the daily-average headroom. Note the 7-day intelligence catch-up no longer contributes,
  because `AddHostedService` is removed — after this change the resident API never runs
  catch-up at all; it runs only on the GitHub runner.
- **GitHub disables scheduled workflows after 60 days of repository inactivity.** That is a
  silent off-switch for the only thing that now writes data. Whatever monitors ingest
  freshness must alert on staleness rather than assume the schedule is still firing.

## Testing

- `RunOnceAsync` returns a failed outcome when an arm throws, and a successful one when every
  configured arm succeeds.
- The entrypoint maps a failed outcome to a non-zero exit code and a successful one to zero.
- An unconfigured arm does not produce a failed outcome.
- Existing composition-root tests must pass untouched, which the no-args-is-web-mode rule
  above guarantees.

## Out of scope

- **`GOOGLE_APPLICATION_CREDENTIALS` is never set by `ingest.bicep`**, although
  `GOOGLE_BILLING_ACCOUNT_ID` is. `GoogleBillingClient` needs the credentials file, so if that
  Key Vault secret is populated the Google arm registers and then fails authentication on
  every cycle. Whether the secret exists has not been checked. This change inherits the
  behaviour unaltered; it is recorded here so the migration does not get blamed for it, and
  it should be settled separately.
- Reducing App Insights ingestion further. The `AppTraces` trim landed on 2026-08-08 in
  `9a8bce9` and `f1d0f2a`; its effect had not yet reached billing data when this was written.

## Rollback

The Bicep change is revertible, but `fpaiobs-ingest` is deleted rather than stopped, so
reverting means redeploying the app, not restarting it. The workflow can be disabled
independently of the infrastructure if the jobs misbehave while the plan change is fine.
