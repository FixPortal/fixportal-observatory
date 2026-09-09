import { fireEvent, render, screen } from '@testing-library/react'
import { beforeEach, describe, expect, test, vi } from 'vitest'
import type { BilledReporting, DailyAggregate, Insight } from '../api/client'
import SummaryCards from './SummaryCards'

const data = vi.hoisted(() => ({
  aggregates: [] as DailyAggregate[],
  aggregatesLoading: false,
  insights: [] as Insight[],
  insightsLoading: false,
  billedReporting: null as BilledReporting | null,
  billedLoading: false,
  billedError: false,
}))

vi.mock('../api/queries', () => ({
  AGGREGATES_DAYS_RANGE: 31,
  useAggregates: () => ({ aggregates: data.aggregates, isError: false, isLoading: data.aggregatesLoading }),
  useInsights: () => ({ insights: data.insights, isError: false, isLoading: data.insightsLoading }),
  useBilledReporting: () => ({ report: data.billedReporting ?? undefined, isLoading: data.billedLoading, isError: data.billedError }),
  dashboardDateRange: () => ({ from: new Date('2026-08-01T12:00:00'), to: new Date('2026-08-31T12:00:00') }),
}))

vi.mock('../lib/currency', () => ({ useUsdToGbp: () => 0.8, formatGbp: (usd: number, rate: number) => `£${(usd * rate).toFixed(2)}`, gbp: (amount: number) => `£${amount.toFixed(2)}` }))

const aggregate = (overrides: Partial<DailyAggregate>): DailyAggregate => ({
  date: '2026-08-24', provider: 'openai', model: 'gpt-5', sourceId: 'openai-usage-api',
  sourceKind: 'providerApi', usageScope: 'api', costBasis: 'none', inputTokens: 0,
  outputTokens: 0, cacheReadTokens: 0, cacheWriteTokens: 0, cacheWrite1hTokens: 0,
  costUsd: 0, unknownCostCount: 0, cacheSavingsUsd: 0, unknownCacheSavingsCount: 0,
  requestCount: 0, ...overrides,
})

beforeEach(() => {
  data.aggregates = [
    aggregate({ costBasis: 'listPriceEstimate', costUsd: 2, inputTokens: 100, cacheReadTokens: 50, requestCount: 1, unknownCacheSavingsCount: 1 }),
    aggregate({ costBasis: 'providerEstimated', costUsd: 3, requestCount: 1 }),
    aggregate({ costBasis: 'notional', costUsd: 4, requestCount: 1 }),
  ]
  data.billedReporting = {
    entryCount: 5003,
    totalGbp: 8,
    dailyAverageGbp: 8 / 31,
    projectedMonthlyGbp: 8 / 31 * 30,
    topVendorName: 'Anthropic',
    topVendorGbp: 8,
    dailySeries: [],
    vendorSeries: [],
    categorySeries: [],
  }
  data.insights = []
  data.aggregatesLoading = false
  data.insightsLoading = false
  data.billedLoading = false
  data.billedError = false
})

describe('SummaryCards loading state', () => {
  test('does not show "Not reported" or zero-valued cards while aggregates/insights are still loading', () => {
    data.aggregates = []
    data.aggregatesLoading = true
    data.insights = []
    data.insightsLoading = true
    render(<SummaryCards />)

    expect(screen.queryByText('Not reported')).not.toBeInTheDocument()
    expect(screen.getByText('Tokens').closest('.fpds-card')).toHaveTextContent('…')
    expect(screen.getByText('New insights').closest('.fpds-card')).toHaveTextContent('…')
  })

  test('does not assert "Not reported" on the Billed spend card while reporting is loading', () => {
    data.billedReporting = null
    data.billedLoading = true
    render(<SummaryCards />)

    const card = screen.getByText('Billed spend · 31 days').closest('.fpds-card')
    expect(card).toHaveTextContent('…')
    expect(card).not.toHaveTextContent('Not reported')
  })

  test('shows an unavailable state on the Billed spend card when reporting fails, not "Not reported"', () => {
    // A fetch failure must not render as a factual claim of data absence on the
    // lead financial card — nothing else on Overview signals the outage.
    data.billedReporting = null
    data.billedError = true
    render(<SummaryCards />)

    const card = screen.getByText('Billed spend · 31 days').closest('.fpds-card')
    expect(card).toHaveTextContent('Unavailable')
    expect(card).toHaveTextContent('Couldn’t load billed spend')
    expect(card).not.toHaveTextContent('Not reported')
  })
})

describe('SummaryCards', () => {
  test('renders six separated truth-basis cards with billed spend as the lead', () => {
    const { container } = render(<SummaryCards />)

    expect(screen.getByText('Billed spend · 31 days')).toBeInTheDocument()
    expect(screen.getByText('List-price estimate')).toBeInTheDocument()
    expect(screen.getByText('Provider estimate')).toBeInTheDocument()
    expect(screen.getByText('Subscription notional')).toBeInTheDocument()
    expect(screen.getByText('Tokens')).toBeInTheDocument()
    expect(screen.getByText('New insights')).toBeInTheDocument()
    expect(screen.getAllByText('USD basis; shown in GBP when reported')).toHaveLength(3)
    expect(container.querySelector('.card-value--lead')?.textContent).toBe('£8.00')
    expect(screen.getByText('£2.40')).toBeInTheDocument()
    expect(screen.getByText('£3.20')).toBeInTheDocument()
  })

  test('uses Not reported for absent money and unknown server savings', () => {
    data.billedReporting = { ...data.billedReporting!, entryCount: 0, totalGbp: 0 }
    render(<SummaryCards />)

    expect(screen.getAllByText('Not reported').length).toBeGreaterThanOrEqual(1)
    expect(screen.getByText(/Cache savings: Not reported/)).toBeInTheDocument()
    expect(screen.getByText('1 savings observation not reported')).toBeInTheDocument()
  })

  test('counts cached input in token totals and input display', () => {
    data.aggregates = [aggregate({
      inputTokens: 1_000_000,
      outputTokens: 2_000_000,
      cacheReadTokens: 3_000_000,
      cacheWriteTokens: 4_000_000,
    })]

    render(<SummaryCards />)

    expect(screen.getByText('10.0M')).toBeInTheDocument()
    expect(screen.getByText('8,000,000 in / 2,000,000 out')).toBeInTheDocument()
    expect(screen.getByText('38% cache hit')).toBeInTheDocument()
  })

  test('does not render the former blended spend, savings claim, or money comparisons', () => {
    render(<SummaryCards />)

    expect(screen.queryByText(/^Spend ·/)).not.toBeInTheDocument()
    expect(screen.queryByText(/saved £/i)).not.toBeInTheDocument()
    expect(screen.queryByText(/vs prior/i)).not.toBeInTheDocument()
    expect(screen.queryByText(/Top model/i)).not.toBeInTheDocument()
  })

  test('flags spend reported under an unrecognized cost basis instead of silently dropping it', () => {
    // Regression: a "unknown"-basis row with a known costUsd used to disappear from
    // every card with zero indication, understating tracked spend against what the
    // Model breakdown table lower on the same screen shows for the same rows.
    data.aggregates = [...data.aggregates, aggregate({ costBasis: 'unknown', costUsd: 31.5, requestCount: 28 })]
    render(<SummaryCards />)

    expect(screen.getByText(/£25\.20 reported under a cost basis/)).toBeInTheDocument()
  })

  test('does not flag Copilot rows (always-zero "none" cost basis) as unclassified spend', () => {
    // Regression: Copilot daily reports carry costBasis "none" with costUsd 0 by DB
    // constraint. Without excluding it, the note used to fire permanently at "£0.00"
    // on any dashboard load that included Copilot data.
    data.aggregates = [...data.aggregates, aggregate({ costBasis: 'none', costUsd: 0, requestCount: 1 })]
    render(<SummaryCards />)

    expect(screen.queryByText(/reported under a cost basis/)).not.toBeInTheDocument()
  })

  test('shows no unclassified-cost note when every row has a recognized basis', () => {
    render(<SummaryCards />)
    expect(screen.queryByText(/reported under a cost basis/)).not.toBeInTheDocument()
  })

  test('qualifies a partially priced estimate with its unpriced observation count', () => {
    // A bucket where only some requests are priced still contributes its partial
    // subtotal — the card must say something was left out.
    data.aggregates = [...data.aggregates, aggregate({ costBasis: 'listPriceEstimate', costUsd: 2, requestCount: 100, unknownCostCount: 3 })]
    render(<SummaryCards />)

    expect(screen.getByText('List-price estimate').closest('.fpds-card')).toHaveTextContent('3 observations not reported')
  })

  test('opens every financial popover within the summary structure', () => {
    render(<SummaryCards />)

    for (const title of ['Billed spend', 'List-price estimate', 'Provider estimate', 'Subscription notional']) {
      fireEvent.click(screen.getByRole('button', { name: title }))
      expect(screen.getByRole('region', { name: title })).toHaveClass('info-popover', 'info-popover--summary')
    }
  })
})
