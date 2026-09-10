import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, expect, test, vi } from 'vitest'
import type { Insight } from '../api/client'
import InsightsFeed from './InsightsFeed'

const data = vi.hoisted(() => ({ insights: [] as Insight[] }))
vi.mock('../api/queries', () => ({ useInsights: () => ({ insights: data.insights, isError: false, isLoading: false }) }))

const insight = (number: number): Insight => ({
  id: String(number),
  generatedAt: '2026-08-26T12:00:00Z',
  insightType: 'summary',
  title: `Insight ${number}`,
  body: 'Details',
  data: {},
  acknowledged: false,
})

function renderFeed() {
  return render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <InsightsFeed />
    </QueryClientProvider>,
  )
}

beforeEach(() => { data.insights = [] })

test('shows five unread insights before revealing the older remainder', async () => {
  data.insights = [1, 2, 3, 4, 5, 6, 7].map(insight)
  const user = userEvent.setup()
  renderFeed()

  expect(screen.getAllByText(/Insight [1-5]/)).toHaveLength(5)
  expect(screen.getByText('Insight 6')).not.toBeVisible()
  await user.click(screen.getByText('Show 2 older insights'))
  expect(screen.getByText('Insight 6')).toBeVisible()
  expect(screen.getByText('Insight 7')).toBeVisible()
})

test('does not add an older-insights disclosure when there are five unread insights', () => {
  data.insights = [1, 2, 3, 4, 5].map(insight)
  const { container } = renderFeed()

  expect(container.querySelector('summary')).not.toBeInTheDocument()
})

test('renders recommendations with the neutral insight label instead of deprecated info status', () => {
  data.insights = [{ ...insight(1), insightType: 'recommendation' }]
  const { container } = renderFeed()

  expect(screen.getByText('Recommendation')).toHaveClass('insight-type')
  expect(container.querySelector('.fpds-badge--info')).not.toBeInTheDocument()
})
