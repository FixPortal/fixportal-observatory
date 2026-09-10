import { describe, expect, it } from 'vitest'
import { toActivityChartRows, toActivityComparisonRows } from './activityTrendRows'

describe('toActivityChartRows', () => {
  it('clamps parallel session minutes at zero on idle days', () => {
    const result = toActivityChartRows([
      { date: '2026-07-01', activeSeconds: 600, wallClockSeconds: 7200 },
    ])

    expect(result[0].wallClockMinutes).toBe(10)
    expect(result[0].overlapMinutes).toBe(0)
  })

  it('keeps parallel session minutes when active time exceeds wall clock', () => {
    const result = toActivityChartRows([
      { date: '2026-07-01', activeSeconds: 7200, wallClockSeconds: 3600 },
    ])

    expect(result[0].wallClockMinutes).toBe(60)
    expect(result[0].overlapMinutes).toBe(60)
  })

  it('aligns selected and comparison activity by period position', () => {
    expect(toActivityComparisonRows(
      [
        { date: '2026-08-01', activeSeconds: 3600, wallClockSeconds: 1800 },
        { date: '2026-08-03', activeSeconds: 600, wallClockSeconds: 600 },
      ],
      { from: new Date('2026-08-01T00:00:00'), to: new Date('2026-08-03T00:00:00') },
      [{ date: '2026-07-02', activeSeconds: 1200, wallClockSeconds: 1200 }],
      { from: new Date('2026-07-01T00:00:00'), to: new Date('2026-07-03T00:00:00') },
    )).toEqual({
      grain: 'day',
      rows: [
        // wallClockSeconds (1800), not the raw activeSeconds sum (3600) — the comparison
        // chart must read the same deduped minutes as the single-period chart.
        { slot: 1, selectedDate: '2026-08-01', comparisonDate: '2026-07-01', selectedMinutes: 30, comparisonMinutes: 0 },
        { slot: 2, selectedDate: '2026-08-02', comparisonDate: '2026-07-02', selectedMinutes: 0, comparisonMinutes: 20 },
        { slot: 3, selectedDate: '2026-08-03', comparisonDate: '2026-07-03', selectedMinutes: 10, comparisonMinutes: 0 },
      ],
    })
  })

  it('sums wall-clock seconds (not raw active-seconds) across a multi-day range', () => {
    const { rows } = toActivityComparisonRows(
      [
        { date: '2026-08-01', activeSeconds: 900, wallClockSeconds: 600 },
        { date: '2026-08-02', activeSeconds: 1200, wallClockSeconds: 900 },
      ],
      { from: new Date('2026-08-01T00:00:00'), to: new Date('2026-08-02T00:00:00') },
      [],
      { from: new Date('2026-07-01T00:00:00'), to: new Date('2026-07-02T00:00:00') },
    )
    expect(rows.map(r => r.selectedMinutes)).toEqual([10, 15])
  })
})
