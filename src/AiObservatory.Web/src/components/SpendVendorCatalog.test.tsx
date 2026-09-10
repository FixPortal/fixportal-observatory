import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen, fireEvent, waitFor, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import SpendVendorCatalog from './SpendVendorCatalog'
// eslint-disable-next-line sonarjs/no-wildcard-import -- vi.spyOn requires the live module namespace
import * as client from '../api/client'

const categories = [
  { id: 'k1', key: 'compute', displayName: 'Compute', colorVar: '', sortOrder: 1, archivedAt: null },
  { id: 'k2', key: 'legacy', displayName: 'Legacy', colorVar: '', sortOrder: 2, archivedAt: '2026-06-01T00:00:00Z' },
]

const vendors = [
  { id: 'v1', key: 'anthropic', displayName: 'Anthropic', provider: 'anthropic', defaultCategoryId: 'k1', archivedAt: null },
  { id: 'v2', key: 'gha', displayName: 'GitHub Actions', provider: null, defaultCategoryId: null, archivedAt: '2026-06-01T00:00:00Z' },
]

function renderPanel() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={qc}>
      <SpendVendorCatalog vendors={vendors} categories={categories} />
    </QueryClientProvider>,
  )
}

describe('SpendVendorCatalog', () => {
  beforeEach(() => vi.restoreAllMocks())

  it('lists both live and archived vendors, visibly distinguishing the archived one', () => {
    renderPanel()
    // "Anthropic" appears more than once here -- the vendor's own name, its
    // provider column (this fixture's vendor shares a name with its provider), and
    // the provider <select>'s own option -- so just prove the row rendered at all.
    expect(screen.getAllByText('Anthropic').length).toBeGreaterThanOrEqual(2)
    expect(screen.getByText('GitHub Actions')).toBeInTheDocument()
    expect(screen.getByText(/archived/i)).toBeInTheDocument()
  })

  it('shows an explicit "no provider" label for a vendor with no token estimate, not a blank cell', () => {
    renderPanel()
    const row = screen.getByText('GitHub Actions').closest('li')!
    expect(within(row).getByText(/no provider/i)).toBeInTheDocument()
  })

  it('renders a set-but-unknown provider slug readably instead of collapsing it into "No provider"', () => {
    // null vs set is load-bearing (SpendVendorPatch tri-state): an unrecognized slug
    // still means the vendor IS provider-mapped.
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    render(
      <QueryClientProvider client={qc}>
        <SpendVendorCatalog
          vendors={[{ id: 'v9', key: 'acme', displayName: 'Acme', provider: 'azure', defaultCategoryId: null, archivedAt: null }]}
          categories={categories}
        />
      </QueryClientProvider>,
    )

    const row = screen.getByText('Acme').closest('li')!
    expect(within(row).queryByText(/no provider/i)).not.toBeInTheDocument()
    expect(within(row).getByText(/azure/i)).toBeInTheDocument()
  })

  it('groups vendor details separately from the fixed action column', () => {
    renderPanel()
    const row = screen.getByText('anthropic').closest('li')!
    const details = row.querySelector('.vendor-catalog__details')

    expect(details).toContainElement(within(row).getByText('Anthropic', { selector: '.sub-list__name' }))
    expect(details).toContainElement(screen.getByLabelText('Default category for Anthropic'))
    expect(row.querySelector('.sub-list__actions--fixed')).not.toBeNull()
  })

  it('offers only live categories as the default-category choice, not the archived one', () => {
    renderPanel()
    // Exact label: the per-row pickers below are labelled "Default category for <vendor>",
    // so a loose regex would now match several selects.
    const select = screen.getByLabelText('Default category') as HTMLSelectElement
    const optionLabels = Array.from(select.options).map(o => o.textContent)
    expect(optionLabels).toContain('Compute')
    expect(optionLabels).not.toContain('Legacy')
  })

  it('does not write a default-category change until the user saves it', async () => {
    const patch = vi.spyOn(client, 'patchSpendVendor').mockResolvedValue({ ...vendors[0], defaultCategoryId: null })
    renderPanel()

    fireEvent.change(screen.getByLabelText('Default category for Anthropic'), { target: { value: '' } })

    expect(patch).not.toHaveBeenCalled()
    const row = screen.getByText('anthropic').closest('li')!
    fireEvent.click(within(row).getByRole('button', { name: /^save$/i }))

    // Explicit null is the whole point — omitting the key means "leave it alone", which
    // is why the default category was previously set-once and unclearable.
    await waitFor(() => expect(patch).toHaveBeenCalledWith('v1', { defaultCategoryId: null }))
  })

  it('cancels a pending default-category change without writing it', () => {
    const patch = vi.spyOn(client, 'patchSpendVendor')
    renderPanel()

    const select = screen.getByLabelText('Default category for Anthropic') as HTMLSelectElement
    fireEvent.change(select, { target: { value: '' } })
    const row = screen.getByText('anthropic').closest('li')!
    fireEvent.click(within(row).getByRole('button', { name: /^cancel$/i }))

    expect(select.value).toBe('k1')
    expect(patch).not.toHaveBeenCalled()
  })

  it('drops the draft when the default category is changed back to its persisted value', () => {
    const patch = vi.spyOn(client, 'patchSpendVendor')
    renderPanel()

    const select = screen.getByLabelText('Default category for Anthropic') as HTMLSelectElement
    fireEvent.change(select, { target: { value: '' } })
    fireEvent.change(select, { target: { value: 'k1' } })

    const row = screen.getByText('anthropic').closest('li')!
    expect(within(row).queryByRole('button', { name: /^save$/i })).not.toBeInTheDocument()
    expect(within(row).queryByRole('button', { name: /^cancel$/i })).not.toBeInTheDocument()
    expect(patch).not.toHaveBeenCalled()
  })

  it('keeps an already-archived default category selectable so the row does not misread as None', () => {
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    // v3's default points at 'k2', which has since been archived.
    const withArchivedDefault = [
      { id: 'v3', key: 'legacy-vendor', displayName: 'Legacy Vendor', provider: null, defaultCategoryId: 'k2', archivedAt: null },
    ]
    render(
      <QueryClientProvider client={qc}>
        <SpendVendorCatalog vendors={withArchivedDefault} categories={categories} />
      </QueryClientProvider>,
    )

    const select = screen.getByLabelText('Default category for Legacy Vendor') as HTMLSelectElement
    // Without the archived option present the browser falls back to the first option,
    // silently reporting "No default category" for a vendor that has one.
    expect(select.value).toBe('k2')
    expect(Array.from(select.options).map(o => o.textContent)).toContain('Legacy (archived)')
  })

  it('creates a vendor with an explicitly chosen provider', async () => {
    const create = vi.spyOn(client, 'createSpendVendor').mockResolvedValue({
      id: 'v3', key: 'openai-direct', displayName: 'OpenAI Direct', provider: 'openai', defaultCategoryId: null, archivedAt: null,
    })
    renderPanel()

    fireEvent.change(screen.getByLabelText(/^key$/i), { target: { value: 'openai-direct' } })
    fireEvent.change(screen.getByLabelText(/display name/i), { target: { value: 'OpenAI Direct' } })
    fireEvent.change(screen.getByLabelText(/provider/i), { target: { value: 'openai' } })
    fireEvent.click(screen.getByRole('button', { name: /add vendor/i }))

    await waitFor(() => expect(create).toHaveBeenCalledTimes(1))
    expect(create).toHaveBeenCalledWith(
      expect.objectContaining({ key: 'openai-direct', displayName: 'OpenAI Direct', provider: 'openai' }),
    )
  })

  it('creates a vendor with no provider as an explicit null, the normal case for unmetered tools', async () => {
    const create = vi.spyOn(client, 'createSpendVendor').mockResolvedValue({
      id: 'v4', key: 'coderabbit', displayName: 'CodeRabbit', provider: null, defaultCategoryId: null, archivedAt: null,
    })
    renderPanel()

    fireEvent.change(screen.getByLabelText(/^key$/i), { target: { value: 'coderabbit' } })
    fireEvent.change(screen.getByLabelText(/display name/i), { target: { value: 'CodeRabbit' } })
    fireEvent.click(screen.getByRole('button', { name: /add vendor/i }))

    await waitFor(() => expect(create).toHaveBeenCalledTimes(1))
    expect(create).toHaveBeenCalledWith(expect.objectContaining({ provider: null }))
  })

  it('surfaces a 400 bad-slug failure from the server rather than a generic message', async () => {
    vi.spyOn(client, 'createSpendVendor').mockRejectedValue(
      new client.ApiError(400, 'Key must be a slug of 60 characters or fewer and DisplayName is required'),
    )
    renderPanel()

    fireEvent.change(screen.getByLabelText(/^key$/i), { target: { value: 'not a slug!' } })
    fireEvent.change(screen.getByLabelText(/display name/i), { target: { value: 'X' } })
    fireEvent.click(screen.getByRole('button', { name: /add vendor/i }))

    await waitFor(() =>
      expect(screen.getByRole('alert')).toHaveTextContent(/slug of 60 characters/))
  })

  it('archives a live vendor', async () => {
    const patch = vi.spyOn(client, 'patchSpendVendor').mockResolvedValue({ ...vendors[0], archivedAt: '2026-07-26T00:00:00Z' })
    renderPanel()

    fireEvent.click(screen.getByRole('button', { name: /^archive anthropic$/i }))

    await waitFor(() => expect(patch).toHaveBeenCalledWith('v1', { archived: true }))
  })

  it('unarchives an archived vendor', async () => {
    const patch = vi.spyOn(client, 'patchSpendVendor').mockResolvedValue({ ...vendors[1], archivedAt: null })
    renderPanel()

    fireEvent.click(screen.getByRole('button', { name: /^unarchive github actions$/i }))

    await waitFor(() => expect(patch).toHaveBeenCalledWith('v2', { archived: false }))
  })

  it('renames a vendor via PATCH DisplayName', async () => {
    const patch = vi.spyOn(client, 'patchSpendVendor').mockResolvedValue({ ...vendors[0], displayName: 'Anthropic Inc' })
    renderPanel()

    fireEvent.click(screen.getByRole('button', { name: /^rename anthropic$/i }))
    const input = screen.getByLabelText(/rename anthropic/i)
    fireEvent.change(input, { target: { value: 'Anthropic Inc' } })
    fireEvent.click(screen.getByRole('button', { name: /^save$/i }))

    await waitFor(() => expect(patch).toHaveBeenCalledWith('v1', { displayName: 'Anthropic Inc' }))
  })

  it('explains that one vendor can span several spend categories', () => {
    renderPanel()
    expect(screen.getByText(/suppliers; categories describe what was purchased/i)).toBeInTheDocument()
  })
})
