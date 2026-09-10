import { fireEvent, render, screen, within } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import GitHubPage from './GitHubPage'

vi.mock('../lib/dateRange', () => ({
  useDateRange: () => ({
    from: new Date('2026-08-01T00:00:00Z'),
    to: new Date('2026-08-31T00:00:00Z'),
    preset: 31,
    setPreset: vi.fn(),
    setCustom: vi.fn(),
    comparisonFrom: new Date('2026-07-01T00:00:00Z'),
    comparisonTo: new Date('2026-07-31T00:00:00Z'),
    comparisonMode: 'previous',
    setComparison: vi.fn(),
    compareWithPrevious: vi.fn(),
  }),
}))

const state = vi.hoisted(() => ({ loading: false, error: false }))

vi.mock('../api/queries', () => ({
  localDate: (date: Date) => date.toISOString().slice(0, 10),
  useGitHubPrs: (from: Date) => ({
    prs: from.getMonth() === 6 ? [
      { repo: 'fix-portal/a', number: 3, title: 'Earlier A', author: 'chris', state: 'merged', createdAt: '2026-07-01T09:00:00Z', mergedAt: '2026-07-01T12:00:00Z', reviewCount: 1, turnaroundHours: 3 },
      { repo: 'fix-portal/a', number: 4, title: 'Earlier A2', author: 'chris', state: 'merged', createdAt: '2026-07-02T09:00:00Z', mergedAt: '2026-07-02T12:00:00Z', reviewCount: 1, turnaroundHours: 3 },
      { repo: 'fix-portal/b', number: 5, title: 'Earlier B', author: 'chris', state: 'merged', createdAt: '2026-07-03T09:00:00Z', mergedAt: '2026-07-03T12:00:00Z', reviewCount: 1, turnaroundHours: 3 },
    ] : [
      { repo: 'fix-portal/a', number: 1, title: 'First PR', author: 'chris', state: 'merged', createdAt: '2026-08-01T09:00:00Z', mergedAt: '2026-08-01T12:00:00Z', reviewCount: 1, turnaroundHours: 2 },
      { repo: 'fix-portal/b', number: 2, title: 'Second PR', author: 'chris', state: 'open', createdAt: '2026-08-02T09:00:00Z', mergedAt: null, reviewCount: 0, turnaroundHours: null },
    ],
    isError: state.error,
    isLoading: state.loading,
  }),
  useGitHubCommitSummary: (from: Date) => ({
    summary: from.getMonth() === 6 ? [
      { repo: 'fix-portal/a', commitCount: 2, additions: 8, deletions: 1 },
      { repo: 'fix-portal/b', commitCount: 4, additions: 20, deletions: 5 },
    ] : [
      { repo: 'fix-portal/a', commitCount: 3, additions: 10, deletions: 2 },
      { repo: 'fix-portal/b', commitCount: 8, additions: 40, deletions: 15 },
    ],
    isError: state.error,
    isLoading: state.loading,
  }),
  useGitHubCi: (from: Date) => ({
    ci: from.getMonth() === 6 ? [
      { repo: 'fix-portal/a', workflowName: 'CI', totalRuns: 4, failedRuns: 1, successRate: 75 },
      { repo: 'fix-portal/b', workflowName: 'CI', totalRuns: 6, failedRuns: 0, successRate: 50 },
    ] : [
      { repo: 'fix-portal/a', workflowName: 'CI', totalRuns: 10, failedRuns: 0, successRate: 100 },
      { repo: 'fix-portal/b', workflowName: 'CI', totalRuns: 10, failedRuns: 2, successRate: 80 },
    ],
    isError: state.error,
    isLoading: state.loading,
  }),
  useGitHubReviews: () => ({
    reviewers: [
      { repo: 'fix-portal/a', reviewer: 'coderabbitai[bot]', isBot: true, reviewCount: 3, pullRequestCount: 2, approvedCount: 2, changesRequestedCount: 1, avgFirstReviewHours: 0.4 },
      { repo: 'fix-portal/b', reviewer: 'chris', isBot: false, reviewCount: 1, pullRequestCount: 1, approvedCount: 1, changesRequestedCount: 0, avgFirstReviewHours: 10 },
    ],
    isError: state.error,
    isLoading: state.loading,
  }),
}))

describe('GitHubPage', () => {
  it('exposes the page-wide controls as a named filter row', () => {
    render(<GitHubPage />)

    expect(screen.getByRole('group', { name: /github filters/i })).toBeInTheDocument()
  })

  it('applies the selected repo to pull requests, commits, CI, and review agents', () => {
    render(<GitHubPage />)

    fireEvent.change(screen.getByLabelText('Repo'), { target: { value: 'fix-portal/a' } })

    expect(screen.queryByText('Second PR')).not.toBeInTheDocument()
    expect(screen.queryAllByRole('cell', { name: 'fix-portal/b' })).toHaveLength(0)
    // One repo cell per panel: pull requests, review agents, commits, CI.
    expect(screen.getAllByRole('cell', { name: 'fix-portal/a' })).toHaveLength(4)
  })

  it('compares aggregate GitHub evidence and honours the repo filter', () => {
    render(<GitHubPage />)

    expect(screen.getByLabelText('Compare from')).toBeInTheDocument()
    expect(within(screen.getByRole('group', { name: 'Pull requests comparison' })).getByText('2')).toBeInTheDocument()
    expect(screen.getByRole('group', { name: 'Commits comparison' })).toHaveTextContent('11vs 6+5')
    expect(screen.getByRole('group', { name: 'CI success comparison' })).toHaveTextContent('90%vs 60%+30 pp')

    fireEvent.change(screen.getByLabelText('Repo'), { target: { value: 'fix-portal/a' } })

    expect(screen.getByRole('group', { name: 'Pull requests comparison' })).toHaveTextContent('1vs 2-1')
    expect(screen.getByRole('group', { name: 'CI success comparison' })).toHaveTextContent('100%vs 75%+25 pp')
  })

  it('holds the comparison summary until all six queries settle', () => {
    // Otherwise it asserts zeros and fabricated deltas against a comparison period
    // whose requests are still in flight.
    state.loading = true
    try {
      render(<GitHubPage />)

      expect(screen.queryByRole('group', { name: 'Pull requests comparison' })).not.toBeInTheDocument()
      expect(screen.getByLabelText('Loading GitHub period comparison')).toBeInTheDocument()
    } finally {
      state.loading = false
    }
  })

  it('withholds the comparison summary when a contributing query fails instead of printing zeros with fabricated deltas', () => {
    // On failure with no cache the arrays settle empty, so the unguarded summary
    // would render zero counts and compute deltas against zero.
    state.error = true
    try {
      render(<GitHubPage />)

      expect(screen.queryByRole('group', { name: 'Pull requests comparison' })).not.toBeInTheDocument()
      expect(screen.queryByLabelText('Loading GitHub period comparison')).not.toBeInTheDocument()
      expect(screen.getByRole('status')).toHaveTextContent(/comparison unavailable/i)
    } finally {
      state.error = false
    }
  })
})
