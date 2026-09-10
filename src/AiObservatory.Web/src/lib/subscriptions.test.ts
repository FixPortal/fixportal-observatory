import { describe, it, expect } from 'vitest'
import { currentBillingPeriodStart, subscriptionUsage } from './subscriptions'

const DATE_2024_03_15 = '2024-03-15'
const DATE_2024_03_01 = '2024-03-01'

describe('currentBillingPeriodStart', () => {
  it('returns the billing day in the current month when today is past it', () => {
    expect(currentBillingPeriodStart(15, '2024-03-20')).toBe(DATE_2024_03_15)
  })

  it('returns the billing day in the current month when today IS the billing day', () => {
    expect(currentBillingPeriodStart(15, DATE_2024_03_15)).toBe(DATE_2024_03_15)
  })

  it('returns last month billing day when today is before billing day', () => {
    expect(currentBillingPeriodStart(15, '2024-03-10')).toBe('2024-02-15')
  })

  it('rolls back to December of the previous year when before billing day in January', () => {
    expect(currentBillingPeriodStart(15, '2024-01-10')).toBe('2023-12-15')
  })

  it('clamps billing day 31 to last day of February (leap year) when today is in March before the 31st', () => {
    // today=2024-03-20, billingDay=31 — clamp(March)=31, 20<31 → prev month Feb 2024 (29 days) → 2024-02-29
    expect(currentBillingPeriodStart(31, '2024-03-20')).toBe('2024-02-29')
  })

  it('clamps billing day 31 in a 30-day month when today is before the clamped day', () => {
    // today=2024-04-05, billingDay=31 — clamp(April)=30, 5<30 → prev month March (31 days) → 2024-03-31
    expect(currentBillingPeriodStart(31, '2024-04-05')).toBe('2024-03-31')
  })

  it('uses the clamped billing day in the current month when today >= clamped day', () => {
    // today=2024-04-30, billingDay=31 — clamp(April)=30, 30>=30 → 2024-04-30
    expect(currentBillingPeriodStart(31, '2024-04-30')).toBe('2024-04-30')
  })

  it('handles billing day 1', () => {
    expect(currentBillingPeriodStart(1, DATE_2024_03_01)).toBe(DATE_2024_03_01)
    expect(currentBillingPeriodStart(1, DATE_2024_03_15)).toBe(DATE_2024_03_01)
  })

  it.each([
    ['2026-08-27', '2026-07-02'],
    ['2026-06-30', '2025-07-02'],
  ])('uses the annual renewal month and day on %s', (today, expected) => {
    expect(currentBillingPeriodStart(2, today, 'annual', 7)).toBe(expected)
  })

  it.each([0, 13])('rejects annual renewal month %s', billingMonth => {
    expect(() => currentBillingPeriodStart(2, '2026-08-27', 'annual', billingMonth)).toThrow(
      'billingMonth must be between 1 and 12.',
    )
  })
})

describe('subscriptionUsage', () => {
  it('reports zero value when the billing period has no activity', () => {
    expect(subscriptionUsage([], 'anthropic', '2026-08-01')).toEqual({
      notionalUsd: 0,
      requestCount: 0,
      unknownCostCount: 0,
    })
  })

  it('keeps activity visible when every monetary value is unreported', () => {
    expect(subscriptionUsage([
      { provider: 'anthropic', date: '2026-08-01', costBasis: 'notional', costUsd: 0, requestCount: 12, unknownCostCount: 12 },
    ], 'anthropic', '2026-08-01')).toEqual({ notionalUsd: null, requestCount: 12, unknownCostCount: 12 })
  })

  it('excludes non-notional, out-of-provider, and pre-period aggregate costs and requests', () => {
    expect(subscriptionUsage([
      { provider: 'anthropic', date: '2026-08-01', costBasis: 'notional', costUsd: 10, requestCount: 1, unknownCostCount: 0 },
      { provider: 'anthropic', date: '2026-08-02', costBasis: 'listPriceEstimate', costUsd: 1000, requestCount: 1, unknownCostCount: 0 },
      { provider: 'anthropic', date: '2026-08-03', costBasis: 'notional', costUsd: 8, requestCount: 1, unknownCostCount: 1 },
      { provider: 'openai', date: '2026-08-02', costBasis: 'notional', costUsd: 7, requestCount: 1, unknownCostCount: 0 },
    ], 'anthropic', '2026-08-01')).toEqual({ notionalUsd: 10, requestCount: 2, unknownCostCount: 1 })
  })
})
