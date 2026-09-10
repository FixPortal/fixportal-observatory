import { test, expect } from 'vitest'
import { groupRuns, formatSeconds, formatMinutes, bankersRound } from './adversarialReviewGrouping'
import type { AdversarialReviewRun } from '../api/client'

function run(p: Partial<AdversarialReviewRun>): AdversarialReviewRun {
  return {
    id: 'test-run',
    reviewer: 'anthropic', model: 'claude-sonnet-4-6', role: 'reviewer', repo: 'r', summary: null,
    inputTokens: 0, outputTokens: 0, costUsd: 0, reviewDurationMs: 0,
    issuesRaised: 0, issuesAccepted: 0, costPerAcceptedFinding: null, chunkCount: null,
    runId: 'R1', recordedAt: '2026-06-27T12:00:00Z', ...p,
  }
}

test('groups participants by runId', () => {
  const groups = groupRuns([
    run({ runId: 'R1', reviewer: 'anthropic', role: 'reviewer' }),
    run({ runId: 'R1', reviewer: 'google', role: 'reviewer' }),
    run({ runId: 'R1', reviewer: 'openai', role: 'reviewer' }),
    run({ runId: 'R1', reviewer: 'anthropic', role: 'judge', model: 'claude-opus-4-8' }),
  ])
  expect(groups).toHaveLength(1)
  expect(groups[0].participants).toHaveLength(4)
})

test('sums per-column totals across participants', () => {
  const groups = groupRuns([
    run({ runId: 'R1', reviewer: 'anthropic', issuesRaised: 4, issuesAccepted: 3, costUsd: 0.2, reviewDurationMs: 1000 }),
    run({ runId: 'R1', reviewer: 'openai', issuesRaised: 5, issuesAccepted: 4, costUsd: 0.4, reviewDurationMs: 2000 }),
  ])
  expect(groups[0].totals).toEqual({ raised: 9, accepted: 7, costUsd: 0.6, durationMs: 3000 })
})

test('flags incomplete when fewer than 4 reviewer vendors', () => {
  const groups = groupRuns([run({ runId: 'R1', reviewer: 'anthropic', role: 'reviewer' })])
  expect(groups[0].isComplete).toBe(false)
  expect(groups[0].statusReason).toContain('1 of 4')
})

test('a pre-Moonshot 3-vendor run reads as incomplete (3 of 4)', () => {
  const base = ['anthropic', 'google', 'openai'].map(v => run({ runId: 'R1', reviewer: v, role: 'reviewer' }))
  const withJudge = groupRuns([...base, run({ runId: 'R1', reviewer: 'anthropic', role: 'judge' })])[0]
  expect(withJudge.isComplete).toBe(false)
  expect(withJudge.statusReason).toContain('3 of 4')
})

test('complete needs 4 reviewer vendors and a judge', () => {
  const base = ['anthropic', 'google', 'openai', 'moonshot'].map(v => run({ runId: 'R1', reviewer: v, role: 'reviewer' }))
  expect(groupRuns(base)[0].isComplete).toBe(false) // no judge yet
  expect(groupRuns([...base, run({ runId: 'R1', reviewer: 'anthropic', role: 'judge' })])[0].isComplete).toBe(true)
})

test('unknown reviewer cannot replace a required vendor', () => {
  const reviewers = ['anthropic', 'google', 'openai', 'moonsh0t']
    .map(v => run({ runId: 'R1', reviewer: v, role: 'reviewer' }))
  const group = groupRuns([...reviewers, run({ runId: 'R1', reviewer: 'anthropic', role: 'judge' })])[0]

  expect(group.isComplete).toBe(false)
  expect(group.statusReason).toContain('3 of 4')
})

test('orders participants reviewers-then-judge, newest run first', () => {
  const groups = groupRuns([
    run({ runId: 'OLD', recordedAt: '2026-06-20T00:00:00Z' }),
    run({ runId: 'NEW', recordedAt: '2026-06-27T00:00:00Z', reviewer: 'anthropic', role: 'judge' }),
    run({ runId: 'NEW', recordedAt: '2026-06-27T00:00:00Z', reviewer: 'openai', role: 'reviewer' }),
  ])
  expect(groups[0].runId).toBe('NEW')
  expect(groups[0].participants.map(p => p.role)).toEqual(['reviewer', 'judge'])
})

test('group recordedAt is the earliest participant time, independent of sort order', () => {
  // Judge recorded last but sorts last; an unknown vendor recorded first but
  // would sort oddly. The group timestamp must be the earliest, not participants[0].
  const groups = groupRuns([
    run({ runId: 'R1', reviewer: 'openai', role: 'reviewer', recordedAt: '2026-06-27T12:00:30Z' }),
    run({ runId: 'R1', reviewer: 'anthropic', role: 'judge', recordedAt: '2026-06-27T12:00:45Z' }),
    run({ runId: 'R1', reviewer: 'anthropic', role: 'reviewer', recordedAt: '2026-06-27T12:00:05Z' }),
  ])
  expect(groups[0].recordedAt).toBe('2026-06-27T12:00:05Z')
})

test('unknown vendor sorts after known reviewers and before the judge', () => {
  const groups = groupRuns([
    run({ runId: 'R1', reviewer: 'anthropic', role: 'judge' }),
    run({ runId: 'R1', reviewer: 'mistral', role: 'reviewer' }),
    run({ runId: 'R1', reviewer: 'anthropic', role: 'reviewer' }),
    run({ runId: 'R1', reviewer: 'openai', role: 'reviewer' }),
  ])
  expect(groups[0].participants.map(p => p.reviewer)).toEqual(['anthropic', 'openai', 'mistral', 'anthropic'])
  expect(groups[0].participants.map(p => p.role)).toEqual(['reviewer', 'reviewer', 'reviewer', 'judge'])
})

test('group summary is taken from any participant carrying one, else null', () => {
  const named = groupRuns([
    run({ runId: 'R1', reviewer: 'anthropic', summary: null }),
    run({ runId: 'R1', reviewer: 'openai', summary: 'Verifying adjusted formatting' }),
  ])
  expect(named[0].summary).toBe('Verifying adjusted formatting')

  const unnamed = groupRuns([run({ runId: 'R2', reviewer: 'anthropic', summary: null })])
  expect(unnamed[0].summary).toBeNull()
})

test('group chunkCount comes from any participant carrying one, else null', () => {
  const batched = groupRuns([
    run({ runId: 'R1', reviewer: 'anthropic', chunkCount: 21 }),
    run({ runId: 'R1', reviewer: 'openai', chunkCount: 21 }),
  ])
  expect(batched[0].chunkCount).toBe(21)

  const single = groupRuns([run({ runId: 'R2', reviewer: 'anthropic' })])
  expect(single[0].chunkCount).toBeNull()
})

test('formatSeconds renders whole seconds', () => {
  expect(formatSeconds(38000)).toBe('38s')
  expect(formatSeconds(188036)).toBe('188s')
  expect(formatSeconds(0)).toBe('0s')
})

test('formatMinutes renders minutes to 1dp', () => {
  expect(formatMinutes(188036)).toBe('3.1m')
  expect(formatMinutes(60000)).toBe('1.0m')
  expect(formatMinutes(0)).toBe('0.0m')
})

test('isValid is false below MIN_VALID_REVIEWERS, true at or above', () => {
  const solo = groupRuns([run({ runId: 'R1', reviewer: 'anthropic', role: 'reviewer' })])
  expect(solo[0].isValid).toBe(false)

  const pair = groupRuns([
    run({ runId: 'R2', reviewer: 'anthropic', role: 'reviewer' }),
    run({ runId: 'R2', reviewer: 'openai', role: 'reviewer' }),
  ])
  expect(pair[0].isValid).toBe(true)
})

test('bankersRound rounds half to even', () => {
  expect(bankersRound(0.125, 2)).toBeCloseTo(0.12) // tie → even (2)
  expect(bankersRound(0.135, 2)).toBeCloseTo(0.14) // tie → even (4)
  expect(bankersRound(2.5, 0)).toBe(2)      // tie → even
  expect(bankersRound(3.5, 0)).toBe(4)      // tie → even
  expect(bankersRound(4.24, 0)).toBe(4)     // normal round down
  expect(bankersRound(4.6, 0)).toBe(5)      // normal round up
  expect(bankersRound(0.03019, 2)).toBeCloseTo(0.03)
})
