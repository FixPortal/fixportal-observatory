import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { MsalProvider } from '@azure/msal-react'
import './index.css'
import App from './App.tsx'
import { ThemeProvider } from './theme/ThemeProvider'
import { msalInstance } from './auth/msal'

const tree = (
  <StrictMode>
    <ThemeProvider>
      <App />
    </ThemeProvider>
  </StrictMode>
)

async function bootstrap() {
  const root = createRoot(document.getElementById('root')!)
  if (!msalInstance) {
    root.render(tree)
    return
  }

  // MSAL v3 requires initialize() before any other call, and the redirect
  // response must be drained before render so the account is in the cache.
  try {
    await msalInstance.initialize()
    const result = await msalInstance.handleRedirectPromise()
    if (result?.account) msalInstance.setActiveAccount(result.account)
  } catch (err) {
    // handleRedirectPromise rejects on routine paths — e.g. AADSTS...access_denied
    // when the user cancels or declines consent. Never leave a blank page: fall
    // through and still mount so AuthGate can show the sign-in screen and retry.
    console.error('MSAL redirect handling failed', err)
  }
  root.render(<MsalProvider instance={msalInstance}>{tree}</MsalProvider>)
}

void bootstrap()
