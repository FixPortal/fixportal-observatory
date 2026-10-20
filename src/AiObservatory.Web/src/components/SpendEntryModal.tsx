import { useClock } from '../lib/useClock'
import { useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import {
  patchSpendEntry,
  postSpendEntries,
  type NewSpendEntry,
  type SpendCategory,
  type SpendEntry,
  type SpendEntryPatch,
  type SpendVendor,
} from '../api/client'
import { invalidateSpendData, localDate } from '../api/queries'

const LISTED_CURRENCIES = ['GBP', 'USD']

interface Props {
  /** When set the form edits this entry (PATCH) instead of adding a new one. */
  entry?: SpendEntry | undefined
  categories: SpendCategory[]
  vendors: SpendVendor[]
  /** The page's visible date range. Bounds the date input so a charge dated outside
   * it cannot be saved and then simply not appear in the (currently unfiltered-by-date)
   * ledger table -- there is no date picker to widen the view until phase 2. */
  from: Date
  to: Date
  onClose: () => void
}

export function SpendEntryModal({ entry, categories, vendors, from, to, onClose }: Props) {
  const minDate = localDate(from)
  const maxDate = localDate(to)
  const qc = useQueryClient()
  const clock = useClock()
  // Clamped into [minDate, maxDate]: "today" can fall outside the mounted bounds if the
  // dashboard is left open past midnight, and an untouched form must still be valid --
  // see the range check in handleSave, which this clamp is not a substitute for.
  const [occurredOn, setOccurredOn] = useState(() => {
    if (entry) return entry.occurredOn
    const today = localDate(clock.now())
    return today < minDate ? minDate : today > maxDate ? maxDate : today
  })
  const [vendorId, setVendorId] = useState(entry?.vendorId ?? vendors[0]?.id ?? '')
  // A vendor's default category applies only while it exists in the live list passed to
  // the picker — an archived default has no matching <option>, so the select would
  // display one category while state silently held another (the trap SpendVendorCatalog's
  // categoryOptions guards against). The first-live-category fallback is for the
  // initialiser only; mid-form it would clobber the category the user already picked.
  const liveDefaultCategoryId = (vendor: SpendVendor | undefined) => {
    const preferred = vendor?.defaultCategoryId
    return preferred && categories.some((c) => c.id === preferred)
      ? preferred
      : (categories[0]?.id ?? '')
  }
  const [categoryId, setCategoryId] = useState(
    () => entry?.categoryId ?? liveDefaultCategoryId(vendors[0]),
  )
  // An edit shows the magnitude; the Charge/Refund toggle below carries the sign.
  const [amount, setAmount] = useState(entry ? String(Math.abs(entry.amount)) : '')
  // The sign lives here, not in the amount box. A refund is stored as a negative amount
  // (see SpendEntry.Amount), but typing a bare "-120" is far more likely to be a slip than
  // an intent, so the box stays positive-only and this toggle is the only way to book one.
  const [isRefund, setIsRefund] = useState(entry ? entry.amount < 0 : false)
  const [currency, setCurrency] = useState(entry?.currency ?? 'GBP')
  const [description, setDescription] = useState(entry?.description ?? '')
  const [formError, setFormError] = useState<string | null>(null)
  const [verdictError, setVerdictError] = useState<string | null>(null)

  const save = useMutation({
    mutationFn: (entry: NewSpendEntry) => postSpendEntries([entry]),
    onSuccess: async (results) => {
      const result = results[0]
      // A per-row verdict is not an HTTP failure, so it has to be read rather than assumed.
      if (result?.status === 'rejected') {
        setVerdictError(result.reason ?? 'Entry rejected')
        return
      }
      await invalidateSpendData(qc)
      onClose()
    },
    onError: (err: Error) => {
      setVerdictError(err.message)
    },
  })

  const update = useMutation({
    mutationFn: ({ id, changes }: { id: string; changes: SpendEntryPatch }) =>
      patchSpendEntry(id, changes),
    onSuccess: async () => {
      await invalidateSpendData(qc)
      onClose()
    },
    onError: (err: Error) => {
      setVerdictError(err.message)
    },
  })

  function onVendorChange(id: string) {
    setVendorId(id)
    // Changing the vendor of an existing entry must not rewrite the category it was filed under.
    if (entry) return
    // Follow the vendor's default category as a starting point — but only when it is
    // still a live, selectable category. Falling back to the first live category here
    // (as the initialiser does) would clobber the category the user already picked.
    const defaultId = vendors.find((v) => v.id === id)?.defaultCategoryId
    if (defaultId && categories.some((c) => c.id === defaultId)) setCategoryId(defaultId)
  }

  function handleSave() {
    setFormError(null)
    setVerdictError(null)
    const parsed = Number(amount)
    // Positive-only in the box: the Charge/Refund toggle applies the sign below. Zero is
    // rejected here as well as server-side (CK_SpendEntry_Amount_NonZero).
    if (amount.trim() === '' || !Number.isFinite(parsed) || parsed <= 0) {
      setFormError('Amount must be a positive number — use Refund to record money back')
      return
    }
    if (!vendorId || !categoryId) {
      setFormError('Pick a vendor and a category')
      return
    }
    // min/max on the input steer the native picker, but noValidate (above) means the
    // form will still submit a typed-in out-of-range date. Without this check, a charge
    // dated outside the visible window would save, return `created`, and then simply not
    // appear -- there is no date picker to widen the view and find it until phase 2.
    // An existing entry may predate the window; only a date the person changed is held to it.
    if (occurredOn !== entry?.occurredOn && (occurredOn < minDate || occurredOn > maxDate)) {
      setFormError(`Date must be between ${minDate} and ${maxDate}`)
      return
    }

    if (entry) {
      saveEdit(entry, parsed)
      return
    }

    save.mutate({
      occurredOn,
      vendorId,
      categoryId,
      amount: isRefund ? -parsed : parsed,
      currency,
      description: description.trim() || null,
      source: 'manual',
      // Manual rows are deliberately un-keyed: a person entering the same charge twice
      // should see two rows and notice, not have the second silently swallowed by
      // deduplication meant for re-imported files.
      entryKey: null,
    })
  }

  // Sends only what differs from the stored entry, so an untouched field is never rewritten
  // (and its FX conversion never re-resolved) as a side effect of fixing another.
  function saveEdit(original: SpendEntry, magnitude: number) {
    const signed = isRefund ? -magnitude : magnitude
    const changes: SpendEntryPatch = {}
    if (occurredOn !== original.occurredOn) changes.occurredOn = occurredOn
    if (vendorId !== original.vendorId) changes.vendorId = vendorId
    if (categoryId !== original.categoryId) changes.categoryId = categoryId
    if (signed !== original.amount) changes.amount = signed
    if (currency !== original.currency) changes.currency = currency
    // The API reads an empty string as "clear it" and null as "leave alone".
    if (description.trim() !== (original.description ?? ''))
      changes.description = description.trim()

    if (Object.keys(changes).length === 0) {
      onClose()
      return
    }
    update.mutate({ id: original.id, changes })
  }

  const error = formError ?? verdictError
  const isPending = save.isPending || update.isPending
  // An entry in a currency the picker does not list must still be representable in the form.
  const unlistedCurrency =
    entry && !LISTED_CURRENCIES.includes(entry.currency) ? entry.currency : null

  return (
    <dialog
      ref={(el) => {
        if (el && !el.open) el.showModal()
      }}
      className="modal"
      aria-labelledby="spend-entry-modal-title"
      onClose={onClose}
    >
      <div className="modal__header">
        <span id="spend-entry-modal-title" className="modal__title">
          {entry ? 'Edit spend entry' : 'Add spend entry'}
        </span>
        <button type="button" className="modal__close" onClick={onClose} aria-label="Close">
          ×
        </button>
      </div>

      <div className="modal__body">
        <div className="sub-form">
          {/* noValidate: this form shows its own role="alert" message instead of
              relying on native constraint-validation bubbles (which would also
              silently swallow the submit event before handleSave ever runs). */}
          <form
            noValidate
            onSubmit={(e) => {
              e.preventDefault()
              handleSave()
            }}
          >
            <div className="sub-form__grid">
              <div>
                <label htmlFor="spend-entry-date" className="sub-form__label">
                  Date
                </label>
                <input
                  id="spend-entry-date"
                  className="sub-form__input"
                  type="date"
                  min={minDate}
                  max={maxDate}
                  value={occurredOn}
                  onChange={(e) => {
                    setOccurredOn(e.target.value)
                  }}
                />
              </div>
              <div>
                <label htmlFor="spend-entry-vendor" className="sub-form__label">
                  Vendor
                </label>
                <select
                  id="spend-entry-vendor"
                  className="sub-form__select"
                  value={vendorId}
                  onChange={(e) => {
                    onVendorChange(e.target.value)
                  }}
                >
                  {vendors.map((v) => (
                    <option key={v.id} value={v.id}>
                      {v.displayName}
                    </option>
                  ))}
                </select>
              </div>
              <div>
                <label htmlFor="spend-entry-category" className="sub-form__label">
                  Category
                </label>
                <select
                  id="spend-entry-category"
                  className="sub-form__select"
                  value={categoryId}
                  onChange={(e) => {
                    setCategoryId(e.target.value)
                  }}
                >
                  {categories.map((c) => (
                    <option key={c.id} value={c.id}>
                      {c.displayName}
                    </option>
                  ))}
                </select>
              </div>
              <div>
                <label htmlFor="spend-entry-amount" className="sub-form__label">
                  Amount
                </label>
                <input
                  id="spend-entry-amount"
                  className="sub-form__input"
                  type="number"
                  step="0.01"
                  min="0"
                  value={amount}
                  onChange={(e) => {
                    setAmount(e.target.value)
                  }}
                  placeholder="0.00"
                />
              </div>
              <fieldset className="sub-form__fieldset">
                <legend className="sub-form__label">Direction</legend>
                <div className="sub-form__radios">
                  <label className="sub-form__radio">
                    <input
                      type="radio"
                      name="spend-entry-direction"
                      value="charge"
                      checked={!isRefund}
                      onChange={() => {
                        setIsRefund(false)
                      }}
                    />
                    Charge
                  </label>
                  <label className="sub-form__radio">
                    <input
                      type="radio"
                      name="spend-entry-direction"
                      value="refund"
                      checked={isRefund}
                      onChange={() => {
                        setIsRefund(true)
                      }}
                    />
                    Refund
                  </label>
                </div>
              </fieldset>
              <div>
                <label htmlFor="spend-entry-currency" className="sub-form__label">
                  Currency
                </label>
                <select
                  id="spend-entry-currency"
                  className="sub-form__select"
                  value={currency}
                  onChange={(e) => {
                    setCurrency(e.target.value)
                  }}
                >
                  <option value="GBP">GBP (£)</option>
                  <option value="USD">USD ($)</option>
                  {unlistedCurrency && <option value={unlistedCurrency}>{unlistedCurrency}</option>}
                </select>
              </div>
              <div>
                <label htmlFor="spend-entry-description" className="sub-form__label">
                  Description (optional)
                </label>
                <input
                  id="spend-entry-description"
                  className="sub-form__input"
                  type="text"
                  maxLength={200}
                  value={description}
                  onChange={(e) => {
                    setDescription(e.target.value)
                  }}
                />
              </div>
            </div>

            {error && (
              <p className="modal__error" role="alert">
                {error}
              </p>
            )}

            <div className="sub-form__actions">
              <button
                type="button"
                className="sub-form__btn sub-form__btn--secondary"
                onClick={onClose}
                disabled={isPending}
              >
                Cancel
              </button>
              <button
                type="submit"
                className="sub-form__btn sub-form__btn--primary"
                disabled={isPending}
              >
                {isPending ? 'Saving...' : 'Save'}
              </button>
            </div>
          </form>
        </div>
      </div>
    </dialog>
  )
}
