import { useState, useMemo, useCallback, useRef } from 'react'
import { InfoPopover } from './InfoPopover'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { patchExtraUsage, type Subscription } from '../api/client'
import { useSubscriptions, useAggregates, localDate } from '../api/queries'
import { providerColor } from '../theme/providerColors'
import { gbp, useUsdToGbp, formatCurrency } from '../lib/currency'
import { billingMonthName, currentBillingPeriodStart, subscriptionUsage } from '../lib/subscriptions'
import SubscriptionModal from './SubscriptionModal'
import { isReadonly } from '../auth/msal'
import { PROVIDER_ORDER, providerDisplayName } from '../config/providers'

const SUBSCRIPTION_AGGREGATES_STALE_MS = 5 * 60_000

function ordinal(n: number): string {
  const s = ['th', 'st', 'nd', 'rd']
  const v = n % 100
  return n + (s[(v - 20) % 10] ?? s[v] ?? s[0])
}

function usagePresentation(
  valueGbp: number | null,
  requestCount: number,
  unknownCostCount: number,
  totalGbp: number,
  totalAmount: number,
  currency: string,
) {
  if (valueGbp === null) {
    return {
      value: 'Not reported',
      ratio: 0,
      label: `${requestCount.toLocaleString()} ${requestCount === 1 ? 'request' : 'requests'} recorded · value not reported`,
    }
  }
  const ratio = totalGbp > 0 ? valueGbp / totalGbp : 0
  const unknownLabel = unknownCostCount > 0
    ? ` · ${unknownCostCount.toLocaleString()} ${unknownCostCount === 1 ? 'request' : 'requests'} not reported`
    : ''
  return {
    value: gbp(valueGbp),
    ratio,
    label: `${Math.round(ratio * 100)}% of ${formatCurrency(totalAmount, currency)} subscription price${unknownLabel}`,
  }
}

function ExtraUsageChip({ sub }: { sub: Subscription }) {
  const qc = useQueryClient()
  const [editing, setEditing] = useState(false)
  const [draft, setDraft] = useState('')
  const escapedRef = useRef(false)
  const focusInput = useCallback((el: HTMLInputElement | null) => { el?.focus() }, [])

  const patch = useMutation({
    mutationFn: (amount: number | null) => patchExtraUsage(sub.id, amount),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['subscriptions'] })
      setEditing(false)
    },
    onError: (err: Error) => {
      alert(`Failed to save extra usage cost: ${err.message}`)
    },
  })

  // Read-only viewers (share link) can't patch — show the value, no edit affordance.
  if (isReadonly) {
    return (
      <span className={`sub-card__extra-chip${sub.extraUsageCost === null ? ' sub-card__extra-chip--null' : ''}`}>
        {sub.extraUsageCost !== null ? `+ ${formatCurrency(sub.extraUsageCost, sub.currency)}` : '—'}
      </span>
    )
  }

  if (editing) {
    return (
      <input
        ref={focusInput}
        className="sub-card__extra-input"
        type="number"
        step="0.01"
        min="0"
        aria-label="Extra usage cost"
        value={draft}
        onChange={e => setDraft(e.target.value)}
        onBlur={() => {
          if (escapedRef.current) { escapedRef.current = false; return }
          const trimmed = draft.trim()
          if (trimmed === '') { patch.mutate(null); return } // blank clears the value
          const val = parseFloat(trimmed)
          // Reject negatives (min="0" only constrains the spinner, not typed input): a
          // negative would deflate the total, % of budget, and over-budget flag.
          if (!Number.isFinite(val) || val < 0) { setEditing(false); return }
          patch.mutate(val)
        }}
        onKeyDown={e => {
          if (e.key === 'Enter') (e.target as HTMLInputElement).blur()
          if (e.key === 'Escape') {
            escapedRef.current = true
            setDraft('')
            setEditing(false)
          }
        }}
      />
    )
  }

  return (
    <button
      type="button"
      className={`sub-card__extra-chip${sub.extraUsageCost === null ? ' sub-card__extra-chip--null' : ''}`}
      onClick={() => {
        escapedRef.current = false
        setDraft(sub.extraUsageCost !== null ? String(sub.extraUsageCost) : '')
        setEditing(true)
      }}
      title="Click to edit extra usage"
    >
      {sub.extraUsageCost !== null ? `+ ${formatCurrency(sub.extraUsageCost, sub.currency)}` : '— add'}
    </button>
  )
}

interface GroupedSubscription {
  /** The full composite the grouping collapses on — provider + currency + interval +
   * month + day. Also the React key: currency separates groups, so a key without it
   * collides and can carry ExtraUsageChip editing state onto the wrong card. */
  groupKey: string
  provider: string
  name: string
  costAmount: number
  extraUsageCost: number | null
  currency: string
  billingInterval: 'monthly' | 'annual'
  billingMonth: number | null
  billingDay: number
  activeFrom: string
  activeTo: string | null
  originalSubscription: Subscription // reference for patching extra usage
  memberCount: number // subs merged into this card; >1 disables inline extra-usage edit
}

export default function SubscriptionPanel() {
  const { subscriptions, isError: subscriptionsError, isLoading: subscriptionsLoading } = useSubscriptions()
  const rate = useUsdToGbp()
  const [modalOpen, setModalOpen] = useState(false)
  const today = localDate(new Date())

  const active = useMemo(
    () =>
      subscriptions
        .filter(s => s.activeFrom <= today && (s.activeTo === null || s.activeTo >= today))
        .sort((a, b) => {
          const ak = a.provider.toLowerCase()
          const bk = b.provider.toLowerCase()
          const ai = PROVIDER_ORDER.indexOf(ak as typeof PROVIDER_ORDER[number])
          const bi = PROVIDER_ORDER.indexOf(bk as typeof PROVIDER_ORDER[number])
          return (ai < 0 ? 99 : ai) - (bi < 0 ? 99 : bi)
        }),
    [subscriptions, today]
  )

  // Collapse only subscriptions that share provider, currency, and billing cycle.
  const collapsed = useMemo(() => {
    const groups: Record<string, GroupedSubscription> = {}
    for (const sub of active) {
      const key = `${sub.provider.toLowerCase()}|${sub.currency.toUpperCase()}|${sub.billingInterval}|${sub.billingMonth ?? ''}|${sub.billingDay}`
      if (!groups[key]) {
        groups[key] = {
          groupKey: key,
          provider: sub.provider,
          name: sub.name,
          costAmount: 0,
          extraUsageCost: null,
          currency: sub.currency,
          billingInterval: sub.billingInterval,
          billingMonth: sub.billingMonth,
          billingDay: sub.billingDay,
          activeFrom: sub.activeFrom,
          activeTo: sub.activeTo,
          originalSubscription: sub,
          memberCount: 0,
        }
      }
      const g = groups[key]
      g.costAmount += sub.costAmount
      if (sub.extraUsageCost !== null) {
        g.extraUsageCost = (g.extraUsageCost ?? 0) + sub.extraUsageCost
      }
      if (g.originalSubscription.id !== sub.id) {
        g.name = `${g.name} + ${sub.name}`
      }
      if (sub.activeFrom < g.activeFrom) {
        g.activeFrom = sub.activeFrom
      }
      g.memberCount += 1
    }
    return Object.values(groups)
  }, [active])

  // Reaches back to the earliest current billing-period start (~12 months for an
  // annual plan), so the range can be long; the staleTime keeps that pull from
  // refetching on every window focus (the default queryClient sets none).
  const aggregateWindow = useMemo(() => {
    const from = collapsed.reduce<string | null>((earliest, sub) => {
      const start = currentBillingPeriodStart(sub.billingDay, today, sub.billingInterval, sub.billingMonth)
      const windowStart = sub.activeFrom > start ? sub.activeFrom : start
      return earliest === null || windowStart < earliest ? windowStart : earliest
    }, null)
    return from === null ? null : { from: new Date(`${from}T00:00:00`), to: new Date(`${today}T00:00:00`) }
  }, [collapsed, today])
  const { aggregates } = useAggregates(aggregateWindow?.from, aggregateWindow?.to, SUBSCRIPTION_AGGREGATES_STALE_MS)

  return (
    <div className="sub-panel">
      <div className="panel">
        <div className="sub-panel-header">
          <div className="sub-panel-title-row">
            <span className="sub-panel-title">Subscriptions</span>
            <InfoPopover id="subscriptions-info" title="Subscriptions">
              <p>Notional usage value applies current public API list prices to eligible subscription activity. It is a comparison, not money charged.</p>
              <p>When subscription logs omit API-only routing flags, standard/global pricing is assumed; Google uses the published short-context rate, and unknown Claude cache-write duration uses the published five-minute rate.</p>
              <p>The cycle resets on the renewal day shown in each card. The progress bar shows notional usage value as a percentage of the subscription price.</p>
            </InfoPopover>
          </div>
          {!isReadonly && (
            <button type="button" className="sub-panel-btn" onClick={() => setModalOpen(true)}>
              Manage subscriptions
            </button>
          )}
        </div>

        {subscriptionsError ? (
          <p className="panel-empty">Failed to load subscriptions.</p>
        ) : subscriptionsLoading ? null : collapsed.length === 0 ? (
          <div className="sub-empty">
            <p className="sub-empty__text">No subscriptions — add one to start tracking.</p>
            {!isReadonly && (
              <button type="button" className="sub-panel-btn" onClick={() => setModalOpen(true)}>
                Add subscription
              </button>
            )}
          </div>
        ) : (
          <div className="sub-cards">
            {collapsed.map(sub => {
              const providerKey = sub.provider.toLowerCase()
              const start = currentBillingPeriodStart(sub.billingDay, today, sub.billingInterval, sub.billingMonth)
              const windowStart = sub.activeFrom > start ? sub.activeFrom : start
              const usage = subscriptionUsage(aggregates, providerKey, windowStart)
              const periodNotionalGbp = usage.notionalUsd === null ? null : usage.notionalUsd * rate
              
              const total = sub.costAmount + (sub.extraUsageCost ?? 0)
              const totalGbp = sub.currency.toUpperCase() === 'USD' ? total * rate : total
              
              const presentation = usagePresentation(periodNotionalGbp, usage.requestCount, usage.unknownCostCount, totalGbp, total, sub.currency)
              const ratio = presentation.ratio
              const pct = Math.min(ratio * 100, 100)
              const isOver = ratio > 1
              const accentColor = providerColor(providerKey)

              return (
                <div key={sub.groupKey} className="sub-card" style={{ borderLeftColor: accentColor }}>
                  <div className="sub-card__header">
                    <span className="sub-card__provider" style={{ color: accentColor }}>
                      {providerDisplayName(providerKey)}
                    </span>
                    <span className="sub-card__plan">{sub.name}</span>
                  </div>

                  <div className="sub-card__cost">
                    {formatCurrency(sub.costAmount, sub.currency)}
                    <span className="sub-card__cost-unit"> /{sub.billingInterval === 'annual' ? 'yr' : 'mo'}</span>
                  </div>

                  <div className="sub-card__extra">
                    <span className="sub-card__extra-label">Extra:</span>
                    {sub.memberCount > 1 ? (
                      // Multiple plans merged: show the combined extra read-only — inline
                      // editing here would only patch the first plan (edit each individually
                      // via Manage subscriptions).
                      <span
                        className={`sub-card__extra-chip${sub.extraUsageCost === null ? ' sub-card__extra-chip--null' : ''}`}
                        title="Combined extra usage across merged plans — edit each plan via Manage subscriptions"
                      >
                        {sub.extraUsageCost !== null ? `+ ${formatCurrency(sub.extraUsageCost, sub.currency)}` : '—'}
                      </span>
                    ) : (
                      <ExtraUsageChip sub={sub.originalSubscription} />
                    )}
                  </div>

                  <div className="sub-card__billing-day">
                    {sub.billingInterval === 'annual'
                      ? `Renews annually on ${sub.billingDay} ${billingMonthName(sub.billingMonth ?? 0)}`
                      : `Renews on the ${ordinal(sub.billingDay)}`}
                  </div>

                  <div className="sub-card__period-row">
                    <span className="sub-card__period-label">Notional usage value</span>
                    <span className={`sub-card__period-value${isOver ? ' sub-card__period-value--over' : ''}`}>
                      {presentation.value}
                    </span>
                  </div>

                  <div className="sub-progress-track">
                    <div
                      className={`sub-progress-fill${isOver ? ' sub-progress-fill--over' : ''}`}
                      style={{ width: `${pct}%`, background: isOver ? undefined : 'var(--brand)' }}
                    />
                  </div>
                  <div className="sub-progress-label">
                    {presentation.label}
                  </div>
                </div>
              )
            })}
          </div>
        )}
      </div>

      {modalOpen && <SubscriptionModal open={modalOpen} onClose={() => setModalOpen(false)} />}
    </div>
  )
}
