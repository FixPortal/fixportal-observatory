import type { GitHubPr, GitHubCommitSummary, GitHubCiSummary } from '../api/client'

export type SortDirection = 'asc' | 'desc'

export type PrSortField = 'repo' | 'createdAt' | 'reviewCount' | 'turnaroundHours'

export function filterPrs(prs: GitHubPr[], query: string): GitHubPr[] {
  const q = query.trim().toLowerCase()
  if (!q) return prs
  return prs.filter((p) => p.title.toLowerCase().includes(q) || p.repo.toLowerCase().includes(q))
}

export function sortPrs(prs: GitHubPr[], field: PrSortField, direction: SortDirection): GitHubPr[] {
  return prs.toSorted((a, b) => {
    let comparison: number
    if (field === 'repo') comparison = a.repo.localeCompare(b.repo)
    else if (field === 'createdAt') comparison = a.createdAt.localeCompare(b.createdAt)
    else if (field === 'reviewCount') comparison = a.reviewCount - b.reviewCount
    else {
      // Nulls (no review yet) always sort last, in both directions — these three
      // returns deliberately bypass the `direction === 'asc' ? ... : -comparison`
      // flip below, so don't fold them into `comparison`.
      if (a.turnaroundHours == null && b.turnaroundHours == null) return 0
      if (a.turnaroundHours == null) return 1
      if (b.turnaroundHours == null) return -1
      comparison = a.turnaroundHours - b.turnaroundHours
    }
    return direction === 'asc' ? comparison : -comparison
  })
}

export type CommitSortField = 'repo' | 'commitCount'

export function sortCommitSummaries(
  summaries: GitHubCommitSummary[], field: CommitSortField, direction: SortDirection,
): GitHubCommitSummary[] {
  return summaries.toSorted((a, b) => {
    const comparison = field === 'repo' ? a.repo.localeCompare(b.repo) : a.commitCount - b.commitCount
    return direction === 'asc' ? comparison : -comparison
  })
}

export type CiSortField = 'repo' | 'totalRuns' | 'successRate'

export function sortCiSummaries(
  summaries: GitHubCiSummary[], field: CiSortField, direction: SortDirection,
): GitHubCiSummary[] {
  return summaries.toSorted((a, b) => {
    let comparison: number
    if (field === 'repo') comparison = a.repo.localeCompare(b.repo)
    else if (field === 'totalRuns') comparison = a.totalRuns - b.totalRuns
    else comparison = a.successRate - b.successRate
    return direction === 'asc' ? comparison : -comparison
  })
}
