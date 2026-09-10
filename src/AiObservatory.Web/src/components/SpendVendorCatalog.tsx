import { useMemo, useState, type FormEvent } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import {
  createSpendVendor, patchSpendVendor,
  type SpendVendor, type SpendCategory, type NewSpendVendor,
} from '../api/client'
import { PROVIDERS, providerDisplayName } from '../config/providers'

interface Props {
  /** All vendors, including archived -- this is a management view, so hiding
   * archived rows here would make un-archiving impossible. */
  vendors: SpendVendor[]
  /** All categories, including archived -- needed to resolve a vendor's default
   * category name even when that category has since been retired. The
   * default-category picker below filters this down to the live subset itself. */
  categories: SpendCategory[]
}

const EMPTY_FORM = { key: '', displayName: '', provider: '', defaultCategoryId: '' }

/** The vendor axis of the spend catalog panel. See SpendCategoryCatalog for the
 * shared shape (list/create/rename/archive) and why Key is never editable. */
export default function SpendVendorCatalog({ vendors, categories }: Props) {
  const qc = useQueryClient()
  const [form, setForm] = useState(EMPTY_FORM)
  const [formError, setFormError] = useState<string | null>(null)
  const [mutationError, setMutationError] = useState<string | null>(null)
  const [renamingId, setRenamingId] = useState<string | null>(null)
  const [renameValue, setRenameValue] = useState('')
  const [categoryDraft, setCategoryDraft] = useState<{ vendorId: string; value: string } | null>(null)

  const liveCategories = useMemo(() => categories.filter(c => c.archivedAt === null), [categories])
  // null means "No provider (not token-metered)" — a load-bearing distinction from a
  // set-but-unknown slug (see SpendVendorPatch's tri-state contract), which renders
  // readably via providerDisplayName instead of collapsing into the null label.
  const providerName = (key: string | null) => key === null ? 'No provider' : providerDisplayName(key)

  // Live categories, plus this vendor's own default when that category has since been
  // archived. Without the second part the select has no option matching its value, so the
  // browser falls back to the first option and the row silently reads "No default
  // category" for a vendor that has one — the same archived-resolution trap the ledger
  // history hit. Archived options are labelled, so retiring one is still visible.
  const categoryOptions = (v: SpendVendor): SpendCategory[] => {
    const current = v.defaultCategoryId
    if (current === null || liveCategories.some(c => c.id === current)) return liveCategories
    const archived = categories.find(c => c.id === current)
    return archived ? [...liveCategories, archived] : liveCategories
  }

  // Prefix match invalidates both ['spend-vendors'] (live, used by pickers) and
  // ['spend-vendors', 'all'] (this panel, SpendLedgerTable's name map).
  // ['billed-reporting'] too — see SpendCategoryCatalog.invalidate for why.
  const invalidate = () => Promise.all([
    qc.invalidateQueries({ queryKey: ['spend-vendors'] }),
    qc.invalidateQueries({ queryKey: ['billed-reporting'] }),
  ])

  const create = useMutation({
    mutationFn: (body: NewSpendVendor) => createSpendVendor(body),
    onSuccess: () => { invalidate(); setForm(EMPTY_FORM) },
    onError: (err: Error) => setFormError(err.message),
  })

  const patch = useMutation({
    mutationFn: ({ id, body }: { id: string; body: Parameters<typeof patchSpendVendor>[1] }) =>
      patchSpendVendor(id, body),
    onSuccess: () => { invalidate(); setRenamingId(null); setCategoryDraft(null) },
    onError: (err: Error) => setMutationError(err.message),
  })

  function submitCreate(e: FormEvent) {
    e.preventDefault()
    setFormError(null)
    if (!form.key.trim() || !form.displayName.trim()) {
      setFormError('Key and display name are required')
      return
    }
    create.mutate({
      key: form.key.trim(),
      displayName: form.displayName.trim(),
      provider: form.provider || null,
      defaultCategoryId: form.defaultCategoryId || null,
    })
  }

  function startRename(v: SpendVendor) {
    setMutationError(null)
    setRenamingId(v.id)
    setRenameValue(v.displayName)
  }

  function saveRename(id: string) {
    if (!renameValue.trim()) { setMutationError('Display name is required'); return }
    patch.mutate({ id, body: { displayName: renameValue.trim() } })
  }

  function toggleArchive(v: SpendVendor) {
    setMutationError(null)
    patch.mutate({ id: v.id, body: { archived: v.archivedAt === null } })
  }

  // "" is the None option — send an explicit null so the API clears the column, rather
  // than omitting the key (which means "leave it alone" and is how clearing used to be
  // impossible). See SpendVendorPatch for the tri-state contract.
  function saveDefaultCategory(v: SpendVendor) {
    if (categoryDraft?.vendorId !== v.id) return
    setMutationError(null)
    patch.mutate({ id: v.id, body: { defaultCategoryId: categoryDraft.value || null } })
  }

  return (
    <div>
      {mutationError && <p className="modal__error" role="alert">{mutationError}</p>}

      <p className="catalog-help">Vendors are suppliers; categories describe what was purchased, so one vendor can span several categories.</p>

      {vendors.length === 0 && <p className="panel-empty">No vendors yet.</p>}

      <ul className="sub-list">
        {vendors.map(v => (
          <li key={v.id} className="sub-list-row sub-list-row--vendor">
            <code className="catalog-key">{v.key}</code>

            {renamingId === v.id ? (
              <>
                <label className="visually-hidden" htmlFor={`vendor-rename-${v.id}`}>
                  {`Rename ${v.displayName}`}
                </label>
                <input
                  id={`vendor-rename-${v.id}`}
                  aria-label={`Rename ${v.displayName}`}
                  className="sub-form__input"
                  value={renameValue}
                  onChange={e => setRenameValue(e.target.value)}
                />
                <span className="sub-list__actions sub-list__actions--fixed">
                  <button type="button" className="sub-list__btn" onClick={() => saveRename(v.id)} disabled={patch.isPending}>
                    Save
                  </button>
                  <button type="button" className="sub-list__btn" onClick={() => setRenamingId(null)}>
                    Cancel
                  </button>
                </span>
              </>
            ) : (
              <>
                <div className="vendor-catalog__details">
                  <div className="vendor-catalog__identity">
                    <span className="sub-list__name">{v.displayName}</span>
                    {v.archivedAt !== null && <span className="catalog-badge">Archived</span>}
                  </div>
                  <div className="vendor-catalog__metadata">
                    <span className="sub-list__cost">{providerName(v.provider)}</span>
                    <label className="visually-hidden" htmlFor={`vendor-category-${v.id}`}>
                      {`Default category for ${v.displayName}`}
                    </label>
                    <select
                      id={`vendor-category-${v.id}`}
                      className="sub-list__select"
                      value={categoryDraft?.vendorId === v.id ? categoryDraft.value : v.defaultCategoryId ?? ''}
                      disabled={patch.isPending}
                      onChange={e => setCategoryDraft(
                        (e.target.value || null) === v.defaultCategoryId
                          ? null
                          : { vendorId: v.id, value: e.target.value },
                      )}
                    >
                      <option value="">No default category</option>
                      {categoryOptions(v).map(c => (
                        <option key={c.id} value={c.id}>
                          {c.displayName}{c.archivedAt !== null && ' (archived)'}
                        </option>
                      ))}
                    </select>
                  </div>
                </div>
                {categoryDraft?.vendorId === v.id ? (
                  <span className="sub-list__actions sub-list__actions--fixed">
                    <button type="button" className="sub-list__btn" onClick={() => saveDefaultCategory(v)} disabled={patch.isPending}>
                      Save
                    </button>
                    <button type="button" className="sub-list__btn" onClick={() => setCategoryDraft(null)}>
                      Cancel
                    </button>
                  </span>
                ) : (
                  <span className="sub-list__actions sub-list__actions--fixed">
                    <button
                      type="button"
                      className="sub-list__btn"
                      onClick={() => startRename(v)}
                      aria-label={`Rename ${v.displayName}`}
                    >
                      Rename
                    </button>
                    <button
                      type="button"
                      className="sub-list__btn"
                      onClick={() => toggleArchive(v)}
                      disabled={patch.isPending}
                      aria-label={`${v.archivedAt === null ? 'Archive' : 'Unarchive'} ${v.displayName}`}
                    >
                      {v.archivedAt === null ? 'Archive' : 'Unarchive'}
                    </button>
                  </span>
                )}
              </>
            )}
          </li>
        ))}
      </ul>

      <div className="sub-form">
        <div className="sub-form__title">Add vendor</div>
        {/* noValidate: same rationale as SpendCategoryCatalog -- maxLength alone would
            let native constraint validation swallow the submit before submitCreate runs. */}
        <form noValidate onSubmit={submitCreate}>
          <div className="sub-form__grid">
            <div>
              <label htmlFor="new-vendor-key" className="sub-form__label">Key</label>
              <input
                id="new-vendor-key"
                className="sub-form__input"
                value={form.key}
                onChange={e => setForm(f => ({ ...f, key: e.target.value }))}
                maxLength={60}
                placeholder="e.g. github-actions"
              />
            </div>
            <div>
              <label htmlFor="new-vendor-name" className="sub-form__label">Display name</label>
              <input
                id="new-vendor-name"
                className="sub-form__input"
                value={form.displayName}
                onChange={e => setForm(f => ({ ...f, displayName: e.target.value }))}
                maxLength={100}
              />
            </div>
            <div>
              <label htmlFor="new-vendor-provider" className="sub-form__label">Provider</label>
              <select
                id="new-vendor-provider"
                className="sub-form__select"
                value={form.provider}
                onChange={e => setForm(f => ({ ...f, provider: e.target.value }))}
              >
                {/* Explicit, obvious "no provider" -- the normal case for CodeRabbit,
                    Gitar and GitHub Actions, which have no token estimate to meter. */}
                <option value="">No provider (not token-metered)</option>
                {PROVIDERS.map(p => <option key={p.key} value={p.key}>{p.displayName}</option>)}
              </select>
            </div>
            <div>
              <label htmlFor="new-vendor-category" className="sub-form__label">Default category</label>
              <select
                id="new-vendor-category"
                className="sub-form__select"
                value={form.defaultCategoryId}
                onChange={e => setForm(f => ({ ...f, defaultCategoryId: e.target.value }))}
              >
                <option value="">None</option>
                {liveCategories.map(c => <option key={c.id} value={c.id}>{c.displayName}</option>)}
              </select>
            </div>
          </div>

          {formError && <p className="modal__error" role="alert">{formError}</p>}

          <div className="sub-form__actions">
            <button type="submit" className="sub-form__btn sub-form__btn--primary" disabled={create.isPending}>
              {create.isPending ? 'Adding...' : 'Add vendor'}
            </button>
          </div>
        </form>
      </div>
    </div>
  )
}
