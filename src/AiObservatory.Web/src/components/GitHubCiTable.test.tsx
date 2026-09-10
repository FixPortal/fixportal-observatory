import { describe, expect, it } from 'vitest'
import { render, screen } from '@testing-library/react'
import GitHubCiTable from './GitHubCiTable'
import type { GitHubCiSummary } from '../api/client'

const ci: GitHubCiSummary[] = [
  { repo: 'fix-portal/a', workflowName: 'ci.yml', totalRuns: 10, failedRuns: 3, successRate: 70 },
  { repo: 'fix-portal/b', workflowName: 'ci.yml', totalRuns: 10, failedRuns: 0, successRate: 100 },
]

describe('GitHubCiTable', () => {
  it('sorts by success rate ascending by default, worst repo first', () => {
    render(<GitHubCiTable ci={ci} />)
    const rows = screen.getAllByRole('row').slice(1) // skip header row
    expect(rows[0]).toHaveTextContent('fix-portal/a')
  })

  it('shows an empty state when there is no CI activity', () => {
    render(<GitHubCiTable ci={[]} />)
    expect(screen.getByText('No CI activity for this period.')).toBeInTheDocument()
  })

  it('shows a loading state', () => {
    render(<GitHubCiTable ci={[]} isLoading />)
    expect(screen.getByText('Loading CI activity...')).toBeInTheDocument()
  })

  it('shows an error state', () => {
    render(<GitHubCiTable ci={[]} isError />)
    expect(screen.getByText('Couldn’t load CI activity.')).toBeInTheDocument()
  })

  it('prefers the loading state over the error state when both are set', () => {
    render(<GitHubCiTable ci={[]} isLoading isError />)
    expect(screen.getByText('Loading CI activity...')).toBeInTheDocument()
    // Precedence, not just presence: a regression that rendered both would still pass
    // the assertion above, so pin that the error copy is genuinely absent.
    expect(screen.queryByText('Couldn’t load CI activity.')).not.toBeInTheDocument()
  })

  it('marks low success rates without emoji glyphs', () => {
    render(<GitHubCiTable ci={ci} />)

    expect(screen.getByText('70%')).toBeInTheDocument()
    expect(screen.queryByText(/⚠/u)).not.toBeInTheDocument()
  })

  it('gives low success rates a non-color text cue for accessibility', () => {
    render(<GitHubCiTable ci={ci} />)

    expect(screen.getByText('(low success rate)')).toBeInTheDocument()
  })
})
