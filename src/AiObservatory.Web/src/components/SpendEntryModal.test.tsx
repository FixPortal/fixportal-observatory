import { systemClock } from '../lib/clock'
import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen, fireEvent, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { SpendEntryModal } from './SpendEntryModal'
import * as client from '../api/client'

const categories = [
  {
    id: 'c1',
    key: 'credits',
    displayName: 'Credits',
    colorVar: '--c',
    sortOrder: 1,
    archivedAt: null,
  },
]
const vendors = [
  {
    id: 'v1',
    key: 'anthropic',
    displayName: 'Anthropic',
    provider: 'anthropic',
    defaultCategoryId: 'c1',
    archivedAt: null,
  },
]

// Computed relative to "now", like SpendPage's own 90-day window, so the default
// occurredOn value (today) always falls inside it regardless of when the suite runs.
const to = systemClock.now()
const from = new Date(to.getTime() - 90 * 86_400_000)

function renderModal(onClose = vi.fn()) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={qc}>
      <SpendEntryModal
        categories={categories}
        vendors={vendors}
        from={from}
        to={to}
        onClose={onClose}
      />
    </QueryClientProvider>,
  )
}

describe('SpendEntryModal', () => {
  beforeEach(() => vi.restoreAllMocks())

  it('posts an array of one, with source manual and no entry key', async () => {
    const post = vi
      .spyOn(client, 'postSpendEntries')
      .mockResolvedValue([{ id: 'e1', status: 'created', reason: null }])
    renderModal()

    fireEvent.change(screen.getByLabelText(/amount/i), { target: { value: '80' } })
    fireEvent.click(screen.getByRole('button', { name: /save/i }))

    await waitFor(() => {
      expect(post).toHaveBeenCalledTimes(1)
    })
    const [payload] = post.mock.calls[0]!
    expect(payload).toHaveLength(1)
    expect(payload[0]!.source).toBe('manual')
    expect(payload[0]!.entryKey).toBeNull()
  })

  it('defaults to Charge and posts a positive amount', async () => {
    const post = vi
      .spyOn(client, 'postSpendEntries')
      .mockResolvedValue([{ id: 'e1', status: 'created', reason: null }])
    renderModal()

    expect(screen.getByRole('radio', { name: /charge/i })).toBeChecked()

    fireEvent.change(screen.getByLabelText(/amount/i), { target: { value: '80' } })
    fireEvent.click(screen.getByRole('button', { name: /save/i }))

    await waitFor(() => {
      expect(post).toHaveBeenCalledTimes(1)
    })
    expect(post.mock.calls[0]![0][0]!.amount).toBe(80)
  })

  it('negates the amount when Refund is selected', async () => {
    const post = vi
      .spyOn(client, 'postSpendEntries')
      .mockResolvedValue([{ id: 'e1', status: 'created', reason: null }])
    renderModal()

    fireEvent.change(screen.getByLabelText(/amount/i), { target: { value: '286.16' } })
    fireEvent.click(screen.getByRole('radio', { name: /refund/i }))
    fireEvent.click(screen.getByRole('button', { name: /save/i }))

    await waitFor(() => {
      expect(post).toHaveBeenCalledTimes(1)
    })
    // Signed amount is how a refund nets off the total — see SpendEntry.Amount.
    expect(post.mock.calls[0]![0][0]!.amount).toBeCloseTo(-286.16)
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
    const post = vi
      .spyOn(client, 'postSpendEntries')
      .mockResolvedValue([{ id: 'e1', status: 'created', reason: null }])
    const pastTo = new Date(systemClock.now().getTime() - 10 * 86_400_000)
    const pastFrom = new Date(pastTo.getTime() - 90 * 86_400_000)
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    render(
      <QueryClientProvider client={qc}>
        <SpendEntryModal
          categories={categories}
          vendors={vendors}
          from={pastFrom}
          to={pastTo}
          onClose={vi.fn()}
        />
      </QueryClientProvider>,
    )

    fireEvent.change(screen.getByLabelText(/amount/i), { target: { value: '80' } })
    fireEvent.click(screen.getByRole('button', { name: /save/i }))

    await waitFor(() => {
      expect(post).toHaveBeenCalledTimes(1)
    })
  })

  it('ignores a vendor default category that is not in the live list (archived)', () => {
    // The archived id has no matching <option>: without a membership check the select
    // displays the first live category while state silently holds the archived one.
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    render(
      <QueryClientProvider client={qc}>
        <SpendEntryModal
          categories={categories}
          vendors={[{ ...vendors[0]!, defaultCategoryId: 'archived-cat' }]}
          from={from}
          to={to}
          onClose={vi.fn()}
        />
      </QueryClientProvider>,
    )

    expect(screen.getByLabelText(/category/i)).toHaveValue('c1')
  })

  const twoCategories = [
    categories[0]!,
    {
      id: 'c2',
      key: 'subscription',
      displayName: 'Subscription',
      colorVar: '--c',
      sortOrder: 2,
      archivedAt: null,
    },
  ]
  const twoVendors = [
    vendors[0]!,
    {
      id: 'v2',
      key: 'openai',
      displayName: 'OpenAI',
      provider: 'openai',
      defaultCategoryId: 'archived-cat',
      archivedAt: null,
    },
  ]

  function renderTwoVendorModal(vendorList = twoVendors) {
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    return render(
      <QueryClientProvider client={qc}>
        <SpendEntryModal
          categories={twoCategories}
          vendors={vendorList}
          from={from}
          to={to}
          onClose={vi.fn()}
        />
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
    renderTwoVendorModal(
      twoVendors.map((v) => (v.id === 'v2' ? { ...v, defaultCategoryId: 'c2' } : v)),
    )

    expect(screen.getByLabelText(/category/i)).toHaveValue('c1')
    fireEvent.change(screen.getByLabelText(/vendor/i), { target: { value: 'v2' } })

    expect(screen.getByLabelText(/category/i)).toHaveValue('c2')
  })

  it('surfaces a rejected verdict instead of closing', async () => {
    vi.spyOn(client, 'postSpendEntries').mockResolvedValue([
      { id: null, status: 'rejected', reason: 'Unknown VendorId' },
    ])
    const onClose = vi.fn()
    renderModal(onClose)

    fireEvent.change(screen.getByLabelText(/amount/i), { target: { value: '80' } })
    fireEvent.click(screen.getByRole('button', { name: /save/i }))

    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('Unknown VendorId'))
    expect(onClose).not.toHaveBeenCalled()
  })
})

describe('SpendEntryModal editing an existing entry', () => {
  beforeEach(() => vi.restoreAllMocks())

  const existing: client.SpendEntry = {
    id: 'e1',
    occurredOn: localIso(from, 5),
    vendorId: 'v1',
    categoryId: 'c1',
    amount: -50,
    currency: 'GBP',
    amountGbp: -50,
    fxRate: 1,
    description: 'Top-up refund',
    source: 'manual',
    entryKey: null,
    recordedAt: '2026-07-01T00:00:00Z',
  }

  function localIso(base: Date, plusDays: number) {
    const d = new Date(base.getTime() + plusDays * 86_400_000)
    return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
  }

  function renderEdit(onClose = vi.fn(), entry = existing) {
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    return render(
      <QueryClientProvider client={qc}>
        <SpendEntryModal
          entry={entry}
          categories={categories}
          vendors={vendors}
          from={from}
          to={to}
          onClose={onClose}
        />
      </QueryClientProvider>,
    )
  }

  it('opens as an edit form filled from the entry, with a refund shown as a positive amount', () => {
    renderEdit()

    expect(screen.getByText('Edit spend entry')).toBeInTheDocument()
    expect(screen.getByLabelText(/amount/i)).toHaveValue(50)
    expect(screen.getByRole('radio', { name: /refund/i })).toBeChecked()
    expect(screen.getByLabelText(/description/i)).toHaveValue('Top-up refund')
    expect(screen.getByLabelText(/date/i)).toHaveValue(existing.occurredOn)
  })

  it('patches only the fields that changed and never posts a new entry', async () => {
    const patch = vi.spyOn(client, 'patchSpendEntry').mockResolvedValue(existing)
    const post = vi.spyOn(client, 'postSpendEntries')
    const onClose = vi.fn()
    renderEdit(onClose)

    fireEvent.change(screen.getByLabelText(/amount/i), { target: { value: '75' } })
    fireEvent.click(screen.getByRole('button', { name: /save/i }))

    await waitFor(() => {
      expect(patch).toHaveBeenCalledTimes(1)
    })
    // Still a refund, so the signed amount stays negative.
    expect(patch).toHaveBeenCalledWith('e1', { amount: -75 })
    expect(post).not.toHaveBeenCalled()
    await waitFor(() => {
      expect(onClose).toHaveBeenCalled()
    })
  })

  it('clears the description by sending an empty string', async () => {
    const patch = vi.spyOn(client, 'patchSpendEntry').mockResolvedValue(existing)
    renderEdit()

    fireEvent.change(screen.getByLabelText(/description/i), { target: { value: '   ' } })
    fireEvent.click(screen.getByRole('button', { name: /save/i }))

    await waitFor(() => {
      expect(patch).toHaveBeenCalledWith('e1', { description: '' })
    })
  })

  it('flipping Refund to Charge sends the positive amount', async () => {
    const patch = vi.spyOn(client, 'patchSpendEntry').mockResolvedValue(existing)
    renderEdit()

    fireEvent.click(screen.getByRole('radio', { name: /charge/i }))
    fireEvent.click(screen.getByRole('button', { name: /save/i }))

    await waitFor(() => {
      expect(patch).toHaveBeenCalledWith('e1', { amount: 50 })
    })
  })

  it('closes without a request when nothing changed', async () => {
    const patch = vi.spyOn(client, 'patchSpendEntry')
    const onClose = vi.fn()
    renderEdit(onClose)

    fireEvent.click(screen.getByRole('button', { name: /save/i }))

    await waitFor(() => {
      expect(onClose).toHaveBeenCalled()
    })
    expect(patch).not.toHaveBeenCalled()
  })

  it('does not rewrite the category when the vendor is changed', () => {
    const twoVendors = [
      ...vendors,
      {
        id: 'v2',
        key: 'openai',
        displayName: 'OpenAI',
        provider: 'openai',
        defaultCategoryId: 'c2',
        archivedAt: null,
      },
    ]
    const twoCategories = [
      ...categories,
      {
        id: 'c2',
        key: 'subscription',
        displayName: 'Subscription',
        colorVar: '--c',
        sortOrder: 2,
        archivedAt: null,
      },
    ]
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    render(
      <QueryClientProvider client={qc}>
        <SpendEntryModal
          entry={existing}
          categories={twoCategories}
          vendors={twoVendors}
          from={from}
          to={to}
          onClose={vi.fn()}
        />
      </QueryClientProvider>,
    )

    fireEvent.change(screen.getByLabelText(/vendor/i), { target: { value: 'v2' } })

    expect(screen.getByLabelText(/category/i)).toHaveValue('c1')
  })

  it('keeps a currency the picker does not list, so the form still describes the entry', () => {
    renderEdit(vi.fn(), { ...existing, currency: 'EUR' })

    expect(screen.getByLabelText(/currency/i)).toHaveValue('EUR')
  })

  it('shows the server message and stays open when the update fails', async () => {
    vi.spyOn(client, 'patchSpendEntry').mockRejectedValue(new Error('Unknown CategoryId'))
    const onClose = vi.fn()
    renderEdit(onClose)

    fireEvent.change(screen.getByLabelText(/amount/i), { target: { value: '75' } })
    fireEvent.click(screen.getByRole('button', { name: /save/i }))

    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('Unknown CategoryId'))
    expect(onClose).not.toHaveBeenCalled()
  })

  it('lets an entry keep a date that has fallen outside the visible window', async () => {
    const patch = vi.spyOn(client, 'patchSpendEntry').mockResolvedValue(existing)
    renderEdit(vi.fn(), { ...existing, occurredOn: '2000-01-01' })

    fireEvent.change(screen.getByLabelText(/amount/i), { target: { value: '75' } })
    fireEvent.click(screen.getByRole('button', { name: /save/i }))

    await waitFor(() => {
      expect(patch).toHaveBeenCalledWith('e1', { amount: -75 })
    })
  })
})
