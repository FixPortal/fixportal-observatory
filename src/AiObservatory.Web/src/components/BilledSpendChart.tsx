import { Bar, BarChart, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'
import type { ValueType } from 'recharts/types/component/DefaultTooltipContent'
import { gbp } from '../lib/currency'
import { formatShortDate } from '../lib/format'
import type { BilledReporting } from '../api/client'
import { buildBilledComparisonSeries } from '../lib/billedComparison'

const TEXT_MUTED = 'var(--text-muted)'

interface Props {
  data: BilledReporting['dailySeries']
  range?: { from: Date; to: Date }
  comparisonData?: BilledReporting['dailySeries']
  comparisonRange?: { from: Date; to: Date }
  selectedLabel?: string
  comparisonLabel?: string
}

export default function BilledSpendChart({
  data, range, comparisonData, comparisonRange,
  selectedLabel = 'Selected period', comparisonLabel = 'Previous period',
}: Props) {
  const comparison = range && comparisonData && comparisonRange
    ? buildBilledComparisonSeries(data, range, comparisonData, comparisonRange)
    : null
  const chartData: Record<string, unknown>[] = comparison
    ? comparison.points.map(point => ({ ...point }))
    : data.map(point => ({ ...point }))

  return (
    <>
      <ResponsiveContainer width="100%" height={220}>
        <BarChart data={chartData}>
          <XAxis
            dataKey={comparison ? 'slot' : 'date'}
            tickFormatter={comparison ? (value: number) => `${comparison.grain === 'day' ? 'Day' : 'Week'} ${value}` : formatShortDate}
            tick={{ fontSize: 10, fill: TEXT_MUTED }}
          />
          <YAxis tick={{ fontSize: 10, fill: TEXT_MUTED }} tickFormatter={(value: number) => gbp(value)} />
          <Tooltip
            contentStyle={{ background: 'var(--card-bg)', border: '1px solid var(--border)', borderRadius: 'var(--r-chip)', color: 'var(--text)' }}
            labelStyle={{ color: 'var(--text)' }}
            itemStyle={{ color: TEXT_MUTED }}
            labelFormatter={value => comparison
              ? `${comparison.grain === 'day' ? 'Day' : 'Week'} ${value}`
              : formatShortDate(String(value ?? ''))}
            formatter={(value: ValueType | undefined) => gbp(Number(Array.isArray(value) ? value[0] : value ?? 0))}
          />
          {comparison ? (
            <>
              <Bar dataKey="selected" name={selectedLabel} fill="var(--brand)" />
              <Bar dataKey="comparison" name={comparisonLabel} fill="var(--text-muted)" fillOpacity={0.55} />
            </>
          ) : (
            <Bar dataKey="amountGbp" name="Billed spend" fill="var(--brand)" />
          )}
        </BarChart>
      </ResponsiveContainer>
      {comparison && (
        <div className="spend-chart__legend" aria-label="Billed spend comparison">
          <span><i className="spend-chart__swatch spend-chart__swatch--selected" />{selectedLabel}</span>
          <span><i className="spend-chart__swatch spend-chart__swatch--comparison" />{comparisonLabel}</span>
        </div>
      )}
    </>
  )
}
