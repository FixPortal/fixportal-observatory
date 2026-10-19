import { fireEvent, render, screen, within } from '@testing-library/react'
import { beforeEach, expect, test, vi } from 'vitest'
import type { AdversarialReviewRun } from '../api/client'
import { SeatsTab } from './SeatsTab'
import type { FindingRow, RunRow } from './seatInfluence'

const findingsData = vi.hoisted(() => ({
  findings: [] as FindingRow[],
  isError: false,
  isLoading: false,
}))
vi.mock('../api/useAdversarialFindings', () => ({
  useAdversarialFindings: () => findingsData,
}))

function run(p: Partial<RunRow>): RunRow {
  return {
    id: 'run-row',
    reviewer: 'zai',
    model: 'glm-5.3',
    role: 'reviewer',
    repo: 'example-app',
    summary: null,
    inputTokens: 0,
    outputTokens: 0,
    costUsd: 2,
    reviewDurationMs: 0,
    issuesRaised: 0,
    issuesAccepted: 0,
    costPerAcceptedFinding: null,
    costUnknown: false,
    chunkCount: null,
    runId: 'R1',
    recordedAt: '2026-10-19T12:00:00Z',
    ...p,
  } satisfies AdversarialReviewRun
}

function finding(p: Partial<FindingRow>): FindingRow {
  return {
    runId: 'R1',
    repo: 'example-app',
    chunkId: 'single',
    findingId: 'F1',
    severity: 'high',
    outcome: 'accepted',
    raisedBy: [{ reviewer: 'zai', model: 'glm-5.3' }],
    assessments: [{ reviewer: 'openai', model: 'gpt-6-astra', verdict: 'agree' }],
    ...p,
  }
}

const runs = [
  run({ id: 'zai', reviewer: 'zai', model: 'glm-5.3' }),
  run({
    id: 'openai',
    reviewer: 'openai',
    model: 'gpt-6-astra',
    costUsd: 0,
    costUnknown: true,
  }),
  run({
    id: 'mixed',
    reviewer: 'anthropic',
    model: 'claude-sonnet-5-5+claude-opus-5-5',
    runId: 'R2',
    recordedAt: '2026-10-20T12:00:00Z',
    costUsd: 4,
  }),
]

const findings = [
  finding({ findingId: 'F1' }),
  finding({
    findingId: 'F2',
    severity: 'medium',
    raisedBy: [
      { reviewer: 'zai', model: 'glm-5.3' },
      { reviewer: 'openai', model: 'gpt-6-astra' },
    ],
  }),
  finding({
    findingId: 'F3',
    severity: 'low',
    outcome: 'refuted',
    raisedBy: [{ reviewer: 'openai', model: 'gpt-6-astra' }],
  }),
  finding({
    runId: 'R2',
    findingId: 'F4',
    severity: 'unknown',
    raisedBy: [{ reviewer: 'anthropic', model: 'claude-sonnet-5-5+claude-opus-5-5' }],
    assessments: [],
  }),
]

function renderTab(runsOverride: RunRow[] = runs) {
  return render(<SeatsTab runs={runsOverride} runsLoading={false} runsError={false} />)
}

beforeEach(() => {
  findingsData.findings = findings
  findingsData.isError = false
  findingsData.isLoading = false
})

test('renders every metrics column, mixed labels, unknown cost, and small samples', () => {
  renderTab()

  const headers = screen.getAllByRole('columnheader').map((header) => header.textContent)
  for (const header of [
    'Seat',
    'Selected influence',
    'Runs',
    'Raised',
    'Accepted',
    'Unique',
    'Corroborated',
    'Peer agreement',
    'Refuted',
    'Dropped',
    'Indeterminate',
    'False-positive rate',
    'Acceptance rate',
    'Known cost',
    'Cost / unique',
    'Unknown-cost runs',
    'Counts-only runs',
    'Sample',
  ]) {
    expect(headers).toContain(header)
  }
  expect(screen.getByText('mixed: claude-sonnet-5-5+claude-opus-5-5')).toBeInTheDocument()
  expect(screen.getByText('1 run unknown cost')).toBeInTheDocument()
  const unknownCostSeat = screen.getByText('gpt-6-astra').closest('tr')
  expect(unknownCostSeat).toBeInTheDocument()
  expect(within(unknownCostSeat!).getByText('Unknown')).toBeInTheDocument()
  expect(screen.getAllByText('small sample')).toHaveLength(3)
})

test('defaults to unique accepted influence per detailed run and switches pivot values', () => {
  renderTab()

  expect(screen.getByText('Unique accepted per detailed run')).toBeInTheDocument()
  expect(screen.getAllByText('1.00').length).toBeGreaterThan(0)

  fireEvent.click(screen.getByRole('button', { name: 'Accepted per run' }))
  expect(screen.getByText('Accepted per run', { selector: '.panel-note' })).toBeInTheDocument()
  expect(screen.getAllByText('2.00').length).toBeGreaterThan(0)

  fireEvent.click(screen.getByRole('button', { name: 'Acceptance rate' }))
  expect(screen.getByText('Acceptance rate', { selector: '.panel-note' })).toBeInTheDocument()
  expect(screen.getAllByText('50.0%').length).toBeGreaterThan(0)
})

test('drills through the selected seat without rendering finding text', () => {
  renderTab()
  fireEvent.click(screen.getByRole('button', { name: 'Select findings for glm-5.3' }))

  const drill = screen.getByRole('region', { name: 'Findings for glm-5.3' })
  expect(within(drill).getByText('F1')).toBeInTheDocument()
  expect(within(drill).getByText('F2')).toBeInTheDocument()
  expect(within(drill).queryByText('F3')).not.toBeInTheDocument()
  expect(within(drill).getByText('openai · gpt-6-astra')).toBeInTheDocument()
  expect(within(drill).queryByText('F4')).not.toBeInTheDocument()
})

test('renders an explicit empty state once loading has finished', () => {
  renderTab([])
  findingsData.findings = []

  expect(screen.getByText('No seat data recorded yet.')).toBeInTheDocument()
})

test('shows loading before declaring the data empty', () => {
  findingsData.isLoading = true
  renderTab([])

  expect(screen.getByText('Loading seat influence...')).toBeInTheDocument()
  expect(screen.queryByText('No seat data recorded yet.')).not.toBeInTheDocument()
})

test('surfaces either query error without treating data as absent', () => {
  findingsData.isError = true
  renderTab()

  expect(screen.getByRole('alert').textContent).toMatch(/couldn.t load seat data/i)
})
