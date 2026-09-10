import { QueryClient } from '@tanstack/react-query'
import { TokenAcquisitionTimeoutError } from '../auth/msal'

export const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      retry: (failureCount, error) => !(error instanceof TokenAcquisitionTimeoutError) && failureCount < 3,
    },
  },
})
