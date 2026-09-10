import { lazy, Suspense, useMemo, useState } from 'react'
import type { ValueType } from 'recharts/types/component/DefaultTooltipContent'
import type { DailyAggregate } from '../api/client'
import { useAggregates } from '../api/queries'
import { PROVIDER_ORDER, providerDisplayName } from '../config/providers'
import { observedTokens } from '../lib/costSummary'
import { providerColor } from '../theme/providerColors'

/* eslint-disable react-refresh/only-export-components -- focused tests exercise the chart's pure evidence shaping without Recharts internals */

export type ProviderSplitMode = 'notional' | 'tokens' | 'activity'
export interface ProviderSlice { provider: string; name: string; value: number; share: number }

const providerOrder = (provider: string) => {
  const index = PROVIDER_ORDER.findIndex(known => known === provider)
  return index < 0 ? Number.MAX_SAFE_INTEGER : index
}

export function buildProviderSlices(rows: DailyAggregate[], mode: ProviderSplitMode): ProviderSlice[] {
  const selected = mode === 'notional'
    ? rows.filter(row => row.costBasis === 'notional' && row.requestCount > row.unknownCostCount)
    : rows
  const totals = selected.reduce<Record<string, number>>((result, row) => {
    const value = mode === 'tokens'
      ? observedTokens(row)
      : mode === 'notional' ? row.costUsd : row.requestCount
    result[row.provider] = (result[row.provider] ?? 0) + value
    return result
  }, {})
  const total = Object.values(totals).reduce((sum, value) => sum + value, 0)
  if (total <= 0) return []

  return Object.entries(totals)
    .map(([provider, value]) => ({
      provider,
      name: providerDisplayName(provider),
      value,
      share: Number(((value / total) * 100).toFixed(2)),
    }))
    .toSorted((a, b) => providerOrder(a.provider) - providerOrder(b.provider) || a.name.localeCompare(b.name))
}

const ChartInner = lazy(() =>
  import('recharts').then(({ PieChart, Pie, Cell, Tooltip, Legend, ResponsiveContainer }) => ({
    default: function Inner({ data, mode }: { data: ProviderSlice[]; mode: ProviderSplitMode }) {
      return (
        <ResponsiveContainer width="100%" height={200}>
          <PieChart>
            <Pie data={data} dataKey="value" nameKey="name" innerRadius={50} outerRadius={80}>
              {data.map(entry => <Cell key={entry.provider} fill={providerColor(entry.provider)} />)}
            </Pie>
            <Tooltip
              contentStyle={{ background: 'var(--card-bg)', border: '1px solid var(--border)', borderRadius: 'var(--r-chip)', color: 'var(--text)' }}
              labelStyle={{ color: 'var(--text)' }}
              itemStyle={{ color: 'var(--text-muted)' }}
              formatter={(value: ValueType | undefined, _name, item) => {
                const amount = Number(Array.isArray(value) ? value[0] : value ?? 0)
                const share = Number((item.payload as ProviderSlice | undefined)?.share ?? 0)
                const measured = mode === 'notional'
                  ? amount.toLocaleString(undefined, { style: 'currency', currency: 'USD' })
                  : `${amount.toLocaleString()} ${mode === 'tokens' ? 'tokens' : 'requests'}`
                return `${measured} · ${share.toFixed(1)}%`
              }}
            />
            <Legend />
          </PieChart>
        </ResponsiveContainer>
      )
    },
  })),
)

interface Props { from?: Date; to?: Date }

export default function ProviderSplit({ from, to }: Props) {
  const { aggregates, isLoading } = useAggregates(from, to)
  const [mode, setMode] = useState<ProviderSplitMode>('notional')
  const data = useMemo(() => buildProviderSlices(aggregates, mode), [aggregates, mode])

  return (
    <>
      <div className="chart-controls">
        <div className="chart-toggle" role="group" aria-label="Provider share metric">
          {(['notional', 'tokens', 'activity'] as const).map(option => (
            <button
              key={option}
              type="button"
              aria-pressed={mode === option}
              className={`chart-toggle-btn ${mode === option ? 'chart-toggle-btn--active' : ''}`}
              onClick={() => setMode(option)}
            >
              {option === 'notional' ? 'Notional' : option === 'tokens' ? 'Tokens' : 'Activity'}
            </button>
          ))}
        </div>
      </div>
      {isLoading ? (
        <div style={{ height: 200 }} className="chart-skeleton" />
      ) : data.length === 0 ? (
        <p className="panel-empty">Not reported for this period.</p>
      ) : (
        <Suspense fallback={<div style={{ height: 200 }} className="panel-empty">Loading chart...</div>}>
          <ChartInner data={data} mode={mode} />
        </Suspense>
      )}
    </>
  )
}
