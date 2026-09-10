import { render, screen } from '@testing-library/react'
import { beforeEach, expect, test, vi } from 'vitest'
import ReportingPage from './ReportingPage'

const useBilledReporting = vi.hoisted(() => vi.fn())

vi.mock('../api/queries', () => ({
  localDate: () => '2026-08-01',
  useBilledReporting,
}))
vi.mock('../components/BudgetRulesPanel', () => ({ default: () => null }))
vi.mock('../components/BilledSpendChart', () => ({ default: () => null }))
vi.mock('../components/BilledVendorSplit', () => ({ default: () => null }))

beforeEach(() => useBilledReporting.mockReset())

test('shows one honest failure state when authoritative billed reporting cannot load', () => {
  useBilledReporting.mockReturnValue({ report: null, isLoading: false, isError: true })

  render(<ReportingPage />)

  expect(screen.getByRole('alert')).toHaveTextContent('Couldn’t load billed reporting')
})

test('compares the selected reporting period with the previous period', () => {
  useBilledReporting
    .mockReturnValueOnce({
      report: {
        entryCount: 2,
        totalGbp: 80,
        dailyAverageGbp: 8,
        projectedMonthlyGbp: 240,
        topVendorName: 'Anthropic',
        topVendorGbp: 50,
        dailySeries: [],
        vendorSeries: [],
        categorySeries: [],
      },
      isLoading: false,
      isError: false,
    })
    .mockReturnValueOnce({
      report: {
        entryCount: 3,
        totalGbp: 100,
        dailyAverageGbp: 5,
        projectedMonthlyGbp: 150,
        topVendorName: 'OpenAI',
        topVendorGbp: 70,
        dailySeries: [],
        vendorSeries: [],
        categorySeries: [],
      },
      isLoading: false,
      isError: false,
    })

  render(<ReportingPage />)

  expect(screen.getByRole('button', { name: 'Previous period' })).toBeInTheDocument()
  expect(screen.getByText('30-day run rate')).toBeInTheDocument()
  expect(screen.getByText('£20.00 lower vs previous period')).toBeInTheDocument()
  expect(screen.getByText('£3.00 higher vs previous period')).toBeInTheDocument()
  expect(screen.getByText('Previous: OpenAI · £70.00')).toBeInTheDocument()
})
