import type { AdversarialReviewStats } from '../api/client'
import type { RunGroup } from './adversarialReviewGrouping'

export type SortDirection = 'asc' | 'desc'

export type StatsSortField =
  | 'reviewer'
  | 'model'
  | 'runCount'
  | 'avgCostPerRun'
  | 'avgIssuesRaised'
  | 'avgIssuesAccepted'
  | 'avgCostPerAcceptedFinding'
  | 'avgDurationMs'

export function filterStats(
  stats: AdversarialReviewStats[],
  query: string,
): AdversarialReviewStats[] {
  const q = query.trim().toLowerCase()
  if (!q) return stats
  return stats.filter(
    (s) => s.reviewer.toLowerCase().includes(q) || s.model.toLowerCase().includes(q),
  )
}

// avgCostPerAcceptedFinding is null when a reviewer has zero accepted findings — nulls
// sort last in BOTH directions (same convention as githubSort's turnaroundHours), so this
// bakes in `direction` itself rather than going through the uniform asc/desc flip below:
// flipping a "null sorts last" comparison for desc would put nulls first instead.
// avgCostPerRun is likewise null when every run in the group had an unknown cost.
type NullableCostField = 'avgCostPerRun' | 'avgCostPerAcceptedFinding'

function compareNullableCost(
  field: NullableCostField,
  a: AdversarialReviewStats,
  b: AdversarialReviewStats,
  direction: SortDirection,
): number {
  const x = a[field]
  const y = b[field]
  if (x == null && y == null) return 0
  if (x == null) return 1
  if (y == null) return -1
  const comparison = x - y
  return direction === 'asc' ? comparison : -comparison
}

const STATS_COMPARATORS: Record<
  Exclude<StatsSortField, NullableCostField>,
  (a: AdversarialReviewStats, b: AdversarialReviewStats) => number
> = {
  reviewer: (a, b) => a.reviewer.localeCompare(b.reviewer),
  model: (a, b) => a.model.localeCompare(b.model),
  runCount: (a, b) => a.runCount - b.runCount,
  avgIssuesRaised: (a, b) => a.avgIssuesRaised - b.avgIssuesRaised,
  avgIssuesAccepted: (a, b) => a.avgIssuesAccepted - b.avgIssuesAccepted,
  avgDurationMs: (a, b) => a.avgDurationMs - b.avgDurationMs,
}

export function sortStats(
  stats: AdversarialReviewStats[],
  field: StatsSortField,
  direction: SortDirection,
): AdversarialReviewStats[] {
  if (field === 'avgCostPerAcceptedFinding' || field === 'avgCostPerRun') {
    return stats.toSorted((a, b) => compareNullableCost(field, a, b, direction))
  }
  const compare = STATS_COMPARATORS[field]
  return stats.toSorted((a, b) => (direction === 'asc' ? compare(a, b) : -compare(a, b)))
}

export type RunSortField = 'recordedAt' | 'repo' | 'raised' | 'accepted' | 'costUsd' | 'durationMs'
export type CompletenessFilter = 'all' | 'complete' | 'incomplete'
export type ValidityFilter = 'all' | 'valid' | 'invalid'

export function filterRunGroups(groups: RunGroup[], query: string): RunGroup[] {
  const q = query.trim().toLowerCase()
  if (!q) return groups
  return groups.filter(
    (g) =>
      (g.repo?.toLowerCase().includes(q) ?? false) ||
      (g.summary?.toLowerCase().includes(q) ?? false),
  )
}

export function filterRunGroupsByStatus(
  groups: RunGroup[],
  completeness: CompletenessFilter,
  validity: ValidityFilter,
): RunGroup[] {
  return groups.filter((g) => {
    if (completeness === 'complete' && !g.isComplete) return false
    if (completeness === 'incomplete' && g.isComplete) return false
    if (validity === 'valid' && !g.isValid) return false
    if (validity === 'invalid' && g.isValid) return false
    return true
  })
}

// Unknown total cost sorts last in both directions (not as 0, which would read as cheapest).
function compareTotalCost(a: RunGroup, b: RunGroup, direction: SortDirection): number {
  const x = a.totals.costUsd
  const y = b.totals.costUsd
  if (x == null && y == null) return 0
  if (x == null) return 1
  if (y == null) return -1
  return direction === 'asc' ? x - y : y - x
}

export function sortRunGroups(
  groups: RunGroup[],
  field: RunSortField,
  direction: SortDirection,
): RunGroup[] {
  return groups.toSorted((a, b) => {
    if (field === 'costUsd') return compareTotalCost(a, b, direction)
    let comparison: number
    if (field === 'recordedAt') comparison = a.recordedAt.localeCompare(b.recordedAt)
    else if (field === 'repo') comparison = (a.repo ?? '').localeCompare(b.repo ?? '')
    else if (field === 'raised') comparison = a.totals.raised - b.totals.raised
    else if (field === 'accepted') comparison = a.totals.accepted - b.totals.accepted
    else comparison = a.totals.durationMs - b.totals.durationMs
    return direction === 'asc' ? comparison : -comparison
  })
}
