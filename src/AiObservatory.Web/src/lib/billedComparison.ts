import { buildComparisonSeries, type ComparisonRange } from './comparisonSeries'

interface DailyPoint { date: string; amountGbp: number }

export interface BilledComparisonPoint {
  slot: number
  selectedDate: string | null
  comparisonDate: string | null
  selected: number
  comparison: number
}

export function buildBilledComparisonSeries(
  selectedSeries: DailyPoint[],
  selectedRange: ComparisonRange,
  comparisonSeries: DailyPoint[],
  comparisonRange: ComparisonRange,
): { grain: 'day' | 'week'; points: BilledComparisonPoint[] } {
  return buildComparisonSeries(
    selectedSeries.map(({ date, amountGbp }) => ({ date, value: amountGbp })), selectedRange,
    comparisonSeries.map(({ date, amountGbp }) => ({ date, value: amountGbp })), comparisonRange,
  )
}
