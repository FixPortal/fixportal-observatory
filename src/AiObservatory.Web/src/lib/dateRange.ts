import { useState, useCallback } from 'react'

export type DateRangePreset = 7 | 31 | 90 | 'thisMonth' | 'lastMonth' | 'thisQuarter'

interface Range { from: Date; to: Date }

export const AGGREGATES_DAYS_RANGE = 31

export const dashboardDateRange = (to = new Date()): Range => ({
  from: shiftDays(to, -(AGGREGATES_DAYS_RANGE - 1)),
  to: new Date(to),
})

const orderedRange = (from: Date, to: Date): Range => from <= to ? { from, to } : { from: to, to: from }

function shiftDays(date: Date, days: number) {
  const shifted = new Date(date)
  shifted.setDate(shifted.getDate() + days)
  return shifted
}

function presetRange(preset: DateRangePreset, now = new Date()): Range {
  if (preset === 'thisMonth') {
    return { from: new Date(now.getFullYear(), now.getMonth(), 1), to: new Date(now) }
  }
  if (preset === 'lastMonth') {
    return {
      from: new Date(now.getFullYear(), now.getMonth() - 1, 1),
      to: new Date(now.getFullYear(), now.getMonth(), 0),
    }
  }
  if (preset === 'thisQuarter') {
    const quarterStartMonth = Math.floor(now.getMonth() / 3) * 3
    return { from: new Date(now.getFullYear(), quarterStartMonth, 1), to: new Date(now) }
  }
  const to = new Date(now)
  const from = shiftDays(to, -(preset - 1))
  return { from, to }
}

function previousRange(range: Range, preset: DateRangePreset | 'custom'): Range {
  if (preset === 'thisMonth' || preset === 'lastMonth') {
    return {
      from: new Date(range.from.getFullYear(), range.from.getMonth() - 1, 1),
      to: new Date(range.from.getFullYear(), range.from.getMonth(), 0),
    }
  }
  if (preset === 'thisQuarter') {
    return {
      from: new Date(range.from.getFullYear(), range.from.getMonth() - 3, 1),
      to: new Date(range.from.getFullYear(), range.from.getMonth(), 0),
    }
  }

  const days = Math.round((Date.UTC(range.to.getFullYear(), range.to.getMonth(), range.to.getDate())
    - Date.UTC(range.from.getFullYear(), range.from.getMonth(), range.from.getDate())) / 86_400_000) + 1
  const to = shiftDays(range.from, -1)
  return { from: shiftDays(to, -(days - 1)), to }
}

export function useDateRange() {
  const [preset, setPresetState] = useState<DateRangePreset | 'custom'>(31)
  const initial = dashboardDateRange()
  const [from, setFrom] = useState<Date>(initial.from)
  const [to, setTo] = useState<Date>(initial.to)
  const [comparisonMode, setComparisonMode] = useState<'previous' | 'custom'>('previous')
  const [customComparison, setCustomComparison] = useState<Range>(() => previousRange(initial, 31))

  const setPreset = useCallback((nextPreset: DateRangePreset) => {
    const range = presetRange(nextPreset)
    setPresetState(nextPreset)
    setFrom(range.from)
    setTo(range.to)
  }, [])

  const setCustom = useCallback((f: Date, t: Date) => {
    const range = orderedRange(f, t)
    setPresetState('custom')
    setFrom(range.from)
    setTo(range.to)
  }, [])

  const setComparison = useCallback((comparisonFrom: Date, comparisonTo: Date) => {
    setComparisonMode('custom')
    setCustomComparison(orderedRange(comparisonFrom, comparisonTo))
  }, [])

  const comparison = comparisonMode === 'previous'
    ? previousRange({ from, to }, preset)
    : customComparison

  return {
    from, to, preset, setPreset, setCustom,
    comparisonFrom: comparison.from,
    comparisonTo: comparison.to,
    comparisonMode,
    setComparison,
    compareWithPrevious: () => setComparisonMode('previous'),
  }
}
