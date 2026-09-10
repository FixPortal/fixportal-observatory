import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, expect, test, vi } from 'vitest'
import type { SpendEntry } from '../api/client'
import SpendPage from './SpendPage'

const reporting = vi.hoisted(() => {
  const liveCategory = { id: 'cloud-id', key: 'cloud', displayName: 'Cloud', colorVar: '', sortOrder: 1, archivedAt: null }
  const liveVendor = { id: 'azure-id', key: 'azure', displayName: 'Azure', provider: 'azure', defaultCategoryId: 'cloud-id', archivedAt: null }
  return {
    entryCount: 1,
    entries: [] as SpendEntry[],
    liveCategories: [liveCategory],
    liveCategory,
    liveVendor,
    useBilledReporting: vi.fn((_from: Date, _to: Date, vendorId?: string, _categoryId?: string) => ({
      report: {
        entryCount: reporting.entryCount,
        totalGbp: vendorId === 'azure-id' ? 50 : 100,
        dailyAverageGbp: 0,
        projectedMonthlyGbp: 0,
        topVendorName: null,
        topVendorGbp: null,
        dailySeries: [],
        vendorSeries: [],
        categorySeries: [{ categoryId: 'cloud-id', name: 'Cloud', amountGbp: vendorId === 'azure-id' ? 50 : 100 }],
      },
      isLoading: false,
      isError: false,
    })),
    deleteSpendEntry: vi.fn(),
  }
})

vi.mock('../api/client', async importOriginal => ({
  ...await importOriginal<typeof import('../api/client')>(),
  deleteSpendEntry: reporting.deleteSpendEntry,
}))

vi.mock('../api/queries', async importOriginal => ({
  ...await importOriginal<typeof import('../api/queries')>(),
  useSpendCategories: () => ({ categories: reporting.liveCategories, isError: false, isLoading: false }),
  useSpendVendors: () => ({ vendors: [reporting.liveVendor], isError: false, isLoading: false }),
  useAllSpendCategories: () => ({ categories: [reporting.liveCategory], isError: false, isLoading: false }),
  useAllSpendVendors: () => ({ vendors: [reporting.liveVendor], isError: false, isLoading: false }),
  useSpendEntries: () => ({ entries: reporting.entries, isLoading: false, isError: false }),
  useBilledReporting: reporting.useBilledReporting,
}))
vi.mock('../auth/msal', () => ({ isReadonly: false }))

afterEach(() => {
  vi.useRealTimers()
  vi.clearAllMocks()
  reporting.entryCount = 1
  reporting.entries = []
  reporting.liveCategories = [reporting.liveCategory]
})

test('shows the same rolling 31-day window as Overview', () => {
  vi.useFakeTimers()
  vi.setSystemTime(new Date('2026-08-28T12:00:00'))
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })

  render(
    <QueryClientProvider client={queryClient}>
      <SpendPage />
    </QueryClientProvider>,
  )

  expect(screen.getByText('29 Jul 2026 – 28 Aug 2026')).toBeInTheDocument()
  expect(screen.getByText('28 Jun 2026 – 28 Jul 2026')).toBeInTheDocument()
})

test('offers calendar periods and updates the default comparison period', () => {
  vi.useFakeTimers()
  vi.setSystemTime(new Date('2026-08-28T12:00:00'))
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })

  render(
    <QueryClientProvider client={queryClient}>
      <SpendPage />
    </QueryClientProvider>,
  )

  expect(screen.getByRole('button', { name: 'This month' })).toBeInTheDocument()
  expect(screen.getByRole('button', { name: 'Last month' })).toBeInTheDocument()
  expect(screen.getByRole('button', { name: 'This quarter' })).toBeInTheDocument()

  fireEvent.click(screen.getByRole('button', { name: 'This month' }))

  expect(screen.getByText('01 Aug 2026 – 28 Aug 2026')).toBeInTheDocument()
  expect(screen.getByText('01 Jul 2026 – 31 Jul 2026')).toBeInTheDocument()
})

test('allows arbitrary selected and comparison dates', () => {
  vi.useFakeTimers()
  vi.setSystemTime(new Date('2026-08-28T12:00:00'))
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })

  render(
    <QueryClientProvider client={queryClient}>
      <SpendPage />
    </QueryClientProvider>,
  )

  fireEvent.change(screen.getByLabelText('Selected from'), { target: { value: '2026-04-01' } })
  fireEvent.change(screen.getByLabelText('Selected to'), { target: { value: '2026-06-30' } })
  fireEvent.change(screen.getByLabelText('Compare from'), { target: { value: '2026-01-01' } })
  fireEvent.change(screen.getByLabelText('Compare to'), { target: { value: '2026-03-31' } })

  expect(screen.getByText('01 Apr 2026 – 30 Jun 2026')).toBeInTheDocument()
  expect(screen.getByText('01 Jan 2026 – 31 Mar 2026')).toBeInTheDocument()
  expect(screen.getByText('vs comparison period')).toBeInTheDocument()
})

test('applies the vendor filter to the authoritative totals', () => {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  render(
    <QueryClientProvider client={queryClient}>
      <SpendPage />
    </QueryClientProvider>,
  )

  expect(screen.getByText('£100.00')).toBeInTheDocument()
  fireEvent.change(screen.getByLabelText('Vendor'), { target: { value: 'azure-id' } })
  expect(screen.getByText('£50.00')).toBeInTheDocument()
})

test('clears a filter whose category leaves the live catalog (e.g. archived)', async () => {
  // The select is controlled on the filter state but its options come from the live
  // list — without clearing, the UI would show "All categories" while every query
  // stayed filtered by a retired id with no way to reset short of a reload.
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const view = render(
    <QueryClientProvider client={queryClient}>
      <SpendPage />
    </QueryClientProvider>,
  )

  fireEvent.change(screen.getByLabelText('Category'), { target: { value: 'cloud-id' } })
  expect(screen.getByLabelText('Category')).toHaveValue('cloud-id')

  reporting.liveCategories = []
  view.rerender(
    <QueryClientProvider client={queryClient}>
      <SpendPage />
    </QueryClientProvider>,
  )

  await waitFor(() => expect(screen.getByLabelText('Category')).toHaveValue(''))
  const lastCall = reporting.useBilledReporting.mock.calls.at(-1)!
  expect(lastCall[3]).toBeUndefined()
})

test('shows a billed-spend comparison chart', () => {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  render(
    <QueryClientProvider client={queryClient}>
      <SpendPage />
    </QueryClientProvider>,
  )

  expect(screen.getByText('Billed spend over time')).toBeInTheDocument()
})

test('says so when the ledger is truncated below the reported entry count', () => {
  // The entries endpoint caps at the newest 5000 rows while the totals come from the
  // uncapped reporting endpoint — the page must not let the two silently disagree.
  reporting.entryCount = 5003
  reporting.entries = [{
    id: 'e1', occurredOn: '2026-08-20', vendorId: 'azure-id', categoryId: 'cloud-id',
    amount: 10, currency: 'GBP', amountGbp: 10, fxRate: 1, description: null,
    source: 'manual', entryKey: null, recordedAt: '2026-08-20T12:00:00Z',
  }]
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  render(
    <QueryClientProvider client={queryClient}>
      <SpendPage />
    </QueryClientProvider>,
  )

  expect(screen.getByText(/Showing the newest 1 of 5,003 entries/)).toBeInTheDocument()
})

test('shows a delete failure inline instead of a blocking alert', async () => {
  reporting.entries = [{
    id: 'e1', occurredOn: '2026-08-20', vendorId: 'azure-id', categoryId: 'cloud-id',
    amount: 10, currency: 'GBP', amountGbp: 10, fxRate: 1, description: null,
    source: 'manual', entryKey: null, recordedAt: '2026-08-20T12:00:00Z',
  }]
  reporting.deleteSpendEntry.mockRejectedValue(new Error('gone'))
  const alertSpy = vi.spyOn(window, 'alert').mockImplementation(() => {})
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  render(
    <QueryClientProvider client={queryClient}>
      <SpendPage />
    </QueryClientProvider>,
  )

  fireEvent.click(screen.getByRole('button', { name: 'Delete entry from 2026-08-20' }))
  fireEvent.click(screen.getByRole('button', { name: 'Confirm delete entry from 2026-08-20' }))

  await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('Failed to delete entry: gone'))
  expect(alertSpy).not.toHaveBeenCalled()
})
