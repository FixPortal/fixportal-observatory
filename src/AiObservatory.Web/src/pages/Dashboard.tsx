import { lazy, Suspense, useEffect, useRef, useState, type KeyboardEvent } from 'react'
import { SummaryCards } from '../components/SummaryCards'
import { SuggestionsCard } from '../components/SuggestionsCard'
import { SourceStatusPanel } from '../components/SourceStatusPanel'
import { ModelBreakdown } from '../components/ModelBreakdown'
import { InsightsFeed } from '../components/InsightsFeed'
import { SubscriptionPanel } from '../components/SubscriptionPanel'
import { AdversarialReviewPanel } from '../components/AdversarialReviewPanel'
import { CavemanStatsPanel } from '../components/CavemanStatsPanel'
import { ReportingPage } from './ReportingPage'
import { ActivityPage } from './ActivityPage'
import { GitHubPage } from './GitHubPage'
import { SpendPage } from './SpendPage'
import { Footer } from '../components/Footer'
import { BrandWordmark } from '../design/BrandWordmark'
import { ThemeToggle } from '../design/ThemeToggle'
import { useDashboardStatus } from '../api/queries'
import { ApiError } from '../api/client'
import { authEnabled, isReadonly, signIn, TokenAcquisitionTimeoutError } from '../auth/msal'
import { useTheme } from '../theme/useTheme'
import { DOCS_ROOT, isDocsPath } from '../docs/docsRoute'

type DashboardTab =
  'overview' | 'adversarial-review' | 'reporting' | 'activity' | 'github' | 'spend' | 'docs'

const TABS: { id: DashboardTab; label: string; readonlyHidden?: boolean }[] = [
  { id: 'overview', label: 'Overview' },
  { id: 'adversarial-review', label: 'Adversarial Review' },
  { id: 'reporting', label: 'Reporting' },
  { id: 'activity', label: 'Activity', readonlyHidden: true },
  { id: 'github', label: 'GitHub', readonlyHidden: true },
  { id: 'spend', label: 'Spend', readonlyHidden: true },
  { id: 'docs', label: 'Docs' },
]

// recharts is heavy and the charts sit below the fold — code-split them so the
// initial payload (nav + summary cards) paints without the chart library.
const DocsPage = lazy(() => import('../docs/DocsPage').then((m) => ({ default: m.DocsPage })))
const SpendChart = lazy(() =>
  import('../components/SpendChart').then((m) => ({ default: m.SpendChart })),
)
const ProviderSplit = lazy(() =>
  import('../components/ProviderSplit').then((m) => ({ default: m.ProviderSplit })),
)

function ErrorBanner({
  error,
  isRetrying,
  isAutomaticallyRetrying,
  onRetry,
}: {
  error: unknown
  isRetrying: boolean
  isAutomaticallyRetrying: boolean
  onRetry: () => Promise<void>
}) {
  const isAuthError =
    error instanceof TokenAcquisitionTimeoutError ||
    (error instanceof ApiError && (error.status === 401 || error.status === 403))
  if (isAuthError) {
    return (
      <div className="error-banner" role="alert">
        Your session has expired or you’re not authorised.{' '}
        {authEnabled && (
          <button type="button" className="error-banner__action" onClick={signIn}>
            Sign in again
          </button>
        )}
      </div>
    )
  }
  return (
    <div className="error-banner">
      <span role="alert">Couldn’t reach the API — data may be unavailable.</span>{' '}
      {isAutomaticallyRetrying && <span role="status">Retrying automatically. </span>}
      <button
        type="button"
        className="error-banner__action"
        disabled={isRetrying}
        onClick={() => {
          void onRetry()
        }}
      >
        {isRetrying ? 'Retrying…' : 'Retry'}
      </button>
    </div>
  )
}

export function Dashboard() {
  const { isError, isLoading, isRetrying, isAutomaticallyRetrying, error, retry } =
    useDashboardStatus()
  const { mode, setMode } = useTheme()
  const [tab, setTab] = useState<DashboardTab>(() =>
    isDocsPath(window.location.pathname) ? 'docs' : 'overview',
  )
  const visibleTabs = TABS.filter((t) => !(t.readonlyHidden && isReadonly))
  const tabRefs = useRef<(HTMLButtonElement | null)[]>([])

  // The docs reader owns /docs/*; every other tab lives at the root path. Back and forward
  // between the two keep the tab in step with the address bar.
  useEffect(() => {
    const onPopState = () => {
      setTab((current) => {
        if (isDocsPath(window.location.pathname)) return 'docs'
        return current === 'docs' ? 'overview' : current
      })
    }
    window.addEventListener('popstate', onPopState)
    return () => {
      window.removeEventListener('popstate', onPopState)
    }
  }, [])

  const selectTab = (next: DashboardTab) => {
    if (next === 'docs' && !isDocsPath(window.location.pathname)) {
      window.history.pushState(null, '', DOCS_ROOT)
    } else if (next !== 'docs' && isDocsPath(window.location.pathname)) {
      window.history.pushState(null, '', '/')
    }
    setTab(next)
  }

  // Tablist arrow-key navigation with roving tabIndex (only the selected tab is in the
  // tab order; arrows move focus + selection between the rest).
  const onTabKeyDown = (e: KeyboardEvent, index: number) => {
    let next: number | null = null
    if (e.key === 'ArrowRight' || e.key === 'ArrowDown') next = (index + 1) % visibleTabs.length
    else if (e.key === 'ArrowLeft' || e.key === 'ArrowUp')
      next = (index - 1 + visibleTabs.length) % visibleTabs.length
    if (next === null) return
    e.preventDefault()
    const tab = visibleTabs[next]
    if (tab !== undefined) selectTab(tab.id)
    tabRefs.current[next]?.focus()
  }

  return (
    <div className="dashboard">
      <header className="app-header">
        <span className="app-header__lockup">
          <BrandWordmark height={48} className="app-header__wordmark" />
          <span className="app-header__descriptor">Observatory</span>
        </span>
        <ThemeToggle value={mode} onChange={setMode} />
      </header>
      <div className="page-nav" role="tablist" aria-label="Dashboard sections">
        {visibleTabs.map((t, i) => (
          <button
            key={t.id}
            ref={(el) => {
              tabRefs.current[i] = el
            }}
            type="button"
            role="tab"
            id={`dashboard-tab-${t.id}`}
            aria-selected={tab === t.id}
            aria-controls="dashboard-tabpanel"
            tabIndex={tab === t.id ? 0 : -1}
            className={`page-nav__tab${tab === t.id ? ' page-nav__tab--active' : ''}`}
            onClick={() => {
              selectTab(t.id)
            }}
            onKeyDown={(e) => {
              onTabKeyDown(e, i)
            }}
          >
            {t.label}
          </button>
        ))}
      </div>
      <main className="dashboard__main">
        <div
          id="dashboard-tabpanel"
          className={`dashboard__tabpanel${tab === 'docs' ? ' dashboard__tabpanel--wide' : ''}`}
          role="tabpanel"
          aria-labelledby={`dashboard-tab-${tab}`}
        >
          {isError && (
            <ErrorBanner
              error={error}
              isRetrying={isRetrying}
              isAutomaticallyRetrying={isAutomaticallyRetrying}
              onRetry={retry}
            />
          )}
          {!isError && isLoading && (
            <output className="loading-banner" aria-live="polite">
              <span className="loading-banner__spinner" aria-hidden="true" />
              Loading data — the API may take a moment to wake up on first load…
            </output>
          )}
          {tab === 'overview' && (
            <>
              <SummaryCards />
              <SuggestionsCard />
              <div className="main-grid">
                <div className="panel">
                  <div className="panel-title">Usage value · last 31 days</div>
                  <Suspense fallback={<div className="chart-skeleton" />}>
                    <SpendChart />
                  </Suspense>
                </div>
                <div className="panel">
                  <div className="panel-title">Provider usage share</div>
                  <Suspense fallback={<div className="chart-skeleton" />}>
                    <ProviderSplit />
                  </Suspense>
                </div>
              </div>
              <div className="collapsible-panel-zone">
                <CavemanStatsPanel />
              </div>
              <SubscriptionPanel />
              <div className="bottom-grid">
                <div className="panel">
                  <div className="panel-title">Model breakdown</div>
                  <ModelBreakdown />
                </div>
                <div className="panel">
                  <div className="panel-title">Insights</div>
                  <InsightsFeed />
                </div>
              </div>
              <SourceStatusPanel />
            </>
          )}
          {tab === 'adversarial-review' && <AdversarialReviewPanel />}
          {tab === 'reporting' && <ReportingPage />}
          {tab === 'activity' && <ActivityPage />}
          {tab === 'github' && <GitHubPage />}
          {tab === 'spend' && <SpendPage />}
          {tab === 'docs' && (
            <Suspense fallback={<div className="chart-skeleton" />}>
              <DocsPage />
            </Suspense>
          )}
        </div>
      </main>
      <Footer />
    </div>
  )
}
