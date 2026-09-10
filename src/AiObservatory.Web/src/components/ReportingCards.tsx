import { Card } from '../design/Card'
import { gbp } from '../lib/currency'
import type { BilledReporting } from '../api/client'

interface Props {
  report: BilledReporting | undefined
  comparisonReport?: BilledReporting
  comparisonLabel?: string
}

const comparisonText = (current: number, previous: number, label: string) => {
  const delta = current - previous
  return delta === 0
    ? `No change vs ${label}`
    : `${gbp(Math.abs(delta))} ${delta > 0 ? 'higher' : 'lower'} vs ${label}`
}

export default function ReportingCards({ report, comparisonReport, comparisonLabel = 'previous period' }: Props) {
  const summary = report?.entryCount ? report : undefined
  const previous = comparisonReport?.entryCount ? comparisonReport : undefined
  return (
    <div className="summary-cards">
      <Card>
        <div className="card-label">Billed spend</div>
        <div className="card-value card-value--lead">{summary ? gbp(summary.totalGbp) : '—'}</div>
        {summary && previous && <div className="card-sub">{comparisonText(summary.totalGbp, previous.totalGbp, comparisonLabel)}</div>}
      </Card>
      <Card>
        <div className="card-label">Daily average</div>
        <div className="card-value">{summary ? gbp(summary.dailyAverageGbp) : '—'}</div>
        {summary && previous && <div className="card-sub">{comparisonText(summary.dailyAverageGbp, previous.dailyAverageGbp, comparisonLabel)}</div>}
      </Card>
      <Card>
        <div className="card-label">30-day run rate</div>
        <div className="card-value">{summary ? gbp(summary.projectedMonthlyGbp) : '—'}</div>
        {summary && previous && <div className="card-sub">{comparisonText(summary.projectedMonthlyGbp, previous.projectedMonthlyGbp, comparisonLabel)}</div>}
      </Card>
      <Card>
        <div className="card-label">Top vendor</div>
        <div className="card-value card-value--model">{summary?.topVendorName ?? '—'}</div>
        {summary && <div className="card-sub">{summary.topVendorGbp === null ? '—' : gbp(summary.topVendorGbp)}</div>}
        {previous && <div className="card-sub">Previous: {previous.topVendorName ?? '—'} · {previous.topVendorGbp === null ? '—' : gbp(previous.topVendorGbp)}</div>}
      </Card>
    </div>
  )
}
