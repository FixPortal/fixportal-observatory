import { useReducer, useRef } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import {
  createSubscription,
  updateSubscription,
  deleteSubscription,
  type Subscription,
} from '../api/client'
import { useSubscriptions } from '../api/queries'
import { formatCurrency } from '../lib/currency'
import { billingMonthName } from '../lib/subscriptions'
import { PROVIDERS, getProvider } from '../config/providers'

interface Props {
  open: boolean
  onClose: () => void
}

interface FormValues {
  provider: string
  name: string
  costAmount: string
  currency: string
  billingInterval: 'monthly' | 'annual'
  billingMonth: string
  billingDay: string
  activeFrom: string
  activeTo: string
}

const EMPTY_FORM: FormValues = {
  provider: 'anthropic',
  name: '',
  costAmount: '',
  currency: 'GBP',
  billingInterval: 'monthly',
  billingMonth: '',
  billingDay: '',
  activeFrom: '',
  activeTo: '',
}

function subToForm(sub: Subscription): FormValues {
  return {
    provider: sub.provider,
    name: sub.name,
    costAmount: String(sub.costAmount),
    currency: sub.currency,
    billingInterval: sub.billingInterval,
    billingMonth: sub.billingMonth === null ? '' : String(sub.billingMonth),
    billingDay: String(sub.billingDay),
    activeFrom: sub.activeFrom,
    activeTo: sub.activeTo ?? '',
  }
}

const capitalize = (s: string) => s.charAt(0).toUpperCase() + s.slice(1)

const FALLBACK_BADGE = { color: 'var(--provider-other)', background: 'color-mix(in srgb, var(--provider-other) 12%, transparent)' }

type FormMode = 'none' | 'add' | (string & {})

interface State {
  formMode: FormMode
  formValues: FormValues
  confirmDeleteId: string | null
  formError: string | null
  mutationError: string | null
}

type Action =
  | { type: 'OPEN_ADD' }
  | { type: 'OPEN_EDIT'; sub: Subscription }
  | { type: 'CLOSE' }
  | { type: 'SET_FORM_VALUE'; field: keyof FormValues; value: string }
  | { type: 'SET_CONFIRM_DELETE'; id: string | null }
  | { type: 'SET_FORM_ERROR'; error: string | null }
  | { type: 'SET_MUTATION_ERROR'; error: string | null }
  | { type: 'ON_DELETE'; id: string }

const initialState: State = {
  formMode: 'none',
  formValues: EMPTY_FORM,
  confirmDeleteId: null,
  formError: null,
  mutationError: null,
}

function reducer(state: State, action: Action): State {
  switch (action.type) {
    case 'OPEN_ADD':
      return { ...initialState, formMode: 'add' }
    case 'OPEN_EDIT':
      return { ...initialState, formMode: action.sub.id, formValues: subToForm(action.sub) }
    case 'CLOSE':
      return { ...state, formMode: 'none', mutationError: null }
    case 'SET_FORM_VALUE':
      return { ...state, formValues: { ...state.formValues, [action.field]: action.value }, formError: null }
    case 'SET_CONFIRM_DELETE':
      return { ...state, confirmDeleteId: action.id }
    case 'SET_FORM_ERROR':
      return { ...state, formError: action.error }
    case 'SET_MUTATION_ERROR':
      return { ...state, mutationError: action.error }
    case 'ON_DELETE':
      if (state.formMode === action.id) {
        return { ...state, formMode: 'none', formValues: EMPTY_FORM, confirmDeleteId: null }
      }
      return { ...state, confirmDeleteId: null }
    default:
      return state
  }
}

export default function SubscriptionModal({ open, onClose }: Props) {
  const { subscriptions } = useSubscriptions()
  const qc = useQueryClient()

  const [state, dispatch] = useReducer(reducer, initialState)
  const editExtraUsageCost = useRef<number | null>(null)

  const addMutation = useMutation({
    mutationFn: createSubscription,
    onSuccess: () => { qc.invalidateQueries({ queryKey: ['subscriptions'] }); dispatch({ type: 'CLOSE' }) },
    onError: () => dispatch({ type: 'SET_MUTATION_ERROR', error: 'Failed to save — please try again.' }),
  })

  const editMutation = useMutation({
    mutationFn: ({ id, body }: { id: string; body: Omit<Subscription, 'id'> }) => updateSubscription(id, body),
    onSuccess: () => { qc.invalidateQueries({ queryKey: ['subscriptions'] }); dispatch({ type: 'CLOSE' }) },
    onError: () => dispatch({ type: 'SET_MUTATION_ERROR', error: 'Failed to save — please try again.' }),
  })

  const deleteMutation = useMutation({
    mutationFn: deleteSubscription,
    onSuccess: (_, id) => {
      qc.invalidateQueries({ queryKey: ['subscriptions'] })
      dispatch({ type: 'ON_DELETE', id })
    },
    onError: () => dispatch({ type: 'SET_MUTATION_ERROR', error: 'Failed to delete — please try again.' }),
  })

  if (!open) return null

  const isSaving = addMutation.isPending || editMutation.isPending
  const isDeleting = deleteMutation.isPending
  const showForm = state.formMode !== 'none'

  function openAdd() {
    editExtraUsageCost.current = null
    dispatch({ type: 'OPEN_ADD' })
  }

  function openEdit(sub: Subscription) {
    editExtraUsageCost.current = sub.extraUsageCost ?? null
    dispatch({ type: 'OPEN_EDIT', sub })
  }

  function set(field: keyof FormValues, value: string) {
    dispatch({ type: 'SET_FORM_VALUE', field, value })
  }

  function buildBody(): Omit<Subscription, 'id'> {
    return {
      provider: state.formValues.provider,
      name: state.formValues.name,
      costAmount: parseFloat(state.formValues.costAmount) || 0,
      currency: state.formValues.currency,
      billingInterval: state.formValues.billingInterval,
      billingMonth: state.formValues.billingInterval === 'annual'
        ? parseInt(state.formValues.billingMonth, 10)
        : null,
      billingDay: parseInt(state.formValues.billingDay, 10) || 1,
      activeFrom: state.formValues.activeFrom,
      activeTo: state.formValues.activeTo || null,
      extraUsageCost: state.formMode === 'add' ? null : editExtraUsageCost.current,
    }
  }

  function validate(): string | null {
    if (!state.formValues.name.trim()) return 'Plan name is required.'
    if (!state.formValues.activeFrom) return 'Active from date is required.'
    const cost = parseFloat(state.formValues.costAmount)
    if (!Number.isFinite(cost) || cost < 0) return 'Cost must be zero or more.'
    if (state.formValues.billingInterval === 'annual') {
      const month = parseInt(state.formValues.billingMonth, 10)
      if (!Number.isInteger(month) || month < 1 || month > 12) return 'Renewal month is required.'
    }
    const day = parseInt(state.formValues.billingDay, 10)
    if (!Number.isInteger(day) || day < 1 || day > 31) return 'Billing day must be between 1 and 31.'
    const curr = state.formValues.currency.toUpperCase()
    if (curr !== 'GBP' && curr !== 'USD') return 'Currency must be GBP or USD.'
    return null
  }

  function handleSave() {
    const err = validate()
    if (err) { dispatch({ type: 'SET_FORM_ERROR', error: err }); return }
    if (state.formMode === 'add') {
      addMutation.mutate(buildBody())
    } else {
      editMutation.mutate({ id: state.formMode, body: buildBody() })
    }
  }

  return (
    <dialog
      ref={el => { if (el && !el.open) el.showModal() }}
      className="modal"
      aria-labelledby="modal-title"
      onClose={onClose}
    >
      <div className="modal__header">
        <span id="modal-title" className="modal__title">Manage subscriptions</span>
        <button type="button" className="modal__close" onClick={onClose} aria-label="Close">×</button>
      </div>

      <div className="modal__body">
        {state.mutationError && (
          <p className="modal__error" role="alert">{state.mutationError}</p>
        )}

        {subscriptions.length === 0 && !showForm && (
          <p className="panel-empty">No subscriptions yet.</p>
        )}

        <ul className="sub-list">
          {subscriptions.map(sub => {
            const provider = getProvider(sub.provider.toLowerCase())
            const colours = provider?.badgeStyle ?? FALLBACK_BADGE
            return (
              <li key={sub.id} className="sub-list-row">
                <span className="sub-list__badge" style={colours}>
                  {provider?.displayName ?? capitalize(sub.provider)}
                </span>
                <span className="sub-list__name">{sub.name}</span>
                <span className="sub-list__cost">{formatCurrency(sub.costAmount, sub.currency)}/{sub.billingInterval === 'annual' ? 'yr' : 'mo'}</span>

                {state.confirmDeleteId === sub.id ? (
                  <span className="sub-list__confirm">
                    <span className="sub-list__confirm-text">Delete?</span>
                    <button
                      type="button"
                      className="sub-list__btn sub-list__btn--danger"
                      onClick={() => deleteMutation.mutate(sub.id)}
                      disabled={isDeleting}
                    >
                      Yes
                    </button>
                    <button
                      type="button"
                      className="sub-list__btn"
                      onClick={() => dispatch({ type: 'SET_CONFIRM_DELETE', id: null })}
                    >
                      No
                    </button>
                  </span>
                ) : (
                  <span className="sub-list__actions">
                    <button
                      type="button"
                      className="sub-list__btn"
                      onClick={() => openEdit(sub)}
                      disabled={isSaving}
                    >
                      Edit
                    </button>
                    <button
                      type="button"
                      className="sub-list__btn sub-list__btn--danger"
                      onClick={() => dispatch({ type: 'SET_CONFIRM_DELETE', id: sub.id })}
                    >
                      Delete
                    </button>
                  </span>
                )}
              </li>
            )
          })}
        </ul>

        {!showForm && (
          <button type="button" className="sub-add-btn" onClick={openAdd}>
            + Add subscription
          </button>
        )}

        {showForm && (
          <div className="sub-form">
            <div className="sub-form__title">
              {state.formMode === 'add' ? 'Add subscription' : 'Edit subscription'}
            </div>
            {/* noValidate: min="0"/min="1" inputs below would otherwise let native
                constraint validation abort the submit before validate() ever runs,
                the same latent bug SpendEntryModal fixed the same way. */}
            <form noValidate onSubmit={(e) => { e.preventDefault(); handleSave() }}>
            <div className="sub-form__grid">
              <div>
                <label htmlFor="sub-form-provider" className="sub-form__label">Provider</label>
                <select
                  id="sub-form-provider"
                  className="sub-form__select"
                  value={state.formValues.provider}
                  onChange={e => set('provider', e.target.value)}
                >
                  {PROVIDERS.map(p => (
                    <option key={p.key} value={p.key}>{p.displayName}</option>
                  ))}
                </select>
              </div>
              <div>
                <label htmlFor="sub-form-name" className="sub-form__label">Plan name</label>
                <input
                  id="sub-form-name"
                  className="sub-form__input"
                  type="text"
                  value={state.formValues.name}
                  onChange={e => set('name', e.target.value)}
                  placeholder="e.g. Claude Max"
                />
              </div>
              <div>
                <label htmlFor="sub-form-cost" className="sub-form__label">
                  {state.formValues.billingInterval === 'annual' ? 'Annual cost' : 'Monthly cost'}
                </label>
                <input
                  id="sub-form-cost"
                  className="sub-form__input"
                  type="number"
                  step="0.01"
                  min="0"
                  value={state.formValues.costAmount}
                  onChange={e => set('costAmount', e.target.value)}
                  placeholder="100.00"
                />
              </div>
              <div>
                <label htmlFor="sub-form-billing-interval" className="sub-form__label">Billing interval</label>
                <select
                  id="sub-form-billing-interval"
                  className="sub-form__select"
                  value={state.formValues.billingInterval}
                  onChange={e => set('billingInterval', e.target.value)}
                >
                  <option value="monthly">Monthly</option>
                  <option value="annual">Annual</option>
                </select>
              </div>
              {state.formValues.billingInterval === 'annual' && (
                <div>
                  <label htmlFor="sub-form-billing-month" className="sub-form__label">Renewal month</label>
                  <select
                    id="sub-form-billing-month"
                    className="sub-form__select"
                    value={state.formValues.billingMonth}
                    onChange={e => set('billingMonth', e.target.value)}
                  >
                    <option value="">Select month</option>
                    {Array.from({ length: 12 }, (_, index) => index + 1).map(month => (
                      <option key={month} value={month}>{billingMonthName(month)}</option>
                    ))}
                  </select>
                </div>
              )}
              <div>
                <label htmlFor="sub-form-currency" className="sub-form__label">Currency</label>
                <select
                  id="sub-form-currency"
                  className="sub-form__select"
                  value={state.formValues.currency}
                  onChange={e => set('currency', e.target.value)}
                >
                  <option value="GBP">GBP (£)</option>
                  <option value="USD">USD ($)</option>
                </select>
              </div>
              <div>
                <label htmlFor="sub-form-billing-day" className="sub-form__label">Billing day</label>
                <input
                  id="sub-form-billing-day"
                  className="sub-form__input"
                  type="number"
                  min="1"
                  max="31"
                  value={state.formValues.billingDay}
                  onChange={e => set('billingDay', e.target.value)}
                  placeholder="15"
                />
              </div>
              <div>
                <label htmlFor="sub-form-active-from" className="sub-form__label">Active from</label>
                <input
                  id="sub-form-active-from"
                  className="sub-form__input"
                  type="date"
                  value={state.formValues.activeFrom}
                  onChange={e => set('activeFrom', e.target.value)}
                />
              </div>
              <div>
                <label htmlFor="sub-form-active-to" className="sub-form__label">Active to (optional)</label>
                <input
                  id="sub-form-active-to"
                  className="sub-form__input"
                  type="date"
                  value={state.formValues.activeTo}
                  onChange={e => set('activeTo', e.target.value)}
                />
              </div>
            </div>
            {state.formError && (
              <p className="modal__error" role="alert">{state.formError}</p>
            )}
            <div className="sub-form__actions">
              <button
                type="button"
                className="sub-form__btn sub-form__btn--secondary"
                onClick={() => dispatch({ type: 'CLOSE' })}
                disabled={isSaving}
              >
                Cancel
              </button>
              <button
                type="submit"
                className="sub-form__btn sub-form__btn--primary"
                disabled={isSaving}
              >
                {isSaving ? 'Saving...' : 'Save'}
              </button>
            </div>
            </form>
          </div>
        )}
      </div>
    </dialog>
  )
}
