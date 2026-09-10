import { lazy, Suspense, useMemo } from 'react'
import type { ValueType } from 'recharts/types/component/DefaultTooltipContent'
import { useActivityDaily } from '../api/queries'
import { formatActiveTime } from '../lib/duration'
import { formatShortDate } from '../lib/format'
import { toActivityChartRows, toActivityComparisonRows } from './activityTrendRows'

const TEXT_MUTED = 'var(--text-muted)'

const ChartInner = lazy(() =>
  import('recharts').then(({ BarChart, Bar, XAxis, YAxis, Tooltip, Legend, ResponsiveContainer }) => ({
    default: function Inner({
      byDate, comparison, selectedLabel, comparisonLabel,
    }: {
      byDate: Record<string, unknown>[]
      comparison?: { grain: 'day' | 'week' }
      selectedLabel: string
      comparisonLabel: string
    }) {
      return (
        <ResponsiveContainer width="100%" height={160}>
          <BarChart data={byDate}>
            <XAxis
              dataKey={comparison ? 'slot' : 'date'}
              tickFormatter={comparison ? (value: number) => `${comparison.grain === 'day' ? 'Day' : 'Week'} ${value}` : formatShortDate}
              tick={{ fontSize: 10, fill: TEXT_MUTED }}
            />
            <YAxis tick={{ fontSize: 10, fill: TEXT_MUTED }} tickFormatter={(v: number) => `${v}m`} />
            <Tooltip
              contentStyle={{ background: 'var(--card-bg)', border: '1px solid var(--border)', borderRadius: 'var(--r-chip)', color: 'var(--text)' }}
              labelStyle={{ color: 'var(--text)' }}
              itemStyle={{ color: TEXT_MUTED }}
              labelFormatter={(label) => comparison
                ? `${comparison.grain === 'day' ? 'Day' : 'Week'} ${label}`
                : formatShortDate(String(label ?? ''))}
              formatter={(v: ValueType | undefined) => formatActiveTime(Number(Array.isArray(v) ? v[0] : v ?? 0) * 60)}
            />
            <Legend wrapperStyle={{ fontSize: 11, color: TEXT_MUTED }} />
            {comparison ? (
              <>
                <Bar dataKey="selectedMinutes" name={selectedLabel} fill="var(--brand)" />
                <Bar dataKey="comparisonMinutes" name={comparisonLabel} fill="var(--text-muted)" fillOpacity={0.55} />
              </>
            ) : (
              <>
                <Bar dataKey="wallClockMinutes" name="Active time" stackId="time" fill="var(--brand)" />
                <Bar dataKey="overlapMinutes" name="Parallel sessions" stackId="time" fill="var(--text-muted)" />
              </>
            )}
          </BarChart>
        </ResponsiveContainer>
      )
    },
  }))
)

interface Props {
  from?: Date
  to?: Date
  comparisonFrom?: Date
  comparisonTo?: Date
  selectedLabel?: string
  comparisonLabel?: string
}

export default function ActivityTrendChart({
  from, to, comparisonFrom, comparisonTo,
  selectedLabel = 'Selected period', comparisonLabel = 'Previous period',
}: Props) {
  const primary = useActivityDaily(from, to)
  const hasComparison = from && to && comparisonFrom && comparisonTo
  // Disabled when no comparison range was given: useActivityDaily with no range fetches
  // the default window — a request nobody asked for whose failure must not fail the chart.
  const comparisonQuery = useActivityDaily(comparisonFrom, comparisonTo, Boolean(hasComparison))

  const byDate = useMemo(
    () => hasComparison
      ? toActivityComparisonRows(primary.daily, { from, to }, comparisonQuery.daily, { from: comparisonFrom, to: comparisonTo })
      : { grain: 'day' as const, rows: toActivityChartRows(primary.daily) },
    [comparisonFrom, comparisonQuery.daily, comparisonTo, from, hasComparison, primary.daily, to],
  )

  if (primary.isError || (hasComparison && comparisonQuery.isError)) return <p className="panel-empty">Couldn’t load activity — try refreshing.</p>
  if (primary.isLoading || (hasComparison && comparisonQuery.isLoading)) return <div className="chart-skeleton" />
  if (primary.daily.length === 0 && (!hasComparison || comparisonQuery.daily.length === 0)) return <p className="panel-empty">No activity data for either period.</p>

  return (
    <Suspense fallback={<div style={{ height: 160 }} className="panel-empty">Loading chart...</div>}>
      <ChartInner
        byDate={byDate.rows.map(row => ({ ...row }))}
        comparison={hasComparison ? { grain: byDate.grain } : undefined}
        selectedLabel={selectedLabel}
        comparisonLabel={comparisonLabel}
      />
    </Suspense>
  )
}
