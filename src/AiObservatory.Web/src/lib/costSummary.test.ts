import { describe, expect, test } from 'vitest'
import type { DailyAggregate, SpendEntry } from '../api/client'
import { summarizeCosts } from './costSummary'

const aggregate = (overrides: Partial<DailyAggregate>): DailyAggregate => ({
  date: '2026-08-24', provider: 'openai', model: 'gpt-5', sourceId: 'openai-usage-api',
  sourceKind: 'providerApi', usageScope: 'api', costBasis: 'none', inputTokens: 0,
  outputTokens: 0, cacheReadTokens: 0, cacheWriteTokens: 0, cacheWrite1hTokens: 0,
  costUsd: 0, unknownCostCount: 0, cacheSavingsUsd: 0, unknownCacheSavingsCount: 0,
  requestCount: 0, ...overrides,
})

const spend = (amountGbp: number): SpendEntry => ({
  id: crypto.randomUUID(), occurredOn: '2026-08-24', vendorId: 'vendor', categoryId: 'category',
  amount: amountGbp, currency: 'GBP', amountGbp, fxRate: 1, description: null, source: 'manual',
  entryKey: null, recordedAt: '2026-08-24T12:00:00Z',
})

describe('summarizeCosts', () => {
  test('keeps billed, estimated, notional, and unknown observations separate', () => {
    const rows = [
      aggregate({ costBasis: 'billed', costUsd: 99, requestCount: 1 }),
      aggregate({ costBasis: 'listPriceEstimate', costUsd: 2, requestCount: 1 }),
      aggregate({ costBasis: 'providerEstimated', costUsd: 3, requestCount: 1 }),
      aggregate({ costBasis: 'notional', costUsd: 4, requestCount: 1 }),
      aggregate({ costBasis: 'unknown', costUsd: 123, cacheReadTokens: 1, unknownCostCount: 1, unknownCacheSavingsCount: 1, requestCount: 1 }),
    ]

    expect(summarizeCosts(rows, [spend(10), spend(-2)])).toEqual({
      billedGbp: 8,
      listPriceEstimateUsd: 2,
      providerEstimateUsd: 3,
      notionalUsd: 4,
      unknownCostObservations: 1,
      unknownListPriceObservations: 0,
      unknownProviderEstimateObservations: 0,
      unknownNotionalObservations: 0,
      cacheSavingsUsd: null,
      unknownCacheSavingsObservations: 1,
      // The "billed" row above (costUsd: 99) has an unrecognized basis for this
      // summarizer (billed spend comes from the ledger, not aggregates) so it lands
      // in unclassifiedUsd rather than silently vanishing.
      unclassifiedUsd: 99,
    })
  })

  test('surfaces a known cost under an unrecognized basis instead of silently dropping it', () => {
    // Distinct from the fully-unknown row above: here unknownCostCount is 0 (every
    // request has a known cost), but costBasis itself is "unknown" — a real shape
    // seen in seeded data (a provider row whose pricing tier isn't classified).
    // Without a default branch this $31.50 would vanish from every summary card.
    const result = summarizeCosts([
      aggregate({ costBasis: 'unknown', costUsd: 31.5, unknownCostCount: 0, requestCount: 28 }),
    ], [])
    expect(result.unclassifiedUsd).toBe(31.5)
    expect(result.listPriceEstimateUsd).toBeNull()
    expect(result.providerEstimateUsd).toBeNull()
    expect(result.notionalUsd).toBeNull()
  })

  test('catches "billed" reaching the aggregate summarizer, but excludes "none"', () => {
    const result = summarizeCosts([
      aggregate({ costBasis: 'billed', costUsd: 10, requestCount: 1 }),
      aggregate({ costBasis: 'none', costUsd: 0, requestCount: 1 }),
    ], [])
    expect(result.unclassifiedUsd).toBe(10)
  })

  test('never flags the always-zero "none" basis as unclassified spend (Copilot daily reports)', () => {
    // CostBasis.None is a legitimate, DB-constraint-enforced always-zero basis
    // (CK_CopilotDailyReport_NoCost). Without excluding it, hitting this branch even
    // once flips unclassifiedUsd from null to 0, and the summary note fires
    // permanently at "£0.00" on any dashboard load that includes Copilot data.
    const result = summarizeCosts([
      aggregate({ costBasis: 'none', costUsd: 0, requestCount: 5 }),
    ], [])
    expect(result.unclassifiedUsd).toBeNull()
  })

  test('excludes any zero-cost row from unclassifiedUsd regardless of basis', () => {
    const result = summarizeCosts([
      aggregate({ costBasis: 'unknown', costUsd: 0, requestCount: 1 }),
    ], [])
    expect(result.unclassifiedUsd).toBeNull()
  })

  test('preserves absence separately from a represented legitimate zero', () => {
    expect(summarizeCosts([], []).billedGbp).toBeNull()
    expect(summarizeCosts([aggregate({ costBasis: 'notional', costUsd: 0, requestCount: 1 })], []).notionalUsd).toBe(0)
  })

  test('sums only known server cache savings and reports unknown observations', () => {
    expect(summarizeCosts([
      aggregate({ costBasis: 'listPriceEstimate', cacheReadTokens: 1, cacheSavingsUsd: 1.25, requestCount: 1 }),
      aggregate({ costBasis: 'listPriceEstimate', cacheReadTokens: 1, unknownCacheSavingsCount: 2, requestCount: 2 }),
    ], [])).toMatchObject({ cacheSavingsUsd: 1.25, unknownCacheSavingsObservations: 2 })
  })

  test('attributes unpriced observations to their own cost basis', () => {
    // A partially priced bucket still contributes its known subtotal — the per-basis
    // count is the card's only signal that the figure is incomplete.
    const result = summarizeCosts([
      aggregate({ costBasis: 'listPriceEstimate', costUsd: 2, requestCount: 100, unknownCostCount: 1 }),
      aggregate({ costBasis: 'notional', costUsd: 4, requestCount: 5, unknownCostCount: 3 }),
    ], [])
    expect(result.listPriceEstimateUsd).toBe(2)
    expect(result.notionalUsd).toBe(4)
    expect(result.unknownListPriceObservations).toBe(1)
    expect(result.unknownProviderEstimateObservations).toBe(0)
    expect(result.unknownNotionalObservations).toBe(3)
    expect(result.unknownCostObservations).toBe(4)
  })
})
