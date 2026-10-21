import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, expect, test, vi } from 'vitest'
import type { Suggestion } from '../api/client'
import { SuggestionsCard } from './SuggestionsCard'

// Test data mirrors docs/superpowers/contracts/suggestions/get-response.json. The
// confirmation-only row is 'open' here because the card consumes the status=open
// GET; the fixture's snoozed copy cannot reach the UI through that query.
const seatSuggestion: Suggestion = {
  id: '6f0b3c64-1f5e-4a43-9d3b-0a1f4d1f0a01',
  key: 'seat.no-unique-catch:claude-sonnet-5-5+claude-opus-5-5',
  ruleId: 'seat.no-unique-catch',
  subject: 'claude-sonnet-5-5+claude-opus-5-5',
  severity: 'attention',
  title: 'Seat mixed: claude-sonnet-5-5+claude-opus-5-5 added no unique accepted catch in 7 runs',
  status: 'open',
  snoozedUntil: null,
  firstSeenAt: '2026-10-12T00:05:00Z',
  lastSeenAt: '2026-10-19T00:05:00Z',
  evidence: {
    window: { from: '2026-08-21', to: '2026-10-18' },
    sampleSize: 7,
    figures: [
      { label: 'Detailed runs', value: 7, unit: 'runs' },
      { label: 'Unique accepted catches', value: 0, unit: 'findings' },
      { label: 'Corroborated accepted catches', value: 4, unit: 'findings' },
      { label: 'Known cost', value: 31.5, unit: 'USD' },
    ],
    runIds: ['20261018T101500Z', '20261015T180348Z', '20261013T093929Z'],
  },
}

const planSuggestion: Suggestion = {
  id: '6f0b3c64-1f5e-4a43-9d3b-0a1f4d1f0a02',
  key: 'plan.costs-more-than-use:OpenAI',
  ruleId: 'plan.costs-more-than-use',
  subject: 'OpenAI',
  severity: 'attention',
  title: 'OpenAI plans cost more than the usage they covered (0.31 of the monthly price)',
  status: 'open',
  snoozedUntil: null,
  firstSeenAt: '2026-10-19T00:05:00Z',
  lastSeenAt: '2026-10-19T00:05:00Z',
  evidence: {
    window: { from: '2026-09-19', to: '2026-10-18' },
    sampleSize: 80,
    figures: [
      { label: 'Notional usage value', value: 17.2, unit: 'GBP' },
      { label: 'Monthly plan price', value: 55, unit: 'GBP' },
      { label: 'Usage to price ratio', value: 0.31, unit: 'ratio' },
      { label: 'fxRate', value: 0.79, unit: 'GBP per USD' },
      { label: 'fxSource', value: 'live', unit: 'text' },
    ],
    runIds: [],
  },
}

const infoSuggestion: Suggestion = {
  id: '6f0b3c64-1f5e-4a43-9d3b-0a1f4d1f0a03',
  key: 'seat.confirmation-only:gpt-6-astra',
  ruleId: 'seat.confirmation-only',
  subject: 'gpt-6-astra',
  severity: 'info',
  title: "Seat gpt-6-astra's accepted catches were always also raised elsewhere (12 of 12)",
  status: 'open',
  snoozedUntil: null,
  firstSeenAt: '2026-10-05T00:05:00Z',
  lastSeenAt: '2026-10-19T00:05:00Z',
  evidence: {
    window: { from: '2026-08-21', to: '2026-10-18' },
    sampleSize: 12,
    figures: [
      { label: 'Corroborated accepted catches', value: 12, unit: 'findings' },
      { label: 'Unique accepted catches', value: 0, unit: 'findings' },
    ],
    runIds: ['20261018T101500Z'],
  },
}

const data = vi.hoisted(() => ({
  suggestions: [] as Suggestion[],
  notEvaluated: [] as { ruleId: string; reason: string }[],
  isLoading: false,
  isError: false,
}))

vi.mock('../api/useSuggestions', () => ({
  useSuggestions: () => ({
    suggestions: data.suggestions,
    notEvaluated: data.notEvaluated,
    isLoading: data.isLoading,
    isError: data.isError,
  }),
}))

const dismissSuggestion = vi.hoisted(() => vi.fn(() => Promise.resolve()))
const snoozeSuggestion = vi.hoisted(() => vi.fn(() => Promise.resolve()))
vi.mock('../api/client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../api/client')>()),
  dismissSuggestion,
  snoozeSuggestion,
}))

vi.mock('../auth/msal', () => ({ isReadonly: false }))

function renderCard() {
  return render(
    <QueryClientProvider
      client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}
    >
      <SuggestionsCard />
    </QueryClientProvider>,
  )
}

beforeEach(() => {
  data.suggestions = []
  data.notEvaluated = []
  data.isLoading = false
  data.isError = false
  dismissSuggestion.mockClear()
  dismissSuggestion.mockImplementation(() => Promise.resolve())
  snoozeSuggestion.mockClear()
  snoozeSuggestion.mockImplementation(() => Promise.resolve())
})

test('orders open suggestions attention before info, then oldest first, with severity badges', () => {
  data.suggestions = [planSuggestion, infoSuggestion, seatSuggestion]
  renderCard()

  const rows = screen.getAllByRole('article')
  expect(rows.map((row) => row.getAttribute('aria-label'))).toEqual([
    seatSuggestion.title,
    planSuggestion.title,
    infoSuggestion.title,
  ])
  expect(within(rows[0]!).getByText('Attention')).toBeInTheDocument()
  expect(within(rows[1]!).getByText('Attention')).toBeInTheDocument()
  expect(within(rows[2]!).getByText('Info')).toBeInTheDocument()
})

test('expanding a seat row shows figures, window, sample size and run ids as plain text', async () => {
  data.suggestions = [seatSuggestion]
  const user = userEvent.setup()
  renderCard()

  const row = screen.getByRole('article', { name: seatSuggestion.title })
  expect(screen.queryByText('Known cost')).not.toBeInTheDocument()

  await user.click(within(row).getByRole('button', { name: 'Show evidence' }))

  expect(within(row).getByText('Detailed runs')).toBeInTheDocument()
  expect(within(row).getByText('7')).toBeInTheDocument()
  expect(within(row).getByText('Known cost')).toBeInTheDocument()
  expect(within(row).getByText('31.5')).toBeInTheDocument()
  expect(within(row).getByText('Window: 2026-08-21 to 2026-10-18')).toBeInTheDocument()
  expect(within(row).getByText('Sample size: 7')).toBeInTheDocument()
  expect(
    within(row).getByText('Run ids: 20261018T101500Z, 20261015T180348Z, 20261013T093929Z'),
  ).toBeInTheDocument()
  expect(within(row).queryByRole('link')).not.toBeInTheDocument()
})

test('a string figure value prints as is and plan rows omit the run ids', async () => {
  data.suggestions = [planSuggestion]
  const user = userEvent.setup()
  renderCard()

  const row = screen.getByRole('article', { name: planSuggestion.title })
  await user.click(within(row).getByRole('button', { name: 'Show evidence' }))

  expect(within(row).getByText('fxSource')).toBeInTheDocument()
  expect(within(row).getByText('live')).toBeInTheDocument()
  expect(within(row).getByText('fxRate')).toBeInTheDocument()
  expect(within(row).getByText('0.79')).toBeInTheDocument()
  expect(within(row).queryByText(/Run ids:/)).not.toBeInTheDocument()
})

test('dismiss and snooze call the client helpers for the row', async () => {
  data.suggestions = [seatSuggestion]
  const user = userEvent.setup()
  renderCard()

  const row = screen.getByRole('article', { name: seatSuggestion.title })
  await user.click(within(row).getByRole('button', { name: 'Dismiss' }))
  await waitFor(() => {
    // react-query hands mutationFn a second context argument; the helper ignores it
    expect(dismissSuggestion).toHaveBeenCalledExactlyOnceWith(seatSuggestion.id, expect.anything())
  })

  await user.click(within(row).getByRole('button', { name: 'Snooze 7 days' }))
  await waitFor(() => {
    expect(snoozeSuggestion).toHaveBeenCalledExactlyOnceWith(seatSuggestion.id, 7)
  })

  await user.click(within(row).getByRole('button', { name: 'Snooze 30 days' }))
  await waitFor(() => {
    expect(snoozeSuggestion).toHaveBeenCalledTimes(2)
    expect(snoozeSuggestion).toHaveBeenLastCalledWith(seatSuggestion.id, 30)
  })
})

test('a rejected dismiss or snooze refetches the list so a row resolved elsewhere drops out', async () => {
  data.suggestions = [seatSuggestion]
  dismissSuggestion.mockImplementation(() => Promise.reject(new Error('409 resolved')))
  snoozeSuggestion.mockImplementation(() => Promise.reject(new Error('404 gone')))
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const invalidate = vi.spyOn(client, 'invalidateQueries')
  const user = userEvent.setup()
  render(
    <QueryClientProvider client={client}>
      <SuggestionsCard />
    </QueryClientProvider>,
  )

  const row = screen.getByRole('article', { name: seatSuggestion.title })
  await user.click(within(row).getByRole('button', { name: 'Dismiss' }))
  await waitFor(() => {
    expect(invalidate).toHaveBeenCalledTimes(1)
  })
  expect(invalidate).toHaveBeenLastCalledWith({ queryKey: ['suggestions'] })

  await user.click(within(row).getByRole('button', { name: 'Snooze 7 days' }))
  await waitFor(() => {
    expect(invalidate).toHaveBeenCalledTimes(2)
  })
})

test('the not-evaluated footer expands to rule ids with reasons and hides when empty', async () => {
  data.suggestions = [seatSuggestion]
  data.notEvaluated = [
    {
      ruleId: 'seat.high-false-positive',
      reason: 'No seat has 10 settled findings in the window yet',
    },
    {
      ruleId: 'seat.cost-outlier',
      reason: 'Fewer than 3 peer seats have a cost per unique catch',
    },
  ]
  const user = userEvent.setup()
  const view = renderCard()

  expect(screen.getByText('2 rules not evaluated')).toBeInTheDocument()
  expect(screen.queryByText('seat.high-false-positive')).not.toBeVisible()

  await user.click(screen.getByText('2 rules not evaluated'))
  expect(screen.getByText('seat.high-false-positive')).toBeVisible()
  expect(screen.getByText('No seat has 10 settled findings in the window yet')).toBeVisible()
  expect(screen.getByText('seat.cost-outlier')).toBeVisible()
  expect(screen.getByText('Fewer than 3 peer seats have a cost per unique catch')).toBeVisible()

  view.unmount()
  data.notEvaluated = []
  renderCard()
  expect(screen.queryByText(/rules not evaluated/)).not.toBeInTheDocument()
})

test('the not-evaluated footer uses the singular for one rule', () => {
  data.suggestions = [seatSuggestion]
  data.notEvaluated = [{ ruleId: 'seat.cost-outlier', reason: 'Fewer than 3 peers' }]
  renderCard()

  expect(screen.getByText('1 rule not evaluated')).toBeInTheDocument()
  expect(screen.queryByText(/2 rules not evaluated/)).not.toBeInTheDocument()
})

test('shows a loading line while the query is pending', () => {
  data.isLoading = true
  renderCard()

  expect(screen.getByText(/loading suggestions/i)).toBeInTheDocument()
})

test('shows an error line when the query fails', () => {
  data.isError = true
  renderCard()

  expect(screen.getByRole('alert')).toHaveTextContent(/couldn.t load suggestions/i)
})

test('the empty state names no rules and keeps the not-evaluated footer', () => {
  data.suggestions = []
  data.notEvaluated = [
    {
      ruleId: 'seat.high-false-positive',
      reason: 'No seat has 10 settled findings in the window yet',
    },
    {
      ruleId: 'seat.cost-outlier',
      reason: 'Fewer than 3 peer seats have a cost per unique catch',
    },
  ]
  renderCard()

  expect(screen.getByText('No open suggestions.')).toBeInTheDocument()
  expect(screen.queryByText(/added no unique accepted catch/)).not.toBeInTheDocument()
  expect(screen.getByText('2 rules not evaluated')).toBeInTheDocument()
})

// Must remain the last test in this file -- vi.doMock('../auth/msal', ...) below is
// never restored, so a test appended after this one would inherit isReadonly: true.
test('hides every write control for a readonly viewer', async () => {
  vi.doMock('../auth/msal', () => ({ isReadonly: true }))
  vi.resetModules()
  const { SuggestionsCard: ReadonlyCard } = await import('./SuggestionsCard')
  data.suggestions = [seatSuggestion]
  render(
    <QueryClientProvider
      client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}
    >
      <ReadonlyCard />
    </QueryClientProvider>,
  )

  expect(screen.getByRole('article', { name: seatSuggestion.title })).toBeInTheDocument()
  expect(screen.queryByRole('button', { name: 'Dismiss' })).not.toBeInTheDocument()
  expect(screen.queryByRole('button', { name: 'Snooze 7 days' })).not.toBeInTheDocument()
  expect(screen.queryByRole('button', { name: 'Snooze 30 days' })).not.toBeInTheDocument()
})
