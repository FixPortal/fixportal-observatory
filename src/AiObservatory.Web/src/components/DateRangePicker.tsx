import { useEffect, useRef, useState } from 'react'
import type { DateRangePreset } from '../lib/dateRange'

const PRESETS = [7, 31, 90] as const

interface Props {
  from: Date
  to: Date
  preset: DateRangePreset | 'custom'
  onPreset: (days: 7 | 31 | 90) => void
  onCustom: (from: Date, to: Date) => void
}

export default function DateRangePicker({ from, to, preset, onPreset, onCustom }: Props) {
  const [popoverOpen, setPopoverOpen] = useState(false)
  const [prevFrom, setPrevFrom] = useState(from)
  const [prevTo, setPrevTo] = useState(to)
  const [fromStr, setFromStr] = useState(() => from.toISOString().slice(0, 10))
  const [toStr, setToStr] = useState(() => to.toISOString().slice(0, 10))
  const popoverRef = useRef<HTMLDivElement>(null)
  const containerRef = useRef<HTMLDivElement>(null)

  // Sync input values when props change — use previous-prop tracking to avoid setState in an effect
  if (from !== prevFrom) {
    setPrevFrom(from)
    setFromStr(from.toISOString().slice(0, 10))
  }
  if (to !== prevTo) {
    setPrevTo(to)
    setToStr(to.toISOString().slice(0, 10))
  }

  useEffect(() => {
    if (!popoverOpen) return undefined

    function onKeyDown(e: KeyboardEvent) {
      if (e.key === 'Escape') setPopoverOpen(false)
    }

    function onMouseDown(e: MouseEvent) {
      if (
        popoverRef.current &&
        !popoverRef.current.contains(e.target as Node) &&
        containerRef.current &&
        !containerRef.current.contains(e.target as Node)
      ) {
        setPopoverOpen(false)
      }
    }

    document.addEventListener('keydown', onKeyDown)
    document.addEventListener('mousedown', onMouseDown)
    return () => {
      document.removeEventListener('keydown', onKeyDown)
      document.removeEventListener('mousedown', onMouseDown)
    }
  }, [popoverOpen])

  function handleApply() {
    const f = new Date(fromStr)
    const t = new Date(toStr)
    if (!isNaN(f.getTime()) && !isNaN(t.getTime())) {
      // Swap an inverted range rather than emitting from > to (which downstream floors to
      // a 1-day span and renders a misleading "no data" instead of the range the user meant).
      const [start, end] = f <= t ? [f, t] : [t, f]
      onCustom(start, end)
      setPopoverOpen(false)
    }
  }

  return (
    <div ref={containerRef} className="date-range">
      <div className="chart-toggle">
        {PRESETS.map(days => (
          <button
            key={days}
            type="button"
            className={`chart-toggle-btn${preset === days ? ' chart-toggle-btn--active' : ''}`}
            onClick={() => { setPopoverOpen(false); onPreset(days) }}
          >
            {days}d
          </button>
        ))}
        <button
          type="button"
          className={`chart-toggle-btn${preset === 'custom' ? ' chart-toggle-btn--active' : ''}`}
          onClick={() => setPopoverOpen(v => !v)}
        >
          Custom
        </button>
      </div>

      {popoverOpen && (
        <div ref={popoverRef} className="date-range__popover">
          <label className="date-range__field">
            From
            <input
              type="date"
              value={fromStr}
              max={toStr}
              onChange={e => setFromStr(e.target.value)}
              className="date-range__input"
            />
          </label>
          <label className="date-range__field">
            To
            <input
              type="date"
              value={toStr}
              min={fromStr}
              onChange={e => setToStr(e.target.value)}
              className="date-range__input"
            />
          </label>
          <button
            type="button"
            onClick={handleApply}
            className="date-range__apply"
          >
            Apply
          </button>
        </div>
      )}
    </div>
  )
}
