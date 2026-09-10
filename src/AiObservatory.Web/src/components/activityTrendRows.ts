import type { DailyActivity } from '../api/client'

export interface ActivityChartRow {
  date: string
  wallClockMinutes: number
  overlapMinutes: number
}

interface Range { from: Date; to: Date }

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

const localEpoch = (date: Date) => Date.UTC(date.getFullYear(), date.getMonth(), date.getDate())
const isoDate = (epoch: number) => new Date(epoch).toISOString().slice(0, 10)

export function toActivityComparisonRows(
  selectedDaily: DailyActivity[], selectedRange: Range,
  comparisonDaily: DailyActivity[], comparisonRange: Range,
): { grain: 'day' | 'week'; rows: ActivityComparisonRow[] } {
  const selectedStart = localEpoch(selectedRange.from)
  const comparisonStart = localEpoch(comparisonRange.from)
  const selectedDays = Math.round((localEpoch(selectedRange.to) - selectedStart) / 86_400_000) + 1
  const comparisonDays = Math.round((localEpoch(comparisonRange.to) - comparisonStart) / 86_400_000) + 1
  const bucketDays = Math.max(selectedDays, comparisonDays) > 92 ? 7 : 1
  // wallClockSeconds (not activeSeconds) — the single-period chart already dedupes
  // overlapping sessions to wall-clock time; using the raw per-session sum here would
  // make the same date range read as a bigger number when toggled into comparison mode.
  const selectedByDate = new Map(selectedDaily.map(day => [day.date, day.wallClockSeconds]))
  const comparisonByDate = new Map(comparisonDaily.map(day => [day.date, day.wallClockSeconds]))

  const rows = Array.from({ length: Math.ceil(Math.max(selectedDays, comparisonDays) / bucketDays) }, (_, index) => {
    const selectedOffset = index * bucketDays
    const comparisonOffset = index * bucketDays
    let selectedSeconds = 0
    let comparisonSeconds = 0
    for (let day = 0; day < bucketDays; day += 1) {
      if (selectedOffset + day < selectedDays) selectedSeconds += selectedByDate.get(isoDate(selectedStart + (selectedOffset + day) * 86_400_000)) ?? 0
      if (comparisonOffset + day < comparisonDays) comparisonSeconds += comparisonByDate.get(isoDate(comparisonStart + (comparisonOffset + day) * 86_400_000)) ?? 0
    }
    return {
      slot: index + 1,
      selectedDate: selectedOffset < selectedDays ? isoDate(selectedStart + selectedOffset * 86_400_000) : null,
      comparisonDate: comparisonOffset < comparisonDays ? isoDate(comparisonStart + comparisonOffset * 86_400_000) : null,
      selectedMinutes: Math.round(selectedSeconds / 60),
      comparisonMinutes: Math.round(comparisonSeconds / 60),
    }
  })

  return { grain: bucketDays === 1 ? 'day' : 'week', rows }
}
