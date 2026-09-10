import { render, screen } from '@testing-library/react'
import { expect, test, vi } from 'vitest'
import ActivityPage from './ActivityPage'

vi.mock('../lib/dateRange', () => ({
  useDateRange: () => ({
    from: new Date('2026-08-01T00:00:00'),
    to: new Date('2026-08-31T00:00:00'),
    preset: 31,
    setPreset: vi.fn(),
    setCustom: vi.fn(),
    comparisonFrom: new Date('2026-07-01T00:00:00'),
    comparisonTo: new Date('2026-07-31T00:00:00'),
    comparisonMode: 'previous',
    setComparison: vi.fn(),
    compareWithPrevious: vi.fn(),
  }),
}))

vi.mock('../api/queries', () => ({
  localDate: (date: Date) => date.toISOString().slice(0, 10),
  useActivityByProject: () => ({ projects: [], isError: false, isLoading: false }),
}))

vi.mock('../components/ActivityTrendChart', () => ({ default: () => null }))
vi.mock('../components/ProjectTreemap', () => ({ default: () => null }))

test('offers the same selected and comparison period controls as Spend', () => {
  render(<ActivityPage />)

  expect(screen.getByLabelText('Selected from')).toBeInTheDocument()
  expect(screen.getByLabelText('Compare from')).toBeInTheDocument()
  expect(screen.getByRole('button', { name: 'Previous period' })).toHaveAttribute('class', expect.stringContaining('active'))
})
