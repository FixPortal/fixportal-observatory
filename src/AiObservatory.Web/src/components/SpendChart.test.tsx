import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, test, vi } from 'vitest'
import type { DailyAggregate } from '../api/client'
import SpendChart, { buildUsageSeries } from './SpendChart'

const data = vi.hoisted(() => ({ aggregates: [] as DailyAggregate[] }))
vi.mock('../api/queries', () => ({ useAggregates: () => ({ aggregates: data.aggregates, isError: false, isLoading: false }) }))
vi.mock('../lib/currency', () => ({ useUsdToGbp: () => 0.8, gbp: (value: number) => `£${value.toFixed(2)}` }))

const aggregate = (overrides: Partial<DailyAggregate> = {}): DailyAggregate => ({
  date: '2026-08-24', provider: 'openai', model: 'gpt-5', sourceId: 'openai-usage-api',
  sourceKind: 'providerApi', usageScope: 'api', costBasis: 'listPriceEstimate', inputTokens: 100,
  outputTokens: 50, cacheReadTokens: 0, cacheWriteTokens: 0, cacheWrite1hTokens: 0,
  costUsd: 2, unknownCostCount: 0, cacheSavingsUsd: 0, unknownCacheSavingsCount: 0,
  requestCount: 1, ...overrides,
})

beforeEach(() => { data.aggregates = [] })

describe('buildUsageSeries', () => {
  test('retains exact source, scope, and basis for same-provider token series', () => {
    const result = buildUsageSeries([
      aggregate(),
      aggregate({ sourceId: 'codex-local', usageScope: 'subscription', costBasis: 'notional' }),
    ], 'tokens', 0.8)

    expect(result.series.map(series => series.label)).toEqual([
      'OpenAI · Usage API · API · List-price estimate',
      'OpenAI · Codex local · Subscription · Notional',
    ])
    expect(new Set(result.series.map(series => series.key)).size).toBe(2)
  })

  test.each(['listPriceEstimate', 'providerEstimated', 'notional'] as const)('filters %s by exact basis', mode => {
    const rows = [
      aggregate({ costBasis: 'listPriceEstimate', costUsd: 1 }),
      aggregate({ costBasis: 'providerEstimated', costUsd: 2 }),
      aggregate({ costBasis: 'notional', costUsd: 3 }),
      aggregate({ costBasis: 'billed', costUsd: 99 }),
      aggregate({ costBasis: 'unknown', costUsd: 99 }),
    ]
    const result = buildUsageSeries(rows, mode, 0.8)

    expect(result.series).toHaveLength(1)
    expect(result.series[0].basis).toBe(mode)
    expect(Object.values(result.byDate[0]).filter(value => typeof value === 'number')).toEqual([
      { listPriceEstimate: 0.8, providerEstimated: 1.6, notional: 2.4 }[mode],
    ])
  })

  test('uses collision-safe identities and readable unknown labels', () => {
    const result = buildUsageSeries([
      aggregate({ provider: 'new-oss-provider', sourceId: 'new-source', usageScope: 'team|api', costBasis: 'none' }),
      aggregate({ provider: 'new-oss-provider|new-source', sourceId: 'team', usageScope: 'api', costBasis: 'none' }),
    ], 'tokens', 1)

    expect(new Set(result.series.map(series => series.key)).size).toBe(2)
    expect(result.series[0].label).toBe('New oss provider · New source · Team api · None')
  })

  test('drops fully unknown estimate rows while retaining qualified mixed rows', () => {
    const result = buildUsageSeries([
      aggregate({ sourceId: 'fully-unknown', requestCount: 2, unknownCostCount: 2, costUsd: 99 }),
      aggregate({ sourceId: 'partly-known', requestCount: 2, unknownCostCount: 1, costUsd: 2 }),
    ], 'listPriceEstimate', 0.8)

    expect(result.series.map(series => series.sourceId)).toEqual(['partly-known'])
    expect(Object.values(result.byDate[0]).filter(value => typeof value === 'number')).toEqual([1.6])
  })

  test('suppresses token series whose total is zero across the period', () => {
    const result = buildUsageSeries([
      aggregate({ sourceId: 'zero-only', inputTokens: 0, outputTokens: 0 }),
      aggregate({ sourceId: 'reported', inputTokens: 10, outputTokens: 5 }),
    ], 'tokens', 1)

    expect(result.series.map(series => series.sourceId)).toEqual(['reported'])
    expect(Object.keys(result.byDate[0])).toHaveLength(2)
  })

  test('counts cached input in token usage', () => {
    const result = buildUsageSeries([
      aggregate({ inputTokens: 10, outputTokens: 5, cacheReadTokens: 100, cacheWriteTokens: 7 }),
    ], 'tokens', 1)

    expect(Object.values(result.byDate[0]).filter(value => typeof value === 'number')).toEqual([122])
  })

  test('retains a fully known zero-cost estimate as reported evidence', () => {
    const result = buildUsageSeries([
      aggregate({ costUsd: 0, requestCount: 1, unknownCostCount: 0 }),
    ], 'listPriceEstimate', 0.8)

    expect(result.series).toHaveLength(1)
    expect(result.byDate).toHaveLength(1)
    expect(Object.values(result.byDate[0]).filter(value => typeof value === 'number')).toEqual([0])
  })
})

test('defaults usage value to subscription notional and reports an unavailable selected estimate', async () => {
  data.aggregates = [aggregate({ costBasis: 'notional' })]
  render(<SpendChart />)

  expect(screen.getByRole('button', { name: 'Notional' })).toHaveAttribute('aria-pressed', 'true')
  const providerEstimate = screen.getByRole('button', { name: 'Provider estimate' })
  providerEstimate.focus()
  expect(providerEstimate).toHaveFocus()
  await userEvent.keyboard('{Enter}')
  expect(screen.getByText('Not reported for this period.')).toBeInTheDocument()
})

test('distinguishes a globally empty result from a missing selected basis', () => {
  render(<SpendChart />)
  expect(screen.getByText('No usage data for this period.')).toBeInTheDocument()
})

test('renders Not reported for non-empty aggregates with zero tokens or fully unknown selected cost', async () => {
  data.aggregates = [aggregate({ inputTokens: 0, outputTokens: 0, requestCount: 1, unknownCostCount: 1 })]
  render(<SpendChart />)

  expect(screen.getByText('Not reported for this period.')).toBeInTheDocument()
  const listPrice = screen.getByRole('button', { name: 'List-price estimate' })
  listPrice.focus()
  await userEvent.keyboard('{Enter}')
  expect(screen.getByText('Not reported for this period.')).toBeInTheDocument()
})

test('renders a known zero estimate as a labelled series rather than missing', async () => {
  data.aggregates = [aggregate({ inputTokens: 0, outputTokens: 0, costUsd: 0, requestCount: 1, unknownCostCount: 0 })]
  render(<SpendChart />)

  const listPrice = screen.getByRole('button', { name: 'List-price estimate' })
  listPrice.focus()
  await userEvent.keyboard('{Enter}')

  expect(screen.queryByText('Not reported for this period.')).not.toBeInTheDocument()
  expect(screen.getByRole('list', { name: 'Usage series' })).toHaveTextContent('OpenAI · Usage API · API · List-price estimate')
})

test('renders full source signatures in a bounded legend outside the fixed-height plot', async () => {
  data.aggregates = [
    aggregate(),
    aggregate({ sourceId: 'codex-local', usageScope: 'subscription', costBasis: 'notional' }),
  ]
  render(<SpendChart />)

  await userEvent.click(screen.getByRole('button', { name: 'Tokens' }))
  const legend = await screen.findByRole('list', { name: 'Usage series' })
  expect(within(legend).getByText('OpenAI · Usage API · API · List-price estimate')).toBeInTheDocument()
  expect(within(legend).getByText('OpenAI · Codex local · Subscription · Notional')).toBeInTheDocument()
  expect(document.querySelector('.usage-chart-plot')).not.toContainElement(legend)
})
