import type { ReactNode } from 'react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { act, renderHook } from '@testing-library/react'
import { afterEach, expect, test, vi } from 'vitest'
import { useAuditRun } from './auditQueries'

vi.mock('../auth/msal', () => ({
  isReadonly: false,
  urlApiKey: '',
  apiKey: '',
  getAccessToken: async () => null,
}))
afterEach(() => {
  vi.useRealTimers()
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

test('active run polling is 2 seconds, pauses for hidden documents, and stops on terminal state', async () => {
  vi.useFakeTimers()
  let hidden = false
  vi.spyOn(document, 'hidden', 'get').mockImplementation(() => hidden)
  let state = 'verifying'
  const fetch = vi.fn(async () => Response.json({ state }))
  vi.stubGlobal('fetch', fetch)
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  )
  const view = renderHook(() => useAuditRun('run'), { wrapper })
  await act(async () => {
    await vi.advanceTimersByTimeAsync(1)
  })
  expect(fetch).toHaveBeenCalledTimes(1)
  await act(async () => {
    await vi.advanceTimersByTimeAsync(1998)
  })
  expect(fetch).toHaveBeenCalledTimes(1)
  await act(async () => {
    await vi.advanceTimersByTimeAsync(2)
  })
  expect(fetch).toHaveBeenCalledTimes(2)
  await act(async () => {
    hidden = true
    document.dispatchEvent(new Event('visibilitychange'))
  })
  await act(async () => {
    await vi.advanceTimersByTimeAsync(6000)
  })
  expect(fetch).toHaveBeenCalledTimes(2)
  await act(async () => {
    hidden = false
    document.dispatchEvent(new Event('visibilitychange'))
  })
  await act(async () => {
    state = 'completed'
    await vi.advanceTimersByTimeAsync(2001)
  })
  expect(view.result.current.data?.state).toBe('completed')
  const terminalCalls = fetch.mock.calls.length
  await act(async () => {
    await vi.advanceTimersByTimeAsync(6000)
  })
  expect(fetch).toHaveBeenCalledTimes(terminalCalls)
  view.unmount()
  client.clear()
})
