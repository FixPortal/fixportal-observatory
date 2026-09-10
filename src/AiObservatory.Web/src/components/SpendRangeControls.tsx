import type { DateRangePreset } from '../lib/dateRange'

const DATE_FORMAT = new Intl.DateTimeFormat('en-GB', { day: '2-digit', month: 'short', year: 'numeric' })

interface Props {
  from: Date
  to: Date
  preset: DateRangePreset | 'custom'
  comparisonFrom: Date
  comparisonTo: Date
  comparisonMode: 'previous' | 'custom'
  onPreset: (preset: DateRangePreset) => void
  onCustom: (from: Date, to: Date) => void
  onComparison: (from: Date, to: Date) => void
  onPreviousComparison: () => void
}

const PRESETS: { value: DateRangePreset; label: string }[] = [
  { value: 7, label: '7 days' },
  { value: 31, label: '31 days' },
  { value: 90, label: '90 days' },
  { value: 'thisMonth', label: 'This month' },
  { value: 'lastMonth', label: 'Last month' },
  { value: 'thisQuarter', label: 'This quarter' },
]

const inputDate = (date: Date) => {
  const year = date.getFullYear()
  const month = String(date.getMonth() + 1).padStart(2, '0')
  const day = String(date.getDate()).padStart(2, '0')
  return `${year}-${month}-${day}`
}

const parseInput = (value: string) => {
  const date = new Date(`${value}T00:00:00`)
  return Number.isNaN(date.getTime()) ? undefined : date
}
const withInputDate = (value: string, apply: (date: Date) => void) => {
  const date = parseInput(value)
  if (date) apply(date)
}
const label = (from: Date, to: Date) => `${DATE_FORMAT.format(from)} – ${DATE_FORMAT.format(to)}`

export default function SpendRangeControls({
  from, to, preset, comparisonFrom, comparisonTo, comparisonMode,
  onPreset, onCustom, onComparison, onPreviousComparison,
}: Props) {
  return (
    <div className="spend-periods">
      <div className="spend-periods__presets" role="group" aria-label="Selected period presets">
        {PRESETS.map(option => (
          <button
            key={option.value}
            type="button"
            aria-pressed={preset === option.value}
            className={`chart-toggle-btn${preset === option.value ? ' chart-toggle-btn--active' : ''}`}
            onClick={() => onPreset(option.value)}
          >
            {option.label}
          </button>
        ))}
      </div>

      <div className="spend-periods__rail">
        <div className="spend-periods__period">
          <span className="spend-periods__eyebrow">Selected</span>
          <strong>{label(from, to)}</strong>
          <div className="spend-periods__dates">
            <label>Selected from<input aria-label="Selected from" type="date" value={inputDate(from)} onChange={event => withInputDate(event.target.value, date => onCustom(date, to))} /></label>
            <span aria-hidden="true">→</span>
            <label>Selected to<input aria-label="Selected to" type="date" value={inputDate(to)} onChange={event => withInputDate(event.target.value, date => onCustom(from, date))} /></label>
          </div>
        </div>

        <span className="spend-periods__versus" aria-hidden="true">vs</span>

        <div className="spend-periods__period spend-periods__period--comparison">
          <div className="spend-periods__comparison-heading">
            <span className="spend-periods__eyebrow">Compare</span>
            <button
              type="button"
              className={`spend-periods__previous${comparisonMode === 'previous' ? ' spend-periods__previous--active' : ''}`}
              onClick={onPreviousComparison}
            >
              Previous period
            </button>
          </div>
          <strong>{label(comparisonFrom, comparisonTo)}</strong>
          <div className="spend-periods__dates">
            <label>Compare from<input aria-label="Compare from" type="date" value={inputDate(comparisonFrom)} onChange={event => withInputDate(event.target.value, date => onComparison(date, comparisonTo))} /></label>
            <span aria-hidden="true">→</span>
            <label>Compare to<input aria-label="Compare to" type="date" value={inputDate(comparisonTo)} onChange={event => withInputDate(event.target.value, date => onComparison(comparisonFrom, date))} /></label>
          </div>
        </div>
      </div>
    </div>
  )
}
