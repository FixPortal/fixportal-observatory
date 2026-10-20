import { useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { Button } from '../design/Button'
import { StatusBadge } from '../design/StatusBadge'
import {
  dismissSuggestion,
  snoozeSuggestion,
  type Suggestion,
  type SuggestionsResponse,
} from '../api/client'
import { useSuggestions } from '../api/useSuggestions'
import { isReadonly } from '../auth/msal'

const SEVERITY_VARIANTS: Record<Suggestion['severity'], 'warn' | 'info'> = {
  attention: 'warn',
  info: 'info',
}

// Attention outranks info; within a severity the oldest firstSeenAt comes first so
// a long-standing condition is not buried under newer ones. ISO timestamps sort
// lexicographically, and Array#sort is stable for equal keys.
function bySeverityThenAge(a: Suggestion, b: Suggestion): number {
  if (a.severity !== b.severity) return a.severity === 'attention' ? -1 : 1
  if (a.firstSeenAt !== b.firstSeenAt) return a.firstSeenAt < b.firstSeenAt ? -1 : 1
  return 0
}

function SuggestionRow({ suggestion }: { suggestion: Suggestion }) {
  const qc = useQueryClient()
  const [expanded, setExpanded] = useState(false)

  const dismiss = useMutation({
    mutationFn: dismissSuggestion,
    // Settled, not success: a 404/409 means the row was resolved since the list loaded, and only
    // a refetch drops it. Errors stay silent like the insights feed.
    onSettled: () => qc.invalidateQueries({ queryKey: ['suggestions'] }),
  })
  const snooze = useMutation({
    mutationFn: ({ id, days }: { id: string; days: number }) => snoozeSuggestion(id, days),
    // Settled, not success: a 404/409 means the row was resolved since the list loaded, and only
    // a refetch drops it. Errors stay silent like the insights feed.
    onSettled: () => qc.invalidateQueries({ queryKey: ['suggestions'] }),
  })

  const evidenceId = `suggestion-evidence-${suggestion.id}`
  const { evidence } = suggestion

  return (
    <article
      className={`suggestion suggestion--${suggestion.severity}`}
      aria-label={suggestion.title}
    >
      <div className="suggestion__summary">
        <StatusBadge
          variant={SEVERITY_VARIANTS[suggestion.severity]}
          label={suggestion.severity === 'attention' ? 'Attention' : 'Info'}
        />
        <div className="suggestion__heading">
          <div className="suggestion__title">{suggestion.title}</div>
          <div className="suggestion__rule">{suggestion.ruleId}</div>
        </div>
        <Button
          variant="ghost"
          size="sm"
          aria-expanded={expanded}
          aria-controls={evidenceId}
          onClick={() => {
            setExpanded((open) => !open)
          }}
        >
          {expanded ? 'Hide evidence' : 'Show evidence'}
        </Button>
      </div>

      {expanded && (
        <div className="suggestion__evidence" id={evidenceId}>
          <dl className="suggestion__figures">
            {evidence.figures.map((figure) => (
              <div className="suggestion__figure" key={figure.label}>
                <dt className="suggestion__figure-label">{figure.label}</dt>
                <dd className="suggestion__figure-value">{figure.value}</dd>
                <dd className="suggestion__figure-unit">{figure.unit}</dd>
              </div>
            ))}
          </dl>
          <div className="suggestion__meta">
            <span>
              Window: {evidence.window.from} to {evidence.window.to}
            </span>
            <span>Sample size: {evidence.sampleSize}</span>
          </div>
          {evidence.runIds.length > 0 && (
            <p className="suggestion__run-ids">Run ids: {evidence.runIds.join(', ')}</p>
          )}
        </div>
      )}

      {!isReadonly && (
        <div className="suggestion__actions">
          <Button
            variant="ghost"
            size="sm"
            onClick={() => {
              dismiss.mutate(suggestion.id)
            }}
            disabled={dismiss.isPending}
          >
            Dismiss
          </Button>
          <Button
            variant="ghost"
            size="sm"
            onClick={() => {
              snooze.mutate({ id: suggestion.id, days: 7 })
            }}
            disabled={snooze.isPending}
          >
            Snooze 7 days
          </Button>
          <Button
            variant="ghost"
            size="sm"
            onClick={() => {
              snooze.mutate({ id: suggestion.id, days: 30 })
            }}
            disabled={snooze.isPending}
          >
            Snooze 30 days
          </Button>
        </div>
      )}
    </article>
  )
}

function NotEvaluatedFooter({
  notEvaluated,
}: {
  notEvaluated: SuggestionsResponse['notEvaluated']
}) {
  if (notEvaluated.length === 0) return null
  return (
    <details className="suggestions-card__not-evaluated">
      <summary>
        {notEvaluated.length} rule{notEvaluated.length === 1 ? '' : 's'} not evaluated
      </summary>
      <ul className="suggestions-card__not-evaluated-list">
        {notEvaluated.map((entry) => (
          <li key={entry.ruleId}>
            <span className="suggestions-card__not-evaluated-rule">{entry.ruleId}</span>
            <span>{entry.reason}</span>
          </li>
        ))}
      </ul>
    </details>
  )
}

export function SuggestionsCard() {
  const { suggestions, notEvaluated, isLoading, isError } = useSuggestions()

  // The response never says which rules ran, so the empty state stays exactly this
  // sentence; the not-evaluated footer (rendered below) carries what is known.
  if (isLoading) return <p className="panel-empty">Loading suggestions...</p>
  if (isError)
    return (
      <p className="panel-empty" role="alert">
        Couldn’t load suggestions — try refreshing
      </p>
    )

  const ordered = [...suggestions].sort(bySeverityThenAge)

  return (
    <section className="panel suggestions-card" aria-label="Suggestions">
      <div className="panel-title">Suggestions</div>
      {ordered.length === 0 ? (
        <p className="panel-empty">No open suggestions.</p>
      ) : (
        <div className="suggestions-card__rows">
          {ordered.map((suggestion) => (
            <SuggestionRow key={suggestion.id} suggestion={suggestion} />
          ))}
        </div>
      )}
      <NotEvaluatedFooter notEvaluated={notEvaluated} />
    </section>
  )
}
