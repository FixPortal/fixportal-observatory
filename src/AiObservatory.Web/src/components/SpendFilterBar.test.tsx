import { describe, it, expect, vi } from 'vitest'
import { render, screen, fireEvent, within } from '@testing-library/react'
import SpendFilterBar from './SpendFilterBar'

const categories = [{ id: 'c1', key: 'credits', displayName: 'Credits', colorVar: '', sortOrder: 1, archivedAt: null }]
const vendors = [{ id: 'v1', key: 'anthropic', displayName: 'Anthropic', provider: 'anthropic', defaultCategoryId: 'c1', archivedAt: null }]

function renderBar(canEdit: boolean, onManageCatalog = vi.fn(), onAddEntry = vi.fn()) {
  return render(
    <SpendFilterBar
      categories={categories}
      vendors={vendors}
      onCategoryChange={vi.fn()}
      onVendorChange={vi.fn()}
      onAddEntry={onAddEntry}
      onManageCatalog={onManageCatalog}
      rangeLabel="29 Jul 2026 – 28 Aug 2026"
      canEdit={canEdit}
    />,
  )
}

describe('SpendFilterBar', () => {
  it('exposes the page-wide controls as a named filter row', () => {
    renderBar(false)

    expect(screen.getByRole('group', { name: /spend filters/i })).toBeInTheDocument()
  })

  it('shows a "Manage catalog" button beside "Add entry" when the viewer can edit', () => {
    renderBar(true)
    const actions = screen.getByRole('group', { name: /spend actions/i })
    expect(within(actions).getAllByRole('button').map(button => button.textContent))
      .toEqual(['Manage catalog', 'Add entry'])
  })

  it('calls onManageCatalog when clicked', () => {
    const onManageCatalog = vi.fn()
    renderBar(true, onManageCatalog)
    fireEvent.click(screen.getByRole('button', { name: /manage catalog/i }))
    expect(onManageCatalog).toHaveBeenCalledTimes(1)
  })

  it('hides both edit actions for a read-only viewer', () => {
    renderBar(false)
    expect(screen.queryByRole('button', { name: /add entry/i })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /manage catalog/i })).not.toBeInTheDocument()
  })
})
