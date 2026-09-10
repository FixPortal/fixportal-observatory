import { describe, it, expect, vi } from 'vitest'
import { render, screen, fireEvent } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import SpendCatalogModal from './SpendCatalogModal'

const categories = [{ id: 'c1', key: 'credits', displayName: 'Credits', colorVar: '', sortOrder: 1, archivedAt: null }]
const vendors = [{ id: 'v1', key: 'anthropic', displayName: 'Anthropic', provider: 'anthropic', defaultCategoryId: 'c1', archivedAt: null }]

function renderModal(onClose = vi.fn()) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={qc}>
      <SpendCatalogModal categories={categories} vendors={vendors} onClose={onClose} />
    </QueryClientProvider>,
  )
}

describe('SpendCatalogModal', () => {
  it('has an accessible dialog name', () => {
    renderModal()
    expect(screen.getByRole('dialog', { name: /manage spend catalog/i })).toBeInTheDocument()
  })

  it('shows the category axis by default', () => {
    renderModal()
    expect(screen.getByText('Credits')).toBeInTheDocument()
  })

  it('switches to the vendor axis on tab click', () => {
    renderModal()
    fireEvent.click(screen.getByRole('tab', { name: /vendors/i }))
    // "Anthropic" appears as both the vendor's name and its provider column in this
    // fixture -- either match proves the vendor axis rendered.
    expect(screen.getAllByText('Anthropic').length).toBeGreaterThan(0)
  })

  it('closes via the close button', () => {
    const onClose = vi.fn()
    renderModal(onClose)
    fireEvent.click(screen.getByRole('button', { name: /close/i }))
    expect(onClose).toHaveBeenCalled()
  })
})
