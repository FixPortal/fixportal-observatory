import type { DailyActivity } from '../api/client'
import { buildComparisonSeries, type ComparisonRange } from '../lib/comparisonSeries'

export interface ActivityChartRow {
  date: string
  wallClockMinutes: number
  overlapMinutes: number
}

export interface ActivityComparisonRow {
  slot: number
  selectedDate: string | null
  comparisonDate: string | null
  selectedMinutes: number
  comparisonMinutes: number
}

export function toActivityChartRows(daily: DailyActivity[]): ActivityChartRow[] {
  return daily
    .toSorted((a, b) => a.date.localeCompare(b.date))
    .map((d) => {
      const activeMinutes = Math.round(d.activeSeconds / 60)
      const wallClockMinutes = Math.round(d.wallClockSeconds / 60)
      return {
        date: d.date,
        wallClockMinutes: Math.min(activeMinutes, wallClockMinutes),
        overlapMinutes: Math.max(0, activeMinutes - wallClockMinutes),
      }
    })
}

export function toActivityComparisonRows(
  selectedDaily: DailyActivity[], selectedRange: ComparisonRange,
  comparisonDaily: DailyActivity[], comparisonRange: ComparisonRange,
): { grain: 'day' | 'week'; rows: ActivityComparisonRow[] } {
  // wallClockSeconds (not activeSeconds) — the single-period chart already dedupes
  // overlapping sessions to wall-clock time; using the raw per-session sum here would
  // make the same date range read as a bigger number when toggled into comparison mode.
  const series = buildComparisonSeries(
    selectedDaily.map(day => ({ date: day.date, value: day.wallClockSeconds })), selectedRange,
    comparisonDaily.map(day => ({ date: day.date, value: day.wallClockSeconds })), comparisonRange,
  )
  return {
    grain: series.grain,
    rows: series.points.map(({ selected, comparison, ...point }) => ({
      ...point,
      selectedMinutes: Math.round(selected / 60),
      comparisonMinutes: Math.round(comparison / 60),
    })),
  }
}
