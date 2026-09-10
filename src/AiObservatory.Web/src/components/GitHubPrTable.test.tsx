import { describe, expect, it } from 'vitest'
import { render, screen, fireEvent, within } from '@testing-library/react'
import GitHubPrTable from './GitHubPrTable'
import type { GitHubPr } from '../api/client'

const prs: GitHubPr[] = [
  { repo: 'fix-portal/a', number: 1, title: 'Add feature', author: 'chris', state: 'open', createdAt: '2026-07-01T09:00:00Z', mergedAt: null, reviewCount: 2, turnaroundHours: 3.5 },
  { repo: 'fix-portal/b', number: 2, title: 'Fix bug', author: 'chris', state: 'merged', createdAt: '2026-07-02T09:00:00Z', mergedAt: '2026-07-02T12:00:00Z', reviewCount: 0, turnaroundHours: null },
]

describe('GitHubPrTable', () => {
  it('renders every PR row', () => {
    render(<GitHubPrTable prs={prs} />)
    expect(screen.getByText(/Add feature/)).toBeInTheDocument()
    expect(screen.getByText(/Fix bug/)).toBeInTheDocument()
  })

  it('filters by search query', () => {
    render(<GitHubPrTable prs={prs} />)
    fireEvent.change(screen.getByLabelText('Search pull requests'), { target: { value: 'bug' } })
    expect(screen.queryByText(/Add feature/)).not.toBeInTheDocument()
    expect(screen.getByText(/Fix bug/)).toBeInTheDocument()
  })

  it('shows an empty state when there is no activity', () => {
    render(<GitHubPrTable prs={[]} />)
    expect(screen.getByText('No PR activity for this period.')).toBeInTheDocument()
  })

  it('renders em dash in the Turnaround column for a PR with no turnaround yet', () => {
    render(<GitHubPrTable prs={prs} />)
    // prs[1] ("Fix bug") has turnaroundHours: null but a non-null mergedAt, so its Merged
    // column renders a date, not a dash — isolating the assertion to the Turnaround cell
    // specifically (not "the row contains a dash somewhere").
    const row = screen.getByText(/Fix bug/).closest('tr')!
    const cells = within(row).getAllByRole('cell')
    expect(cells[7]).toHaveTextContent('—')
  })

  it('renders the merged date for a merged PR and an em dash for one still open', () => {
    render(<GitHubPrTable prs={prs} />)
    const mergedRow = screen.getByText(/Fix bug/).closest('tr')!
    const openRow = screen.getByText(/Add feature/).closest('tr')!
    expect(within(mergedRow).getAllByRole('cell')[5]).toHaveTextContent(new Date(prs[1].mergedAt!).toLocaleDateString())
    expect(within(openRow).getAllByRole('cell')[5]).toHaveTextContent('—')
  })
})
