export interface ComparisonRange { from: Date; to: Date }

interface DailyValue { date: string; value: number }

export interface ComparisonPoint {
  slot: number
  selectedDate: string | null
  comparisonDate: string | null
  selected: number
  comparison: number
}

const localEpoch = (date: Date) => Date.UTC(date.getFullYear(), date.getMonth(), date.getDate())
const isoDate = (epoch: number) => new Date(epoch).toISOString().slice(0, 10)

export function buildComparisonSeries(
  selectedSeries: DailyValue[], selectedRange: ComparisonRange,
  comparisonSeries: DailyValue[], comparisonRange: ComparisonRange,
): { grain: 'day' | 'week'; points: ComparisonPoint[] } {
  const selectedStart = localEpoch(selectedRange.from)
  const comparisonStart = localEpoch(comparisonRange.from)
  const selectedDays = Math.round((localEpoch(selectedRange.to) - selectedStart) / 86_400_000) + 1
  const comparisonDays = Math.round((localEpoch(comparisonRange.to) - comparisonStart) / 86_400_000) + 1
  const bucketDays = Math.max(selectedDays, comparisonDays) > 92 ? 7 : 1
  const totalsByDate = (series: DailyValue[]) => {
    const totals = new Map<string, number>()
    for (const point of series) totals.set(point.date, (totals.get(point.date) ?? 0) + point.value)
    return totals
  }
  const selectedByDate = totalsByDate(selectedSeries)
  const comparisonByDate = totalsByDate(comparisonSeries)

  const points = Array.from({ length: Math.ceil(Math.max(selectedDays, comparisonDays) / bucketDays) }, (_, index) => {
    const selectedOffset = index * bucketDays
    const comparisonOffset = index * bucketDays
    let selected = 0
    let comparison = 0
    for (let day = 0; day < bucketDays; day += 1) {
      if (selectedOffset + day < selectedDays) selected += selectedByDate.get(isoDate(selectedStart + (selectedOffset + day) * 86_400_000)) ?? 0
      if (comparisonOffset + day < comparisonDays) comparison += comparisonByDate.get(isoDate(comparisonStart + (comparisonOffset + day) * 86_400_000)) ?? 0
    }
    return {
      slot: index + 1,
      selectedDate: selectedOffset < selectedDays ? isoDate(selectedStart + selectedOffset * 86_400_000) : null,
      comparisonDate: comparisonOffset < comparisonDays ? isoDate(comparisonStart + comparisonOffset * 86_400_000) : null,
      selected,
      comparison,
    }
  })

  return { grain: bucketDays === 1 ? 'day' : 'week', points }
}
