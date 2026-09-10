import { render, screen } from '@testing-library/react'
import { beforeEach, expect, test, vi } from 'vitest'
import Dashboard from './Dashboard'

const dashboardStatus = vi.hoisted(() => ({ isError: false, isLoading: false, error: null as unknown }))
const authMock = vi.hoisted(() => ({
  TokenAcquisitionTimeoutError: class extends Error {},
  signIn: vi.fn(),
}))

vi.mock('../api/queries', () => ({ useDashboardStatus: () => dashboardStatus }))
vi.mock('../theme/useTheme', () => ({ useTheme: () => ({ mode: 'dark', setMode: vi.fn() }) }))
vi.mock('../auth/msal', () => ({ authEnabled: true, isReadonly: false, ...authMock }))
vi.mock('../components/SummaryCards', () => ({ default: () => <section aria-label="Summary evidence">Summary</section> }))
vi.mock('../components/SourceStatusPanel', () => ({ default: () => <div className="source-status-zone"><section aria-label="Source freshness">Sources</section></div> }))
vi.mock('../components/CavemanStatsPanel', () => ({ default: () => <section aria-label="Caveman statistics" /> }))
vi.mock('../components/SubscriptionPanel', () => ({ default: () => <section aria-label="Subscriptions" /> }))
vi.mock('../components/ModelBreakdown', () => ({ default: () => null }))
vi.mock('../components/InsightsFeed', () => ({ default: () => null }))
vi.mock('../components/SpendChart', () => ({ default: () => null }))
vi.mock('../components/ProviderSplit', () => ({ default: () => null }))

beforeEach(() => {
  dashboardStatus.isError = false
  dashboardStatus.isLoading = false
  dashboardStatus.error = null
  authMock.signIn.mockClear()
})

test('offers sign-in recovery when token acquisition stalls', () => {
  dashboardStatus.isError = true
  dashboardStatus.error = new authMock.TokenAcquisitionTimeoutError()

  render(<Dashboard />)

  expect(screen.getByText(/session has expired or you’re not authorised/i)).toBeInTheDocument()
  expect(screen.getByRole('button', { name: 'Sign in again' })).toBeInTheDocument()
})

test('keeps source freshness out of the focal overview and communicates usage-only chart truth', async () => {
  render(<Dashboard />)
  const summary = screen.getByRole('region', { name: 'Summary evidence' })
  const sources = await screen.findByRole('region', { name: 'Source freshness' })
  expect(summary.compareDocumentPosition(sources) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
  expect(summary.nextElementSibling).not.toContainElement(sources)
  expect(screen.getByText('Usage value · last 31 days')).toBeInTheDocument()
  expect(screen.getByText('Provider usage share')).toBeInTheDocument()
})

test('places data-source operations after the overview evidence', async () => {
  render(<Dashboard />)
  const sources = await screen.findByRole('region', { name: 'Source freshness' })
  const usageGrid = screen.getByText('Usage value · last 31 days').closest('.main-grid')!
  const caveman = screen.getByRole('region', { name: 'Caveman statistics' }).closest('.collapsible-panel-zone')!
  const subscriptions = screen.getByRole('region', { name: 'Subscriptions' })
  const bottomGrid = screen.getByText('Model breakdown').closest('.bottom-grid')!
  const sourceZone = sources.closest('.source-status-zone')!

  expect(usageGrid.nextElementSibling).toBe(caveman)
  expect(caveman.nextElementSibling).toBe(subscriptions)
  expect(bottomGrid.nextElementSibling).toBe(sourceZone)
})
