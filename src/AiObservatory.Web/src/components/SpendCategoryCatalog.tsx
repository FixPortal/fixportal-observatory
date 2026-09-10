import { useState, type FormEvent } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import {
  createSpendCategory, patchSpendCategory,
  type SpendCategory, type NewSpendCategory,
} from '../api/client'

interface Props {
  /** All categories, including archived -- this is a management view, so hiding
   * archived rows here would make un-archiving impossible. */
  categories: SpendCategory[]
}

const EMPTY_FORM = { key: '', displayName: '', sortOrder: '0' }

/** One axis of the spend catalog panel: list (incl. archived), create, rename,
 * archive/un-archive. Key is set once at creation and never edited afterward --
 * imports and the portal feed reference it by key, which is the entire reason a
 * separate immutable slug exists alongside DisplayName. */
export default function SpendCategoryCatalog({ categories }: Props) {
  const qc = useQueryClient()
  const [form, setForm] = useState(EMPTY_FORM)
  const [formError, setFormError] = useState<string | null>(null)
  const [mutationError, setMutationError] = useState<string | null>(null)
  const [renamingId, setRenamingId] = useState<string | null>(null)
  const [renameValue, setRenameValue] = useState('')

  // Prefix match invalidates both ['spend-categories'] (live, used by pickers) and
  // ['spend-categories', 'all'] (this panel, SpendLedgerTable's name map).
  // ['billed-reporting'] too: its response embeds resolved category/vendor NAMES
  // (series labels, topVendorName), which a rename/archival would otherwise leave
  // stale in cache until that query happened to refetch. Ledger rows carry ids and
  // resolve names through the catalog map, so ['spend-entries'] needs no refresh.
  const invalidate = () => Promise.all([
    qc.invalidateQueries({ queryKey: ['spend-categories'] }),
    qc.invalidateQueries({ queryKey: ['billed-reporting'] }),
  ])

  const create = useMutation({
    mutationFn: (body: NewSpendCategory) => createSpendCategory(body),
    onSuccess: () => { invalidate(); setForm(EMPTY_FORM) },
    onError: (err: Error) => setFormError(err.message),
  })

  const patch = useMutation({
    mutationFn: ({ id, body }: { id: string; body: Parameters<typeof patchSpendCategory>[1] }) =>
      patchSpendCategory(id, body),
    onSuccess: () => { invalidate(); setRenamingId(null) },
    onError: (err: Error) => setMutationError(err.message),
  })

  function submitCreate(e: FormEvent) {
    e.preventDefault()
    setFormError(null)
    if (!form.key.trim() || !form.displayName.trim()) {
      setFormError('Key and display name are required')
      return
    }
    const sortOrder = Number(form.sortOrder)
    if (!Number.isFinite(sortOrder)) {
      setFormError('Sort order must be a number')
      return
    }
    create.mutate({
      key: form.key.trim(),
      displayName: form.displayName.trim(),
      colorVar: null,
      sortOrder,
    })
  }

  function startRename(c: SpendCategory) {
    setMutationError(null)
    setRenamingId(c.id)
    setRenameValue(c.displayName)
  }

  function saveRename(id: string) {
    if (!renameValue.trim()) { setMutationError('Display name is required'); return }
    patch.mutate({ id, body: { displayName: renameValue.trim() } })
  }

  function toggleArchive(c: SpendCategory) {
    setMutationError(null)
    patch.mutate({ id: c.id, body: { archived: c.archivedAt === null } })
  }

  return (
    <div>
      {mutationError && <p className="modal__error" role="alert">{mutationError}</p>}

      {categories.length === 0 && <p className="panel-empty">No categories yet.</p>}

      <ul className="sub-list">
        {categories.map(c => (
          <li key={c.id} className="sub-list-row">
            <code className="catalog-key">{c.key}</code>

            {renamingId === c.id ? (
              <>
                <label className="visually-hidden" htmlFor={`category-rename-${c.id}`}>
                  {`Rename ${c.displayName}`}
                </label>
                <input
                  id={`category-rename-${c.id}`}
                  aria-label={`Rename ${c.displayName}`}
                  className="sub-form__input"
                  value={renameValue}
                  onChange={e => setRenameValue(e.target.value)}
                />
                <span className="sub-list__actions">
                  <button type="button" className="sub-list__btn" onClick={() => saveRename(c.id)} disabled={patch.isPending}>
                    Save
                  </button>
                  <button type="button" className="sub-list__btn" onClick={() => setRenamingId(null)}>
                    Cancel
                  </button>
                </span>
              </>
            ) : (
              <>
                <span className="sub-list__name">{c.displayName}</span>
                {c.archivedAt !== null && <span className="catalog-badge">Archived</span>}
                <span className="sub-list__actions">
                  <button
                    type="button"
                    className="sub-list__btn"
                    onClick={() => startRename(c)}
                    aria-label={`Rename ${c.displayName}`}
                  >
                    Rename
                  </button>
                  <button
                    type="button"
                    className="sub-list__btn"
                    onClick={() => toggleArchive(c)}
                    disabled={patch.isPending}
                    aria-label={`${c.archivedAt === null ? 'Archive' : 'Unarchive'} ${c.displayName}`}
                  >
                    {c.archivedAt === null ? 'Archive' : 'Unarchive'}
                  </button>
                </span>
              </>
            )}
          </li>
        ))}
      </ul>

      <div className="sub-form">
        <div className="sub-form__title">Add category</div>
        {/* noValidate: maxLength alone would otherwise let native constraint validation
            swallow the submit before the guards above run -- the same gap SpendEntryModal
            fixed the same way. */}
        <form noValidate onSubmit={submitCreate}>
          <div className="sub-form__grid">
            <div>
              <label htmlFor="new-category-key" className="sub-form__label">Key</label>
              <input
                id="new-category-key"
                className="sub-form__input"
                value={form.key}
                onChange={e => setForm(f => ({ ...f, key: e.target.value }))}
                maxLength={60}
                placeholder="e.g. inference"
              />
            </div>
            <div>
              <label htmlFor="new-category-name" className="sub-form__label">Display name</label>
              <input
                id="new-category-name"
                className="sub-form__input"
                value={form.displayName}
                onChange={e => setForm(f => ({ ...f, displayName: e.target.value }))}
                maxLength={100}
              />
            </div>
            <div>
              <label htmlFor="new-category-sort" className="sub-form__label">Sort order</label>
              <input
                id="new-category-sort"
                className="sub-form__input"
                type="number"
                value={form.sortOrder}
                onChange={e => setForm(f => ({ ...f, sortOrder: e.target.value }))}
              />
            </div>
          </div>

          {formError && <p className="modal__error" role="alert">{formError}</p>}

          <div className="sub-form__actions">
            <button type="submit" className="sub-form__btn sub-form__btn--primary" disabled={create.isPending}>
              {create.isPending ? 'Adding...' : 'Add category'}
            </button>
          </div>
        </form>
      </div>
    </div>
  )
}
