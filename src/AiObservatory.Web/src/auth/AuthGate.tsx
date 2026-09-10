import { useEffect, type ReactNode } from 'react'
import { useIsAuthenticated, useMsal } from '@azure/msal-react'
import { BrandWordmark } from '../design/BrandWordmark'
import { authEnabled, isEmbedded, loginRequest, urlApiKey } from './msal'

/**
 * Gates the app behind Entra sign-in. When auth is disabled (no client id baked,
 * i.e. local dev) it renders children untouched. Otherwise it shows a sign-in
 * screen until an account is present, then keeps that account active so silent
 * token acquisition can find it.
 */
export default function AuthGate({ children }: { children: ReactNode }) {
  // Viewer share link (?key=...) bypasses Entra sign-in — read-only access for
  // colleagues without an Entra account. The key auths the API directly.
  if (!authEnabled || (urlApiKey && !isEmbedded)) return <>{children}</>
  return <EntraGate>{children}</EntraGate>
}

function EntraGate({ children }: { children: ReactNode }) {
  const isAuthenticated = useIsAuthenticated()
  const { instance, accounts, inProgress } = useMsal()

  useEffect(() => {
    if (accounts.length > 0 && !instance.getActiveAccount()) {
      instance.setActiveAccount(accounts[0])
    }
  }, [accounts, instance])

  if (isAuthenticated) return <>{children}</>

  const signingIn = inProgress !== 'none'
  return (
    <main className="auth-gate">
      <BrandWordmark height={48} className="auth-gate__wordmark" />
      <span className="auth-gate__descriptor">Observatory</span>
      <button
        type="button"
        className="auth-gate__button"
        disabled={signingIn}
        onClick={() => { instance.loginRedirect(loginRequest).catch(() => { /* redirect aborted */ }) }}
      >
        {signingIn ? 'Signing in…' : 'Sign in with Microsoft'}
      </button>
    </main>
  )
}
