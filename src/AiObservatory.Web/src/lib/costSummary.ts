export interface CostAggregate {
  costBasis: string
  costUsd: number
  unknownCostCount: number
  cacheReadTokens: number
  cacheWriteTokens: number
  cacheSavingsUsd: number
  unknownCacheSavingsCount: number
  requestCount: number
}

export interface SpendAmount {
  amountGbp: number
}

interface TokenAggregate {
  inputTokens: number
  outputTokens: number
  cacheReadTokens: number
  cacheWriteTokens: number
}

export const observedInputTokens = (aggregate: TokenAggregate) =>
  aggregate.inputTokens + aggregate.cacheReadTokens + aggregate.cacheWriteTokens

export const observedTokens = (aggregate: TokenAggregate) => observedInputTokens(aggregate) + aggregate.outputTokens

// "none" is a legitimate, DB-constraint-enforced always-zero basis (e.g. Copilot daily
// reports); a zero-cost row under any basis carries nothing worth flagging either way.
const isUnclassifiedSpend = (aggregate: CostAggregate) => aggregate.costBasis !== 'none' && aggregate.costUsd !== 0

// Only rows with observed cache traffic say anything about savings; among those, a
// bucket contributes its subtotal only when at least one request's savings are known.
function accumulateCacheSavings(aggregates: CostAggregate[]): { cacheSavingsUsd: number | null; unknownCacheSavingsObservations: number } {
  let cacheSavingsUsd: number | null = null
  let unknownCacheSavingsObservations = 0
  for (const aggregate of aggregates) {
    if (aggregate.cacheReadTokens + aggregate.cacheWriteTokens === 0) continue
    unknownCacheSavingsObservations += aggregate.unknownCacheSavingsCount
    if (aggregate.requestCount > aggregate.unknownCacheSavingsCount) {
      cacheSavingsUsd = (cacheSavingsUsd ?? 0) + aggregate.cacheSavingsUsd
    }
  }
  return { cacheSavingsUsd, unknownCacheSavingsObservations }
}

export interface CostSummary {
  billedGbp: number | null
  listPriceEstimateUsd: number | null
  providerEstimateUsd: number | null
  notionalUsd: number | null
  unknownCostObservations: number
  /** Per-card qualifiers: requests in each basis bucket whose cost the provider did
   * not report. A partially priced bucket still contributes its known subtotal, so
   * without these the cards would print a partial figure as if it were complete. */
  unknownListPriceObservations: number
  unknownProviderEstimateObservations: number
  unknownNotionalObservations: number
  cacheSavingsUsd: number | null
  unknownCacheSavingsObservations: number
  /** costUsd from rows whose costBasis is none of the three named buckets above (e.g.
   * "unknown", or an aggregate-sourced "billed" row — a smaller, distinct signal from
   * the ledger-sourced Billed spend card) — a known nonzero dollar figure that none of
   * the summary cards would otherwise show. Excludes "none", the legitimate always-zero
   * basis Copilot daily reports use (a real CK_CopilotDailyReport_NoCost DB constraint),
   * and any zero-cost row in general, so the note only surfaces real dropped spend
   * rather than firing permanently at £0.00. Null when nothing fell into this bucket. */
  unclassifiedUsd: number | null
}

export function summarizeCosts(aggregates: CostAggregate[], spendEntries: SpendAmount[]): CostSummary {
  let billedGbp = spendEntries.length === 0 ? null : 0
  let listPriceEstimateUsd: number | null = null
  let providerEstimateUsd: number | null = null
  let notionalUsd: number | null = null
  let unknownCostObservations = 0
  const unknownByBasis: Record<'listPriceEstimate' | 'providerEstimated' | 'notional', number> = {
    listPriceEstimate: 0, providerEstimated: 0, notional: 0,
  }
  const { cacheSavingsUsd, unknownCacheSavingsObservations } = accumulateCacheSavings(aggregates)
  let unclassifiedUsd: number | null = null

  for (const entry of spendEntries) billedGbp! += entry.amountGbp

  for (const aggregate of aggregates) {
    unknownCostObservations += aggregate.unknownCostCount
    if (aggregate.costBasis in unknownByBasis) {
      unknownByBasis[aggregate.costBasis as keyof typeof unknownByBasis] += aggregate.unknownCostCount
    }

    if (aggregate.requestCount <= aggregate.unknownCostCount) continue
    switch (aggregate.costBasis) {
      case 'listPriceEstimate':
        listPriceEstimateUsd = (listPriceEstimateUsd ?? 0) + aggregate.costUsd
        break
      case 'providerEstimated':
        providerEstimateUsd = (providerEstimateUsd ?? 0) + aggregate.costUsd
        break
      case 'notional':
        notionalUsd = (notionalUsd ?? 0) + aggregate.costUsd
        break
      default:
        // A row with a known, nonzero costUsd but a cost basis outside the three named
        // buckets (e.g. "unknown", "billed") would otherwise vanish from every summary
        // card with no indication anything was left out.
        if (isUnclassifiedSpend(aggregate)) {
          unclassifiedUsd = (unclassifiedUsd ?? 0) + aggregate.costUsd
        }
        break
    }
  }

  return {
    billedGbp,
    listPriceEstimateUsd,
    providerEstimateUsd,
    notionalUsd,
    unknownCostObservations,
    unknownListPriceObservations: unknownByBasis.listPriceEstimate,
    unknownProviderEstimateObservations: unknownByBasis.providerEstimated,
    unknownNotionalObservations: unknownByBasis.notional,
    cacheSavingsUsd,
    unknownCacheSavingsObservations,
    unclassifiedUsd,
  }
}
