// Entra (Azure AD) sign-in wiring. The whole module is a no-op unless
// VITE_AAD_CLIENT_ID is set at build time: local `npm run dev` ships no Entra
// config, so `msalInstance` is null, `authEnabled` is false, and the app talks to
// the (key-free) local API directly. In production the build bakes the client id,
// tenant, and API scope, and every request carries a bearer token.
import { PublicClientApplication, InteractionRequiredAuthError, type Configuration, type RedirectRequest } from '@azure/msal-browser'
import { windowWebViewPort } from '../ide/partnerBridge'

const clientId = import.meta.env.VITE_AAD_CLIENT_ID ?? ''
const tenantId = import.meta.env.VITE_AAD_TENANT_ID ?? ''
const apiScope = import.meta.env.VITE_AAD_API_SCOPE ?? ''

export const authEnabled = clientId.length > 0

/** Scopes requested for the API access token (and at interactive sign-in). */
export const loginRequest: RedirectRequest = { scopes: apiScope ? [apiScope] : [] }
const TOKEN_TIMEOUT_MS = 10_000

export class TokenAcquisitionTimeoutError extends Error {
  constructor() {
    super('Sign-in timed out')
    this.name = 'TokenAcquisitionTimeoutError'
  }
}

const config: Configuration = {
  auth: {
    clientId,
    authority: `https://login.microsoftonline.com/${tenantId}`,
    redirectUri: window.location.origin,
    postLogoutRedirectUri: window.location.origin,
  },
  cache: { cacheLocation: 'localStorage' },
}

export const msalInstance = authEnabled ? new PublicClientApplication(config) : null

/**
 * Acquire an API access token for the signed-in account, refreshing silently.
 * Returns null when auth is disabled (dev) or no account is signed in. On a silent
 * failure (expired refresh token, consent revoked) it kicks off an interactive
 * redirect and returns null for this attempt — the page reloads post-redirect.
 */
/** Start an interactive sign-in (redirect). No-op when auth is disabled. */
export function signIn(): void {
  msalInstance?.loginRedirect(loginRequest).catch(() => { /* redirect aborted */ })
}

// Single-flight guard for the interactive redirect. The dashboard mounts many queries at
// once; without this the first caller starts the redirect and the rest throw
// BrowserAuthError: interaction_in_progress, which would escape raw into a react-query
// queryFn and render as a misleading "outage" banner.
let redirectInFlight: Promise<void> | null = null

export async function getAccessToken(): Promise<string | null> {
  if (!msalInstance) return null
  const instance = msalInstance
  const account = instance.getActiveAccount() ?? instance.getAllAccounts()[0]
  if (!account) return null
  let timeoutId: number | undefined
  try {
    const result = await Promise.race([
      instance.acquireTokenSilent({ ...loginRequest, account }),
      new Promise<never>((_, reject) => {
        timeoutId = window.setTimeout(() => reject(new TokenAcquisitionTimeoutError()), TOKEN_TIMEOUT_MS)
      }),
    ])
    return result.accessToken
  } catch (err) {
    // Only an interaction-required failure (expired/revoked consent) warrants an
    // interactive redirect. A transient/network error is rethrown so react-query can
    // retry — force-redirecting to Microsoft on a blip would be a needless bounce.
    if (!(err instanceof InteractionRequiredAuthError)) {
      throw err
    }
    // Share one redirect across concurrent callers and swallow interaction_in_progress
    // so it never escapes; the page reloads once the redirect completes.
    redirectInFlight ??= instance
      .acquireTokenRedirect({ ...loginRequest, account })
      .catch(() => { /* interaction_in_progress or redirect aborted; page will reload */ })
    await redirectInFlight
    return null
  } finally {
    if (timeoutId !== undefined) window.clearTimeout(timeoutId)
  }
}

// Self-host mode: set VITE_API_KEY to authenticate the UI with the API key instead
// of Entra. AuthGate is a no-op (no sign-in screen); the key is injected as
// X-Observatory-Key on every request. Takes no effect when authEnabled is true.
export const apiKey = import.meta.env.VITE_API_KEY ?? ''

// Viewer-key share link: ?key=<value> in the URL grants read-only access without
// Entra sign-in. Persist to sessionStorage so a reload / tab-restore — which re-evaluates
// this module against the already-stripped URL — still finds the key, instead of dropping
// the viewer to the Entra sign-in screen they can't pass. Scoped to the tab, cleared on close.
const VIEWER_KEY_STORAGE = 'observatory:viewer-key'
// Embedded mode is only honoured when a real WebView2 host channel is present —
// the bare ?embed=fixportal-ide parameter must not be able to discard a viewer key
// on its own, or a tampered share link would strand the recipient at the Entra
// sign-in screen with the key already stripped from the URL.
export const isEmbedded =
  new URLSearchParams(window.location.search).get('embed') === 'fixportal-ide' &&
  windowWebViewPort() !== null

function readViewerKey(): string {
  if (isEmbedded) return ''
  const fromUrl = new URLSearchParams(window.location.search).get('key')
  if (fromUrl) {
    try { window.sessionStorage.setItem(VIEWER_KEY_STORAGE, fromUrl) } catch { /* storage blocked */ }
    return fromUrl
  }
  try { return window.sessionStorage.getItem(VIEWER_KEY_STORAGE) ?? '' } catch { return '' }
}

export const urlApiKey = readViewerKey()

// Strip the key from the visible URL once captured so it does not linger in the
// address bar, browser history, or any Referer header. The value is already held
// in urlApiKey (and sessionStorage) for the session.
if ((urlApiKey || isEmbedded) && typeof window.history?.replaceState === 'function') {
  const stripped = new URL(window.location.href)
  if (stripped.searchParams.has('key')) {
    stripped.searchParams.delete('key')
    window.history.replaceState({}, '', stripped.pathname + stripped.search + stripped.hash)
  }
}

// A viewer-key session is read-only — the API rejects every non-GET without the
// admin key. Used to hide write controls so colleagues on a share link aren't
// shown buttons that would only 401. (A viewer key is the only credential a
// ?key= visitor holds; Entra and self-host keys never set urlApiKey.)
export const isReadonly = urlApiKey.length > 0
