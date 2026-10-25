# Public demo instance

A second, separate Azure stack that serves synthetic data read-only, so people can try the dashboard without credentials or a local install. It shares no resources, secrets or GitHub environment with production.

## What it is, and is not

- It is the same API and SPA built with `OBSERVATORY_DEMO_MODE=true` and `VITE_DEMO_BANNER=true`. The SPA shows a synthetic-data banner.
- The database holds only the synthetic seed. `POST /api/dev/reset-demo` (admin key) restores it; `demo-reset.yml` does that nightly.
- Writes are blocked in demo mode. The read-only key is baked into the public bundle by design and can only read.
- It has no ingest worker (`deployIngest=false`) and no provider, billing or other real credentials, by design. Nothing in it can reach a real provider account.
- It is not production. Production resources use the `fpaiobs` prefix; the bootstrap script refuses any prefix starting with it.

## What a visitor can explore

- **Overview:** fourteen rolling days of invented API and subscription/notional usage across Anthropic, Google, OpenAI and Copilot models, two subscription examples, three insights and an evidence-backed seat suggestion. All usage rows retain the `demo-seed` source identity.
- **Adversarial Review:** five complete synthetic panels with the five active reviewer vendors and a judge. Scenario-specific finding IDs and varied outcomes populate the Seats view, including shared and unique accepted catches, refuted, dropped and indeterminate findings, and one unknown-cost row.
- **Reporting:** three months of invented billed ledger entries for three vendors, with different monthly totals so period comparisons have data.
- **Docs:** the same in-app guide as a normal installation. Self-audit remains disabled in the public read-only demo because there is no private native evidence or registered capture machine.

Source status correctly reports no live provider connections. The demo does not call or impersonate upstream accounts, and its invented monetary values are examples of the dashboard's evidence categories rather than price or invoice claims. The reset replaces these examples together, including the review findings and suggestion.

## One-time setup

Order matters.

Prerequisite: the operator running the script needs more than a logged-in `az` session. They must be able to create role assignments (Owner or User Access Administrator on the subscription or the resource group), because the script grants itself Key Vault Secrets Officer and grants the deploy identity Contributor.

1. **Deploy identity.** Create an Entra app registration for the demo workflows, add **two** federated credentials, make sure it has a service principal, and note its client id. The subjects are `repo:FixPortal/fixportal-observatory:environment:demo` and the ID-based form `repo:FixPortal@<owner id>/fixportal-observatory@<repo id>:environment:demo`. This repository has `use_immutable_subject` on (`gh api repos/FixPortal/fixportal-observatory/actions/oidc/customization/sub`), so GitHub presents the ID-based subject and the name-based one alone fails with `AADSTS700213: No matching federated identity record found`. Read the ids from `sub_claim_prefix` in that response. Production carries both forms for the same reason. Do not grant it any role yet: the bootstrap script grants `Contributor` scoped to the demo resource group (`fpaiodemo-rg`) when you pass `-DeployClientId`. If you omit that parameter, grant it by hand after step 2: `az role assignment create --assignee <client-id> --role Contributor --scope <resource group id>`, and set the `AZURE_CLIENT_ID` variable on the `demo` environment. This is separate from the production `fpaiobs-deploy` app.
2. **Run the bootstrap script** with PowerShell 7.2 or newer, as an operator logged in to `az` and `gh`:

   ```powershell
   pwsh -File infra/scripts/bootstrap-demo.ps1 -DeployClientId <client-id>
   ```

   It creates the resource group, PostgreSQL server and database, Static Web App, the Bicep stack (`deployIngest=false`, `demoMode=true`, no Entra sign-in, no custom domain), the Key Vault secrets, and the GitHub environment `demo` with its variables (`AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, `DEMO_API_URL`, `DEMO_READONLY_KEY`) and secrets (`DEMO_ADMIN_KEY`, `DEMO_SWA_DEPLOYMENT_TOKEN`). The environment is restricted to `main`. It prints no key. Re-running regenerates every key, the database password and the Static Web App deployment token, and re-applies them everywhere. The only accepted `-Prefix` is `fpaiodemo` (case-sensitive, the default): the API starts only against a database host beginning `fpaiodemo-` (`DemoHostPrefix` in `src/AiObservatory.Api/DemoMode.cs`), and the script refuses any `fpaiobs*` prefix.
3. **Custom domain (optional).** Add a CNAME from your hostname to the Static Web App default hostname the script printed, then re-run the script with `-SwaCustomDomain <host>` (a bare lowercase hostname: no scheme, no slash). The script passes it to Bicep as `swaCustomDomain` and sets the API's CORS origin (`SWA_ORIGIN`) to `https://<host>`. Do not hand-run `az deployment group create` against the demo resource group: `infra/main.bicep` defaults are the production values (`prefix=fpaiobs`, `demoMode=false`, `deployIngest=true`, the production `aadClientId`). A re-run rotates every key (see below), so dispatch `deploy-demo.yml` afterwards. Once the custom domain is attached the default `*.azurestaticapps.net` origin is no longer CORS-allowed (observed: no `Access-Control-Allow-Origin` header for it), so move the README link and the repository `homepage` to the custom host in the same change.
   Before dispatching, check the API's Key Vault references resolved. A restart does not re-resolve them, so the script re-saves the four reference settings after it writes the secrets. Observed without that step: on the first live run all four stayed `AccessToKeyVaultDenied` although the role assignment was correct and the API crashed at startup with `Couldn't set @microsoft.keyvault(vaultname` from `DemoMode.EnsureSafeDatabase`; on the custom-domain re-run the app kept the old secret versions and crashed with `28P01: password authentication failed for user "aiobsadmin"` against the rotated database password. In both cases a restart did not clear it and re-saving an app setting with its own value did. If a run still leaves a row unresolved:

   ```powershell
   $sub = az account show --query id -o tsv
   az rest --method get --url "https://management.azure.com/subscriptions/$sub/resourceGroups/fpaiodemo-rg/providers/Microsoft.Web/sites/fpaiodemo-api/config/configreferences/appsettings?api-version=2022-03-01" --query "value[].{name:name,status:properties.status}" -o table
   ```

   Every row should read `Resolved`. If not, write `[{ "name": "DB_CONNECTION", "value": "@Microsoft.KeyVault(VaultName=fpaiodemo-kv;SecretName=db-connection)" }]` to a file (the array-of-records form the Azure CLI documents; a flat `{ "NAME": "value" }` object was also accepted by the installed CLI) and run `az webapp config appsettings set -g fpaiodemo-rg -n fpaiodemo-api --settings "@<file>"`. Pass the setting through a file: the `az` wrapper on Windows mis-parses the parentheses and semicolons inline.
4. **Dispatch `deploy-demo.yml`** from `main`. It publishes the API, probes for the expected `401`, then builds and uploads the SPA.
5. **Dispatch `demo-reset.yml`** once to seed the database. It then runs nightly at 03:17 UTC.
6. **Link it from the README.** Once the real demo URL exists, point the "public demo" line in `README.md` at it, and set the repository `homepage` field (`gh repo edit --homepage <url>`). Before linking, load the page in a browser and confirm the synthetic-data banner and figures render; a 200 on the root does not prove the API calls work.

`demo-reset.yml` is a scheduled workflow, and GitHub disables scheduled workflows after 60 days without repository activity. On a quiet repository the nightly reset can stop silently; re-enable it from the Actions tab (select the workflow, then Enable workflow), or dispatch it by hand.

Check the result: `GET <DEMO_API_URL>/api/aggregates` with the read-only key (`X-Observatory-Key`) should return `200`.

### `swaOrigin` must be a bare origin

`swaOrigin` is matched as an exact CORS origin: the API passes the single `SWA_ORIGIN` value to `WithOrigins`. It must be the origin visitors actually load the SPA from, `https://<swa default hostname>` or, once a custom domain is announced, `https://<custom host>`, with **no trailing slash** and no path. A trailing slash or the wrong host does not raise an error: the browser origin never matches, and every API call from the demo SPA fails. The bootstrap script builds it correctly; only a hand-run `az deployment` can get this wrong.

## Rotating the read-only key

1. Set a new value for the Key Vault secret `observatory-readonly-api-key` in `fpaiodemo-kv`.
2. Restart the API: `az webapp restart --resource-group fpaiodemo-rg --name fpaiodemo-api`. A restart alone does not make the app pick up a new secret version (see the Key Vault reference note above); also re-save the `OBSERVATORY_READONLY_API_KEY` reference setting through a file, as described there.
3. Update the `DEMO_READONLY_KEY` variable on the `demo` environment.
4. Dispatch `deploy-demo.yml` so the web bundle is rebuilt with the new key.

Until step 4 completes the live demo shows authentication errors.

A bootstrap script re-run rotates the read-only key the same way, and the deployed SPA keeps the old key until `deploy-demo.yml` is dispatched again.

## Tearing down

Key Vault purge protection is on, so deleting the demo resource group leaves a soft-deleted vault, and re-running the script then fails on it. Recover the vault before re-running: `az keyvault recover --name fpaiodemo-kv`.

## Re-run behaviour not yet observed

Observed on the first live runs (2026-10-11, three runs against one server): the reuse path (password reset, enable public access) and `firewall-rule create` on an existing rule exited 0.

Observed on the fourth live run (2026-10-11, the custom-domain re-run, exit 0): `az postgres flexible-server db create` for an existing database and `az role assignment create` for existing Key Vault Secrets Officer and Contributor assignments both completed without throwing. The `db create` warning "Creating database with utf8 charset" still prints on a re-run; it did not fail.

`az postgres flexible-server create --public-access None` leaves `publicNetworkAccess` Disabled (the CLI help says otherwise), and firewall rules then fail with "Firewall rule operations are not supported for a server without public access enabled". The script creates the server with `--public-access None` and then runs `az postgres flexible-server update --public-access Enabled` for a new and a reused server alike. It creates no firewall rule itself: `infra/modules/postgresql.bicep` adds one per App Service outbound IP (`allow-app-N`). Observed on the live demo: with an extra `0.0.0.0` (all Azure services) rule deleted and the API restarted, `demo-reset.yml` returned 200 and `/api/aggregates` returned 200 with only those per-IP rules. Do not add a `0.0.0.0` rule.

- UNVERIFIED: the per-IP rules are sufficient on a freshly bootstrapped server, not only on the one that once had the `0.0.0.0` rule. Refuted if the first `reset-demo` after a clean bootstrap returns a 5xx or times out.

## Notes for the production operator

The App Service settings array order changed in `infra/main.bicep`; the set of settings is identical. When you compare `az deployment group what-if` for the production resource group, compare setting names and values, not the diff shape.

## Cost

The stack is a Free Static Web App, a Burstable `Standard_B1ms` PostgreSQL server with 32 GB, and the App Service plan and Application Insights defined in `infra/`. Check the SKUs against the Azure price calculator before running the script; the database is the paid part, and it bills whether or not anyone visits. The App Service plan is Free (F1) with Always On off, so the API cold-starts after about 20 minutes idle (each start runs the EF migrations), and F1 stops the app when it exceeds 3 CPU-minutes in any 5 minutes or 60 CPU-minutes in a day, until the quota resets.
