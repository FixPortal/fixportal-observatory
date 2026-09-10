import { useState } from 'react'
import ReactMarkdown from 'react-markdown'
import rehypeSanitize from 'rehype-sanitize'
import { Button } from '../design/Button'
import { StatusBadge } from '../design/StatusBadge'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { acknowledgeInsight, explainInsight, type Insight } from '../api/client'
import { useInsights } from '../api/queries'
import { isReadonly } from '../auth/msal'

const TYPE_LABELS: Record<string, string> = {
  summary: 'Summary', efficiency: 'Efficiency', anomaly: 'Anomaly', recommendation: 'Recommendation',
  budgetalert: 'Budget alert',
}

const INSIGHT_VARIANTS: Record<string, 'ok' | 'warn' | 'bad' | 'info'> = {
  anomaly: 'bad',
  efficiency: 'ok',
  budgetalert: 'warn',
}

const EXPLAINABLE = new Set(['recommendation', 'efficiency'])

function InsightRow({ insight }: { insight: Insight }) {
  const qc = useQueryClient()
  const [explanation, setExplanation] = useState<string | null>(null)
  const [explainError, setExplainError] = useState(false)

  const ack = useMutation({
    mutationFn: acknowledgeInsight,
    onSuccess: () => qc.invalidateQueries({ queryKey: ['insights'] }),
    onError: () => { /* dismiss silently fails; button re-enables for retry */ },
  })

  const explain = useMutation({
    mutationFn: () => explainInsight(insight.id),
    onMutate: () => setExplainError(false),
    onSuccess: (data) => {
      setExplainError(false)
      setExplanation(data.explanation)
      qc.invalidateQueries({ queryKey: ['insights'] })
    },
    onError: () => setExplainError(true),
  })

  const canExplain = EXPLAINABLE.has(insight.insightType)

  return (
    <div className={`insight insight-${insight.insightType}`}>
      {INSIGHT_VARIANTS[insight.insightType]
        ? <StatusBadge variant={INSIGHT_VARIANTS[insight.insightType]} label={TYPE_LABELS[insight.insightType] ?? insight.insightType} />
        : <div className="insight-type">{TYPE_LABELS[insight.insightType] ?? insight.insightType}</div>
      }
      <div className="insight-title">{insight.title}</div>
      <div className="insight-body">
        <ReactMarkdown rehypePlugins={[rehypeSanitize]}>{insight.body}</ReactMarkdown>
      </div>
      {explanation && (
        <div className="insight-explanation">
          <div className="insight-explanation__label">How to implement</div>
          <div className="insight-body">
            <ReactMarkdown rehypePlugins={[rehypeSanitize]}>{explanation}</ReactMarkdown>
          </div>
        </div>
      )}
      {explainError && (
        <p className="insight-explain-error">Failed to load guidance. Try again.</p>
      )}
      {!isReadonly && (
        <div className="insight-actions">
          {canExplain && !explanation && (
            <Button variant="ghost" size="sm" onClick={() => explain.mutate()} disabled={explain.isPending}>
              {explain.isPending ? 'Loading...' : 'How to implement'}
            </Button>
          )}
          <Button variant="ghost" size="sm" onClick={() => ack.mutate(insight.id)} disabled={ack.isPending}>Dismiss</Button>
        </div>
      )}
    </div>
  )
}

export default function InsightsFeed() {
  const { insights, isLoading } = useInsights()
  const unread = insights.filter(i => !i.acknowledged)
  if (isLoading) return <p className="panel-empty">Loading insights...</p>
  if (unread.length === 0) return <p className="panel-empty">No unread insights.</p>

  const visible = unread.slice(0, 5)
  const older = unread.slice(5)

  return (
    <div className="insights-feed">
      {visible.map(insight => (
        <InsightRow key={insight.id} insight={insight} />
      ))}
      {older.length > 0 && (
        <details className="insights-older">
          <summary>Show {older.length} older insight{older.length === 1 ? '' : 's'}</summary>
          <div className="insights-feed insights-feed--older">
            {older.map(insight => <InsightRow key={insight.id} insight={insight} />)}
          </div>
        </details>
      )}
    </div>
  )
}
