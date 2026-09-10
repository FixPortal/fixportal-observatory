/// <reference types="vite/client" />

interface ImportMetaEnv {
  /** Absolute API base for production (cross-origin); unset in dev (Vite proxy). */
  readonly VITE_API_BASE?: string
  /** Entra app-registration client id. Unset => sign-in disabled (local dev). */
  readonly VITE_AAD_CLIENT_ID?: string
  /** Entra tenant id (directory) the app signs into. */
  readonly VITE_AAD_TENANT_ID?: string
  /** Full API scope requested for the access token, e.g. api://<clientId>/access_as_user. */
  readonly VITE_AAD_API_SCOPE?: string
  /** Optional footer attribution — who runs this instance ("Built by …"). */
  readonly VITE_ATTRIBUTION_NAME?: string
  /** Link target for the attribution name. */
  readonly VITE_ATTRIBUTION_URL?: string
}

interface ImportMeta {
  readonly env: ImportMetaEnv
}
