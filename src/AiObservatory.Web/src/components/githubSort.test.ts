import { describe, expect, it } from 'vitest'
import { filterPrs, sortPrs, sortCommitSummaries, sortCiSummaries } from './githubSort'
import type { GitHubPr, GitHubCommitSummary, GitHubCiSummary } from '../api/client'

const pr = (overrides: Partial<GitHubPr>): GitHubPr => ({
  repo: 'fix-portal/example', number: 1, title: 'A PR', author: 'chris', state: 'open',
  createdAt: '2026-07-01T09:00:00Z', mergedAt: null, reviewCount: 0, turnaroundHours: null,
  ...overrides,
})

describe('filterPrs', () => {
  it('matches on title case-insensitively', () => {
    const result = filterPrs([pr({ title: 'Add Feature' }), pr({ title: 'Fix bug' })], 'feature')
    expect(result).toHaveLength(1)
    expect(result[0].title).toBe('Add Feature')
  })

  it('returns everything when query is blank', () => {
    const items = [pr({}), pr({ number: 2 })]
    expect(filterPrs(items, '  ')).toHaveLength(2)
  })
})

describe('sortPrs', () => {
  it('sorts by createdAt descending by default direction', () => {
    const older = pr({ number: 1, createdAt: '2026-07-01T09:00:00Z' })
    const newer = pr({ number: 2, createdAt: '2026-07-02T09:00:00Z' })
    const result = sortPrs([older, newer], 'createdAt', 'desc')
    expect(result[0].number).toBe(2)
  })

  it('sorts by reviewCount ascending', () => {
    const a = pr({ number: 1, reviewCount: 3 })
    const b = pr({ number: 2, reviewCount: 1 })
    const result = sortPrs([a, b], 'reviewCount', 'asc')
    expect(result[0].number).toBe(2)
  })

  it('sorts missing turnaround values last in both directions', () => {
    const unreviewed = pr({ number: 1, turnaroundHours: null })
    const fast = pr({ number: 2, turnaroundHours: 1 })
    const slow = pr({ number: 3, turnaroundHours: 10 })

    expect(sortPrs([unreviewed, slow, fast], 'turnaroundHours', 'asc').map((p) => p.number))
      .toEqual([2, 3, 1])
    expect(sortPrs([unreviewed, slow, fast], 'turnaroundHours', 'desc').map((p) => p.number))
      .toEqual([3, 2, 1])
  })
})

describe('sortCommitSummaries', () => {
  it('sorts by commitCount descending', () => {
    const a: GitHubCommitSummary = { repo: 'a', commitCount: 2, additions: 0, deletions: 0 }
    const b: GitHubCommitSummary = { repo: 'b', commitCount: 5, additions: 0, deletions: 0 }
    const result = sortCommitSummaries([a, b], 'commitCount', 'desc')
    expect(result[0].repo).toBe('b')
  })
})

describe('sortCiSummaries', () => {
  it('sorts by successRate ascending, surfacing the worst repo first', () => {
    const a: GitHubCiSummary = { repo: 'a', workflowName: 'ci', totalRuns: 10, failedRuns: 1, successRate: 90 }
    const b: GitHubCiSummary = { repo: 'b', workflowName: 'ci', totalRuns: 10, failedRuns: 5, successRate: 50 }
    const result = sortCiSummaries([a, b], 'successRate', 'asc')
    expect(result[0].repo).toBe('b')
  })
})
