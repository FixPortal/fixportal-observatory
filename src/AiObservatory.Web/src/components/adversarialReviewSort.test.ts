import { describe, expect, it } from 'vitest'
import { filterStats, sortStats, filterRunGroups, filterRunGroupsByStatus, sortRunGroups } from './adversarialReviewSort'
import type { AdversarialReviewStats } from '../api/client'
import type { RunGroup } from './adversarialReviewGrouping'

const stat = (overrides: Partial<AdversarialReviewStats>): AdversarialReviewStats => ({
  reviewer: 'anthropic', model: 'claude-sonnet-4-6', role: 'reviewer',
  runCount: 1, avgCostPerRun: 1, avgIssuesRaised: 1, avgIssuesAccepted: 1,
  avgCostPerAcceptedFinding: 1, avgDurationMs: 1000,
  ...overrides,
})

describe('filterStats', () => {
  it('matches on reviewer or model, case-insensitively', () => {
    const result = filterStats(
      [stat({ reviewer: 'anthropic' }), stat({ reviewer: 'google', model: 'gemini-2.5-pro' })],
      'GEMINI',
    )
    expect(result).toHaveLength(1)
    expect(result[0].reviewer).toBe('google')
  })

  it('returns everything when query is blank', () => {
    expect(filterStats([stat({}), stat({ reviewer: 'google' })], '  ')).toHaveLength(2)
  })
})

describe('sortStats', () => {
  it('sorts by avgCostPerRun ascending', () => {
    const cheap = stat({ reviewer: 'a', avgCostPerRun: 0.1 })
    const pricey = stat({ reviewer: 'b', avgCostPerRun: 5 })
    const result = sortStats([pricey, cheap], 'avgCostPerRun', 'asc')
    expect(result[0].reviewer).toBe('a')
  })

  it('sorts missing avgCostPerAcceptedFinding last in both directions', () => {
    const noAccepted = stat({ reviewer: 'a', avgCostPerAcceptedFinding: null })
    const cheap = stat({ reviewer: 'b', avgCostPerAcceptedFinding: 1 })
    const pricey = stat({ reviewer: 'c', avgCostPerAcceptedFinding: 10 })

    expect(sortStats([noAccepted, pricey, cheap], 'avgCostPerAcceptedFinding', 'asc').map((s) => s.reviewer))
      .toEqual(['b', 'c', 'a'])
    expect(sortStats([noAccepted, pricey, cheap], 'avgCostPerAcceptedFinding', 'desc').map((s) => s.reviewer))
      .toEqual(['c', 'b', 'a'])
  })

  it('sorts by reviewer alphabetically', () => {
    const result = sortStats([stat({ reviewer: 'openai' }), stat({ reviewer: 'anthropic' })], 'reviewer', 'asc')
    expect(result.map((s) => s.reviewer)).toEqual(['anthropic', 'openai'])
  })
})

const group = (overrides: Partial<RunGroup>): RunGroup => ({
  runId: 'R1', repo: 'fix-portal/example', summary: null, recordedAt: '2026-07-01T09:00:00Z',
  participants: [], totals: { raised: 1, accepted: 1, costUsd: 1, durationMs: 1000 },
  isComplete: true, statusReason: 'complete', isValid: true, chunkCount: null,
  ...overrides,
})

describe('filterRunGroups', () => {
  it('matches on repo or summary, case-insensitively', () => {
    const result = filterRunGroups(
      [group({ runId: 'R1', repo: 'fix-portal/alpha' }), group({ runId: 'R2', repo: 'fix-portal/beta' })],
      'BETA',
    )
    expect(result).toHaveLength(1)
    expect(result[0].runId).toBe('R2')
  })
})

describe('filterRunGroupsByStatus', () => {
  const complete = group({ runId: 'R1', isComplete: true, isValid: true })
  const incompleteValid = group({ runId: 'R2', isComplete: false, isValid: true })
  const incompleteInvalid = group({ runId: 'R3', isComplete: false, isValid: false })

  it('passes everything through with "all"/"all"', () => {
    expect(filterRunGroupsByStatus([complete, incompleteValid, incompleteInvalid], 'all', 'all')).toHaveLength(3)
  })

  it('filters to complete only', () => {
    const result = filterRunGroupsByStatus([complete, incompleteValid], 'complete', 'all')
    expect(result.map((g) => g.runId)).toEqual(['R1'])
  })

  it('filters to incomplete only', () => {
    const result = filterRunGroupsByStatus([complete, incompleteValid], 'incomplete', 'all')
    expect(result.map((g) => g.runId)).toEqual(['R2'])
  })

  it('filters to invalid only', () => {
    const result = filterRunGroupsByStatus([incompleteValid, incompleteInvalid], 'all', 'invalid')
    expect(result.map((g) => g.runId)).toEqual(['R3'])
  })

  it('combines completeness and validity filters', () => {
    const result = filterRunGroupsByStatus([complete, incompleteValid, incompleteInvalid], 'incomplete', 'valid')
    expect(result.map((g) => g.runId)).toEqual(['R2'])
  })
})

describe('sortRunGroups', () => {
  it('sorts by recordedAt descending by default direction', () => {
    const older = group({ runId: 'R1', recordedAt: '2026-07-01T09:00:00Z' })
    const newer = group({ runId: 'R2', recordedAt: '2026-07-02T09:00:00Z' })
    expect(sortRunGroups([older, newer], 'recordedAt', 'desc')[0].runId).toBe('R2')
  })

  it('sorts by total cost', () => {
    const cheap = group({ runId: 'R1', totals: { raised: 1, accepted: 1, costUsd: 0.1, durationMs: 1000 } })
    const pricey = group({ runId: 'R2', totals: { raised: 1, accepted: 1, costUsd: 5, durationMs: 1000 } })
    expect(sortRunGroups([cheap, pricey], 'costUsd', 'desc')[0].runId).toBe('R2')
  })

  it('treats a null repo as sorting first', () => {
    const noRepo = group({ runId: 'R1', repo: null })
    const withRepo = group({ runId: 'R2', repo: 'fix-portal/example' })
    expect(sortRunGroups([withRepo, noRepo], 'repo', 'asc')[0].runId).toBe('R1')
  })
})
