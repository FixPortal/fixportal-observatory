import type { AuditEvidence, PresentationValue } from './auditTypes'
import { summarizeCosts, type CostAggregate } from '../lib/costSummary'
import { formatCurrency } from '../lib/currency'

const INTEGER_FIELDS = [
  'unknownCostCount',
  'cacheReadTokens',
  'cacheWriteTokens',
  'unknownCacheSavingsCount',
  'requestCount',
] as const
const FIELDS = [
  'billedGbp',
  'listPriceEstimateUsd',
  'providerEstimateUsd',
  'notionalUsd',
  'unclassifiedUsd',
  'cacheSavingsUsd',
  'unknownCostObservations',
  'unknownListPriceObservations',
  'unknownProviderEstimateObservations',
  'unknownNotionalObservations',
  'unknownCacheSavingsObservations',
] as const
const MAX_SAFE = BigInt(Number.MAX_SAFE_INTEGER)

/** This boundary observes the existing number-based production function. It is not the verifier. */
export function summarizeFrozenPresentation(items: AuditEvidence[]): {
  values: PresentationValue[]
  diagnosticCodes: string[]
} {
  const aggregates = items.filter((row) => row.kind === 'aggregate').map((row) => row.record)
  const spendRows = items.filter((row) => row.kind === 'spend')
  if (
    aggregates.some(
      (row) => !representableMoney(row.costUsd) || !representableMoney(row.cacheSavingsUsd),
    ) ||
    spendRows.some((row) => !representableMoney(row.record.amountGbp))
  ) {
    return { values: [], diagnosticCodes: ['unrepresentable_presentation_value'] }
  }
  // Every consumed count, eligibility comparison and possible summed count must be exact.
  for (const field of INTEGER_FIELDS) {
    let sum = 0n
    for (const aggregate of aggregates) {
      const value = aggregate[field]
      if (!/^\d+$/.test(value)) return unsafe()
      sum += BigInt(value)
      if (sum > MAX_SAFE) return unsafe()
    }
  }
  // Production adds cache-read and cache-write before its eligibility comparison.
  if (
    aggregates.some((row) => BigInt(row.cacheReadTokens) + BigInt(row.cacheWriteTokens) > MAX_SAFE)
  )
    return unsafe()
  const productionRows: CostAggregate[] = aggregates.map((row) => ({
    costBasis: row.key.costBasis,
    costUsd: Number(row.costUsd),
    unknownCostCount: Number(row.unknownCostCount),
    cacheReadTokens: Number(row.cacheReadTokens),
    cacheWriteTokens: Number(row.cacheWriteTokens),
    cacheSavingsUsd: Number(row.cacheSavingsUsd),
    unknownCacheSavingsCount: Number(row.unknownCacheSavingsCount),
    requestCount: Number(row.requestCount),
  }))
  const spend = spendRows.map((row) => ({ amountGbp: Number(row.record.amountGbp) }))
  const actual = summarizeCosts(productionRows, spend)
  const values: PresentationValue[] = []
  for (const field of FIELDS) {
    const value = actual[field]
    if (value !== null && (!Number.isFinite(value) || /e/i.test(String(value))))
      return { values: [], diagnosticCodes: ['unrepresentable_presentation_value'] }
    const isCount = field.startsWith('unknown')
    if (isCount && !Number.isSafeInteger(value)) return unsafe()
    const currency = isCount ? 'XXX' : field === 'billedGbp' ? 'GBP' : 'USD'
    values.push({
      field,
      groupKey: 'all',
      unroundedValue: value === null ? null : String(value),
      formattedValue:
        value === null
          ? null
          : isCount
            ? String(value)
            : formatCurrency(value, currency).replace(/[^\d.-]/g, ''),
      currency,
    })
  }
  // summarizeCosts has no native-currency output. Never fill this from reference math.
  return {
    values,
    diagnosticCodes: spend.length ? ['missing_presentation_value:billedNative'] : [],
  }
}

function unsafe(): { values: PresentationValue[]; diagnosticCodes: string[] } {
  return { values: [], diagnosticCodes: ['unsafe_integer_presentation_coverage'] }
}

function representableMoney(value: string): boolean {
  if (!/^-?\d+(?:\.\d+)?$/.test(value)) return false
  const [whole = '', fraction = ''] = value.replace(/^-/, '').split('.')
  const integer = whole.replace(/^0+/, '') || '0'
  const fractional = fraction.replace(/0+$/, '')
  // A lost cent or significant digit cannot become an observation of the original input.
  const coefficient = BigInt(integer + fractional)
  if (coefficient > MAX_SAFE) return false
  const sign = value.startsWith('-') && coefficient !== 0n ? '-' : ''
  const normalized = sign + integer + (fractional ? '.' + fractional : '')
  return Number.isFinite(Number(value)) && String(Number(value)) === normalized
}
