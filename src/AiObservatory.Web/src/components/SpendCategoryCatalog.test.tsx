import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen, fireEvent, waitFor, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import SpendCategoryCatalog from './SpendCategoryCatalog'
// eslint-disable-next-line sonarjs/no-wildcard-import -- vi.spyOn requires the live module namespace
import * as client from '../api/client'

const categories = [
  { id: 'c1', key: 'credits', displayName: 'Credits', colorVar: '--provider-anthropic', sortOrder: 1, archivedAt: null },
  { id: 'c2', key: 'subscriptions', displayName: 'Subscriptions', colorVar: '', sortOrder: 2, archivedAt: '2026-06-01T00:00:00Z' },
]

function renderPanel() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={qc}>
      <SpendCategoryCatalog categories={categories} />
    </QueryClientProvider>,
  )
}

describe('SpendCategoryCatalog', () => {
  beforeEach(() => vi.restoreAllMocks())

  it('lists both live and archived categories, visibly distinguishing the archived one', () => {
    renderPanel()
    expect(screen.getByText('Credits')).toBeInTheDocument()
    expect(screen.getByText('Subscriptions')).toBeInTheDocument()
    expect(screen.getByText(/archived/i)).toBeInTheDocument()
  })

  it('does not let the key change even while renaming', () => {
    renderPanel()
    const row = screen.getByText('credits').closest('li')!
    fireEvent.click(within(row).getByRole('button', { name: /^rename credits$/i }))
    // Only the display-name rename input appears in this row -- the key cell stays
    // plain, un-editable text; imports and the portal feed key off it.
    const textboxes = within(row).getAllByRole('textbox')
    expect(textboxes).toHaveLength(1)
    expect(textboxes[0]).toHaveValue('Credits')
  })

  it('creates a category from the form', async () => {
    const create = vi.spyOn(client, 'createSpendCategory').mockResolvedValue({
      id: 'c3', key: 'inference', displayName: 'Inference', colorVar: '', sortOrder: 3, archivedAt: null,
    })
    renderPanel()

    fireEvent.change(screen.getByLabelText(/key/i), { target: { value: 'inference' } })
    fireEvent.change(screen.getByLabelText(/display name/i), { target: { value: 'Inference' } })
    fireEvent.click(screen.getByRole('button', { name: /add category/i }))

    await waitFor(() => expect(create).toHaveBeenCalledTimes(1))
    expect(create).toHaveBeenCalledWith(
      expect.objectContaining({ key: 'inference', displayName: 'Inference', colorVar: null }),
    )
  })

  it('does not expose the internal CSS colour variable to catalog users', () => {
    renderPanel()
    expect(screen.queryByLabelText(/colour variable/i)).not.toBeInTheDocument()
  })

  it('surfaces a 409 duplicate-key failure from the server rather than a generic message', async () => {
    vi.spyOn(client, 'createSpendCategory').mockRejectedValue(
      new client.ApiError(409, 'Category key already exists: credits'),
    )
    renderPanel()

    fireEvent.change(screen.getByLabelText(/key/i), { target: { value: 'credits' } })
    fireEvent.change(screen.getByLabelText(/display name/i), { target: { value: 'Credits' } })
    fireEvent.click(screen.getByRole('button', { name: /add category/i }))

    await waitFor(() =>
      expect(screen.getByRole('alert')).toHaveTextContent('Category key already exists: credits'))
  })

  it('archives a live category', async () => {
    const patch = vi.spyOn(client, 'patchSpendCategory').mockResolvedValue({ ...categories[0], archivedAt: '2026-07-26T00:00:00Z' })
    renderPanel()

    fireEvent.click(screen.getByRole('button', { name: /^archive credits$/i }))

    await waitFor(() => expect(patch).toHaveBeenCalledWith('c1', { archived: true }))
  })

  it('unarchives an archived category', async () => {
    const patch = vi.spyOn(client, 'patchSpendCategory').mockResolvedValue({ ...categories[1], archivedAt: null })
    renderPanel()

    fireEvent.click(screen.getByRole('button', { name: /^unarchive subscriptions$/i }))

    await waitFor(() => expect(patch).toHaveBeenCalledWith('c2', { archived: false }))
  })

  it('renames a category via PATCH DisplayName', async () => {
    const patch = vi.spyOn(client, 'patchSpendCategory').mockResolvedValue({ ...categories[0], displayName: 'AI Credits' })
    renderPanel()

    fireEvent.click(screen.getByRole('button', { name: /^rename credits$/i }))
    const input = screen.getByLabelText(/rename credits/i)
    fireEvent.change(input, { target: { value: 'AI Credits' } })
    fireEvent.click(screen.getByRole('button', { name: /^save$/i }))

    await waitFor(() => expect(patch).toHaveBeenCalledWith('c1', { displayName: 'AI Credits' }))
  })
})
