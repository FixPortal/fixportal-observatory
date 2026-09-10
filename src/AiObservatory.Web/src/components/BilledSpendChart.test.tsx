import { expect, test } from 'vitest'
import { buildBilledComparisonSeries } from '../lib/billedComparison'

test('aligns daily periods by position and fills dates without spend with zero', () => {
  const result = buildBilledComparisonSeries(
    [
      { date: '2026-08-01', amountGbp: 100 },
      { date: '2026-08-01', amountGbp: -30 },
      { date: '2026-08-03', amountGbp: 40 },
    ],
    { from: new Date('2026-08-01T00:00:00'), to: new Date('2026-08-03T00:00:00') },
    [{ date: '2026-07-02', amountGbp: 20 }],
    { from: new Date('2026-07-01T00:00:00'), to: new Date('2026-07-03T00:00:00') },
  )

  expect(result).toEqual({
    grain: 'day',
    points: [
      { slot: 1, selectedDate: '2026-08-01', comparisonDate: '2026-07-01', selected: 70, comparison: 0 },
      { slot: 2, selectedDate: '2026-08-02', comparisonDate: '2026-07-02', selected: 0, comparison: 20 },
      { slot: 3, selectedDate: '2026-08-03', comparisonDate: '2026-07-03', selected: 40, comparison: 0 },
    ],
  })
})

test('uses weekly buckets for long arbitrary ranges', () => {
  const result = buildBilledComparisonSeries(
    [
      { date: '2026-01-01', amountGbp: 10 },
      { date: '2026-01-07', amountGbp: 20 },
      { date: '2026-01-08', amountGbp: 30 },
    ],
    { from: new Date('2026-01-01T00:00:00'), to: new Date('2026-04-30T00:00:00') },
    [],
    { from: new Date('2025-09-03T00:00:00'), to: new Date('2025-12-31T00:00:00') },
  )

  expect(result.grain).toBe('week')
  expect(result.points.slice(0, 2)).toEqual([
    { slot: 1, selectedDate: '2026-01-01', comparisonDate: '2025-09-03', selected: 30, comparison: 0 },
    { slot: 2, selectedDate: '2026-01-08', comparisonDate: '2025-09-10', selected: 30, comparison: 0 },
  ])
})
