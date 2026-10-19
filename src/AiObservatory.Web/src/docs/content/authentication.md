# Authentication

How callers prove who they are, which routes each credential opens, and how the web app chooses a credential. Setting names and defaults are in [Configuration](/docs/configuration); the routes themselves are in [API](/docs/api).

## Credentials at a glance

| Credential                                         | How it is presented             | Who uses it                                                                  | What it allows                                                       |
| -------------------------------------------------- | ------------------------------- | ---------------------------------------------------------------------------- | -------------------------------------------------------------------- |
| Entra sign-in (JWT bearer)                         | `Authorization: Bearer <token>` | People using the web app in a deployment with Entra enabled                  | Everything under `/api`, read and write, including admin-only reads. |
| Admin API key (`OBSERVATORY_API_KEY`)              | `X-Observatory-Key: <key>`      | Machine callers (sweeper, hooks, scripts), operators, self-hosted web builds | Everything under `/api`, any method.                                 |
| Read-only API key (`OBSERVATORY_READONLY_API_KEY`) | `X-Observatory-Key: <key>`      | Share links, the public demo build                                           | `GET` requests only, and not the admin-only reads listed below.      |
| IDE API key (`OBSERVATORY_IDE_API_KEY`)            | `X-Observatory-IDE-Key: <key>`  | The IDE integration                                                          | Only the routes under `/api/ide/v1`. It does not open `/api`.        |

Keys are compared in constant time. A key of a different length than the expected one is rejected without comparison.

## Which routes need which credential

All routes live under `/api`, except the ingest worker's `/healthz`, which has no authentication and reports only worker liveness, and the OpenAPI document, which is served without authentication in `Development` only.

### The standard rule for `/api`

Every `/api` route runs this check, in this order:

1. If the request carries a valid Entra token, it is allowed. Nothing else is checked.
2. If the method is `GET`:
   - When a read-only key is configured, the request must carry `X-Observatory-Key` equal to the read-only key or the admin key. Otherwise the answer is 401.
   - When no read-only key is configured but an admin key is, the request must carry the admin key. Otherwise 401.
   - When neither key is configured: allowed in the `Development` environment, 503 anywhere else.
3. For every other method (`POST`, `PUT`, `PATCH`, `DELETE`, and also `HEAD`): the request must carry the admin key. If no admin key is configured, it is allowed in `Development` and 503 elsewhere. A wrong or missing key is 401.

A missing key never silently opens data: outside `Development` an unconfigured deployment answers 503, and in practice the app refuses to start without keys (see [Startup checks](#startup-checks)).

### Admin-only reads

These `GET` routes accept an Entra token or the admin key but **not** the read-only key. A request with only the read-only key gets 401.

| Route                                                                                         | Why                                           |
| --------------------------------------------------------------------------------------------- | --------------------------------------------- |
| `GET /api/activity/daily`, `GET /api/activity/by-project`                                     | Project names reveal what you are working on. |
| `GET /api/github/prs`, `/api/github/reviews`, `/api/github/commits/summary`, `/api/github/ci` | Repository names and authors.                 |
| `GET /api/spend/entries`                                                                      | Row-level ledger.                             |

Two write routes, `DELETE /api/activity/sessions/disallowed-projects` and `POST /api/spend/feed-status`, also carry the admin-only check. It accepts an Entra token or the admin key, which is what every other write already requires, so in practice they behave like any other write.

The aggregate-only `GET /api/spend/reporting` is not admin-only, so read-only viewers can see the [Reporting](/docs/reporting) figures but not the ledger rows.

### The IDE route group

`GET /api/ide/v1/routing-snapshot` and `POST /api/ide/v1/events` use only the IDE key. An Entra token or the admin key does not open them. If no IDE key is configured they answer 503 outside `Development`.

### Response codes you will see

| Status | Meaning                                                                                      |
| ------ | -------------------------------------------------------------------------------------------- |
| 401    | The key is missing or wrong, or the route is admin-only and you presented the read-only key. |
| 403    | Only produced in demo mode, for a write request: `This is a read-only demo instance.`        |
| 429    | The per-IP rate limit was exceeded. Default 120 requests per minute.                         |
| 503    | The server has no key configured for this route outside `Development`.                       |

## Entra sign-in

Entra sign-in is enabled in two places, and both must be configured:

- **API:** `AzureAd:ClientId` non-blank. The API then validates bearer tokens with the Microsoft Identity Web library using the `AzureAd` configuration section.
- **Web build:** `VITE_AAD_CLIENT_ID`, `VITE_AAD_TENANT_ID` and `VITE_AAD_API_SCOPE`. A non-empty client id turns on the sign-in screen.

With the API side off, bearer tokens are never inspected and only keys apply.

### What the API checks

The API accepts any request that arrives with a valid, authenticated token for its configured app. It does not check roles, scopes or group membership itself. Who may sign in is therefore decided in Entra, not in Observatory. The bundled setup script, `infra/scripts/setup-entra.ps1`, creates a single-tenant app registration that:

- is both the SPA client and the API it calls;
- registers one SPA redirect URI, taken from the script's `-RedirectUri` parameter; the default is the maintainers' own site, so pass the address your web app is served from;
- exposes one delegated scope, `access_as_user`, and uses v2 access tokens;
- pre-authorises itself for that scope (no consent prompt);
- sets the enterprise application to require user assignment, and assigns the signed-in user, so only assigned users can sign in.

Run it once as an administrator who can create app registrations, then copy the printed client id, tenant id and scope into the web build variables and the API settings. If you do not use the script, set equivalent restrictions yourself: without user assignment, anyone in the tenant can obtain a token that this API accepts.

### What the web app does

- With Entra enabled and no account, the app shows the wordmark, the label "Observatory" and one button, "Sign in with Microsoft". The button redirects to Microsoft and is disabled while sign-in is in progress.
- After sign-in the app keeps the account active and requests an access token silently before each API call. Account data is cached in the browser's `localStorage`.
- If silent acquisition needs the user again (for example consent was revoked or the refresh token expired), the app starts one interactive redirect, shared by all pending requests.
- Token acquisition that takes longer than 10 seconds is reported as a timeout.
- A 401 or 403 from the API, or a token timeout, shows the banner "Your session has expired or you’re not authorised." with a "Sign in again" button when Entra is enabled. Any other failure shows "Couldn’t reach the API — data may be unavailable." with Retry.

## How the web app chooses a credential

For each API request the web app picks the first match:

1. A viewer key from the page URL (`?key=`), sent as `X-Observatory-Key`.
2. An Entra access token, sent as a bearer token.
3. `VITE_API_KEY` baked into the build, sent as `X-Observatory-Key`.
4. Nothing. This works only against a `Development` API with no keys set.

The viewer key deliberately outranks a token. Otherwise someone who had once signed in on the same origin would send a token that is refused and never send the key from the link.

## Read-only web mode

The web app is in read-only mode when either:

- the URL carried a `?key=` viewer key, or
- the build has `VITE_DEMO_BANNER=true`.

In this mode the app:

- hides the **Activity**, **GitHub** and **Spend** tabs, because the API would answer 401 to a read-only key for them;
- hides write controls: the subscription edit and extra-usage controls (the extra-usage figure is shown but not editable), the budget-rule add and remove controls and the alert email and Slack edit and clear controls, the action buttons on each insight (such as "How to implement"), the suggestion actions (such as Dismiss), and the control that removes a whole sweep on the Adversarial Review tab.

This hiding is a convenience. The API enforces the real rule: a read-only key cannot write whatever the browser shows.

### Viewer share links

Append `?key=<read-only-key>` to the app address to give someone read access without Entra.

- The key is kept in `sessionStorage` for that browser tab, so a reload still works, and is removed when the tab closes.
- The key is removed from the address bar immediately after it is captured, so it does not stay in history or in a `Referer` header.
- When Entra is enabled, a link with a viewer key skips the sign-in screen. The visitor still holds only the read-only key.
- Treat the link as a credential: anyone who has it can read every route the read-only key opens. Rotate `OBSERVATORY_READONLY_API_KEY` to revoke it.

### Embedded mode

`?embed=fixportal-ide` is honoured only when the page runs inside a WebView2 host that provides the host message channel. When honoured, any viewer key in the URL is ignored and the normal Entra gate applies. The parameter on its own, in an ordinary browser, does nothing. This stops a tampered link from discarding a viewer key. See [Access](/docs/access).

## Machine callers

Local producers and scripts have no browser session, so they use keys:

- The sweeper and other writers send `X-Observatory-Key` with the **admin** key. Their calls are `POST`s (events, activity sessions, caveman sessions, adversarial-review runs, sweep status), so a read-only key is rejected.
- A caller that only needs to read can use the read-only key, but not for the admin-only reads in the table above.
- The IDE integration uses its own header and key.

Give each machine the minimum it needs and keep the admin key out of shared documents.

## Startup checks

Outside the `Development` environment the API refuses to start unless:

- `OBSERVATORY_API_KEY`, `OBSERVATORY_READONLY_API_KEY` and `OBSERVATORY_IDE_API_KEY` are each set to a value of at least 16 characters, not equal to `change-me`, and not an unresolved Key Vault reference;
- the admin key differs from the read-only key;
- the IDE key differs from both and has no leading or trailing whitespace.

The error message names the offending setting. The API never logs or returns the key values.

## Development mode

When `ASPNETCORE_ENVIRONMENT` is `Development`:

- the key-format checks above are skipped;
- a route with no applicable key configured is allowed instead of answering 503. The Docker Compose stack runs this way with only the admin key set (default `change-me`), so a Compose instance is for local use only and is bound to `127.0.0.1`;
- the OpenAPI document and `POST /api/dev/seed` are available.

Never run a reachable instance in `Development`.

## Demo mode

With `OBSERVATORY_DEMO_MODE=true` every non-`GET`, non-`HEAD`, non-`OPTIONS` request is refused with 403, even with the admin key. Only `POST /api/dev/seed` and `POST /api/dev/reset-demo` pass the block, and both still need the admin key. The demo web build bakes the read-only key, which can only read. See [Deployment](/docs/deployment#demo-instance).

## Related protections

- **Rate limiting:** a fixed window per client IP, applied before the key check, 120 requests per minute by default. It keys on the connecting address, not on the `X-Observatory-Key` header, so rotating a header does not obtain a new allowance.
- **CORS:** one allowed browser origin, `SWA_ORIGIN`. This is not authentication: it only controls which web origins a browser lets call the API.
- **Unresolved Key Vault references** in key settings stop startup outside `Development`, so a broken secret reference never becomes a usable key.

## References

- `src/AiObservatory.Api/ApiKeyEndpointFilter.cs`
- `src/AiObservatory.Api/AdminOnlyApiKeyEndpointFilter.cs`
- `src/AiObservatory.Api/IdeApiKeyEndpointFilter.cs`
- `src/AiObservatory.Api/Program.cs`
- `src/AiObservatory.Api/DemoWriteBlockMiddleware.cs`
- `src/AiObservatory.Web/src/auth/msal.ts`
- `src/AiObservatory.Web/src/auth/AuthGate.tsx`
- `src/AiObservatory.Web/src/api/client.ts`
- `infra/scripts/setup-entra.ps1`
