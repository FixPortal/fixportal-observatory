import { describe, expect, it } from 'vitest'
import { render, screen } from '@testing-library/react'
import GitHubCommitTable from './GitHubCommitTable'
import type { GitHubCommitSummary } from '../api/client'

const summary: GitHubCommitSummary[] = [
  { repo: 'fix-portal/a', commitCount: 3, additions: 10, deletions: 2 },
  { repo: 'fix-portal/b', commitCount: 8, additions: 40, deletions: 15 },
]

describe('GitHubCommitTable', () => {
  it('renders every row with repo, commit count, and churn visible', () => {
    render(<GitHubCommitTable summary={summary} />)
    expect(screen.getByText('fix-portal/a')).toBeInTheDocument()
    expect(screen.getByText('fix-portal/b')).toBeInTheDocument()
    expect(screen.getByText('3')).toBeInTheDocument()
    expect(screen.getByText('+10 / -2')).toBeInTheDocument()
    expect(screen.getByText('+40 / -15')).toBeInTheDocument()
  })

  it('sorts by commit count descending by default, busiest repo first', () => {
    render(<GitHubCommitTable summary={summary} />)
    const rows = screen.getAllByRole('row').slice(1) // skip header row
    expect(rows[0]).toHaveTextContent('fix-portal/b')
  })

  it('shows an empty state when there is no commit activity', () => {
    render(<GitHubCommitTable summary={[]} />)
    expect(screen.getByText('No commit activity for this period.')).toBeInTheDocument()
  })
})
