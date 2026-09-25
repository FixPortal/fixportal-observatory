import { render, screen } from '@testing-library/react'
import { beforeEach, expect, test, vi } from 'vitest'
import Dashboard from './Dashboard'

// Separate file from Dashboard.test.tsx because isReadonly is a module-level
// const from ../auth/msal, and vi.mock is hoisted per file — Dashboard.test.tsx
// permanently mocks it false, so the readonly-hiding branch (Dashboard.tsx:63)
// never rendered in any test. This file exercises the true branch: a viewer-key
// session must not see the tabs that gate the billed ledger.
const dashboardStatus = vi.hoisted(() => ({ isError: false, isLoading: false, error: null as unknown }))
const authMock = vi.hoisted(() => ({
  TokenAcquisitionTimeoutError: class extends Error {},
  signIn: vi.fn(),
}))

vi.mock('../api/queries', () => ({ useDashboardStatus: () => dashboardStatus }))
vi.mock('../theme/useTheme', () => ({ useTheme: () => ({ mode: 'dark', setMode: vi.fn() }) }))
vi.mock('../auth/msal', () => ({ authEnabled: true, isReadonly: true, ...authMock }))
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

test('a readonly viewer-key session cannot reach the Activity, GitHub or Spend tabs', () => {
  render(<Dashboard />)

  expect(screen.getByRole('tab', { name: 'Overview' })).toBeInTheDocument()
  expect(screen.getByRole('tab', { name: 'Reporting' })).toBeInTheDocument()
  expect(screen.queryByRole('tab', { name: 'Activity' })).not.toBeInTheDocument()
  expect(screen.queryByRole('tab', { name: 'GitHub' })).not.toBeInTheDocument()
  expect(screen.queryByRole('tab', { name: 'Spend' })).not.toBeInTheDocument()
})
