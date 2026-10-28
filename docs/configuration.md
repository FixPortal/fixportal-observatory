# Configuration reference

How the API and the web app are configured and secured. Provider and acquisition variables
(OpenAI, Anthropic, Google, GitHub, local collectors) are in [provider-setup.md](provider-setup.md);
this page covers access keys, sign-in, the browser build and the startup checks.
Facts come from `src/AiObservatory.Api/Program.cs`, the endpoint filters, `DemoMode.cs`, the ingest
`Program.cs` and `src/AiObservatory.Web/src/auth/msal.ts`.

Settings are read from configuration, so each one can be an environment variable or an
`appsettings` entry. Use `__` for `:` in environment variable names (`RateLimiting__ApiPermitLimit`).

## Access model

The API has three keys and an optional Entra sign-in.

| Setting | Header | What it allows |
|---|---|---|
| `OBSERVATORY_API_KEY` | `X-Observatory-Key` | Admin: every read and every write |
| `OBSERVATORY_READONLY_API_KEY` | `X-Observatory-Key` | Read-only: `GET` requests on the general read surface |
| `OBSERVATORY_IDE_API_KEY` | `X-Observatory-IDE-Key` | The IDE integration endpoints only |

- A signed-in Entra user is treated as admin and needs no key.
- A `GET` accepts the read-only key or the admin key. Any other method needs the admin key.
- Some `GET` routes that expose project and repository names accept only the admin key or an Entra
  user, never the read-only key.
- With a key unset, in the Development environment the request goes through. Anywhere else the
  endpoint answers `503`, so a misconfigured deployment fails closed instead of opening up. If the
  read-only key is unset but the admin key is set, `GET` still works with the admin key.
- Keys are compared in constant time.

### Startup checks outside Development

The API refuses to start unless all of the following hold:

- `OBSERVATORY_API_KEY`, `OBSERVATORY_READONLY_API_KEY` and `OBSERVATORY_IDE_API_KEY` are each at
  least 16 characters, are not `change-me`, and do not start with `@Microsoft.KeyVault(` (an
  unresolved Key Vault reference).
- `OBSERVATORY_IDE_API_KEY` has no leading or trailing whitespace.
- The admin key and the read-only key differ.
- The IDE key differs from both of them.

In every environment, including Development, the API also refuses to start without `DB_CONNECTION`,
or when `RateLimiting:ApiPermitLimit` is set to anything but a positive integer.

The Compose file in this repository defaults `OBSERVATORY_API_KEY` to `change-me` for local use.

## API settings

| Setting | Default | Notes |
|---|---|---|
| `DB_CONNECTION` | none | PostgreSQL connection string. Required. The API applies pending EF Core migrations when it starts. |
| `AzureAd:ClientId` | unset | Setting it switches on Entra JWT bearer sign-in for people. The rest of the `AzureAd` section (tenant, audience and so on) is read by Microsoft.Identity.Web. |
| `SWA_ORIGIN` | `https://fpaiobs-swa.azurestaticapps.net` | The one origin allowed by CORS, with any method and header. Set it to the origin that serves your web app. |
| `RateLimiting:ApiPermitLimit` | `120` | Requests per minute per client IP address. Over the limit the API answers `429`. |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | unset | Setting it switches on Application Insights telemetry. |
| `OBSERVATORY_DEMO_MODE` | unset | `true` runs the read-only demo variant. See below. |

### Demo mode

With `OBSERVATORY_DEMO_MODE=true` the API is read-only for everyone, including the admin key: any
request that is not `GET`, `HEAD` or `OPTIONS` answers `403` "This is a read-only demo instance."
The two paths `/api/dev/seed` and `/api/dev/reset-demo` stay reachable, and both still need the
admin key. The API also refuses to start unless every
host in the database connection is a demo host (name starting `fpaiodemo-`) or one of `localhost`,
`127.0.0.1`, `::1` or `db`, so demo mode cannot be switched on against real data by mistake.

## Web app build settings

The web app is configured at build time through Vite variables.

| Variable | Default | Notes |
|---|---|---|
| `VITE_API_BASE` | empty | Base URL of the API for a production build, which calls it cross-origin. Empty uses same-origin `/api`, which is what the development proxy provides. |
| `VITE_AAD_CLIENT_ID` | empty | Entra application (client) id. Setting it switches on sign-in. |
| `VITE_AAD_TENANT_ID` | empty | Entra tenant id, used to build the sign-in authority. |
| `VITE_AAD_API_SCOPE` | empty | Scope requested for the API access token. |
| `VITE_API_KEY` | empty | Self-host mode: the UI sends this value as `X-Observatory-Key` on every request instead of asking people to sign in. It has no effect when `VITE_AAD_CLIENT_ID` is set. The value is compiled into the browser bundle, so anyone who can load the site can read it: use a key whose access you are content to give them, such as the read-only key for a view-only site. |
| `VITE_DEMO_BANNER` | unset | Shows the synthetic-data banner. |
| `VITE_ATTRIBUTION_NAME`, `VITE_ATTRIBUTION_URL` | unset | Footer attribution. |

A read-only share link can carry the viewer key as `?key=<value>`. The web app keeps it in
`sessionStorage` for that tab, so a reload does not lose it, and it is cleared when the tab closes.

## Upgrading

The API applies pending EF Core migrations when it starts, so an upgrade needs no separate migration
step: deploy the new build and start it. Read [CHANGELOG.md](../CHANGELOG.md) for what changed
first, and back up the database before you start a build that adds migrations, because this page
does not describe a way to undo one.

## Ingest startup failures

The ingest process stops at startup, with a message, when:

- `CLAUDE_CODE_USAGE_ENABLED=true` but `ANTHROPIC_BILLING_KEY` is not set. The message is
  "CLAUDE_CODE_USAGE_ENABLED requires ANTHROPIC_BILLING_KEY with an organization Admin API key."
- Only one of `GOOGLE_CLOUD_PROJECT_ID` and `GOOGLE_BILLING_EXPORT_TABLE` is set. They must be set together.
- `GOOGLE_BILLING_EXPORT_MAX_BYTES_BILLED` is set to something other than a positive whole number of bytes.

`PRICING_RETIRED_SOURCES_ENABLED` is not a failure cause. It decides whether the Kimi, xAI and
OpenRouter pricing catalogs are fetched; see [truth-and-pricing.md](truth-and-pricing.md).

## Where a failure shows up

| Symptom | Likely cause |
|---|---|
| API exits at startup naming a key setting | A startup check above failed. |
| `503` from a keyed endpoint | The key it needs is unset and the environment is not Development. |
| `401` from a keyed endpoint | The header is missing or the value does not match. Check the header name for that key. |
| `429` | More than `RateLimiting:ApiPermitLimit` requests in a minute from one IP address. |
| Browser blocks the API call | `SWA_ORIGIN` does not match the origin serving the web app. |
