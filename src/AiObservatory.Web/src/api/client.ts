// Prod build calls the API cross-origin (VITE_API_BASE); dev uses the Vite proxy.
import { getAccessToken, apiKey, urlApiKey } from '../auth/msal'

const BASE = (import.meta.env.VITE_API_BASE ?? '') + '/api'

/** Carries the HTTP status so the UI can tell auth failures from outages. */
export class ApiError extends Error {
  readonly status: number
  constructor(status: number, message: string) {
    super(message)
    this.name = 'ApiError'
    this.status = status
  }
}

// Auth priority: Entra JWT > URL viewer key > VITE_API_KEY > none (dev).
// Entra: production default for signed-in humans. URL key: read-only share link
// (?key=...) for colleagues without Entra access. VITE_API_KEY: self-host.
async function authHeaders(): Promise<Record<string, string>> {
  const token = await getAccessToken()
  if (token) return { Authorization: `Bearer ${token}` }
  if (urlApiKey) return { 'X-Observatory-Key': urlApiKey }
  if (apiKey) return { 'X-Observatory-Key': apiKey }
  return {}
}

async function request(path: string, init: RequestInit = {}): Promise<Response> {
  const res = await fetch(`${BASE}${path}`, {
    ...init,
    headers: { ...(init.headers as Record<string, string>), ...(await authHeaders()) },
  })
  if (!res.ok) throw new ApiError(res.status, await readErrorMessage(res, path, init.method ?? 'GET'))
  return res
}

// Minimal APIs' Results.BadRequest(object?)/Results.Conflict(object?) JSON-encode whatever
// they're given -- a `string` argument (e.g. Results.Conflict($"Category key already exists:
// {key}") in SpendCatalogEndpoints) goes over the wire as a JSON string, quote characters and
// all, with an `application/json` Content-Type (verified against the running API: a duplicate
// category key came back as `"Category key already exists: <key>"`, quotes included). A raw
// res.text() would leave those quotes in the alert a user sees. A ProblemDetails object (a
// model-binding failure, say) is also a realistic 400 body, so an object gets its
// detail/title picked out rather than stringified wholesale.
async function readErrorMessage(res: Response, path: string, method: string): Promise<string> {
  const generic = `${method} ${path} failed: ${res.status}`
  const bodyText = res.text ? await res.text().catch(() => '') : ''
  if (!bodyText) return generic

  const contentType = res.headers?.get?.('content-type') ?? ''
  if (!contentType.includes('json')) return bodyText

  try {
    const parsed: unknown = JSON.parse(bodyText)
    if (typeof parsed === 'string') return parsed || generic
    if (parsed && typeof parsed === 'object') {
      const problem = parsed as { title?: string; detail?: string }
      return problem.detail ?? problem.title ?? generic
    }
    return generic
  } catch {
    return bodyText
  }
}

export type SourceKind = 'providerApi' | 'localTelemetry' | 'manual' | 'synthetic' | 'legacy' | string
export type UsageScope = 'api' | 'subscription' | 'mixed' | 'unknown' | string
export type CostBasis = 'billed' | 'providerEstimated' | 'listPriceEstimate' | 'notional' | 'none' | 'unknown' | string

export interface DailyAggregate {
  date: string
  provider: string
  model: string
  sourceId: string
  sourceKind: SourceKind
  usageScope: UsageScope
  costBasis: CostBasis
  inputTokens: number
  outputTokens: number
  cacheReadTokens: number
  cacheWriteTokens: number
  cacheWrite1hTokens: number
  costUsd: number
  unknownCostCount: number
  cacheSavingsUsd: number
  unknownCacheSavingsCount: number
  requestCount: number
}

export interface Insight {
  id: string
  generatedAt: string
  insightType: 'summary' | 'efficiency' | 'anomaly' | 'recommendation' | 'budgetalert'
  title: string
  body: string
  data: Record<string, unknown>
  acknowledged: boolean
}

export interface Subscription {
  id: string
  provider: string
  name: string
  costAmount: number
  currency: string
  billingInterval: 'monthly' | 'annual'
  billingMonth: number | null
  billingDay: number
  activeFrom: string        // ISO date yyyy-MM-dd
  activeTo: string | null
  extraUsageCost: number | null
}

async function getJson<T>(path: string, params?: Record<string, string | undefined>): Promise<T> {
  const entries = Object.entries(params ?? {}).filter((e): e is [string, string] => e[1] != null)
  const qs = entries.length ? '?' + new URLSearchParams(entries).toString() : ''
  const res = await request(`${path}${qs}`)
  return res.json() as Promise<T>
}

const jsonHeaders = { 'Content-Type': 'application/json' }

export const getAggregates = (from?: string, to?: string) =>
  getJson<DailyAggregate[]>('/aggregates', { from, to })

export const getInsights = () => getJson<Insight[]>('/insights')

export interface SourceStatusResponse {
  sourceId: string
  status: 'configured' | 'fresh' | 'stale' | 'failing' | 'unavailable' | 'notConfigured' | string
  isConfigured: boolean
  lastAttemptAt: string | null
  lastSuccessAt: string | null
  latestObservationAt: string | null
  consecutiveFailureCount: number
  lastError: string | null
}

export const getSourceStatuses = () => getJson<SourceStatusResponse[]>('/sources/status')

export const getSubscriptions = () => getJson<Subscription[]>('/subscriptions')

export interface DailyActivity {
  date: string
  activeSeconds: number
  wallClockSeconds: number
}

export interface ProjectActivity {
  project: string
  sessionCount: number
  activeSeconds: number
  sharePercent: number
}

export const getActivityDaily = (from?: string, to?: string) =>
  getJson<DailyActivity[]>('/activity/daily', { from, to })

export const getActivityByProject = (from?: string, to?: string) =>
  getJson<ProjectActivity[]>('/activity/by-project', { from, to })

export interface GitHubPr {
  repo: string
  number: number
  title: string
  author: string
  state: 'open' | 'merged' | 'closed'
  createdAt: string
  mergedAt: string | null
  reviewCount: number
  turnaroundHours: number | null
}

export interface GitHubCommitSummary {
  repo: string
  commitCount: number
  additions: number
  deletions: number
}

export interface GitHubReviewerSummary {
  repo: string
  reviewer: string
  isBot: boolean
  reviewCount: number
  pullRequestCount: number
  approvedCount: number
  changesRequestedCount: number
  avgFirstReviewHours: number
}

export interface GitHubCiSummary {
  repo: string
  workflowName: string
  totalRuns: number
  failedRuns: number
  successRate: number
}

export const getGitHubPrs = (from?: string, to?: string) =>
  getJson<GitHubPr[]>('/github/prs', { from, to })

export const getGitHubCommitSummary = (from?: string, to?: string) =>
  getJson<GitHubCommitSummary[]>('/github/commits/summary', { from, to })

export const getGitHubCi = (from?: string, to?: string) =>
  getJson<GitHubCiSummary[]>('/github/ci', { from, to })

export const getGitHubReviews = (from?: string, to?: string) =>
  getJson<GitHubReviewerSummary[]>('/github/reviews', { from, to })

export const acknowledgeInsight = async (id: string): Promise<void> => {
  await request(`/insights/${id}/acknowledge`, { method: 'POST' })
}

export const createSubscription = async (body: Omit<Subscription, 'id'>): Promise<Subscription> => {
  const res = await request('/subscriptions', {
    method: 'POST',
    headers: jsonHeaders,
    body: JSON.stringify(body),
  })
  return res.json() as Promise<Subscription>
}

export const updateSubscription = async (id: string, body: Omit<Subscription, 'id'>): Promise<Subscription> => {
  const res = await request(`/subscriptions/${id}`, {
    method: 'PUT',
    headers: jsonHeaders,
    body: JSON.stringify(body),
  })
  return res.json() as Promise<Subscription>
}

export const patchExtraUsage = async (id: string, amount: number | null): Promise<Subscription> => {
  const res = await request(`/subscriptions/${id}/extra-usage`, {
    method: 'PATCH',
    headers: jsonHeaders,
    body: JSON.stringify({ amount }),
  })
  return res.json() as Promise<Subscription>
}

export const deleteSubscription = async (id: string): Promise<void> => {
  await request(`/subscriptions/${id}`, { method: 'DELETE' })
}

export const explainInsight = async (id: string): Promise<{ explanation: string }> => {
  const res = await request(`/insights/${id}/explain`, { method: 'POST' })
  return res.json() as Promise<{ explanation: string }>
}

export type ReviewRole = 'reviewer' | 'judge'

export interface AdversarialReviewRun {
  id: string
  reviewer: string
  model: string
  role: ReviewRole
  repo: string | null
  summary: string | null
  inputTokens: number
  outputTokens: number
  costUsd: number
  reviewDurationMs: number
  issuesRaised: number
  issuesAccepted: number
  costPerAcceptedFinding: number | null
  chunkCount: number | null
  runId: string
  recordedAt: string
}

export interface AdversarialReviewStats {
  reviewer: string
  model: string
  role: ReviewRole
  runCount: number
  avgCostPerRun: number
  avgIssuesRaised: number
  avgIssuesAccepted: number
  avgCostPerAcceptedFinding: number | null
  avgDurationMs: number
}

export const getAdversarialReviewRuns = () =>
  getJson<AdversarialReviewRun[]>('/adversarial-review/runs')

export const getAdversarialReviewStats = () =>
  getJson<AdversarialReviewStats[]>('/adversarial-review/stats')

export const deleteAdversarialReviewRun = async (runId: string): Promise<void> => {
  await request(`/adversarial-review/runs/${encodeURIComponent(runId)}`, { method: 'DELETE' })
}

export interface CavemanStats {
  sessions: number
  totalOutputTokens: number
  totalEstSavedTokens: number
  totalEstSavedUsd: number
}

export const getCavemanStats = () => getJson<CavemanStats>('/caveman-stats')

export interface BudgetRule {
  id: string
  provider: string | null
  period: 'daily' | 'weekly' | 'monthly'
  thresholdGbp: number
  evaluationStartsOn: string
  lastTriggeredAt: string | null
  currentSpendGbp: number
  windowStart: string
  windowEnd: string
}

export const getBudgetRules = () => getJson<BudgetRule[]>('/budget-rules')

export const createBudgetRule = async (body: Pick<BudgetRule, 'provider' | 'period' | 'thresholdGbp'>): Promise<void> => {
  await request('/budget-rules', { method: 'POST', headers: jsonHeaders, body: JSON.stringify(body) })
}

export const deleteBudgetRule = async (id: string): Promise<void> => {
  await request(`/budget-rules/${id}`, { method: 'DELETE' })
}

export interface NotificationSettings {
  emailConfigured: boolean
  emailMasked: string | null
  slackConfigured: boolean
  slackMasked: string | null
}

export const getNotificationSettings = () => getJson<NotificationSettings>('/notification-settings')

export const updateNotificationSettings = async (
  body: { alertEmailTo?: string | null; slackWebhookUrl?: string | null },
): Promise<NotificationSettings> => {
  const res = await request('/notification-settings', { method: 'PUT', headers: jsonHeaders, body: JSON.stringify(body) })
  return res.json() as Promise<NotificationSettings>
}

export interface SpendCategory {
  id: string
  key: string
  displayName: string
  colorVar: string
  sortOrder: number
  archivedAt: string | null
}

export interface SpendVendor {
  id: string
  key: string
  displayName: string
  provider: string | null      // null for vendors with no token estimate
  defaultCategoryId: string | null
  archivedAt: string | null
}

// The C# enum is serialized through the API's global camelCase JsonStringEnumConverter,
// not the `HasConversion<string>` EF uses to store it — that conversion is DB-only. So the
// wire value is 'manual' / 'csv' / 'portal', matching how Provider already lower-cases
// ('anthropic' etc. above), not the PascalCase SpendSource member names.
export const SPEND_SOURCE_VALUES = ['manual', 'csv', 'portal', 'api'] as const
export type SpendSourceValue = typeof SPEND_SOURCE_VALUES[number]

export interface SpendEntry {
  id: string
  occurredOn: string           // ISO date yyyy-MM-dd
  vendorId: string
  categoryId: string
  amount: number               // as charged; signed — negative is a refund/credit
  currency: string
  amountGbp: number            // frozen at write, signed — sum this, never `amount`
  fxRate: number
  description: string | null
  source: SpendSourceValue
  entryKey: string | null
  recordedAt: string
}

export interface SpendEntryResult {
  id: string | null
  status: 'created' | 'duplicate' | 'rejected'
  reason: string | null
}

export interface NewSpendEntry {
  occurredOn: string
  vendorId: string
  categoryId: string
  amount: number
  currency: string
  description: string | null
  source: SpendSourceValue
  entryKey: string | null
}

// includeArchived defaults to false server-side; pass true so a historical row whose
// category/vendor has since been archived can still resolve a display name (spec §8).
export const getSpendCategories = (includeArchived = false) =>
  getJson<SpendCategory[]>('/spend/categories', { includeArchived: includeArchived ? 'true' : undefined })

export const getSpendVendors = (includeArchived = false) =>
  getJson<SpendVendor[]>('/spend/vendors', { includeArchived: includeArchived ? 'true' : undefined })

export interface NewSpendCategory {
  key: string
  displayName: string
  colorVar: string | null
  sortOrder: number
}

// All fields optional -- PATCH only touches what is present in the body.
export interface SpendCategoryPatch {
  displayName?: string
  colorVar?: string
  sortOrder?: number
  archived?: boolean
}

export const createSpendCategory = async (body: NewSpendCategory): Promise<SpendCategory> => {
  const res = await request('/spend/categories', { method: 'POST', headers: jsonHeaders, body: JSON.stringify(body) })
  return res.json() as Promise<SpendCategory>
}

export const patchSpendCategory = async (id: string, body: SpendCategoryPatch): Promise<SpendCategory> => {
  const res = await request(`/spend/categories/${id}`, { method: 'PATCH', headers: jsonHeaders, body: JSON.stringify(body) })
  return res.json() as Promise<SpendCategory>
}

export interface NewSpendVendor {
  key: string
  displayName: string
  provider: string | null
  defaultCategoryId: string | null
}

export interface SpendVendorPatch {
  displayName?: string
  archived?: boolean
  /**
   * Tri-state, and the distinction is load-bearing: omit the key to leave the vendor's
   * default category untouched, send an explicit `null` to clear it, send a GUID to
   * repoint it. JSON.stringify drops `undefined` properties and keeps `null`, so the
   * three cases survive the wire exactly as the API reads them.
   */
  defaultCategoryId?: string | null
}

export const createSpendVendor = async (body: NewSpendVendor): Promise<SpendVendor> => {
  const res = await request('/spend/vendors', { method: 'POST', headers: jsonHeaders, body: JSON.stringify(body) })
  return res.json() as Promise<SpendVendor>
}

export const patchSpendVendor = async (id: string, body: SpendVendorPatch): Promise<SpendVendor> => {
  const res = await request(`/spend/vendors/${id}`, { method: 'PATCH', headers: jsonHeaders, body: JSON.stringify(body) })
  return res.json() as Promise<SpendVendor>
}

export const getSpendEntries = (from: string, to: string, vendorId?: string, categoryId?: string) =>
  getJson<SpendEntry[]>('/spend/entries', { from, to, vendorId, categoryId })

export interface BilledReporting {
  entryCount: number
  totalGbp: number
  dailyAverageGbp: number
  projectedMonthlyGbp: number
  topVendorName: string | null
  topVendorGbp: number | null
  dailySeries: { date: string; amountGbp: number }[]
  vendorSeries: { vendorId: string; name: string; provider: string | null; amountGbp: number }[]
  categorySeries: { categoryId: string; name: string; amountGbp: number }[]
}

export const getBilledReporting = (from: string, to: string, vendorId?: string, categoryId?: string) =>
  getJson<BilledReporting>('/spend/reporting', { from, to, vendorId, categoryId })

/** Always an array — the manual form sends one. */
export const postSpendEntries = async (entries: NewSpendEntry[]): Promise<SpendEntryResult[]> => {
  const res = await request('/spend/entries', {
    method: 'POST',
    headers: jsonHeaders,
    body: JSON.stringify(entries),
  })
  return res.json() as Promise<SpendEntryResult[]>
}

export const deleteSpendEntry = async (id: string): Promise<void> => {
  await request(`/spend/entries/${id}`, { method: 'DELETE' })
}
