import { fireEvent, render, screen, within } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, test, vi } from 'vitest'
import type { SourceStatusResponse } from '../api/client'
import SourceStatusPanel, { formatLastSuccess, mergeSourceStatuses } from './SourceStatusPanel'

const data = vi.hoisted(() => ({ statuses: [] as SourceStatusResponse[], isError: false, isLoading: false }))
vi.mock('../api/queries', () => ({ useSourceStatuses: () => data }))

const status = (overrides: Partial<SourceStatusResponse>): SourceStatusResponse => ({
  sourceId: 'openai-usage-api', status: 'fresh', isConfigured: true,
  lastAttemptAt: '2026-08-25T11:00:00Z', lastSuccessAt: '2026-08-25T11:00:00Z',
  latestObservationAt: '2026-08-25T10:00:00Z', consecutiveFailureCount: 0, lastError: null,
  ...overrides,
})

beforeEach(() => {
  localStorage.clear()
  data.statuses = []
  data.isError = false
  data.isLoading = false
  vi.useFakeTimers()
  vi.setSystemTime(new Date('2026-08-25T12:00:00Z'))
})

afterEach(() => vi.useRealTimers())

describe('source status helpers', () => {
  test('lets API truth win, synthesizes missing registry capabilities, preserves unknowns, and orders deterministically', () => {
    const rows = mergeSourceStatuses([
      status({ sourceId: 'openai-usage-api', status: 'failing', consecutiveFailureCount: 2 }),
      status({ sourceId: 'z-new-source', status: 'fresh' }),
      status({ sourceId: 'a-new-source', status: 'configured' }),
    ])
    expect(rows.find(row => row.sourceId === 'openai-usage-api')).toMatchObject({ status: 'failing', consecutiveFailureCount: 2 })
    expect(rows.find(row => row.sourceId === 'codex-local')).toMatchObject({ status: 'notConfigured', isConfigured: false, lastSuccessAt: null })
    expect(rows.slice(-2).map(row => row.sourceId)).toEqual(['a-new-source', 'z-new-source'])
  })

  test('formats deterministic relative and absolute success evidence and preserves null', () => {
    expect(formatLastSuccess(null, new Date('2026-08-25T12:00:00Z'))).toBeNull()
    expect(formatLastSuccess('2026-08-25T11:00:00Z', new Date('2026-08-25T12:00:00Z'))).toEqual({
      relative: '1 hour ago', absolute: '25 Aug 2026, 12:00', dateTime: '2026-08-25T11:00:00Z',
    })
  })
})

test('maps all statuses, shows failure evidence, and uses native time/details/link semantics', async () => {
  data.statuses = [
    status({ sourceId: 'anthropic-usage-api', status: 'fresh' }),
    status({ sourceId: 'anthropic-cost-report', status: 'configured' }),
    status({ sourceId: 'claude-code-usage-api', status: 'stale' }),
    status({ sourceId: 'claude-local', status: 'failing', consecutiveFailureCount: 3, lastError: 'Sanitized failure only' }),
    status({ sourceId: 'claude-pricing', status: 'unavailable' }),
  ]
  render(<SourceStatusPanel />)

  for (const label of ['Fresh', 'Configured', 'Stale', 'Failing', 'Unavailable', 'Not connected']) {
    expect(screen.getAllByText(label).length).toBeGreaterThan(0)
  }
  const panelButton = screen.getByRole('button', { name: /Data sources/ })
  expect(panelButton).toHaveAttribute('aria-expanded', 'false')
  expect(screen.getByText('1 reporting · 12 not connected · 3 need attention')).toBeInTheDocument()
  expect(screen.getByText(/Collection health for optional APIs and local telemetry/i)).toBeInTheDocument()
  fireEvent.click(panelButton)
  const failingRow = screen.getByText('Claude local').closest('li')!
  expect(within(failingRow).getByText('3 failures')).toBeInTheDocument()
  const disclosure = failingRow.querySelector('summary')!
  expect(disclosure).toHaveAccessibleName('Show error for Claude local')
  expect(disclosure).toHaveProperty('tabIndex', 0)
  disclosure.focus()
  expect(disclosure).toHaveFocus()
  fireEvent.click(disclosure)
  expect(within(failingRow).getByText('Sanitized failure only')).toBeInTheDocument()
  expect(failingRow.querySelector('time')).toHaveAttribute('datetime', '2026-08-25T11:00:00Z')
  const setup = screen.getByRole('link', { name: 'Setup: Codex local' })
  setup.focus()
  expect(setup).toHaveFocus()
  expect(setup).toHaveAttribute('href', expect.stringContaining('docs/provider-setup.md'))
})

test('shows readable API-only sources without inventing setup links and does not mask query errors', () => {
  data.statuses = [status({ sourceId: 'new-oss-source', status: 'fresh' })]
  const { rerender } = render(<SourceStatusPanel />)
  fireEvent.click(screen.getByRole('button', { name: /Data sources/ }))
  const unknownRow = screen.getByText('New oss source').closest('li')!
  expect(within(unknownRow).queryByRole('link')).not.toBeInTheDocument()

  data.isError = true
  rerender(<SourceStatusPanel />)
  // The panel is the only visible surface for /source-statuses: on failure it must
  // stay rendered as a collapsed shell carrying the error, not vanish silently.
  expect(screen.getByRole('button', { name: /Data sources/ })).toBeInTheDocument()
  expect(screen.getByText('Couldn’t load collection status')).toBeInTheDocument()
})

test('keeps the registry rows in place while source status is loading', () => {
  data.isLoading = true
  render(<SourceStatusPanel />)
  fireEvent.click(screen.getByRole('button', { name: /Data sources/ }))

  const panel = screen.getByRole('region', { name: 'Source freshness' })
  expect(panel).toHaveAttribute('aria-busy', 'true')
  expect(within(panel).getAllByRole('listitem')).toHaveLength(17)
  expect(within(panel).getByText('Repository activity')).toBeInTheDocument()
  expect(within(panel).getByText('GitHub billing')).toBeInTheDocument()
  expect(within(panel).getAllByText('Loading')).toHaveLength(17)
  expect(within(panel).queryByRole('link')).not.toBeInTheDocument()
})

test('renders the collapsed shell with an inline error when source statuses fail to load', () => {
  data.isError = true
  render(<SourceStatusPanel />)

  const panelButton = screen.getByRole('button', { name: /Data sources/ })
  expect(panelButton).toHaveAttribute('aria-expanded', 'false')
  expect(screen.getByText('Couldn’t load collection status')).toBeInTheDocument()

  fireEvent.click(panelButton)
  expect(panelButton).toHaveAttribute('aria-expanded', 'true')
  expect(screen.getByText(/try refreshing/)).toBeInTheDocument()
  expect(screen.queryByRole('listitem')).not.toBeInTheDocument()
})
