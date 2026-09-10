import { describe, it, expect } from 'vitest'
import { render, screen } from '@testing-library/react'
import SpendTotals from './SpendTotals'

describe('SpendTotals', () => {
  it('shows the filtered total in GBP', () => {
    render(<SpendTotals total={412.8} entryCount={14} largestCategory="Subscriptions" />)
    expect(screen.getByText('£412.80')).toBeInTheDocument()
  })

  it('shows the entry count', () => {
    render(<SpendTotals total={412.8} entryCount={14} largestCategory="Subscriptions" />)
    expect(screen.getByText('14')).toBeInTheDocument()
  })

  it('renders a dash rather than a category name when nothing is in range', () => {
    render(<SpendTotals total={0} entryCount={0} largestCategory={null} />)
    expect(screen.getByText('£0.00')).toBeInTheDocument()
    expect(screen.getByText('—')).toBeInTheDocument()
  })

  it('shows a favourable reduction against the comparison period', () => {
    render(
      <SpendTotals
        total={80}
        entryCount={14}
        largestCategory="Subscriptions"
        comparisonTotal={100}
        comparisonLabel="Previous period"
      />,
    )

    expect(screen.getByText('£20.00 lower')).toBeInTheDocument()
    expect(screen.getByText('20.0%')).toBeInTheDocument()
  })

  it('does not invent a percentage from a zero or negative comparison baseline', () => {
    const { rerender } = render(
      <SpendTotals
        total={40}
        entryCount={1}
        largestCategory="Cloud"
        comparisonTotal={0}
        comparisonLabel="Previous period"
      />,
    )

    expect(screen.getByText('£40.00 higher')).toBeInTheDocument()
    expect(screen.getByText('No prior spend')).toBeInTheDocument()

    rerender(
      <SpendTotals
        total={40}
        entryCount={1}
        largestCategory="Cloud"
        comparisonTotal={-10}
        comparisonLabel="Previous period"
      />,
    )
    expect(screen.getByText('£50.00 higher')).toBeInTheDocument()
    expect(screen.queryByText(/%/)).not.toBeInTheDocument()
  })
})
