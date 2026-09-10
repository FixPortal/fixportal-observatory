import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { renderHook, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, test, vi } from 'vitest'
import type { ReactNode } from 'react'

const client = vi.hoisted(() => ({
  getAggregates: vi.fn().mockResolvedValue([]), getInsights: vi.fn().mockResolvedValue([]),
  getSubscriptions: vi.fn().mockResolvedValue([]), getSourceStatuses: vi.fn().mockResolvedValue([]),
  getSpendEntries: vi.fn().mockResolvedValue([]),
  getActivityDaily: vi.fn().mockResolvedValue([]),
  getBilledReporting: vi.fn().mockResolvedValue({ entryCount: 0, dailySeries: [], vendorSeries: [] }),
}))

vi.mock('./client', async importOriginal => ({ ...await importOriginal<typeof import('./client')>(), ...client }))

import {
  dashboardDateRange, invalidateSpendData, localDate, useActivityDaily, useAggregates,
  useBilledReporting, useDashboardStatus, useSpendEntries,
} from './queries'

const wrapper = ({ children }: { children: ReactNode }) => (
  <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>{children}</QueryClientProvider>
)

beforeEach(() => {
  client.getAggregates.mockResolvedValue([])
  client.getInsights.mockResolvedValue([])
  client.getSubscriptions.mockResolvedValue([])
  client.getSourceStatuses.mockResolvedValue([])
  client.getSpendEntries.mockResolvedValue([])
  client.getBilledReporting.mockResolvedValue({ entryCount: 0, dailySeries: [], vendorSeries: [] })
})

afterEach(() => {
  vi.useRealTimers()
  vi.unstubAllEnvs()
  vi.clearAllMocks()
})

describe('dashboard queries', () => {
  test('leaves source status errors out of the dashboard error state', async () => {
    // W-02: source-status is an optional panel concern (SourceStatusPanel renders
    // nothing on error); it must not light the page-level banner.
    client.getSourceStatuses.mockRejectedValueOnce(new Error('source status unavailable'))
    const { result } = renderHook(() => useDashboardStatus(), { wrapper })
    await waitFor(() => expect(result.current.isLoading).toBe(false))
    expect(result.current.isError).toBe(false)
    expect(client.getSourceStatuses).not.toHaveBeenCalled()
  })

  test('includes aggregate errors in the dashboard error state', async () => {
    const aggregatesError = new Error('aggregates unavailable')
    client.getAggregates.mockRejectedValueOnce(aggregatesError)
    const { result } = renderHook(() => useDashboardStatus(), { wrapper })
    await waitFor(() => expect(result.current.isError).toBe(true))
    expect(result.current.error).toBe(aggregatesError)
  })

  test('uses the shared aggregate rolling range unchanged for billed reporting', async () => {
    vi.useFakeTimers()
    vi.setSystemTime(new Date('2026-08-31T12:00:00'))
    const range = dashboardDateRange()
    renderHook(() => {
      useAggregates()
      useBilledReporting(range.from, range.to)
    }, { wrapper })
    await vi.runAllTimersAsync()
    expect(client.getAggregates).toHaveBeenCalledWith(range.from.toISOString().slice(0, 10), range.to.toISOString().slice(0, 10))
    expect(client.getBilledReporting).toHaveBeenCalledWith(range.from.toISOString().slice(0, 10), range.to.toISOString().slice(0, 10))
  })

  test('keeps all 31 local calendar dates across the BST spring transition', () => {
    vi.stubEnv('TZ', 'Europe/London')
    const range = dashboardDateRange(new Date('2026-03-30T00:30:00+01:00'))
    expect(localDate(range.from)).toBe('2026-02-28')
    expect(localDate(range.to)).toBe('2026-03-30')
  })

  test('shares one dated aggregates cache entry across dated and no-arg callers', async () => {
    const range = dashboardDateRange()
    const { result } = renderHook(() => {
      const status = useDashboardStatus()
      useAggregates(range.from, range.to)
      useAggregates() // no-arg: must land on the same dated key, not a bare ['aggregates']
      useBilledReporting(range.from, range.to)
      return status
    }, { wrapper })

    await waitFor(() => expect(result.current.isLoading).toBe(false))
    expect(client.getAggregates).toHaveBeenCalledOnce()
    expect(client.getBilledReporting).toHaveBeenCalledOnce()
    expect(client.getAggregates).toHaveBeenCalledWith(localDate(range.from), localDate(range.to))
    expect(client.getBilledReporting).toHaveBeenCalledWith(localDate(range.from), localDate(range.to))
  })

  test('leaves billed reporting errors out of dashboard status', async () => {
    // W-02: a failing /spend/reporting (e.g. a 403 a healthy session can't fix by
    // re-authenticating) is gated by SpendPage/ReportingPage, not the global banner.
    client.getBilledReporting.mockRejectedValueOnce(new Error('ledger unavailable'))
    const { result } = renderHook(() => useDashboardStatus(), { wrapper })
    await waitFor(() => expect(result.current.isLoading).toBe(false))
    expect(result.current.isError).toBe(false)
    expect(client.getBilledReporting).not.toHaveBeenCalled()
  })

  test('passes spend vendor and category filters to billed reporting', async () => {
    const from = new Date('2026-08-01')
    const to = new Date('2026-08-31')

    renderHook(
      () => (useBilledReporting as unknown as (
        from: Date, to: Date, vendorId?: string, categoryId?: string,
      ) => unknown)(from, to, 'azure-id', 'cloud-id'),
      { wrapper },
    )

    await waitFor(() => expect(client.getBilledReporting).toHaveBeenCalled())
    expect(client.getBilledReporting).toHaveBeenCalledWith('2026-08-01', '2026-08-31', 'azure-id', 'cloud-id')
  })

  test('passes spend vendor and category filters to the capped ledger query', async () => {
    renderHook(
      () => useSpendEntries(new Date('2026-08-01'), new Date('2026-08-31'), 'azure-id', 'cloud-id'),
      { wrapper },
    )

    await waitFor(() => expect(client.getSpendEntries).toHaveBeenCalled())
    expect(client.getSpendEntries).toHaveBeenCalledWith('2026-08-01', '2026-08-31', 'azure-id', 'cloud-id')
  })

  test('does not fetch the default activity window when the query is disabled', async () => {
    // W-10: a missing range means "fetch the default window", not "skip" — a chart
    // given no comparison range must opt out explicitly, not fire a spare request.
    const { result } = renderHook(() => useActivityDaily(undefined, undefined, false), { wrapper })
    await waitFor(() => expect(result.current.isLoading).toBe(false))
    expect(client.getActivityDaily).not.toHaveBeenCalled()
  })

  test('invalidates both ledger and authoritative reporting after a write', async () => {
    const queryClient = new QueryClient()
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries').mockResolvedValue()

    await invalidateSpendData(queryClient)

    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['spend-entries'] })
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['billed-reporting'] })
  })
})
