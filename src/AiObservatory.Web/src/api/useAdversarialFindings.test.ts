import { expect, test } from 'vitest'
import { FINDINGS_WINDOW_DAYS, findingsWindowStart } from './useAdversarialFindings'

test('the findings window opens FINDINGS_WINDOW_DAYS before the clock, as a UTC date', () => {
  const clock = { now: () => new Date('2026-11-01T23:30:00Z') }

  expect(FINDINGS_WINDOW_DAYS).toBe(180)
  expect(findingsWindowStart(clock)).toBe('2026-05-05')
})
