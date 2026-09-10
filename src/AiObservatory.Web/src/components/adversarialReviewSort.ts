import type { AdversarialReviewStats } from '../api/client'
import type { RunGroup } from './adversarialReviewGrouping'

export type SortDirection = 'asc' | 'desc'

export type StatsSortField =
  | 'reviewer' | 'model' | 'runCount' | 'avgCostPerRun'
  | 'avgIssuesRaised' | 'avgIssuesAccepted' | 'avgCostPerAcceptedFinding' | 'avgDurationMs'

export function filterStats(stats: AdversarialReviewStats[], query: string): AdversarialReviewStats[] {
  const q = query.trim().toLowerCase()
  if (!q) return stats
  return stats.filter((s) => s.reviewer.toLowerCase().includes(q) || s.model.toLowerCase().includes(q))
}

// avgCostPerAcceptedFinding is null when a reviewer has zero accepted findings — nulls
// sort last in BOTH directions (same convention as githubSort's turnaroundHours), so this
// bakes in `direction` itself rather than going through the uniform asc/desc flip below:
// flipping a "null sorts last" comparison for desc would put nulls first instead.
function compareNullableCostPerFinding(
  a: AdversarialReviewStats, b: AdversarialReviewStats, direction: SortDirection,
): number {
  if (a.avgCostPerAcceptedFinding == null && b.avgCostPerAcceptedFinding == null) return 0
  if (a.avgCostPerAcceptedFinding == null) return 1
  if (b.avgCostPerAcceptedFinding == null) return -1
  const comparison = a.avgCostPerAcceptedFinding - b.avgCostPerAcceptedFinding
  return direction === 'asc' ? comparison : -comparison
}

const STATS_COMPARATORS: Record<
  Exclude<StatsSortField, 'avgCostPerAcceptedFinding'>,
  (a: AdversarialReviewStats, b: AdversarialReviewStats) => number
> = {
  reviewer: (a, b) => a.reviewer.localeCompare(b.reviewer),
  model: (a, b) => a.model.localeCompare(b.model),
  runCount: (a, b) => a.runCount - b.runCount,
  avgCostPerRun: (a, b) => a.avgCostPerRun - b.avgCostPerRun,
  avgIssuesRaised: (a, b) => a.avgIssuesRaised - b.avgIssuesRaised,
  avgIssuesAccepted: (a, b) => a.avgIssuesAccepted - b.avgIssuesAccepted,
  avgDurationMs: (a, b) => a.avgDurationMs - b.avgDurationMs,
}

export function sortStats(
  stats: AdversarialReviewStats[], field: StatsSortField, direction: SortDirection,
): AdversarialReviewStats[] {
  if (field === 'avgCostPerAcceptedFinding') {
    return stats.toSorted((a, b) => compareNullableCostPerFinding(a, b, direction))
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
    (g) => (g.repo?.toLowerCase().includes(q) ?? false) || (g.summary?.toLowerCase().includes(q) ?? false),
  )
}

export function filterRunGroupsByStatus(
  groups: RunGroup[], completeness: CompletenessFilter, validity: ValidityFilter,
): RunGroup[] {
  return groups.filter((g) => {
    if (completeness === 'complete' && !g.isComplete) return false
    if (completeness === 'incomplete' && g.isComplete) return false
    if (validity === 'valid' && !g.isValid) return false
    if (validity === 'invalid' && g.isValid) return false
    return true
  })
}

export function sortRunGroups(
  groups: RunGroup[], field: RunSortField, direction: SortDirection,
): RunGroup[] {
  return groups.toSorted((a, b) => {
    let comparison: number
    if (field === 'recordedAt') comparison = a.recordedAt.localeCompare(b.recordedAt)
    else if (field === 'repo') comparison = (a.repo ?? '').localeCompare(b.repo ?? '')
    else if (field === 'raised') comparison = a.totals.raised - b.totals.raised
    else if (field === 'accepted') comparison = a.totals.accepted - b.totals.accepted
    else if (field === 'costUsd') comparison = a.totals.costUsd - b.totals.costUsd
    else comparison = a.totals.durationMs - b.totals.durationMs
    return direction === 'asc' ? comparison : -comparison
  })
}
