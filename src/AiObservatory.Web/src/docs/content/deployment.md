# Deployment

How Observatory runs: the Docker Compose stack, a manual run for development, the database and migrations, the ingest worker, the web server, the demo instance and the Azure infrastructure the repository defines. This chapter describes only what the repository contains.

## Components

| Component       | What it is                                                                                                                                                                                  | Needs                                                            |
| --------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------- |
| Database        | PostgreSQL (the compose stack and the demo bootstrap script use version 16).                                                                                                                | A connection string in `DB_CONNECTION` for both programs.        |
| API             | ASP.NET Core on .NET 10. Serves `/api`, applies database migrations at start, runs the background worker (insights, budget alerts, GitHub billing sync, source-health digest, suggestions). | The database and API keys.                                       |
| Ingest worker   | A .NET worker that polls provider APIs and refreshes price catalogs. Serves only `/healthz`.                                                                                                | The database and the credentials for the sources you want.       |
| Web app         | A React single-page app built to static files.                                                                                                                                              | Somewhere to serve them, and the API reachable from the browser. |
| Local producers | Scripts on developer machines that post usage to the API. See [Local producers](/docs/local-producers).                                                                                     | An admin key and the API URL.                                    |

The API and the ingest worker are separate processes with separate settings. See [Configuration](/docs/configuration).

## Docker Compose

The `docker-compose.yml` in the repository root starts a complete local stack.

```powershell
docker compose up --build
```

The stack has five services.

| Service  | Image or build                                               | Notes                                                                                                                                                     |
| -------- | ------------------------------------------------------------ | --------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `db`     | `postgres:16-alpine`                                         | Database `aiobservatory`. Published on `127.0.0.1:5433` only. Data lives in the named volume `db-data`.                                                   |
| `api`    | Built from `src/AiObservatory.Api/Dockerfile`                | Listens on port 8080 inside the network. Runs with `ASPNETCORE_ENVIRONMENT=Development`. Its health check calls `GET /api/aggregates` with the admin key. |
| `seed`   | `curlimages/curl`                                            | Runs once after the API is healthy: `POST /api/dev/seed`. The seed is synthetic and labelled `demo-seed`; it only writes into an empty database.          |
| `ingest` | Built from `src/AiObservatory.Ingest/Dockerfile`             | Starts after the seed finishes. Its health check calls `GET /healthz`. Sources activate only when their settings are present.                             |
| `web`    | Built from `src/AiObservatory.Web/Dockerfile`, nginx runtime | Published on `127.0.0.1:4173`. Open `http://localhost:4173`.                                                                                              |

Things to know about this stack:

- It is for local use. Everything is bound to loopback, the API runs in `Development`, and the default admin key is `change-me`. Set `OBSERVATORY_API_KEY` in your shell before starting to use another value.
- The web image holds no key. The nginx proxy reads `OBSERVATORY_API_KEY` from the container environment at start and adds it to `/api` requests, so the key is in neither the JavaScript bundle nor the image layers. Anyone who can reach the web port can still use the app with the admin key, which is why the port is bound to loopback. The proxy adds the key only to requests the app itself made (`Sec-Fetch-Site` same-origin, or an `Origin` equal to its own host), so a page on another site cannot drive admin POSTs through it. The key may use only letters, digits and `. _ ~ + / = : @ ! # % ^ & * ( ) -`: it is written into the proxy configuration as a quoted string, and the container refuses to start with a message naming `OBSERVATORY_API_KEY` otherwise. With the key unset the container starts and the API answers 401.
- Host variables are mapped onto API and ingest settings. The mapping is listed in [Configuration](/docs/configuration#docker-compose-variables).
- Local database credentials in the compose file are development defaults. Do not reuse them elsewhere.
- Removing the stack keeps the data. To start from nothing, remove the volume as well: `docker compose down -v`.

## Manual run

Use this when developing the code. You need the .NET 10 SDK, Node `^22.22.2`, `^24.15.0` or `>=26.0.0` (the `engines` range in the web `package.json`), and PostgreSQL 16. The simplest database is the compose one:

```powershell
docker compose up -d --wait db
```

Set the connection string and key, then start the API and the web dev server in separate shells. Replace the placeholders.

```powershell
$env:DB_CONNECTION = 'Host=127.0.0.1;Port=5433;Database=aiobservatory;Username=<user>;Password=<password>'
$env:OBSERVATORY_API_KEY = '<observatory-api-key>'
dotnet run --project src/AiObservatory.Api
```

```powershell
$env:VITE_API_KEY = '<observatory-api-key>'
npm --prefix src/AiObservatory.Web ci
npm --prefix src/AiObservatory.Web run dev
```

Notes:

- Use `127.0.0.1` rather than `localhost` for the compose database. The port is published on IPv4 only, and on Windows `localhost` tries IPv6 first, which adds about two seconds to every new connection.
- The API launch profile listens on `http://localhost:5039`, and the Vite dev server proxies `/api` to that address, so `VITE_API_BASE` stays empty.
- The launch profiles run in `Development`, so key-format checks are relaxed.
- To run the ingest worker as well, set `DB_CONNECTION` in a third shell and run `dotnet run --project src/AiObservatory.Ingest`. Its launch profile listens on `http://localhost:5040`.
- The local sweeper needs Node 24 or later. See [Local producers](/docs/local-producers).

## Database and migrations

- The store is PostgreSQL, accessed through Npgsql and EF Core.
- **Only the API applies migrations**, once at start, before it serves requests. The ingest worker never migrates.
- Because of that, upgrade the API first. The repository's own pipeline stops the ingest worker before deploying the API so that nothing writes while migrations run, deploys the API, then starts ingest again.
- The first start also performs a one-time backfill: if no notification-settings row exists and the legacy `BUDGET_ALERT_EMAIL_TO` setting holds a valid address, it is copied into the stored recipient. After that the setting is ignored.
- The repository's infrastructure template manages only the database's firewall rules. Creating the server, the database and the admin login is a bootstrap step outside the recurring deploy, and so are the passwords and secrets.

## Ingest worker

- Two background loops run in one process: the provider polling loop (first cycle immediately at start, then every `Ingest:PollingIntervalMinutes`, default 60) and the price-catalog refresh (at start when due, then daily).
- A source whose settings are missing is recorded as not configured. See [Data sources](/docs/data-sources) and [Providers](/docs/providers).
- `GET /healthz` returns 200 while the polling loop is alive and 503 if it has died. Linux App Service kills any container that does not answer an HTTP startup probe, which is why a worker hosts a web endpoint at all. It is the only endpoint, by design, because the worker holds provider credentials that the public API does not.
- A late `lastCycleCompletedAt` is shown in the body but does not make the endpoint unhealthy, so a long polling interval cannot cause restarts.
- Configure Application Insights (`APPLICATIONINSIGHTS_CONNECTION_STRING`) on the worker as well as the API. Without telemetry a worker that fails to start looks like a quiet one.

## Web server and proxy

The web image builds the app and serves it with nginx. The relevant behaviour of `nginx.conf.template`, which the nginx image turns into its configuration at start:

| Path          | Behaviour                                                                                                                                                                                                                                                                                                                                                                           |
| ------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `/api/`       | Proxied to `http://api:8080/api/`, with `Host`, `X-Real-IP` and `X-Forwarded-For` set, and `X-Observatory-Key` set to the container's `OBSERVATORY_API_KEY` for same-origin requests unless the request already carries one (a `?key=` viewer link). A cross-site request is passed on without a key. This keeps the browser same-origin, so no CORS, sign-in or key in the bundle. |
| `/assets/`    | Served with `Cache-Control: public, max-age=31536000, immutable`, because Vite fingerprints the file names.                                                                                                                                                                                                                                                                         |
| Anything else | Falls back to `index.html`, so client-side routes such as `/docs/api` work on reload.                                                                                                                                                                                                                                                                                               |

Responses of types JavaScript, JSON, SVG and CSS are gzip-compressed.

The API trusts `X-Forwarded-For` only from private network ranges, so behind this proxy the rate limiter sees the real client address. If you put the API behind a different proxy, make sure it sits in one of `10.0.0.0/8`, `172.16.0.0/12` or `192.168.0.0/16`, otherwise every client shares the proxy's address and its allowance.

On Azure Static Web Apps there is no nginx. The build ships `staticwebapp.config.json`, copied from `public/` into the root of the uploaded files, whose `navigationFallback` serves `index.html` for client-side routes such as `/docs/api`. Requests under `/assets/` and for file types such as `.js`, `.css` and `.png` are excluded, so a missing file is still a 404. Without that file a reload or shared link to a docs chapter answers with the platform's 404 page.

If you serve the web app from a different origin than the API (as the Azure Static Web Apps topology does), set `VITE_API_BASE` at build time to the API's origin and set the API's `SWA_ORIGIN` to the exact origin of the web app, with no trailing slash.

## Verifying a deployment

| Check                                                                             | Expected                                                                                                                                                                                    |
| --------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `GET <api>/api/aggregates` with no key                                            | 401 from a healthy app that has an API key configured. The repository's pipeline uses exactly this probe, so that no key is needed for a health check. A 200, 3xx, 404 or 5xx is a failure. |
| `GET <api>/api/aggregates` with the read-only or admin key in `X-Observatory-Key` | 200                                                                                                                                                                                         |
| `GET <web>/docs/introduction`                                                     | 200 with the app's `index.html`. A 404 from the platform means the client-side route fallback is missing (see [Web server and proxy](#web-server-and-proxy)).                               |
| `GET <ingest>/healthz`                                                            | 200 with `workerRunning: true`                                                                                                                                                              |
| `GET <api>/api/sources/status`                                                    | One row per source; a source you configured should leave `notConfigured`. See [Data sources](/docs/data-sources).                                                                           |
| Worker log line beginning `Intelligence worker arms`                              | Names the alert channels that can deliver and whether GitHub billing is enabled.                                                                                                            |
| Ingest log line beginning `Provider polling arms`                                 | One line listing each source as `enabled` or `NOT CONFIGURED`.                                                                                                                              |

## Azure infrastructure

The `infra/` folder holds a Bicep template and two helper scripts for an Azure deployment of the API, the worker and the web app. You do not need Azure to run Observatory.

### What the template defines

| Resource                   | Notes                                                                                                                                                                                                    |
| -------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Key Vault                  | RBAC authorization, soft delete and purge protection on. Network access is open by default because App Service Key Vault references arrive from shared App Service addresses; access control is by role. |
| App Service plan           | Linux. Size `B1` for a normal stack, `F1` (Free) in demo mode.                                                                                                                                           |
| API web app                | .NET 10 on the plan, HTTPS only, system-assigned identity, Always On except in demo mode.                                                                                                                |
| Ingest web app             | On the same plan, HTTPS only, Always On, health check path `/healthz`. Omitted when `deployIngest` is false.                                                                                             |
| Key Vault role assignments | Each app's identity gets the Key Vault Secrets User role.                                                                                                                                                |
| PostgreSQL firewall rules  | One rule per possible outbound IP of the API and ingest apps. The server itself is referenced as existing, not created.                                                                                  |
| Application Insights       | Backed by a Log Analytics workspace with 30-day retention and a 1 GB daily cap. The cap is a runaway guard: when it trips, data for the rest of the UTC day is discarded.                                |
| Static Web App             | Referenced as existing. Only the custom-domain binding is managed. The site content is uploaded by the deploy workflow.                                                                                  |

Template parameters:

| Parameter                                                                           | Meaning                                                                                            |
| ----------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------- |
| `prefix`                                                                            | Name prefix for every resource.                                                                    |
| `location`, `computeLocation`                                                       | Region for shared resources and for compute.                                                       |
| `aadTenantId`, `aadClientId`                                                        | Entra app registration. An empty client id leaves sign-in off.                                     |
| `anthropicBillingSecretName`, `copilotOrgSecretName`, `githubActivityOrgSecretName` | Key Vault secret names for the optional ingest settings. Pass an empty string to omit the setting. |
| `deployIngest`                                                                      | Whether to create the ingest app.                                                                  |
| `demoMode`                                                                          | Demo stack: Free plan, demo-mode flag, no integration credentials.                                 |
| `swaOrigin`, `swaCustomDomain`                                                      | CORS origin for the API and the Static Web App custom domain.                                      |

The defaults in `main.bicep` are the maintainers' own production values. When deploying your own stack, override `prefix`, `aadTenantId`, `aadClientId`, `swaOrigin` and `swaCustomDomain`.

### Secrets and settings

The template creates no secrets. You create the Key Vault secrets before deploying, and the app settings reference them with `@Microsoft.KeyVault(VaultName=...;SecretName=...)`.

| App    | Settings sourced from Key Vault                                                                                                                                                                       |
| ------ | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| API    | `DB_CONNECTION`, `OBSERVATORY_API_KEY`, `OBSERVATORY_READONLY_API_KEY`, `OBSERVATORY_IDE_API_KEY`; outside demo mode also `ANTHROPIC_API_KEY`, `GITHUB_TOKEN`, `GITHUB_BILLING_ORG`                   |
| Ingest | `DB_CONNECTION`, `GITHUB_TOKEN`, `Ingest__GitHubRepoAllowlist`, `GOOGLE_BILLING_ACCOUNT_ID` (set, but not read by the code); optionally `ANTHROPIC_BILLING_KEY`, `COPILOT_ORG`, `GITHUB_ACTIVITY_ORG` |

Other settings the template writes are plain values: `APPLICATIONINSIGHTS_CONNECTION_STRING`, `SWA_ORIGIN`, `AzureAd__Instance`, `AzureAd__TenantId`, `AzureAd__ClientId` and, in demo mode, `OBSERVATORY_DEMO_MODE`.

Key Vault reference behaviour that has caught operators out:

- A reference that cannot be resolved arrives in the app as the literal `@Microsoft.KeyVault(...)` text. The ingest worker and the GitHub billing arm treat that as unset. The API refuses to start with such a value in a key setting.
- Restarting an app does not make it re-resolve a reference. Re-saving the app setting does.
- Reference resolution can be checked through the App Service `configreferences` API; every row should read `Resolved`.
- App settings written by the template are a full replace, which is why a bare redeploy keeps the optional ingest settings by default and you must pass an empty name to remove one.

### Sign-in setup

`infra/scripts/setup-entra.ps1` creates the single-tenant Entra app registration used for dashboard sign-in and prints the client id, tenant id and scope for the web build and for `aadClientId`. It needs the Azure CLI signed in as a user who can create app registrations. What it configures is described in [Authentication](/docs/authentication#what-the-api-checks).

### The repository's deployment pipeline

For reference, the repository's own GitHub Actions flow is:

1. CI builds and tests. A push to `main` that passes the CI gate calls the deploy workflow; a manual dispatch from `main` is also possible and skips the CI gate.
2. Azure sign-in uses OIDC federated credentials, with no stored Azure secret. Jobs run in a protected `production` environment.
3. **API job:** checks that the IDE key's Key Vault reference is resolved, stops the ingest app, publishes and deploys the API, then probes `/api/aggregates` until it answers exactly 401 (up to 10 minutes).
4. **Ingest job:** publishes and deploys the worker.
5. **Ensure-ingest job:** runs even if a deploy failed, starts the ingest app (three attempts) and probes `/healthz` until it answers 200.
6. **Web job:** builds the app with the `VITE_*` variables and uploads it to the Static Web App with its deployment token. This is the one remaining stored deploy credential.
7. Infrastructure changes are a separate manual workflow that applies the Bicep template incrementally.

Adapt the resource names and environments to your own stack.

## Demo instance

The demo is a second, separate Azure stack that serves synthetic data read-only, so people can try the dashboard without credentials. It shares nothing with production.

What it is:

- The same API and web app, built with `OBSERVATORY_DEMO_MODE=true` and `VITE_DEMO_BANNER=true`. The app shows a synthetic-data banner.
- A database that holds only the synthetic seed. `POST /api/dev/reset-demo` with the admin key restores it, and a scheduled workflow does that nightly at 03:17 UTC.
- Writes are refused for everyone, including the admin key. The read-only key is baked into the public bundle by design and can only read.
- No ingest worker and no provider, billing or other real credentials, so nothing in it can reach a real provider account.

Safeguards in code:

- Demo mode refuses to start unless every database host in `DB_CONNECTION` starts with `fpaiodemo-` or is a local host (`localhost`, `127.0.0.1`, `::1`, `db`). It reports the host, never the credentials.
- The background worker is not registered, so no insights are generated and no alerts or billing syncs run.

Setting it up (summary; the operator guide is `docs/demo.md` in the repository):

1. Create an Entra app registration for the demo workflows with federated credentials for the `demo` GitHub environment. Add two federated credentials, the name-based subject and the ID-based one: a repository with immutable OIDC subjects presents the ID-based subject, and the name-based one alone fails with `AADSTS700213`.
2. Run `infra/scripts/bootstrap-demo.ps1` with PowerShell 7.2 or newer, as an operator signed in to both `az` and `gh` and able to create role assignments. It creates the resource group, PostgreSQL server and database, Static Web App, the Bicep stack with `deployIngest=false` and `demoMode=true`, the Key Vault secrets, and the `demo` GitHub environment with its variables and secrets. It prints no key. The only accepted `-Prefix` is `fpaiodemo`; any `fpaiobs*` prefix is refused. Pass `-DeployClientId` to have the script grant the deploy identity access, and `-SwaCustomDomain <host>` to serve on a custom domain (the CNAME must exist first).
3. Dispatch the demo deploy workflow from `main`, then the demo reset workflow once to seed the database.

Operating notes:

- Re-running the bootstrap script regenerates every key and the database password, and it rotates the Static Web App deployment token too. The deployed web bundle keeps the old read-only key until the demo deploy workflow is dispatched again, and until then the demo shows authentication errors.
- `swaOrigin` is matched as an exact CORS origin: `https://<host>` with no trailing slash and no path. A mistake produces no error; the browser simply blocks every call.
- Scheduled workflows are disabled by GitHub after 60 days without repository activity, so the nightly reset can stop silently. Re-enable it from the Actions tab or run it by hand.
- The Free plan has no Always On, so the API cold-starts after about 20 minutes idle, and each start runs the migrations. Free plans also stop the app when it passes CPU quotas (3 CPU-minutes per 5 minutes, 60 CPU-minutes per day) until the quota resets.
- The database is the paid part and bills whether or not anyone visits.
- Deleting the demo resource group leaves a soft-deleted Key Vault (purge protection is on); recover it with `az keyvault recover --name <vault>` before re-running the script.
- Do not add an all-Azure-services (`0.0.0.0`) firewall rule; the template adds one rule per App Service outbound IP.

## References

- `docker-compose.yml`, `src/AiObservatory.Api/Dockerfile`, `src/AiObservatory.Ingest/Dockerfile`, `src/AiObservatory.Web/Dockerfile`, `src/AiObservatory.Web/nginx.conf.template`
- `src/AiObservatory.Api/Program.cs`, `src/AiObservatory.Ingest/Program.cs`, `src/AiObservatory.Api/DemoMode.cs`
- `infra/main.bicep`, `infra/modules/*.bicep`, `infra/scripts/bootstrap-demo.ps1`, `infra/scripts/setup-entra.ps1`
- `.github/workflows/deploy.yml`, `.github/workflows/infra.yml`, `.github/workflows/demo-reset.yml`, `.github/workflows/deploy-demo.yml`
- `docs/demo.md`, `README.md`
