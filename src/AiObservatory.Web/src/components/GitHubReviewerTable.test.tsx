import { describe, expect, it } from 'vitest'
import { render, screen } from '@testing-library/react'
import GitHubReviewerTable from './GitHubReviewerTable'
import type { GitHubReviewerSummary } from '../api/client'

const reviewers: GitHubReviewerSummary[] = [
  {
    repo: 'fix-portal/a',
    reviewer: 'coderabbitai[bot]',
    isBot: true,
    reviewCount: 6,
    pullRequestCount: 4,
    approvedCount: 2,
    changesRequestedCount: 3,
    avgFirstReviewHours: 0.3,
  },
  {
    repo: 'fix-portal/a',
    reviewer: 'chris',
    isBot: false,
    reviewCount: 2,
    pullRequestCount: 2,
    approvedCount: 2,
    changesRequestedCount: 0,
    avgFirstReviewHours: 14.5,
  },
]

describe('GitHubReviewerTable', () => {
  it('marks a review agent and leaves a human unmarked', () => {
    render(<GitHubReviewerTable reviewers={reviewers} />)

    const agentRow = screen.getByRole('row', { name: /coderabbitai/ })
    const humanRow = screen.getByRole('row', { name: /chris/ })
    expect(agentRow).toHaveTextContent('Agent')
    expect(humanRow).not.toHaveTextContent('Agent')
  })

  it('renders every counted column for a reviewer', () => {
    render(<GitHubReviewerTable reviewers={reviewers} />)

    const agentRow = screen.getByRole('row', { name: /coderabbitai/ })
    const cells = screen.getAllByRole('cell').filter((cell) => agentRow.contains(cell))
    expect(cells.map((cell) => cell.textContent)).toEqual([
      'fix-portal/a',
      'coderabbitai[bot] Agent',
      '4',
      '6',
      '2',
      '3',
      '18m',
    ])
  })

  it('shows an empty state when nobody reviewed in the period', () => {
    render(<GitHubReviewerTable reviewers={[]} />)
    expect(screen.getByText('No reviews submitted in this period.')).toBeInTheDocument()
  })

  it('shows a loading state', () => {
    render(<GitHubReviewerTable reviewers={[]} isLoading />)
    expect(screen.getByText('Loading review activity...')).toBeInTheDocument()
  })

  it('shows an error state', () => {
    render(<GitHubReviewerTable reviewers={[]} isError />)
    expect(screen.getByText('Couldn’t load review activity.')).toBeInTheDocument()
  })
})
