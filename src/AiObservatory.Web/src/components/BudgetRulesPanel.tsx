import { useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { Button } from '../design/Button'
import { createBudgetRule, deleteBudgetRule, updateNotificationSettings } from '../api/client'
import { useBudgetRules, useInsights, useNotificationSettings } from '../api/queries'
import { isReadonly } from '../auth/msal'
import { gbp } from '../lib/currency'

const PROVIDERS = ['anthropic', 'copilot', 'google', 'openai'] as const
const PERIODS = ['daily', 'weekly', 'monthly'] as const

const capitalize = (s: string) => s.charAt(0).toUpperCase() + s.slice(1)

type Channel = 'email' | 'slack'

interface NotificationChannelRowProps {
  channel: Channel
  label: string
  configured: boolean
  masked: string | null
  onSave: (value: string) => Promise<unknown>
  onClear: () => void
  isSaving: boolean
}

interface ConfiguredChannelActionsProps {
  label: string
  isSaving: boolean
  onEdit: () => void
  onClear: () => void
}

// ponytail: split out of NotificationChannelRow's configured branch purely to bring that
// branch's conditional count under sonarjs/expression-complexity's limit of 3 -- no behavior
// change (Minor #7).
function ConfiguredChannelActions({ label, isSaving, onEdit, onClear }: ConfiguredChannelActionsProps) {
  const [confirmingRemove, setConfirmingRemove] = useState(false)

  if (isReadonly) return null

  if (confirmingRemove) {
    return (
      <>
        <Button variant="danger" size="sm" disabled={isSaving} onClick={() => { onClear(); setConfirmingRemove(false) }}>
          Confirm
        </Button>
        <Button variant="ghost" size="sm" onClick={() => setConfirmingRemove(false)}>
          Cancel
        </Button>
      </>
    )
  }

  return (
    <>
      <Button variant="ghost" size="sm" aria-label={`Edit ${label.toLowerCase()}`} onClick={onEdit}>
        Edit
      </Button>
      <Button variant="danger" size="sm" aria-label={`Remove ${label.toLowerCase()}`} onClick={() => setConfirmingRemove(true)}>
        Remove
      </Button>
    </>
  )
}

function NotificationChannelRow({ channel, label, configured, masked, onSave, onClear, isSaving }: NotificationChannelRowProps) {
  const [editing, setEditing] = useState(false)
  const [value, setValue] = useState('')
  const fieldId = `notification-${channel}-input`
  const fieldLabel = channel === 'email' ? 'Email address' : 'Slack webhook URL'

  // Only close the row and clear the typed value once the save actually succeeds —
  // closing unconditionally discarded a rejected edit (e.g. invalid email/webhook
  // format) with no way to see or fix what was typed. Shared by the Save button and
  // the input's Enter key so both paths behave identically.
  async function submit() {
    if (isSaving || value.trim() === '') return
    try {
      await onSave(value.trim())
      setEditing(false)
      setValue('')
    } catch {
      // Error is surfaced by the panel-level saveError banner; keep the row open.
    }
  }

  if (editing) {
    return (
      <div className="budget-rules__channel-row">
        <span className="budget-rules__channel-label">{label}</span>
        <label htmlFor={fieldId} className="visually-hidden">{fieldLabel}</label>
        <input
          id={fieldId}
          type="text"
          value={value}
          onChange={e => setValue(e.target.value)}
          onKeyDown={e => { if (e.key === 'Enter') { e.preventDefault(); void submit() } }}
          placeholder={channel === 'email' ? 'you@example.com' : 'https://hooks.slack.com/services/...'}
          className="budget-rules__control"
        />
        <Button variant="primary" size="sm" disabled={isSaving || value.trim() === ''} onClick={() => void submit()}>
          Save
        </Button>
        <Button variant="ghost" size="sm" onClick={() => { setEditing(false); setValue('') }}>
          Cancel
        </Button>
      </div>
    )
  }

  return (
    <div className="budget-rules__channel-row">
      <span className="budget-rules__channel-label">{label}</span>
      {configured ? (
        <>
          <span className="budget-rules__channel-value">{masked}</span>
          <ConfiguredChannelActions label={label} isSaving={isSaving} onEdit={() => setEditing(true)} onClear={onClear} />
        </>
      ) : (
        <>
          <span className="budget-rules__channel-value budget-rules__channel-value--unset">Not set</span>
          {!isReadonly && (
            <Button variant="ghost" size="sm" aria-label={`Add ${label.toLowerCase()}`} onClick={() => setEditing(true)}>
              Add
            </Button>
          )}
        </>
      )}
    </div>
  )
}

function NotificationSettingsSection() {
  const qc = useQueryClient()
  const { settings, isError: loadError } = useNotificationSettings()
  const [saveError, setSaveError] = useState<string | null>(null)

  const save = useMutation({
    mutationFn: updateNotificationSettings,
    onMutate: () => setSaveError(null),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['notification-settings'] }),
    onError: (e: Error) => setSaveError(`Couldn’t save notification settings: ${e.message}`),
  })

  if (loadError) {
    return (
      <div className="panel budget-rules__history">
        <div className="panel-title">Notifications</div>
        <p className="panel-empty" role="alert">Failed to load notification settings.</p>
      </div>
    )
  }

  if (!settings) return null

  return (
    <div className="panel budget-rules__history">
      <div className="panel-title">Notifications</div>
      {saveError && <p className="panel-empty" role="alert">{saveError}</p>}
      <NotificationChannelRow
        channel="email"
        label="Email"
        configured={settings.emailConfigured}
        masked={settings.emailMasked}
        onSave={value => save.mutateAsync({ alertEmailTo: value })}
        onClear={() => save.mutate({ alertEmailTo: null })}
        isSaving={save.isPending}
      />
      <NotificationChannelRow
        channel="slack"
        label="Slack"
        configured={settings.slackConfigured}
        masked={settings.slackMasked}
        onSave={value => save.mutateAsync({ slackWebhookUrl: value })}
        onClear={() => save.mutate({ slackWebhookUrl: null })}
        isSaving={save.isPending}
      />
    </div>
  )
}

export default function BudgetRulesPanel() {
  const qc = useQueryClient()
  const { rules, isLoading, isError } = useBudgetRules()
  const { insights } = useInsights()

  const [panelOpen, setPanelOpen] = useState(false)
  const [provider, setProvider] = useState<string>('')
  const [period, setPeriod] = useState<'daily' | 'weekly' | 'monthly'>('monthly')
  const [threshold, setThreshold] = useState<string>('')
  const [mutationError, setMutationError] = useState<string | null>(null)
  const [confirmDeleteId, setConfirmDeleteId] = useState<string | null>(null)

  const deleteRule = useMutation({
    mutationFn: deleteBudgetRule,
    onMutate: () => setMutationError(null),
    onSuccess: () => { qc.invalidateQueries({ queryKey: ['budget-rules'] }); setConfirmDeleteId(null) },
    onError: (e: Error) => setMutationError(`Couldn’t remove the rule: ${e.message}`),
  })

  const addRule = useMutation({
    mutationFn: () =>
      createBudgetRule({
        provider: provider === '' ? null : provider,
        period,
        thresholdGbp: parseFloat(threshold),
      }),
    onMutate: () => setMutationError(null),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['budget-rules'] })
      setPanelOpen(false)
      setProvider('')
      setPeriod('monthly')
      setThreshold('')
    },
    onError: (e: Error) => setMutationError(`Couldn’t add the rule: ${e.message}`),
  })

  const budgetAlerts = insights
    .filter(i => i.title.startsWith('Budget alert:'))
    .sort((a, b) => b.generatedAt.localeCompare(a.generatedAt))
    .slice(0, 10)

  function handleOpenPanel() {
    setProvider('')
    setPeriod('monthly')
    setThreshold('')
    setPanelOpen(true)
  }

  function handleSubmit(e: React.FormEvent) {
    e.preventDefault()
    const val = parseFloat(threshold)
    if (!Number.isFinite(val) || val <= 0) return
    addRule.mutate()
  }

  return (
    <section>
      <div className="panel">
        <div className="budget-rules__header">
          <div className="budget-rules__title-row">
            <span className="panel-title">
              Budget Rules
            </span>
          </div>
          {!isReadonly && (
            <Button variant="ghost" size="sm" onClick={handleOpenPanel} disabled={panelOpen}>
              + Add rule
            </Button>
          )}
        </div>

        <div className="budget-rules__body">
          {isError && <p className="panel-empty">Failed to load budget rules.</p>}
          {mutationError && <p className="panel-empty" role="alert">{mutationError}</p>}
          {!isError && !isLoading && rules.length === 0 && (
            <p className="panel-empty">No budget rules configured.</p>
          )}
          {rules.length > 0 && (
            <div className="model-table-wrap">
              <table className="budget-rules__table">
              <thead>
                <tr>
                  <th>
                    Provider
                  </th>
                  <th>
                    Period
                  </th>
                  <th>
                    Current / limit
                  </th>
                  <th>
                    Last fired
                  </th>
                  {!isReadonly && <th aria-label="Actions" />}
                </tr>
              </thead>
              <tbody>
                {rules.map(rule => (
                  <tr key={rule.id}>
                    <td data-label="Provider">
                      {rule.provider ? capitalize(rule.provider) : 'All providers'}
                    </td>
                    <td data-label="Period">
                      {capitalize(rule.period)}
                    </td>
                    <td data-label="Current / limit">
                      <span className="budget-rules__amount">{gbp(rule.currentSpendGbp)} / {gbp(rule.thresholdGbp)}</span>
                      <span className={`budget-rules__status${rule.currentSpendGbp > rule.thresholdGbp ? ' budget-rules__status--over' : ''}`}>
                        {rule.currentSpendGbp > rule.thresholdGbp ? 'Over limit' : 'Within limit'}
                      </span>
                    </td>
                    <td data-label="Last fired">
                      {rule.lastTriggeredAt
                        ? new Date(rule.lastTriggeredAt).toLocaleString()
                        : 'Never'}
                    </td>
                    {!isReadonly && (
                      <td className="budget-rules__actions">
                        {confirmDeleteId === rule.id ? (
                          <span>
                            <Button
                              variant="danger"
                              size="sm"
                              onClick={() => deleteRule.mutate(rule.id)}
                              disabled={deleteRule.isPending}
                            >
                              Confirm
                            </Button>
                            <Button variant="ghost" size="sm" onClick={() => setConfirmDeleteId(null)}>
                              Cancel
                            </Button>
                          </span>
                        ) : (
                          <Button variant="danger" size="sm" onClick={() => setConfirmDeleteId(rule.id)}>
                            Remove
                          </Button>
                        )}
                      </td>
                    )}
                  </tr>
                ))}
              </tbody>
              </table>
            </div>
          )}
        </div>
      </div>

      {panelOpen && (
        <div className="panel budget-rules__history">
          <div className="panel-title">Add Budget Rule</div>
          <form onSubmit={handleSubmit}>
            <div className="budget-rules__form-grid">
              <label className="budget-rules__field">
                Provider
                <select
                  value={provider}
                  onChange={e => setProvider(e.target.value)}
                  className="budget-rules__control"
                >
                  <option value="">All providers</option>
                  {PROVIDERS.map(p => (
                    <option key={p} value={p}>{capitalize(p)}</option>
                  ))}
                </select>
              </label>

              <label className="budget-rules__field">
                Period
                <select
                  value={period}
                  onChange={e => setPeriod(e.target.value as typeof period)}
                  className="budget-rules__control"
                >
                  {PERIODS.map(p => (
                    <option key={p} value={p}>{capitalize(p)}</option>
                  ))}
                </select>
              </label>

              <label className="budget-rules__field">
                Threshold (GBP)
                <input
                  type="number"
                  min="0.01"
                  step="0.01"
                  value={threshold}
                  onChange={e => setThreshold(e.target.value)}
                  placeholder="e.g. 50"
                  required
                  className="budget-rules__control"
                />
              </label>
            </div>

            <div className="budget-rules__actions">
              <Button type="submit" variant="primary" size="sm" disabled={addRule.isPending || threshold === ''}>
                {addRule.isPending ? 'Adding...' : 'Add rule'}
              </Button>
              <Button type="button" variant="ghost" size="sm" onClick={() => setPanelOpen(false)}>
                Cancel
              </Button>
            </div>
          </form>
        </div>
      )}

      <NotificationSettingsSection />

      <div className="panel budget-rules__history">
        <div className="panel-title">Alert History</div>
        {budgetAlerts.length === 0 ? (
          <p className="panel-empty">No budget alerts triggered.</p>
        ) : (
          <div className="budget-rules__history">
            {budgetAlerts.map(alert => (
              <div
                key={alert.id}
                className="insight insight-anomaly"
              >
                <div className="insight-title">{alert.title}</div>
                <div className="insight-body">
                  {alert.body}
                </div>
                <div className="budget-rules__history-time">
                  {new Date(alert.generatedAt).toLocaleString()}
                </div>
              </div>
            ))}
          </div>
        )}
      </div>
    </section>
  )
}
