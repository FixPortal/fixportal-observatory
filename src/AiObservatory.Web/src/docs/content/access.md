# Access

This page covers how you get into the dashboard and what you can see once you are in: Entra sign-in, API-key access, the read-only mode, the theme control, how the tabs and address bar behave, and the embedded mode. The server-side rules for keys and roles are in [Authentication](/docs/authentication).

## How the web app authenticates

The web app picks one credential for every API request. The order is fixed:

| Priority | Credential                           | When it applies                                                                   |
| -------- | ------------------------------------ | --------------------------------------------------------------------------------- |
| 1        | Viewer key from a `?key=` share link | The tab was opened with `?key=<value>`, or the key was stored earlier in this tab |
| 2        | Entra access token                   | The build has Entra configured and a user is signed in                            |
| 3        | `VITE_API_KEY` baked into the build  | The instance is self-hosted with a build-time key                                 |
| 4        | Nothing                              | Local development with no key configured on the API                               |

The viewer key outranks the token on purpose. A visitor on a share link who also holds a Microsoft account for the same origin would otherwise send a token the API refuses, and the link would fail.

Keys travel in the `X-Observatory-Key` header. Tokens travel as a `Bearer` token.

## Signing in with Entra

Entra sign-in is on only when the build was made with `VITE_AAD_CLIENT_ID`. Without it the app never shows a sign-in screen.

When it is on and nobody is signed in, the app shows the Observatory logo and one button: `Sign in with Microsoft`. While a sign-in is in progress the button is disabled and reads `Signing in…`. Sign-in is a full-page redirect to Microsoft and back.

After sign-in:

- The app acquires an access token for the API scope (`VITE_AAD_API_SCOPE`) and renews it silently.
- If silent renewal needs the user, because consent was revoked or the session expired, the app redirects to Microsoft again.
- If token acquisition takes longer than 10 seconds, the request fails with a timeout, and the banner described below appears.
- A network error during renewal does not force a redirect. The request is retried instead.

The API treats any user with a valid token as fully authorised: read and write. It does not check roles for these routes. Control who may obtain a token in Entra.

The build variables are listed in [Configuration](/docs/configuration).

### The session expired banner

If one of the three requests listed under the connection banner below returns 401 or 403, or the token times out, a banner appears above the tab content on every tab:

`Your session has expired or you’re not authorised.`

When Entra is enabled the banner has a `Sign in again` button. When it is not, the banner has no button, because there is nothing to sign in to. The banner treats 401 and 403 alike. For the key rules behind them, see [Authentication](/docs/authentication).

### The connection banner

If the API cannot be reached, the banner reads `Couldn’t reach the API — data may be unavailable.` While the app is retrying automatically it also shows `Retrying automatically.`, and a `Retry` button that reads `Retrying…` while it works. Requests are retried up to three times before they are treated as failed. A token timeout is never retried automatically.

The banner watches three queries: the 31-day aggregates, the insights and the subscriptions. A failure in any of them shows the banner, whichever tab you are on. A failure in the billed-spend, source-status or suggestions queries does not; those panels report their own errors.

While those first requests are in flight, a loading banner reads `Loading data — the API may take a moment to wake up on first load…`.

## API-key access

There are two web-side key modes.

### Viewer share link

A link of the form `https://<host>/?key=<read-only key>` opens the dashboard without Entra sign-in.

- The key is read from the URL, stored in the tab's `sessionStorage` under `observatory:viewer-key`, and removed from the address bar immediately, so it does not remain in the visible URL, the history or a `Referer` header.
- The key lasts for the life of the tab. A reload or a restored tab still finds it.
- The visitor is in [read-only mode](/docs/access#read-only-mode).
- The key you share must be the **read-only** key. The admin key in a link would give the visitor write access to the API, even though the interface hides the write controls.

### Self-hosted build key

When a build sets `VITE_API_KEY`, the app sends that key on every request and shows no sign-in screen. It takes effect only if Entra is not enabled.

The key is compiled into the JavaScript bundle. Anyone who can load the app can read it. Use this mode only on a network you control, and treat the key as shared with everyone who can reach the page. The Docker Compose stack does not do this: its nginx proxy adds the admin key at runtime, so the key is not in the bundle.

A build whose `VITE_API_KEY` is the read-only key does not turn the interface read-only by itself. The write controls would show, and the API would refuse their requests. Read-only mode is driven by the share link or by the demo build flag.

## Read-only mode

The interface is read-only when either of these is true:

- the tab holds a viewer key from a share link, or
- the build was made with `VITE_DEMO_BANNER=true`, as the public demo is.

In read-only mode:

| Where              | What changes                                                                                                                            |
| ------------------ | --------------------------------------------------------------------------------------------------------------------------------------- |
| Tab bar            | The `Activity`, `GitHub` and `Spend` tabs are hidden. The server only returns that data to the admin key or a signed-in user.           |
| Subscriptions      | The `Manage subscriptions` and `Add subscription` buttons are hidden, and the extra-usage chip shows its value without an edit control. |
| Insights           | The `How to implement` and `Dismiss` buttons are hidden.                                                                                |
| Suggestions        | The `Dismiss`, `Snooze 7 days` and `Snooze 30 days` buttons are hidden.                                                                 |
| Adversarial Review | The `Remove sweep` button is hidden.                                                                                                    |
| Reporting          | The edit and remove controls for budget rules and for the configured alert channels are hidden.                                         |

The Overview, `Adversarial Review`, `Reporting` and `Docs` tabs remain. The API itself enforces the rule: the read-only key can make `GET` requests to most routes, and every other method needs the admin key (or a signed-in user). The routes behind the Activity and GitHub tabs and the ledger entries list refuse the read-only key even for `GET`. Hiding the controls only saves a viewer from buttons that would be refused.

A demo build also shows a banner above the header: `Demo instance: every figure here is synthetic and the dashboard is read-only.` with a link to the project on GitHub.

## Theme

The header has a three-option control labelled `Theme`: `Light`, `Dark` and `System`.

- `System` follows your operating system setting and changes with it while the page is open. It is the default.
- Your choice is stored in the browser's local storage under `aiobs-theme`. If storage is blocked, the control still works for the session but is not remembered.
- With the keyboard, the arrow keys move between the options and select the one they land on.

## Tabs and the address bar

- The tab bar is a keyboard-navigable list: the arrow keys move focus and select, wrapping at the ends, and only the selected tab is in the tab order.
- The selected tab is not stored in the URL, with one exception: the Docs tab. Opening it moves the address to `/docs`, and each chapter lives at `/docs/<chapter>`, with section anchors such as `/docs/spend#ledger`. Leaving the Docs tab returns the address to `/`.
- You can open a docs address directly, bookmark it or share it. Back and forward keep the selected tab in step with the address.
- A reload outside `/docs` returns to the Overview.
- Panels that can collapse (`Caveman` and `Data sources` on the Overview) remember whether they are open in local storage, one key per panel. They start collapsed.

## Embedded mode

Observatory can be embedded in a desktop host that provides a WebView2 channel. Embedded mode is on only when both are true:

- the address contains `?embed=fixportal-ide`, and
- the page can see a real WebView2 host channel.

The parameter alone does nothing. This stops a tampered link from switching the app into embedded mode.

When embedded:

1. The app tells the host it is ready, before any sign-in, so the host can tell "waiting for sign-in" from "broken".
2. A status line appears above the dashboard. It reads `Following mission <id>` when the host sends a mission, `Following repository <name>` when it sends a repository, and `Global Observatory` otherwise. The host updates it as its context changes.
3. A `?key=` viewer key is ignored. An embedded dashboard uses Entra or the build key.

The status line is a label. It does not filter any figure on the dashboard.

## References

- `src/AiObservatory.Web/src/auth/msal.ts` for the credential order, token timeout, viewer key storage and the read-only rule.
- `src/AiObservatory.Web/src/api/client.ts` for the header selection.
- `src/AiObservatory.Web/src/pages/Dashboard.tsx` for the tabs, banners and docs routing.
- `src/AiObservatory.Web/src/ide/EmbeddedContext.tsx` and `src/AiObservatory.Web/src/ide/partnerBridge.ts` for embedded mode.
- `src/AiObservatory.Web/src/theme/ThemeProvider.tsx` for the theme storage.
- `src/AiObservatory.Api/ApiKeyEndpointFilter.cs` and `src/AiObservatory.Api/AdminOnlyApiKeyEndpointFilter.cs` for what each credential may call.
