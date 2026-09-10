import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen } from '@testing-library/react'
import { beforeEach, describe, expect, test, vi } from 'vitest'
import type { Subscription } from '../api/client'
import SubscriptionPanel from './SubscriptionPanel'

const mockData = vi.hoisted(() => ({
  aggregates: [{
    provider: 'google', date: '2026-08-01', costBasis: 'notional', costUsd: 0,
    requestCount: 7, unknownCostCount: 7,
  }],
  aggregateRange: [] as Date[],
}))

const annualSubscription = {
  id: 'google-one',
  provider: 'google',
  name: 'Google One',
  costAmount: 189.99,
  currency: 'GBP',
  billingInterval: 'annual',
  billingMonth: 7,
  billingDay: 2,
  activeFrom: '2026-07-02',
  activeTo: null,
  extraUsageCost: null,
} as unknown as Subscription

vi.mock('../api/queries', () => ({
  useSubscriptions: () => ({ subscriptions: [annualSubscription], isError: false, isLoading: false }),
  useAggregates: (from?: Date, to?: Date) => {
    mockData.aggregateRange = from && to ? [from, to] : []
    return { aggregates: mockData.aggregates, isError: false, isLoading: false }
  },
  localDate: () => '2026-08-27',
}))

vi.mock('../lib/currency', () => ({
  useUsdToGbp: () => 1,
  formatCurrency: (amount: number) => `£${amount.toFixed(2)}`,
  gbp: (amount: number) => `£${amount.toFixed(2)}`,
}))

vi.mock('../auth/msal', () => ({ isReadonly: true }))

describe('SubscriptionPanel', () => {
  beforeEach(() => {
    mockData.aggregateRange = []
    mockData.aggregates = [{
      provider: 'google', date: '2026-08-01', costBasis: 'notional', costUsd: 0,
      requestCount: 7, unknownCostCount: 7,
    }]
  })

  test('renders annual price and renewal date', () => {
    render(
      <QueryClientProvider client={new QueryClient()}>
        <SubscriptionPanel />
      </QueryClientProvider>,
    )

    expect(screen.getByText('/yr')).toBeInTheDocument()
    expect(screen.getByText('Renews annually on 2 July')).toBeInTheDocument()
  })

  test('requests aggregates for the full current subscription period', () => {
    render(
      <QueryClientProvider client={new QueryClient()}>
        <SubscriptionPanel />
      </QueryClientProvider>,
    )

    expect(mockData.aggregateRange).toHaveLength(2)
    expect(mockData.aggregateRange[0]).toEqual(new Date(2026, 6, 2))
    expect(mockData.aggregateRange[1]).toEqual(new Date(2026, 7, 27))
  })

  test('reports activity without inventing a zero monetary value', () => {
    render(
      <QueryClientProvider client={new QueryClient()}>
        <SubscriptionPanel />
      </QueryClientProvider>,
    )

    expect(screen.getByText('Not reported')).toBeInTheDocument()
    expect(screen.getByText('7 requests recorded · value not reported')).toBeInTheDocument()
    expect(screen.queryByText('£0.00')).not.toBeInTheDocument()
  })

  test('qualifies a partial monetary value with the unpriced request count', () => {
    mockData.aggregates = [{
      provider: 'google', date: '2026-08-01', costBasis: 'notional', costUsd: 5,
      requestCount: 7, unknownCostCount: 2,
    }]

    render(
      <QueryClientProvider client={new QueryClient()}>
        <SubscriptionPanel />
      </QueryClientProvider>,
    )

    expect(screen.getByText('£5.00')).toBeInTheDocument()
    expect(screen.getByText(/2 requests not reported/)).toBeInTheDocument()
  })
})
