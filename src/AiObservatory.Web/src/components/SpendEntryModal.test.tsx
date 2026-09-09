import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen, fireEvent, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import SpendEntryModal from './SpendEntryModal'
// eslint-disable-next-line sonarjs/no-wildcard-import -- vi.spyOn requires the live module namespace
import * as client from '../api/client'

const categories = [{ id: 'c1', key: 'credits', displayName: 'Credits', colorVar: '--c', sortOrder: 1, archivedAt: null }]
const vendors = [{ id: 'v1', key: 'anthropic', displayName: 'Anthropic', provider: 'anthropic', defaultCategoryId: 'c1', archivedAt: null }]

// Computed relative to "now", like SpendPage's own 90-day window, so the default
// occurredOn value (today) always falls inside it regardless of when the suite runs.
const to = new Date()
const from = new Date(to.getTime() - 90 * 86_400_000)

function renderModal(onClose = vi.fn()) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={qc}>
      <SpendEntryModal categories={categories} vendors={vendors} from={from} to={to} onClose={onClose} />
    </QueryClientProvider>,
  )
}

describe('SpendEntryModal', () => {
  beforeEach(() => vi.restoreAllMocks())

  it('posts an array of one, with source manual and no entry key', async () => {
    const post = vi.spyOn(client, 'postSpendEntries')
      .mockResolvedValue([{ id: 'e1', status: 'created', reason: null }])
    renderModal()

    fireEvent.change(screen.getByLabelText(/amount/i), { target: { value: '80' } })
    fireEvent.click(screen.getByRole('button', { name: /save/i }))

    await waitFor(() => expect(post).toHaveBeenCalledTimes(1))
    const [payload] = post.mock.calls[0]
    expect(payload).toHaveLength(1)
    expect(payload[0].source).toBe('manual')
    expect(payload[0].entryKey).toBeNull()
  })

  it('defaults to Charge and posts a positive amount', async () => {
    const post = vi.spyOn(client, 'postSpendEntries')
      .mockResolvedValue([{ id: 'e1', status: 'created', reason: null }])
    renderModal()

    expect(screen.getByRole('radio', { name: /charge/i })).toBeChecked()

    fireEvent.change(screen.getByLabelText(/amount/i), { target: { value: '80' } })
    fireEvent.click(screen.getByRole('button', { name: /save/i }))

    await waitFor(() => expect(post).toHaveBeenCalledTimes(1))
    expect(post.mock.calls[0][0][0].amount).toBe(80)
  })

  it('negates the amount when Refund is selected', async () => {
    const post = vi.spyOn(client, 'postSpendEntries')
      .mockResolvedValue([{ id: 'e1', status: 'created', reason: null }])
    renderModal()

    fireEvent.change(screen.getByLabelText(/amount/i), { target: { value: '286.16' } })
    fireEvent.click(screen.getByRole('radio', { name: /refund/i }))
    fireEvent.click(screen.getByRole('button', { name: /save/i }))

    await waitFor(() => expect(post).toHaveBeenCalledTimes(1))
    // Signed amount is how a refund nets off the total — see SpendEntry.Amount.
    expect(post.mock.calls[0][0][0].amount).toBeCloseTo(-286.16)
  })

  it('refuses a typed negative amount — the toggle is the only way to book a refund', async () => {
    const post = vi.spyOn(client, 'postSpendEntries')
    renderModal()

    fireEvent.change(screen.getByLabelText(/amount/i), { target: { value: '-5' } })
    fireEvent.click(screen.getByRole('button', { name: /save/i }))

    await waitFor(() => expect(screen.getByRole('alert')).toBeInTheDocument())
    expect(post).not.toHaveBeenCalled()
  })

  it('refuses a zero amount in either direction', async () => {
    const post = vi.spyOn(client, 'postSpendEntries')
    renderModal()

    fireEvent.change(screen.getByLabelText(/amount/i), { target: { value: '0' } })
    fireEvent.click(screen.getByRole('radio', { name: /refund/i }))
    fireEvent.click(screen.getByRole('button', { name: /save/i }))

    await waitFor(() => expect(screen.getByRole('alert')).toBeInTheDocument())
    expect(post).not.toHaveBeenCalled()
  })

  it('refuses to save a date outside the visible window', async () => {
    const post = vi.spyOn(client, 'postSpendEntries')
    renderModal()

    fireEvent.change(screen.getByLabelText(/amount/i), { target: { value: '80' } })
    // Well before `from` -- there is no picker to widen the view and find this row
    // again until phase 2, so an out-of-range save must never reach the server.
    fireEvent.change(screen.getByLabelText(/date/i), { target: { value: '2000-01-01' } })
    fireEvent.click(screen.getByRole('button', { name: /save/i }))

    await waitFor(() => expect(screen.getByRole('alert')).toBeInTheDocument())
    expect(post).not.toHaveBeenCalled()
  })

  it('submits an untouched form when "today" is later than the visible window', async () => {
    // No fake clock needed: bounds dated entirely in the past make the real "today"
    // fall after maxDate, which is exactly what happens if the dashboard is left open
    // past midnight -- the default date must still clamp into range and submit.
    const post = vi.spyOn(client, 'postSpendEntries')
      .mockResolvedValue([{ id: 'e1', status: 'created', reason: null }])
    const pastTo = new Date(Date.now() - 10 * 86_400_000)
    const pastFrom = new Date(pastTo.getTime() - 90 * 86_400_000)
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    render(
      <QueryClientProvider client={qc}>
        <SpendEntryModal categories={categories} vendors={vendors} from={pastFrom} to={pastTo} onClose={vi.fn()} />
      </QueryClientProvider>,
    )

    fireEvent.change(screen.getByLabelText(/amount/i), { target: { value: '80' } })
    fireEvent.click(screen.getByRole('button', { name: /save/i }))

    await waitFor(() => expect(post).toHaveBeenCalledTimes(1))
  })

  it('ignores a vendor default category that is not in the live list (archived)', () => {
    // The archived id has no matching <option>: without a membership check the select
    // displays the first live category while state silently holds the archived one.
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    render(
      <QueryClientProvider client={qc}>
        <SpendEntryModal
          categories={categories}
          vendors={[{ ...vendors[0], defaultCategoryId: 'archived-cat' }]}
          from={from} to={to} onClose={vi.fn()}
        />
      </QueryClientProvider>,
    )

    expect(screen.getByLabelText(/category/i)).toHaveValue('c1')
  })

  const twoCategories = [
    categories[0],
    { id: 'c2', key: 'subscription', displayName: 'Subscription', colorVar: '--c', sortOrder: 2, archivedAt: null },
  ]
  const twoVendors = [
    vendors[0],
    { id: 'v2', key: 'openai', displayName: 'OpenAI', provider: 'openai', defaultCategoryId: 'archived-cat', archivedAt: null },
  ]

  function renderTwoVendorModal(vendorList = twoVendors) {
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    return render(
      <QueryClientProvider client={qc}>
        <SpendEntryModal categories={twoCategories} vendors={vendorList} from={from} to={to} onClose={vi.fn()} />
      </QueryClientProvider>,
    )
  }

  it('keeps the chosen category when the new vendor’s default is archived', () => {
    // Mid-form the first-live-category fallback is wrong: switching to a vendor whose
    // default is archived must not clobber the category the user already picked.
    renderTwoVendorModal()

    fireEvent.change(screen.getByLabelText(/category/i), { target: { value: 'c2' } })
    fireEvent.change(screen.getByLabelText(/vendor/i), { target: { value: 'v2' } })

    expect(screen.getByLabelText(/category/i)).toHaveValue('c2')
  })

  it('applies the new vendor’s default on vendor change when it is live', () => {
    renderTwoVendorModal(twoVendors.map(v => v.id === 'v2' ? { ...v, defaultCategoryId: 'c2' } : v))

    expect(screen.getByLabelText(/category/i)).toHaveValue('c1')
    fireEvent.change(screen.getByLabelText(/vendor/i), { target: { value: 'v2' } })

    expect(screen.getByLabelText(/category/i)).toHaveValue('c2')
  })

  it('surfaces a rejected verdict instead of closing', async () => {
    vi.spyOn(client, 'postSpendEntries')
      .mockResolvedValue([{ id: null, status: 'rejected', reason: 'Unknown VendorId' }])
    const onClose = vi.fn()
    renderModal(onClose)

    fireEvent.change(screen.getByLabelText(/amount/i), { target: { value: '80' } })
    fireEvent.click(screen.getByRole('button', { name: /save/i }))

    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('Unknown VendorId'))
    expect(onClose).not.toHaveBeenCalled()
  })
})
