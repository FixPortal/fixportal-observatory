import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { renderHook, act } from '@testing-library/react'
import { useDateRange } from './dateRange'

describe('useDateRange', () => {
  const FIXED = new Date('2026-06-21T12:00:00Z')
  beforeEach(() => { vi.useFakeTimers(); vi.setSystemTime(FIXED) })
  afterEach(() => { vi.useRealTimers() })

  it('defaults to 31-day preset', () => {
    const { result } = renderHook(() => useDateRange())
    expect(result.current.preset).toBe(31)
    const { to, from } = result.current
    const diffDays = Math.round((to.getTime() - from.getTime()) / 86400000)
    expect(diffDays).toBe(30) // 31 days inclusive = 30 day difference
  })

  it('setPreset(7) sets a 7-day window', () => {
    const { result } = renderHook(() => useDateRange())
    act(() => result.current.setPreset(7))
    expect(result.current.preset).toBe(7)
    const diffDays = Math.round((result.current.to.getTime() - result.current.from.getTime()) / 86400000)
    expect(diffDays).toBe(6)
  })

  it('setCustom changes to custom preset', () => {
    const { result } = renderHook(() => useDateRange())
    const from = new Date('2026-05-01')
    const to = new Date('2026-05-31')
    act(() => result.current.setCustom(from, to))
    expect(result.current.preset).toBe('custom')
    expect(result.current.from).toEqual(from)
    expect(result.current.to).toEqual(to)
  })

  it('sets calendar month and quarter ranges from the local current date', () => {
    const { result } = renderHook(() => useDateRange())

    act(() => result.current.setPreset('thisMonth' as never))
    expect(result.current.from).toEqual(new Date('2026-06-01T00:00:00'))
    expect(result.current.to).toEqual(new Date('2026-06-21T12:00:00Z'))

    act(() => result.current.setPreset('lastMonth' as never))
    expect(result.current.from).toEqual(new Date('2026-05-01T00:00:00'))
    expect(result.current.to).toEqual(new Date('2026-05-31T00:00:00'))

    act(() => result.current.setPreset('thisQuarter' as never))
    expect(result.current.from).toEqual(new Date('2026-04-01T00:00:00'))
    expect(result.current.to).toEqual(new Date('2026-06-21T12:00:00Z'))
  })

  it('defaults comparison to the preceding equivalent period', () => {
    const { result } = renderHook(() => useDateRange())

    expect(result.current.comparisonFrom).toEqual(new Date('2026-04-21T12:00:00Z'))
    expect(result.current.comparisonTo).toEqual(new Date('2026-05-21T12:00:00Z'))

    act(() => result.current.setPreset('thisMonth' as never))
    expect(result.current.comparisonFrom).toEqual(new Date('2026-05-01T00:00:00'))
    expect(result.current.comparisonTo).toEqual(new Date('2026-05-31T00:00:00'))
  })

  it('allows an arbitrary comparison range', () => {
    const { result } = renderHook(() => useDateRange())
    const comparisonFrom = new Date('2026-01-01')
    const comparisonTo = new Date('2026-03-31')

    act(() => result.current.setComparison(comparisonFrom, comparisonTo))

    expect(result.current.comparisonMode).toBe('custom')
    expect(result.current.comparisonFrom).toEqual(comparisonFrom)
    expect(result.current.comparisonTo).toEqual(comparisonTo)
  })

  it('normalises inverted selected and comparison dates', () => {
    const { result } = renderHook(() => useDateRange())

    act(() => result.current.setCustom(new Date('2026-05-31'), new Date('2026-05-01')))
    expect(result.current.from).toEqual(new Date('2026-05-01'))
    expect(result.current.to).toEqual(new Date('2026-05-31'))

    act(() => result.current.setComparison(new Date('2026-03-31'), new Date('2026-01-01')))
    expect(result.current.comparisonFrom).toEqual(new Date('2026-01-01'))
    expect(result.current.comparisonTo).toEqual(new Date('2026-03-31'))
  })
})
