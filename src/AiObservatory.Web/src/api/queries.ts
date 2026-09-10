import { useMemo } from 'react'
import { useQuery, type QueryClient } from '@tanstack/react-query'
import {
  getAggregates, getInsights, getSubscriptions,
  getAdversarialReviewRuns, getAdversarialReviewStats, getCavemanStats,
  getBudgetRules, getNotificationSettings,
  getActivityDaily, getActivityByProject,
  getGitHubPrs, getGitHubCommitSummary, getGitHubCi, getGitHubReviews,
  getSpendCategories, getSpendVendors, getSpendEntries, getBilledReporting, getSourceStatuses,
  type DailyAggregate, type Insight, type Subscription,
  type AdversarialReviewRun, type AdversarialReviewStats, type CavemanStats,
  type BudgetRule, type DailyActivity, type ProjectActivity,
  type GitHubPr, type GitHubCommitSummary, type GitHubCiSummary, type GitHubReviewerSummary,
  type SpendCategory, type SpendVendor, type SpendEntry, type BilledReporting, type SourceStatusResponse,
  type NotificationSettings,
} from './client'
import { dashboardDateRange } from '../lib/dateRange'
export { AGGREGATES_DAYS_RANGE, dashboardDateRange } from '../lib/dateRange'

export const invalidateSpendData = (queryClient: QueryClient) => Promise.all([
  queryClient.invalidateQueries({ queryKey: ['spend-entries'] }),
  queryClient.invalidateQueries({ queryKey: ['billed-reporting'] }),
])

// Shared query hooks. Components subscribe directly (react-query deduplicates by
// key), so data is not props-drilled from the page and each panel can resolve
// and render independently.

// Local calendar date (yyyy-MM-dd) from the machine's timezone — NOT toISOString(),
// which emits the UTC date and, in the 00:00–00:59 local window under a positive offset
// (e.g. BST), reports yesterday. That off-by-one drove the billing-period start, the
// active-subscription filter, and the range labels a day early. One helper, every consumer.
export const localDate = (d: Date) => {
  const y = d.getFullYear()
  const m = String(d.getMonth() + 1).padStart(2, '0')
  const day = String(d.getDate()).padStart(2, '0')
  return `${y}-${m}-${day}`
}

export function useAggregates(from?: Date, to?: Date, staleTime?: number): { aggregates: DailyAggregate[]; isError: boolean; isLoading: boolean } {
  // No range means the shared dashboard window — resolve and key on it explicitly so
  // every Overview consumer lands on the SAME ['aggregates', from, to] cache entry
  // rather than a second, independently-refetching bare ['aggregates'] one.
  const hasRange = from != null && to != null
  const range = hasRange ? { from: from!, to: to! } : dashboardDateRange()
  const { data = [], isError, isPending } = useQuery({
    queryKey: ['aggregates', localDate(range.from), localDate(range.to)],
    queryFn: () => getAggregates(localDate(range.from), localDate(range.to)),
    staleTime,
  })
  return { aggregates: data, isError, isLoading: isPending }
}

export function useActivityDaily(from?: Date, to?: Date, enabled = true): { daily: DailyActivity[]; isError: boolean; isLoading: boolean } {
  const hasRange = from != null && to != null
  const { data = [], isError, isLoading } = useQuery({
    queryKey: hasRange ? ['activity-daily', localDate(from!), localDate(to!)] : ['activity-daily'],
    queryFn: hasRange
      ? () => getActivityDaily(localDate(from!), localDate(to!))
      : () => getActivityDaily(),
    // Callers without a comparison range must pass enabled: false — no range means
    // "fetch the default window", never "skip the request".
    enabled,
  })
  return { daily: data, isError, isLoading }
}

export function useActivityByProject(from?: Date, to?: Date): { projects: ProjectActivity[]; isError: boolean; isLoading: boolean } {
  const hasRange = from != null && to != null
  const { data = [], isError, isPending } = useQuery({
    queryKey: hasRange ? ['activity-by-project', localDate(from!), localDate(to!)] : ['activity-by-project'],
    queryFn: hasRange
      ? () => getActivityByProject(localDate(from!), localDate(to!))
      : () => getActivityByProject(),
  })
  return { projects: data, isError, isLoading: isPending }
}

export function useGitHubPrs(from?: Date, to?: Date): { prs: GitHubPr[]; isError: boolean; isLoading: boolean } {
  const hasRange = from != null && to != null
  const { data = [], isError, isPending } = useQuery({
    queryKey: hasRange ? ['github-prs', localDate(from!), localDate(to!)] : ['github-prs'],
    queryFn: hasRange ? () => getGitHubPrs(localDate(from!), localDate(to!)) : () => getGitHubPrs(),
  })
  return { prs: data, isError, isLoading: isPending }
}

export function useGitHubCommitSummary(from?: Date, to?: Date): { summary: GitHubCommitSummary[]; isError: boolean; isLoading: boolean } {
  const hasRange = from != null && to != null
  const { data = [], isError, isPending } = useQuery({
    queryKey: hasRange ? ['github-commits-summary', localDate(from!), localDate(to!)] : ['github-commits-summary'],
    queryFn: hasRange ? () => getGitHubCommitSummary(localDate(from!), localDate(to!)) : () => getGitHubCommitSummary(),
  })
  return { summary: data, isError, isLoading: isPending }
}

export function useGitHubCi(from?: Date, to?: Date): { ci: GitHubCiSummary[]; isError: boolean; isLoading: boolean } {
  const hasRange = from != null && to != null
  const { data = [], isError, isPending } = useQuery({
    queryKey: hasRange ? ['github-ci', localDate(from!), localDate(to!)] : ['github-ci'],
    queryFn: hasRange ? () => getGitHubCi(localDate(from!), localDate(to!)) : () => getGitHubCi(),
  })
  return { ci: data, isError, isLoading: isPending }
}

export function useGitHubReviews(from?: Date, to?: Date): { reviewers: GitHubReviewerSummary[]; isError: boolean; isLoading: boolean } {
  const hasRange = from != null && to != null
  const { data = [], isError, isPending } = useQuery({
    queryKey: hasRange ? ['github-reviews', localDate(from!), localDate(to!)] : ['github-reviews'],
    queryFn: hasRange ? () => getGitHubReviews(localDate(from!), localDate(to!)) : () => getGitHubReviews(),
  })
  return { reviewers: data, isError, isLoading: isPending }
}

export function useInsights(): { insights: Insight[]; isError: boolean; isLoading: boolean } {
  const { data = [], isError, isPending } = useQuery({ queryKey: ['insights'], queryFn: getInsights })
  return { insights: data, isError, isLoading: isPending }
}

export function useSubscriptions(): { subscriptions: Subscription[]; isError: boolean; isLoading: boolean } {
  const { data = [], isError, isPending } = useQuery({ queryKey: ['subscriptions'], queryFn: getSubscriptions })
  return { isError, subscriptions: data, isLoading: isPending }
}

export function useSourceStatuses(): { statuses: SourceStatusResponse[]; isError: boolean; isLoading: boolean } {
  const { data = [], isError, isPending } = useQuery({ queryKey: ['source-statuses'], queryFn: getSourceStatuses })
  return { statuses: data, isError, isLoading: isPending }
}

export function useAdversarialReviewRuns(): { runs: AdversarialReviewRun[]; isError: boolean; isLoading: boolean } {
  const { data = [], isError, isPending } = useQuery({ queryKey: ['adversarial-review-runs'], queryFn: getAdversarialReviewRuns })
  return { runs: data, isError, isLoading: isPending }
}

export function useAdversarialReviewStats(): { stats: AdversarialReviewStats[]; isError: boolean; isLoading: boolean } {
  const { data = [], isError, isPending } = useQuery({ queryKey: ['adversarial-review-stats'], queryFn: getAdversarialReviewStats })
  return { stats: data, isError, isLoading: isPending }
}

export function useCavemanStats(): { stats: CavemanStats | undefined; isError: boolean; isLoading: boolean } {
  const { data, isError, isPending } = useQuery({ queryKey: ['caveman-stats'], queryFn: getCavemanStats })
  return { stats: data, isError, isLoading: isPending }
}

export function useBudgetRules(): { rules: BudgetRule[]; isLoading: boolean; isError: boolean } {
  const { data = [], isPending, isError } = useQuery({ queryKey: ['budget-rules'], queryFn: getBudgetRules })
  return { rules: data, isLoading: isPending, isError }
}

export function useNotificationSettings(): {
  settings: NotificationSettings | undefined
  isLoading: boolean
  isError: boolean
} {
  const { data, isPending, isError } = useQuery({
    queryKey: ['notification-settings'],
    queryFn: getNotificationSettings,
  })
  return { settings: data, isLoading: isPending, isError }
}

// Live only — the pickers (SpendFilterBar, SpendEntryModal). A retired category or
// vendor must not be selectable again. Errors are surfaced, not swallowed into an
// empty list: an empty picker reads as "no categories exist", which is a false claim.
export function useSpendCategories(): { categories: SpendCategory[]; isError: boolean; isLoading: boolean } {
  const { data = [], isError, isPending } = useQuery({ queryKey: ['spend-categories'], queryFn: () => getSpendCategories() })
  return { categories: data, isError, isLoading: isPending }
}

export function useSpendVendors(): { vendors: SpendVendor[]; isError: boolean; isLoading: boolean } {
  const { data = [], isError, isPending } = useQuery({ queryKey: ['spend-vendors'], queryFn: () => getSpendVendors() })
  return { vendors: data, isError, isLoading: isPending }
}

// Includes archived rows, so a historical ledger entry can still resolve the display
// name of a category/vendor that has since been retired (spec §8) — used for the
// ledger table's name maps and SpendPage's largestCategory, never for a picker.
export function useAllSpendCategories(): { categories: SpendCategory[]; isError: boolean; isLoading: boolean } {
  const { data = [], isError, isPending } = useQuery({ queryKey: ['spend-categories', 'all'], queryFn: () => getSpendCategories(true) })
  return { categories: data, isError, isLoading: isPending }
}

export function useAllSpendVendors(): { vendors: SpendVendor[]; isError: boolean; isLoading: boolean } {
  const { data = [], isError, isPending } = useQuery({ queryKey: ['spend-vendors', 'all'], queryFn: () => getSpendVendors(true) })
  return { vendors: data, isError, isLoading: isPending }
}

export function useSpendEntries(from: Date, to: Date, vendorId?: string, categoryId?: string): {
  entries: SpendEntry[]
  isLoading: boolean
  isError: boolean
} {
  const { data = [], isPending, isError } = useQuery({
    queryKey: ['spend-entries', localDate(from), localDate(to), vendorId, categoryId],
    queryFn: () => getSpendEntries(localDate(from), localDate(to), vendorId, categoryId),
  })
  return { entries: data, isLoading: isPending, isError }
}

export function useBilledReporting(from: Date, to: Date, vendorId?: string, categoryId?: string): {
  report: BilledReporting | undefined
  isLoading: boolean
  isError: boolean
} {
  const { data, isPending, isError } = useQuery({
    queryKey: vendorId || categoryId
      ? ['billed-reporting', localDate(from), localDate(to), vendorId, categoryId]
      : ['billed-reporting', localDate(from), localDate(to)],
    queryFn: () => vendorId || categoryId
      ? getBilledReporting(localDate(from), localDate(to), vendorId, categoryId)
      : getBilledReporting(localDate(from), localDate(to)),
  })
  return { report: data, isLoading: isPending, isError }
}

// Page-level status for the Overview tab ONLY: the queries its own panels render from.
// Billed-reporting and source-statuses are deliberately excluded — their failures are
// scoped to the panels that own them (SpendPage/ReportingPage gates, SourceStatusPanel
// renders nothing), so one optional endpoint failing must not light a global banner
// (worst case: a 403 from /spend/reporting telling a healthy session to sign in again).
export function useDashboardStatus(): { isError: boolean; isLoading: boolean; error: unknown } {
  const range = useMemo(() => dashboardDateRange(), [])
  const from = localDate(range.from)
  const to = localDate(range.to)
  const { isError: aIsError, isPending: aIsPending, error: aError } = useQuery({ queryKey: ['aggregates', from, to], queryFn: () => getAggregates(from, to) })
  const { isError: iIsError, isPending: iIsPending, error: iError } = useQuery({ queryKey: ['insights'], queryFn: getInsights })
  const { isError: sIsError, isPending: sIsPending, error: sError } = useQuery({ queryKey: ['subscriptions'], queryFn: getSubscriptions })
  const states = [
    { isError: aIsError, isPending: aIsPending, error: aError },
    { isError: iIsError, isPending: iIsPending, error: iError },
    { isError: sIsError, isPending: sIsPending, error: sError },
  ]
  return {
    isError: states.some(state => state.isError),
    isLoading: states.some(state => state.isPending),
    error: states.find(state => state.error != null)?.error,
  }
}
