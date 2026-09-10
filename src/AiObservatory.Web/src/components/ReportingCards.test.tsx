import { render, screen } from '@testing-library/react'
import { expect, test } from 'vitest'
import ReportingCards from './ReportingCards'

test('keeps an incomplete top-vendor projection truthful instead of formatting null as money', () => {
  render(<ReportingCards report={{
    entryCount: 1,
    totalGbp: 10,
    dailyAverageGbp: 10,
    projectedMonthlyGbp: 300,
    topVendorName: null,
    topVendorGbp: null,
    dailySeries: [],
    vendorSeries: [],
    categorySeries: [],
  }} />)

  const card = screen.getByText('Top vendor').parentElement!
  expect(card).toHaveTextContent('—')
  expect(card).not.toHaveTextContent('£0.00')
})

test('does not present an empty ledger as a zero-cost claim', () => {
  render(<ReportingCards report={{
    entryCount: 0,
    totalGbp: 0,
    dailyAverageGbp: 0,
    projectedMonthlyGbp: 0,
    topVendorName: null,
    topVendorGbp: null,
    dailySeries: [],
    vendorSeries: [],
    categorySeries: [],
  }} />)

  const card = screen.getByText('Billed spend').parentElement!
  expect(card).toHaveTextContent('—')
  expect(card).not.toHaveTextContent('£0.00')
})

test('reports a non-empty ledger that nets to zero as zero billed spend', () => {
  render(<ReportingCards report={{
    entryCount: 2,
    totalGbp: 0,
    dailyAverageGbp: 0,
    projectedMonthlyGbp: 0,
    topVendorName: 'Anthropic',
    topVendorGbp: 0,
    dailySeries: [],
    vendorSeries: [],
    categorySeries: [],
  }} />)

  expect(screen.getByText('Billed spend').parentElement).toHaveTextContent('£0.00')
})

test('names the monthly projection as a 30-day run rate', () => {
  render(<ReportingCards report={{
    entryCount: 1,
    totalGbp: 10,
    dailyAverageGbp: 2,
    projectedMonthlyGbp: 60,
    topVendorName: 'OpenAI',
    topVendorGbp: 10,
    dailySeries: [],
    vendorSeries: [],
    categorySeries: [],
  }} />)

  expect(screen.getByText('30-day run rate')).toBeInTheDocument()
  expect(screen.queryByText('Projected / month')).not.toBeInTheDocument()
})
