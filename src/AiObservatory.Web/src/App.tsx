import { QueryClientProvider } from '@tanstack/react-query'
import Dashboard from './pages/Dashboard'
import AuthGate from './auth/AuthGate'
import EmbeddedContext from './ide/EmbeddedContext'
import { queryClient } from './api/queryClient'

// Module-scoped so a StrictMode double-invoke or remount never discards the cache.
export default function App() {
  return (
    <QueryClientProvider client={queryClient}>
      {/* EmbeddedContext sits ABOVE AuthGate deliberately: it posts partner.ready to the
          IDE host. Nested inside the gate, an embed that has not signed in never posts,
          so the IDE cannot tell "waiting for sign-in" from "partner broken" and sits on
          Connecting until it times out. */}
      <EmbeddedContext>
        <AuthGate>
          <Dashboard />
        </AuthGate>
      </EmbeddedContext>
    </QueryClientProvider>
  )
}
