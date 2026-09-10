import { describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen } from '@testing-library/react'
import SpendLedgerTable from './SpendLedgerTable'
import type { SpendCategory, SpendEntry, SpendVendor } from '../api/client'

const liveCategory: SpendCategory = {
  id: 'c-live', key: 'credits', displayName: 'Credits', colorVar: '--c', sortOrder: 1, archivedAt: null,
}
const archivedCategory: SpendCategory = {
  id: 'c-archived', key: 'old-cat', displayName: 'Retired Category', colorVar: '--c', sortOrder: 2,
  archivedAt: '2026-01-01T00:00:00Z',
}
const vendor: SpendVendor = {
  id: 'v1', key: 'anthropic', displayName: 'Anthropic', provider: 'anthropic', defaultCategoryId: 'c-live', archivedAt: null,
}

const entryAgainstArchivedCategory: SpendEntry = {
  id: 'e1', occurredOn: '2026-07-01', vendorId: 'v1', categoryId: 'c-archived',
  amount: 80, currency: 'GBP', amountGbp: 80, fxRate: 1,
  description: 'Top-up', source: 'manual', entryKey: null, recordedAt: '2026-07-01T00:00:00Z',
}

describe('SpendLedgerTable', () => {
  it('resolves an archived category to its display name rather than an em-dash', () => {
    // The category list passed in must include archived rows (SpendPage's
    // useAllSpendCategories) -- otherwise a retired category's historical entries
    // render an em-dash instead of the name they were actually recorded against.
    render(
      <SpendLedgerTable
        entries={[entryAgainstArchivedCategory]}
        categories={[liveCategory, archivedCategory]}
        vendors={[vendor]}
        onDelete={vi.fn()}
        canEdit={false}
      />,
    )

    expect(screen.getByText('Retired Category')).toBeInTheDocument()
    expect(screen.queryByText('—')).not.toBeInTheDocument()
  })

  it('falls back to an em-dash when the category truly is not in the supplied list', () => {
    render(
      <SpendLedgerTable
        entries={[entryAgainstArchivedCategory]}
        categories={[liveCategory]}
        vendors={[vendor]}
        onDelete={vi.fn()}
        canEdit={false}
      />,
    )

    expect(screen.getByText('—')).toBeInTheDocument()
  })

  it('announces an empty filter result to assistive tech', () => {
    render(<SpendLedgerTable entries={[]} categories={[]} vendors={[]} onDelete={vi.fn()} canEdit={false} />)

    expect(screen.getByRole('status')).toHaveTextContent('No spend recorded for this filter.')
  })

  it('names the table for assistive tech', () => {
    render(
      <SpendLedgerTable
        entries={[entryAgainstArchivedCategory]}
        categories={[liveCategory, archivedCategory]}
        vendors={[vendor]}
        onDelete={vi.fn()}
        canEdit={false}
      />,
    )

    expect(screen.getByRole('table', { name: /spend ledger/i })).toBeInTheDocument()
  })

  it('marks a refund row so it cannot be misread as a charge', () => {
    const refund: SpendEntry = {
      ...entryAgainstArchivedCategory, id: 'e2', amount: -30, amountGbp: -30, description: 'Credit',
    }
    render(
      <SpendLedgerTable
        entries={[refund]}
        categories={[liveCategory, archivedCategory]}
        vendors={[vendor]}
        onDelete={vi.fn()}
        canEdit={false}
      />,
    )

    // The minus sign alone is easy to miss in a column of figures, so the row also states
    // it for assistive tech and carries a class the stylesheet colours.
    const cell = screen.getByText(/Refund:/).closest('td')!
    expect(cell).toHaveClass('spend-ledger__num--refund')
    expect(cell).toHaveTextContent('-£30.00')
  })

  it('leaves a charge row unmarked', () => {
    render(
      <SpendLedgerTable
        entries={[entryAgainstArchivedCategory]}
        categories={[liveCategory, archivedCategory]}
        vendors={[vendor]}
        onDelete={vi.fn()}
        canEdit={false}
      />,
    )

    expect(screen.queryByText(/Refund:/)).not.toBeInTheDocument()
  })

  it('disables the delete button while a delete is pending', () => {
    render(
      <SpendLedgerTable
        entries={[entryAgainstArchivedCategory]}
        categories={[liveCategory, archivedCategory]}
        vendors={[vendor]}
        onDelete={vi.fn()}
        canEdit
        isDeleting
      />,
    )

    expect(screen.getByRole('button', { name: /delete entry/i })).toBeDisabled()
  })

  it('renders deletion as a themed destructive action', () => {
    render(
      <SpendLedgerTable
        entries={[entryAgainstArchivedCategory]}
        categories={[liveCategory, archivedCategory]}
        vendors={[vendor]}
        onDelete={vi.fn()}
        canEdit
      />,
    )

    expect(screen.getByRole('button', { name: /delete entry/i })).toHaveClass('fpds-btn--danger')
  })

  it('requires a confirm click before calling onDelete, so a stray click cannot delete a real spend record', () => {
    const onDelete = vi.fn()
    render(
      <SpendLedgerTable
        entries={[entryAgainstArchivedCategory]}
        categories={[liveCategory, archivedCategory]}
        vendors={[vendor]}
        onDelete={onDelete}
        canEdit
      />,
    )

    fireEvent.click(screen.getByRole('button', { name: /delete entry/i }))
    expect(onDelete).not.toHaveBeenCalled()

    fireEvent.click(screen.getByRole('button', { name: /confirm delete entry/i }))
    expect(onDelete).toHaveBeenCalledWith('e1')
  })

  it('cancels the pending delete without calling onDelete', () => {
    const onDelete = vi.fn()
    render(
      <SpendLedgerTable
        entries={[entryAgainstArchivedCategory]}
        categories={[liveCategory, archivedCategory]}
        vendors={[vendor]}
        onDelete={onDelete}
        canEdit
      />,
    )

    fireEvent.click(screen.getByRole('button', { name: /delete entry/i }))
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }))

    expect(onDelete).not.toHaveBeenCalled()
    expect(screen.getByRole('button', { name: /delete entry/i })).toBeInTheDocument()
  })
})
