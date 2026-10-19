import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { beforeEach, describe, expect, test, vi } from 'vitest'
import type { AdversarialReviewRun, AdversarialReviewStats } from '../api/client'
import { AdversarialReviewPanel } from './AdversarialReviewPanel'

const data = vi.hoisted(() => ({
  stats: [] as AdversarialReviewStats[],
  statsError: false,
  statsLoading: false,
  runs: [] as AdversarialReviewRun[],
  runsError: false,
  runsLoading: false,
}))
vi.mock('../api/queries', () => ({
  useAdversarialReviewStats: () => ({
    stats: data.stats,
    isError: data.statsError,
    isLoading: data.statsLoading,
  }),
  useAdversarialReviewRuns: () => ({
    runs: data.runs,
    isError: data.runsError,
    isLoading: data.runsLoading,
  }),
}))

const deleteAdversarialReviewRun = vi.hoisted(() => vi.fn(() => Promise.resolve()))
vi.mock('../api/client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../api/client')>()),
  deleteAdversarialReviewRun,
}))

vi.mock('../auth/msal', () => ({ isReadonly: false }))

function run(p: Partial<AdversarialReviewRun>): AdversarialReviewRun {
  return {
    id: 'p1',
    reviewer: 'anthropic',
    model: 'claude-sonnet-4-6',
    role: 'reviewer',
    repo: 'fix-portal/example',
    summary: null,
    inputTokens: 0,
    outputTokens: 0,
    costUsd: 1,
    reviewDurationMs: 1000,
    issuesRaised: 2,
    issuesAccepted: 1,
    costPerAcceptedFinding: 1,
    costUnknown: false,
    chunkCount: null,
    runId: 'R1',
    recordedAt: '2026-08-27T12:00:00Z',
    ...p,
  }
}

function renderPanel() {
  return render(
    <QueryClientProvider client={new QueryClient()}>
      <AdversarialReviewPanel />
    </QueryClientProvider>,
  )
}

beforeEach(() => {
  localStorage.clear() // run cards persist their expanded state
  data.stats = []
  data.statsError = false
  data.statsLoading = false
  data.runs = []
  data.runsError = false
  data.runsLoading = false
  deleteAdversarialReviewRun.mockClear()
})

describe('AdversarialReviewPanel unknown cost', () => {
  const stat = (p: Partial<AdversarialReviewStats>): AdversarialReviewStats => ({
    reviewer: 'zai',
    model: 'glm',
    role: 'reviewer',
    runCount: 3,
    avgCostPerRun: null,
    avgIssuesRaised: 2,
    avgIssuesAccepted: 1,
    avgCostPerAcceptedFinding: null,
    avgDurationMs: 1000,
    unknownCostRuns: 3,
    ...p,
  })

  test('an all-unknown group renders Unknown, never $0.00', () => {
    data.stats = [stat({})]
    renderPanel()

    expect(screen.getAllByText('Unknown')).toHaveLength(2)
    expect(screen.queryByText('$0.00')).not.toBeInTheDocument()
  })

  test('a partly-unknown group shows the excluded run count', () => {
    data.stats = [stat({ avgCostPerRun: 2, avgCostPerAcceptedFinding: 2, unknownCostRuns: 1 })]
    renderPanel()

    expect(screen.getAllByText('$2.00 (1 unknown excluded)')).toHaveLength(2)
  })
})

describe('AdversarialReviewPanel runs list unknown cost', () => {
  test('an unknown-cost participant renders Unknown and the run total is flagged partial', () => {
    data.runs = [
      run({ id: 'a', reviewer: 'anthropic', costUsd: 1, summary: 'Partial cost run' }),
      run({
        id: 'z',
        reviewer: 'zai',
        costUsd: 0,
        costUnknown: true,
        costPerAcceptedFinding: null,
      }),
    ]
    renderPanel()
    fireEvent.click(screen.getByRole('button', { name: /Partial cost run/ }))

    expect(screen.getByText(/\+ 1 unknown/)).toBeInTheDocument()
    expect(screen.getAllByText('Unknown').length).toBeGreaterThan(0)
  })

  test('an unknown-cost anthropic participant does not mark the known total as putative', () => {
    data.runs = [
      run({ id: 'g', reviewer: 'google', costUsd: 1, summary: 'Mixed putative run' }),
      run({
        id: 'a',
        reviewer: 'anthropic',
        costUsd: 0,
        costUnknown: true,
        costPerAcceptedFinding: null,
      }),
    ]
    renderPanel()

    const total = screen.getByText('$1.00', { selector: '.adv-run__totals b' })
    expect(total).not.toHaveAttribute('title')
    expect(screen.queryByText('~$1.00')).not.toBeInTheDocument()
  })
})

describe('AdversarialReviewPanel loading state', () => {
  test('shows loading text instead of the empty-state while queries are pending', () => {
    data.statsLoading = true
    data.runsLoading = true
    renderPanel()

    expect(screen.getByText('Loading review stats...')).toBeInTheDocument()
    expect(screen.getByText('Loading runs...')).toBeInTheDocument()
    expect(screen.queryByText('No adversarial-review runs recorded yet.')).not.toBeInTheDocument()
    expect(screen.queryByText('No runs recorded yet.')).not.toBeInTheDocument()
  })

  test('falls back to empty state once loading finishes with no data', () => {
    renderPanel()

    expect(screen.getByText('No adversarial-review runs recorded yet.')).toBeInTheDocument()
    expect(screen.getByText('No runs recorded yet.')).toBeInTheDocument()
  })
})

describe('AdversarialReviewPanel run status filters', () => {
  beforeEach(() => {
    data.runs = [
      run({
        id: 'a',
        runId: 'R1',
        reviewer: 'anthropic',
        role: 'reviewer',
        summary: 'Full complete run',
      }),
      run({ id: 'b', runId: 'R1', reviewer: 'google', role: 'reviewer' }),
      run({ id: 'c', runId: 'R1', reviewer: 'openai', role: 'reviewer' }),
      run({ id: 'd', runId: 'R1', reviewer: 'zai', role: 'reviewer' }),
      run({ id: 'g', runId: 'R1', reviewer: 'deepseek', role: 'reviewer' }),
      run({ id: 'e', runId: 'R1', reviewer: 'anthropic', role: 'judge' }),
      run({
        id: 'f',
        runId: 'R2',
        reviewer: 'anthropic',
        role: 'reviewer',
        summary: 'Solo invalid run',
      }),
    ]
  })

  test('filters to incomplete runs', () => {
    renderPanel()
    fireEvent.change(screen.getByLabelText('Filter runs by completeness'), {
      target: { value: 'incomplete' },
    })

    expect(screen.getByText('Solo invalid run')).toBeInTheDocument()
    expect(screen.queryByText('Full complete run')).not.toBeInTheDocument()
  })

  test('filters to invalid runs (below the minimum reviewer threshold)', () => {
    renderPanel()
    fireEvent.change(screen.getByLabelText('Filter runs by validity'), {
      target: { value: 'invalid' },
    })

    expect(screen.getByText('Solo invalid run')).toBeInTheDocument()
    expect(screen.queryByText('Full complete run')).not.toBeInTheDocument()
  })
})

describe('AdversarialReviewPanel sweep removal', () => {
  beforeEach(() => {
    data.runs = [run({ runId: 'R1', summary: 'Removable run' })]
  })

  test('requires a confirm click before calling the delete API', async () => {
    renderPanel()

    fireEvent.click(screen.getByRole('button', { name: /Removable run/ }))
    fireEvent.click(screen.getByRole('button', { name: 'Remove sweep' }))
    expect(deleteAdversarialReviewRun).not.toHaveBeenCalled()

    fireEvent.click(screen.getByRole('button', { name: 'Confirm remove' }))
    await waitFor(() => {
      expect(deleteAdversarialReviewRun).toHaveBeenCalledWith('R1', expect.anything())
    })
  })
})
