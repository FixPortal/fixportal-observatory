import { fireEvent, render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, expect, test, vi } from 'vitest'
import type { DailyAggregate } from '../api/client'
import ModelBreakdown, { groupModelRows } from './ModelBreakdown'

const data = vi.hoisted(() => ({ aggregates: [] as DailyAggregate[] }))
vi.mock('../api/queries', () => ({ useAggregates: () => ({ aggregates: data.aggregates, isError: false, isLoading: false }) }))
vi.mock('../lib/currency', () => ({ useUsdToGbp: () => 0.8, formatGbp: (usd: number, rate: number) => `£${(usd * rate).toFixed(2)}` }))

const aggregate = (overrides: Partial<DailyAggregate> = {}): DailyAggregate => ({
  date: '2026-08-24', provider: 'openai', model: 'gpt-5', sourceId: 'openai-usage-api',
  sourceKind: 'providerApi', usageScope: 'api', costBasis: 'listPriceEstimate', inputTokens: 100,
  outputTokens: 100, cacheReadTokens: 0, cacheWriteTokens: 0, cacheWrite1hTokens: 0,
  costUsd: 2, unknownCostCount: 0, cacheSavingsUsd: 0, unknownCacheSavingsCount: 0,
  requestCount: 2, ...overrides,
})

beforeEach(() => { data.aggregates = [] })

test('groups by the collision-safe five-dimensional evidence grain', () => {
  const rows = groupModelRows([
    aggregate(), aggregate({ sourceId: 'codex-local', usageScope: 'subscription', costBasis: 'notional' }),
    aggregate({ provider: 'new-oss-provider', model: 'gpt-5', sourceId: 'new-source' }),
  ])
  expect(rows).toHaveLength(3)
  expect(new Set(rows.map(row => row.key)).size).toBe(3)
  expect(rows.map(row => [row.providerLabel, row.sourceLabel, row.scopeLabel, row.basisLabel])).toContainEqual([
    'New oss provider', 'New source', 'API', 'List-price estimate',
  ])
})

test('distinguishes missing, legitimate zero, and partially known cost', () => {
  const [missing] = groupModelRows([aggregate({ costUsd: 0, requestCount: 2, unknownCostCount: 2 })])
  const [zero] = groupModelRows([aggregate({ costUsd: 0, requestCount: 2, unknownCostCount: 0 })])
  const [mixed] = groupModelRows([aggregate({ costUsd: 2, requestCount: 3, unknownCostCount: 1 })])
  expect(missing.costReported).toBe(false)
  expect(zero).toMatchObject({ costReported: true, cost: 0, cpm: 0 })
  expect(mixed).toMatchObject({ costReported: true, unknownCostCount: 1, cpm: null })
})

test('includes cached input in cost per million tokens', () => {
  const [row] = groupModelRows([aggregate({
    inputTokens: 100,
    outputTokens: 100,
    cacheReadTokens: 600,
    cacheWriteTokens: 200,
    costUsd: 2,
  })])

  expect(row.cpm).toBe(2_000)
})

test('renders provenance, missing and mixed cost truth, and native filter/sort controls', async () => {
  data.aggregates = [
    aggregate({ model: 'missing-model', costUsd: 0, requestCount: 2, unknownCostCount: 2 }),
    aggregate({ model: 'zero-model', costUsd: 0, requestCount: 2, unknownCostCount: 0, sourceId: 'codex-local', usageScope: 'subscription', costBasis: 'notional' }),
    aggregate({ model: 'mixed-model', costUsd: 2, requestCount: 3, unknownCostCount: 1, sourceId: 'new-source' }),
  ]
  render(<ModelBreakdown />)

  expect(screen.getByRole('columnheader', { name: /Source/ })).toBeInTheDocument()
  expect(screen.getByRole('columnheader', { name: /Scope/ })).toBeInTheDocument()
  expect(screen.getByRole('columnheader', { name: /Basis/ })).toBeInTheDocument()
  const missingRow = screen.getByText('missing-model').closest('tr')!
  expect(within(missingRow).getAllByText('Not reported')).toHaveLength(2)
  expect(within(screen.getByText('zero-model').closest('tr')!).getAllByText('£0.00')).toHaveLength(2)
  expect(within(screen.getByText('mixed-model').closest('tr')!).getByText('1 observation not reported')).toBeInTheDocument()
  expect(within(screen.getByText('mixed-model').closest('tr')!).getByText('Not reported')).toBeInTheDocument()
  const modelSort = screen.getByRole('button', { name: /sort by model/i })
  modelSort.focus()
  expect(modelSort).toHaveFocus()
  await userEvent.keyboard('{Enter}')
  expect(modelSort.closest('th')).toHaveAttribute('aria-sort', 'ascending')
  const providerFilter = screen.getByRole('button', { name: 'OpenAI' })
  providerFilter.focus()
  await userEvent.keyboard('{Enter}')
  expect(providerFilter).toHaveAttribute('aria-pressed', 'true')
  fireEvent.change(screen.getByRole('textbox', { name: 'Search models' }), { target: { value: 'mixed' } })
  expect(screen.queryByText('zero-model')).not.toBeInTheDocument()
})
