import { gbp } from '../lib/currency'

interface Props {
  total: number
  entryCount: number
  largestCategory: string | null
  comparisonTotal?: number
  comparisonLabel?: string
}

/**
 * Region 2. Every figure here is of the CURRENT filter, not the calendar month —
 * that is what makes "the filtered aggregate" unambiguous.
 */
export default function SpendTotals({ total, entryCount, largestCategory, comparisonTotal, comparisonLabel }: Props) {
  const delta = comparisonTotal == null ? null : total - comparisonTotal
  const direction = delta == null || delta === 0 ? 'same' : delta < 0 ? 'lower' : 'higher'
  const percentage = comparisonTotal != null && comparisonTotal > 0 && delta !== null
    ? Math.abs(delta / comparisonTotal * 100)
    : null

  return (
    <div className="spend-totals">
      <div className="spend-totals__card">
        <span className="spend-totals__label">Filtered total</span>
        <span className="spend-totals__value">{gbp(total)}</span>
      </div>
      <div className="spend-totals__card">
        <span className="spend-totals__label">Entries</span>
        <span className="spend-totals__value">{entryCount}</span>
      </div>
      <div className="spend-totals__card">
        <span className="spend-totals__label">Largest category</span>
        <span className="spend-totals__value">{largestCategory ?? '—'}</span>
      </div>
      {delta !== null && (
        <div className={`spend-totals__card spend-totals__card--comparison spend-totals__card--${direction}`}>
          <span className="spend-totals__label">vs {comparisonLabel ?? 'comparison period'}</span>
          <span className="spend-totals__value">
            {direction === 'same' ? 'No change' : `${gbp(Math.abs(delta))} ${direction}`}
          </span>
          <span className="spend-totals__comparison">
            {percentage == null
              ? comparisonTotal === 0 ? 'No prior spend' : `Compared with ${gbp(comparisonTotal ?? 0)}`
              : `${percentage.toFixed(1)}%`}
          </span>
        </div>
      )}
    </div>
  )
}
