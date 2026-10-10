# Public demo instance

A second, separate Azure stack that serves synthetic data read-only, so people can try the dashboard without credentials or a local install. It shares no resources, secrets or GitHub environment with production.

## What it is, and is not

- It is the same API and SPA built with `OBSERVATORY_DEMO_MODE=true` and `VITE_DEMO_BANNER=true`. The SPA shows a synthetic-data banner.
- The database holds only the synthetic seed. `POST /api/dev/reset-demo` (admin key) restores it; `demo-reset.yml` does that nightly.
- Writes are blocked in demo mode. The read-only key is baked into the public bundle by design and can only read.
- It has no ingest worker (`deployIngest=false`) and no provider, billing or other real credentials, by design. Nothing in it can reach a real provider account.
- It is not production. Production resources use the `fpaiobs` prefix; the bootstrap script refuses any prefix starting with it.

## One-time setup

Order matters.

1. **Deploy identity.** Create an Entra app registration for the demo workflows, add a federated credential with subject `repo:FixPortal/fixportal-observatory:environment:demo`, make sure it has a service principal, and note its client id. Do not grant it any role yet: the bootstrap script grants `Contributor` scoped to the demo resource group (`fpaiodemo-rg`) when you pass `-DeployClientId`. If you omit that parameter, grant it by hand after step 2: `az role assignment create --assignee <client-id> --role Contributor --scope <resource group id>`, and set the `AZURE_CLIENT_ID` variable on the `demo` environment. This is separate from the production `fpaiobs-deploy` app.
2. **Run the bootstrap script** with PowerShell 7.2 or newer, as an operator logged in to `az` and `gh`:

   ```powershell
   pwsh -File infra/scripts/bootstrap-demo.ps1 -DeployClientId <client-id>
   ```

   It creates the resource group, PostgreSQL server and database, Static Web App, the Bicep stack (`deployIngest=false`, `demoMode=true`, no Entra sign-in, no custom domain), the Key Vault secrets, and the GitHub environment `demo` with its variables (`AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, `DEMO_API_URL`, `DEMO_READONLY_KEY`) and secrets (`DEMO_ADMIN_KEY`, `DEMO_SWA_DEPLOYMENT_TOKEN`). The environment is restricted to `main`. It prints no key. Re-running regenerates every key and the database password and re-applies them everywhere.
3. **DNS (optional).** To serve the demo on your own hostname, add a CNAME to the Static Web App default hostname the script printed, then redeploy the infrastructure with `swaCustomDomain` set. The CORS origin (`swaOrigin`) stays the default hostname unless the SPA is served only from the custom domain.
4. **Dispatch `deploy-demo.yml`** from `main`. It publishes the API, probes for the expected `401`, then builds and uploads the SPA.
5. **Dispatch `demo-reset.yml`** once to seed the database. It then runs nightly at 03:17 UTC.

Check the result: `GET <DEMO_API_URL>/api/aggregates` with the read-only key (`X-Observatory-Key`) should return `200`.

### `swaOrigin` must be a bare origin

`swaOrigin` is matched as an exact CORS origin. It must be `https://<swa default hostname>` with **no trailing slash** and no path. A trailing slash does not raise an error: the browser origin never matches, and every API call from the demo SPA fails. The bootstrap script builds it correctly; only a hand-run `az deployment` can get this wrong.

## Rotating the read-only key

1. Set a new value for the Key Vault secret `observatory-readonly-api-key` in `fpaiodemo-kv`.
2. Restart the API: `az webapp restart --resource-group fpaiodemo-rg --name fpaiodemo-api`.
3. Update the `DEMO_READONLY_KEY` variable on the `demo` environment.
4. Dispatch `deploy-demo.yml` so the web bundle is rebuilt with the new key.

Until step 4 completes the live demo shows authentication errors.

## Cost

The stack is a Free Static Web App, a Burstable `Standard_B1ms` PostgreSQL server with 32 GB, and the App Service plan and Application Insights defined in `infra/`. Check the SKUs against the Azure price calculator before running the script; the database and the App Service plan are the paid parts, and they bill whether or not anyone visits.
