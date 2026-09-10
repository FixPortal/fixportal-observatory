import { lazy, Suspense, useMemo, useState } from 'react'
import type { ValueType } from 'recharts/types/component/DefaultTooltipContent'
import type { DailyAggregate } from '../api/client'
import { useAggregates } from '../api/queries'
import { costBasisDisplayName, providerDisplayName, sourceDisplayName, usageScopeDisplayName } from '../config/providers'
import { useUsdToGbp, gbp } from '../lib/currency'
import { observedTokens } from '../lib/costSummary'
import { formatShortDate } from '../lib/format'
import { providerColor } from '../theme/providerColors'

/* eslint-disable react-refresh/only-export-components -- focused tests exercise the chart's pure evidence shaping without Recharts internals */

const TEXT_MUTED = 'var(--text-muted)'

export type UsageChartMode = 'tokens' | 'listPriceEstimate' | 'providerEstimated' | 'notional'

export interface UsageSeries {
  key: string
  provider: string
  sourceId: string
  scope: string
  basis: string
  providerLabel: string
  sourceLabel: string
  scopeLabel: string
  basisLabel: string
  label: string
}

export interface UsageChartResult {
  byDate: Record<string, string | number>[]
  series: UsageSeries[]
}

const grainKey = (parts: string[]) => parts.map(part => `${part.length}:${part}`).join('')

export function buildUsageSeries(rows: DailyAggregate[], mode: UsageChartMode, usdToGbp: number): UsageChartResult {
  const selected = mode === 'tokens'
    ? rows
    : rows.filter(row => row.costBasis === mode && row.requestCount > row.unknownCostCount)
  const seriesByKey = new Map<string, UsageSeries>()
  const dates = new Map<string, Record<string, string | number>>()

  for (const row of selected) {
    const basis = mode === 'tokens' ? row.costBasis : mode
    const key = grainKey([row.provider, row.sourceId, row.usageScope, basis])
    if (!seriesByKey.has(key)) {
      const providerLabel = providerDisplayName(row.provider)
      const sourceLabel = sourceDisplayName(row.sourceId)
      const scopeLabel = usageScopeDisplayName(row.usageScope)
      const basisLabel = costBasisDisplayName(basis)
      seriesByKey.set(key, {
        key, provider: row.provider, sourceId: row.sourceId, scope: row.usageScope, basis,
        providerLabel, sourceLabel, scopeLabel, basisLabel,
        label: `${providerLabel} · ${sourceLabel} · ${scopeLabel} · ${basisLabel}`,
      })
    }
    const date = dates.get(row.date) ?? { date: row.date }
    const value = mode === 'tokens'
      ? observedTokens(row)
      : row.costUsd * usdToGbp
    date[key] = Number((Number(date[key] ?? 0) + value).toFixed(4))
    dates.set(row.date, date)
  }

  const series = mode === 'tokens'
    ? [...seriesByKey.values()].filter(item => [...dates.values()].some(date => Number(date[item.key] ?? 0) !== 0))
    : [...seriesByKey.values()]
  const keys = new Set(series.map(item => item.key))
  const byDate = [...dates.values()]
    .map(date => Object.fromEntries(Object.entries(date).filter(([key]) => key === 'date' || keys.has(key))))
    .filter(date => Object.keys(date).length > 1)
    .toSorted((a, b) => String(a.date).localeCompare(String(b.date)))

  return { byDate, series }
}

const ChartInner = lazy(() =>
  import('recharts').then(({ BarChart, Bar, XAxis, YAxis, Tooltip, ResponsiveContainer }) => ({
    default: function Inner({ result, mode }: { result: UsageChartResult; mode: UsageChartMode }) {
      const tokens = mode === 'tokens'
      return (
        <ResponsiveContainer width="100%" height={160}>
          <BarChart data={result.byDate}>
            <XAxis dataKey="date" tickFormatter={formatShortDate} tick={{ fontSize: 10, fill: TEXT_MUTED }} />
            <YAxis
              tick={{ fontSize: 10, fill: TEXT_MUTED }}
              tickFormatter={(value: number) => tokens ? `${Number(value / 1_000_000).toFixed(1)}M` : `£${value}`}
            />
            <Tooltip
              contentStyle={{ background: 'var(--card-bg)', border: '1px solid var(--border)', borderRadius: 'var(--r-chip)', color: 'var(--text)' }}
              labelStyle={{ color: 'var(--text)' }}
              itemStyle={{ color: TEXT_MUTED }}
              labelFormatter={label => formatShortDate(String(label ?? ''))}
              formatter={(value: ValueType | undefined) => {
                const amount = Number(Array.isArray(value) ? value[0] : value ?? 0)
                return tokens ? `${amount.toLocaleString()} tokens` : gbp(amount, 3)
              }}
            />
            {result.series.map(series => (
              <Bar key={series.key} dataKey={series.key} name={series.label} stackId="usage" fill={providerColor(series.provider)} />
            ))}
          </BarChart>
        </ResponsiveContainer>
      )
    },
  })),
)

interface Props { from?: Date; to?: Date }

const MODES: { mode: UsageChartMode; label: string }[] = [
  { mode: 'tokens', label: 'Tokens' },
  { mode: 'listPriceEstimate', label: 'List-price estimate' },
  { mode: 'providerEstimated', label: 'Provider estimate' },
  { mode: 'notional', label: 'Notional' },
]

export default function SpendChart({ from, to }: Props) {
  const { aggregates, isLoading } = useAggregates(from, to)
  const rate = useUsdToGbp()
  const [mode, setMode] = useState<UsageChartMode>('notional')
  const result = useMemo(() => buildUsageSeries(aggregates, mode, rate), [aggregates, mode, rate])

  if (isLoading) return <div className="chart-skeleton" />
  if (aggregates.length === 0) return <p className="panel-empty">No usage data for this period.</p>

  return (
    <>
      <div className="chart-controls">
        <div className="chart-toggle" aria-label="Usage value" role="group">
          {MODES.map(option => (
            <button
              key={option.mode}
              type="button"
              aria-pressed={mode === option.mode}
              onClick={() => setMode(option.mode)}
              className={`chart-toggle-btn ${mode === option.mode ? 'chart-toggle-btn--active' : ''}`}
            >
              {option.label}
            </button>
          ))}
        </div>
      </div>
      {result.series.length === 0 ? (
        <p className="panel-empty">Not reported for this period.</p>
      ) : (
        <>
          <div className="usage-chart-plot">
            <Suspense fallback={<div style={{ height: 160 }} className="panel-empty">Loading chart...</div>}>
              <ChartInner result={result} mode={mode} />
            </Suspense>
          </div>
          <ul className="usage-chart-legend" aria-label="Usage series">
            {result.series.map(series => (
              <li key={series.key}>
                <span className="usage-chart-legend__swatch" style={{ background: providerColor(series.provider) }} aria-hidden="true" />
                <span>{series.label}</span>
              </li>
            ))}
          </ul>
        </>
      )}
    </>
  )
}
