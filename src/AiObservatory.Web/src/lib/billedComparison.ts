interface DailyPoint { date: string; amountGbp: number }
interface Range { from: Date; to: Date }

export interface BilledComparisonPoint {
  slot: number
  selectedDate: string | null
  comparisonDate: string | null
  selected: number
  comparison: number
}

const localEpoch = (date: Date) => Date.UTC(date.getFullYear(), date.getMonth(), date.getDate())
const isoDate = (epoch: number) => new Date(epoch).toISOString().slice(0, 10)

export function buildBilledComparisonSeries(
  selectedSeries: DailyPoint[],
  selectedRange: Range,
  comparisonSeries: DailyPoint[],
  comparisonRange: Range,
): { grain: 'day' | 'week'; points: BilledComparisonPoint[] } {
  const selectedStart = localEpoch(selectedRange.from)
  const comparisonStart = localEpoch(comparisonRange.from)
  const selectedDays = Math.round((localEpoch(selectedRange.to) - selectedStart) / 86_400_000) + 1
  const comparisonDays = Math.round((localEpoch(comparisonRange.to) - comparisonStart) / 86_400_000) + 1
  const bucketDays = Math.max(selectedDays, comparisonDays) > 92 ? 7 : 1
  const grain = bucketDays === 1 ? 'day' : 'week'
  const selectedByDate = totalsByDate(selectedSeries)
  const comparisonByDate = totalsByDate(comparisonSeries)

  const points = Array.from({ length: Math.ceil(Math.max(selectedDays, comparisonDays) / bucketDays) }, (_, index) => {
    const selectedOffset = index * bucketDays
    const comparisonOffset = index * bucketDays
    let selected = 0
    let comparison = 0
    for (let day = 0; day < bucketDays; day += 1) {
      if (selectedOffset + day < selectedDays) {
        selected += selectedByDate.get(isoDate(selectedStart + (selectedOffset + day) * 86_400_000)) ?? 0
      }
      if (comparisonOffset + day < comparisonDays) {
        comparison += comparisonByDate.get(isoDate(comparisonStart + (comparisonOffset + day) * 86_400_000)) ?? 0
      }
    }
    return {
      slot: index + 1,
      selectedDate: selectedOffset < selectedDays ? isoDate(selectedStart + selectedOffset * 86_400_000) : null,
      comparisonDate: comparisonOffset < comparisonDays ? isoDate(comparisonStart + comparisonOffset * 86_400_000) : null,
      selected,
      comparison,
    }
  })

  return { grain, points }
}

function totalsByDate(series: DailyPoint[]) {
  const totals = new Map<string, number>()
  for (const point of series) totals.set(point.date, (totals.get(point.date) ?? 0) + point.amountGbp)
  return totals
}
