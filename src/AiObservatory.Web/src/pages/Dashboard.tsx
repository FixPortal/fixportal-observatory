import { lazy, Suspense, useRef, useState, type KeyboardEvent } from 'react'
import SummaryCards from '../components/SummaryCards'
import SourceStatusPanel from '../components/SourceStatusPanel'
import ModelBreakdown from '../components/ModelBreakdown'
import InsightsFeed from '../components/InsightsFeed'
import SubscriptionPanel from '../components/SubscriptionPanel'
import AdversarialReviewPanel from '../components/AdversarialReviewPanel'
import CavemanStatsPanel from '../components/CavemanStatsPanel'
import ReportingPage from './ReportingPage'
import ActivityPage from './ActivityPage'
import GitHubPage from './GitHubPage'
import SpendPage from './SpendPage'
import Footer from '../components/Footer'
import { BrandWordmark } from '../design/BrandWordmark'
import { ThemeToggle } from '../design/ThemeToggle'
import { useDashboardStatus } from '../api/queries'
import { ApiError } from '../api/client'
import { authEnabled, isReadonly, signIn, TokenAcquisitionTimeoutError } from '../auth/msal'
import { useTheme } from '../theme/useTheme'

type DashboardTab = 'overview' | 'adversarial-review' | 'reporting' | 'activity' | 'github' | 'spend'

const TABS: { id: DashboardTab; label: string; readonlyHidden?: boolean }[] = [
  { id: 'overview', label: 'Overview' },
  { id: 'adversarial-review', label: 'Adversarial Review' },
  { id: 'reporting', label: 'Reporting' },
  { id: 'activity', label: 'Activity', readonlyHidden: true },
  { id: 'github', label: 'GitHub', readonlyHidden: true },
  { id: 'spend', label: 'Spend', readonlyHidden: true },
]

// recharts is heavy and the charts sit below the fold — code-split them so the
// initial payload (nav + summary cards) paints without the chart library.
const SpendChart = lazy(() => import('../components/SpendChart'))
const ProviderSplit = lazy(() => import('../components/ProviderSplit'))

function ErrorBanner({ error }: { error: unknown }) {
  const isAuthError = error instanceof TokenAcquisitionTimeoutError
    || (error instanceof ApiError && (error.status === 401 || error.status === 403))
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
    <div className="error-banner" role="alert">
      Couldn’t reach the API — data may be unavailable. Check the API service and try refreshing.
    </div>
  )
}

export default function Dashboard() {
  const { isError, isLoading, error } = useDashboardStatus()
  const { mode, setMode } = useTheme()
  const [tab, setTab] = useState<DashboardTab>('overview')
  const visibleTabs = TABS.filter(t => !(t.readonlyHidden && isReadonly))
  const tabRefs = useRef<(HTMLButtonElement | null)[]>([])

  // Tablist arrow-key navigation with roving tabIndex (only the selected tab is in the
  // tab order; arrows move focus + selection between the rest).
  const onTabKeyDown = (e: KeyboardEvent, index: number) => {
    let next: number | null = null
    if (e.key === 'ArrowRight' || e.key === 'ArrowDown') next = (index + 1) % visibleTabs.length
    else if (e.key === 'ArrowLeft' || e.key === 'ArrowUp') next = (index - 1 + visibleTabs.length) % visibleTabs.length
    if (next === null) return
    e.preventDefault()
    setTab(visibleTabs[next].id)
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
            ref={el => { tabRefs.current[i] = el }}
            type="button"
            role="tab"
            id={`dashboard-tab-${t.id}`}
            aria-selected={tab === t.id}
            aria-controls="dashboard-tabpanel"
            tabIndex={tab === t.id ? 0 : -1}
            className={`page-nav__tab${tab === t.id ? ' page-nav__tab--active' : ''}`}
            onClick={() => setTab(t.id)}
            onKeyDown={e => onTabKeyDown(e, i)}
          >
            {t.label}
          </button>
        ))}
      </div>
      <main className="dashboard__main">
      <div id="dashboard-tabpanel" className="dashboard__tabpanel" role="tabpanel" aria-labelledby={`dashboard-tab-${tab}`}>
        {isError && <ErrorBanner error={error} />}
        {!isError && isLoading && (
          <output className="loading-banner" aria-live="polite">
            <span className="loading-banner__spinner" aria-hidden="true" />
            Loading data — the API may take a moment to wake up on first load…
          </output>
        )}
        {tab === 'overview' && (
          <>
            <SummaryCards />
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
      </div>
      </main>
      <Footer />
    </div>
  )
}
