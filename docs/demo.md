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

Prerequisite: the operator running the script needs more than a logged-in `az` session. They must be able to create role assignments (Owner or User Access Administrator on the subscription or the resource group), because the script grants itself Key Vault Secrets Officer and grants the deploy identity Contributor.

1. **Deploy identity.** Create an Entra app registration for the demo workflows, add **two** federated credentials, make sure it has a service principal, and note its client id. The subjects are `repo:FixPortal/fixportal-observatory:environment:demo` and the ID-based form `repo:FixPortal@<owner id>/fixportal-observatory@<repo id>:environment:demo`. This repository has `use_immutable_subject` on (`gh api repos/FixPortal/fixportal-observatory/actions/oidc/customization/sub`), so GitHub presents the ID-based subject and the name-based one alone fails with `AADSTS700213: No matching federated identity record found`. Read the ids from `sub_claim_prefix` in that response. Production carries both forms for the same reason. Do not grant it any role yet: the bootstrap script grants `Contributor` scoped to the demo resource group (`fpaiodemo-rg`) when you pass `-DeployClientId`. If you omit that parameter, grant it by hand after step 2: `az role assignment create --assignee <client-id> --role Contributor --scope <resource group id>`, and set the `AZURE_CLIENT_ID` variable on the `demo` environment. This is separate from the production `fpaiobs-deploy` app.
2. **Run the bootstrap script** with PowerShell 7.2 or newer, as an operator logged in to `az` and `gh`:

   ```powershell
   pwsh -File infra/scripts/bootstrap-demo.ps1 -DeployClientId <client-id>
   ```

   It creates the resource group, PostgreSQL server and database, Static Web App, the Bicep stack (`deployIngest=false`, `demoMode=true`, no Entra sign-in, no custom domain), the Key Vault secrets, and the GitHub environment `demo` with its variables (`AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, `DEMO_API_URL`, `DEMO_READONLY_KEY`) and secrets (`DEMO_ADMIN_KEY`, `DEMO_SWA_DEPLOYMENT_TOKEN`). The environment is restricted to `main`. It prints no key. Re-running regenerates every key and the database password and re-applies them everywhere. The only accepted `-Prefix` is `fpaiodemo` (case-sensitive, the default): the API starts only against a database host beginning `fpaiodemo-` (`DemoHostPrefix` in `src/AiObservatory.Api/DemoMode.cs`), and the script refuses any `fpaiobs*` prefix.
3. **Custom domain (optional).** Add a CNAME from your hostname to the Static Web App default hostname the script printed, then re-run the script with `-SwaCustomDomain <host>` (a bare lowercase hostname: no scheme, no slash). The script passes it to Bicep as `swaCustomDomain` and sets the API's CORS origin (`SWA_ORIGIN`) to `https://<host>`. Do not hand-run `az deployment group create` against the demo resource group: `infra/main.bicep` defaults are the production values (`prefix=fpaiobs`, `demoMode=false`, `deployIngest=true`, the production `aadClientId`). A re-run rotates every key (see below), so dispatch `deploy-demo.yml` afterwards.
   Before dispatching, check the API's Key Vault references resolved. On the first live run all four stayed `AccessToKeyVaultDenied` after the script finished, although the role assignment was correct, and the API crashed at startup with `Couldn't set @microsoft.keyvault(vaultname` from `DemoMode.EnsureSafeDatabase`. A restart did not clear it; re-saving an app setting with its own value did:

   ```powershell
   $sub = az account show --query id -o tsv
   az rest --method get --url "https://management.azure.com/subscriptions/$sub/resourceGroups/fpaiodemo-rg/providers/Microsoft.Web/sites/fpaiodemo-api/config/configreferences/appsettings?api-version=2022-03-01" --query "value[].{name:name,status:properties.status}" -o table
   ```

   Every row should read `Resolved`. If not, write `{ "DB_CONNECTION": "@Microsoft.KeyVault(VaultName=fpaiodemo-kv;SecretName=db-connection)" }` to a file and run `az webapp config appsettings set -g fpaiodemo-rg -n fpaiodemo-api --settings "@<file>"`. Pass the setting through a file: the `az` wrapper on Windows mis-parses the parentheses and semicolons inline.
4. **Dispatch `deploy-demo.yml`** from `main`. It publishes the API, probes for the expected `401`, then builds and uploads the SPA.
5. **Dispatch `demo-reset.yml`** once to seed the database. It then runs nightly at 03:17 UTC.
6. **Link it from the README.** Once the real demo URL exists, replace the target of the "public demo" line in `README.md` (currently `docs/demo.md`) with it.

`demo-reset.yml` is a scheduled workflow, and GitHub disables scheduled workflows after 60 days without repository activity. On a quiet repository the nightly reset can stop silently; re-enable it from the Actions tab (select the workflow, then Enable workflow), or dispatch it by hand.

Check the result: `GET <DEMO_API_URL>/api/aggregates` with the read-only key (`X-Observatory-Key`) should return `200`.

### `swaOrigin` must be a bare origin

`swaOrigin` is matched as an exact CORS origin: the API passes the single `SWA_ORIGIN` value to `WithOrigins`. It must be the origin visitors actually load the SPA from, `https://<swa default hostname>` or, once a custom domain is announced, `https://<custom host>`, with **no trailing slash** and no path. A trailing slash or the wrong host does not raise an error: the browser origin never matches, and every API call from the demo SPA fails. The bootstrap script builds it correctly; only a hand-run `az deployment` can get this wrong.

## Rotating the read-only key

1. Set a new value for the Key Vault secret `observatory-readonly-api-key` in `fpaiodemo-kv`.
2. Restart the API: `az webapp restart --resource-group fpaiodemo-rg --name fpaiodemo-api`.
3. Update the `DEMO_READONLY_KEY` variable on the `demo` environment.
4. Dispatch `deploy-demo.yml` so the web bundle is rebuilt with the new key.

Until step 4 completes the live demo shows authentication errors.

A bootstrap script re-run rotates the read-only key the same way, and the deployed SPA keeps the old key until `deploy-demo.yml` is dispatched again.

## Tearing down

Key Vault purge protection is on, so deleting the demo resource group leaves a soft-deleted vault, and re-running the script then fails on it. Recover the vault before re-running: `az keyvault recover --name fpaiodemo-kv`.

## Re-run behaviour not yet observed

Observed on the first live runs (2026-10-11, three runs against one server): the reuse path (password reset, enable public access) and `firewall-rule create` on an existing rule exited 0.

- UNVERIFIED: re-running `az postgres flexible-server db create` for an existing database is assumed idempotent. Its first success was the third run; it has not run against an existing database. Refuted if a second full run throws "Failed to create database aiobservatory".
- UNVERIFIED: re-running `az role assignment create` for an existing assignment is assumed to succeed. The grants first executed on the third run. Refuted if a second full run throws on the Key Vault Secrets Officer or Contributor grant.

`az postgres flexible-server create --public-access None` leaves `publicNetworkAccess` Disabled (the CLI help says otherwise), and firewall rules then fail with "Firewall rule operations are not supported for a server without public access enabled". The script creates the server with `--public-access 0.0.0.0` and re-enables public access on a reused server.

## Notes for the production operator

The App Service settings array order changed in `infra/main.bicep`; the set of settings is identical. When you compare `az deployment group what-if` for the production resource group, compare setting names and values, not the diff shape.

## Cost

The stack is a Free Static Web App, a Burstable `Standard_B1ms` PostgreSQL server with 32 GB, and the App Service plan and Application Insights defined in `infra/`. Check the SKUs against the Azure price calculator before running the script; the database and the App Service plan are the paid parts, and they bill whether or not anyone visits.
