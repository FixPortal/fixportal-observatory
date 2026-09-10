import { QueryClientProvider } from '@tanstack/react-query'
import Dashboard from './pages/Dashboard'
import AuthGate from './auth/AuthGate'
import EmbeddedContext from './ide/EmbeddedContext'
import { queryClient } from './api/queryClient'

// Module-scoped so a StrictMode double-invoke or remount never discards the cache.
export default function App() {
  return (
    <QueryClientProvider client={queryClient}>
      <AuthGate>
        <EmbeddedContext>
          <Dashboard />
        </EmbeddedContext>
      </AuthGate>
    </QueryClientProvider>
  )
}
