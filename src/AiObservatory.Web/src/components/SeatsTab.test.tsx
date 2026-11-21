import { fireEvent, render, screen, within } from '@testing-library/react'
import type { ReactNode } from 'react'
import { beforeEach, expect, test, vi } from 'vitest'
import type { AdversarialReviewRun } from '../api/client'
import { SeatsTab } from './SeatsTab'
import type { FindingRow, RunRow } from './seatInfluence'

const findingsData = vi.hoisted(() => ({
  findings: [] as FindingRow[],
  since: '2000-01-01',
  isError: false,
  isLoading: false,
}))
vi.mock('../api/useAdversarialFindings', () => ({
  useAdversarialFindings: () => findingsData,
}))
// Real recharts cannot lay out in jsdom; these stand-ins expose the data each chart is given.
vi.mock('recharts', () => ({
  ScatterChart: ({ children }: { children?: ReactNode }) => <div>{children}</div>,
  Scatter: ({ data }: { data: unknown }) => <pre data-testid="scatter">{JSON.stringify(data)}</pre>,
  LineChart: ({ data, children }: { data: unknown; children?: ReactNode }) => (
    <div>
      <pre data-testid="rows">{JSON.stringify(data)}</pre>
      {children}
    </div>
  ),
  ResponsiveContainer: ({ children }: { children?: ReactNode }) => <div>{children}</div>,
  Line: () => null,
  XAxis: () => null,
  YAxis: () => null,
  ZAxis: () => null,
  Tooltip: () => null,
  Legend: () => null,
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
  findingsData.since = '2000-01-01'
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

// C9-AR-4: finding ids are unique only within a chunk, so the drill-down keys and shows the chunk.
test('lists same-numbered findings from different chunks as separate keyed rows', () => {
  const errors = vi.spyOn(console, 'error').mockImplementation(() => undefined)
  findingsData.findings = [
    finding({ chunkId: 'C1', findingId: 'F1' }),
    finding({ chunkId: 'C2', findingId: 'F1' }),
  ]
  renderTab([run({ id: 'zai' })])

  const drill = screen.getByRole('region', { name: 'Findings for glm-5.3' })
  expect(within(drill).getByRole('columnheader', { name: 'Chunk' })).toBeInTheDocument()
  expect(within(drill).getByText('C1')).toBeInTheDocument()
  expect(within(drill).getByText('C2')).toBeInTheDocument()
  expect(errors.mock.calls.flat().join(' ')).not.toMatch(/same key/)
  errors.mockRestore()
})

// C9-AR-9: a seat that raised nothing has no acceptance rate; it must not read 0.0% or plot at 0.
test('shows an empty acceptance rate as a dash in Selected influence and drops it from the chart', async () => {
  findingsData.findings = []
  renderTab([run({ id: 'zai', issuesRaised: 0, issuesAccepted: 0 })])
  fireEvent.click(screen.getByRole('button', { name: 'Acceptance rate' }))

  const row = screen.getByRole('button', { name: 'Select findings for glm-5.3' }).closest('tr')!
  expect(within(row).getAllByRole('cell')[1]).toHaveTextContent('—')
  expect(JSON.parse((await screen.findByTestId('scatter')).textContent)).toEqual([])
  const rows = JSON.parse((await screen.findByTestId('rows')).textContent) as Record<
    string,
    unknown
  >[]
  expect(rows.map((item) => item['seat-0'])).toEqual([null])
})

// C9-AR-5: a week in which a seat had no runs has no influence value, so its line breaks.
test('leaves influence unset for a week in which a seat had no runs, and keeps its cost at zero', async () => {
  findingsData.findings = []
  renderTab([
    run({ id: 'a1', runId: 'A1', recordedAt: '2026-10-05T12:00:00Z' }),
    run({ id: 'a2', runId: 'A2', recordedAt: '2026-10-19T12:00:00Z' }),
    run({
      id: 'o1',
      runId: 'B1',
      reviewer: 'openai',
      model: 'gpt-6-astra',
      recordedAt: '2026-10-12T12:00:00Z',
    }),
  ])

  const rows = JSON.parse((await screen.findByTestId('rows')).textContent) as Record<
    string,
    unknown
  >[]
  const middle = rows.find((item) => item['week'] === '2026-W42')!
  expect(middle['seat-0']).toBeNull()
  expect(middle['seat-0-cost']).toBe(0)
})

// C9-AR-6: the drill-down uses the same date window as the metrics above it.
test('leaves a run older than the findings window out of the drill-down as well', () => {
  findingsData.since = '2026-10-20'
  findingsData.findings = [
    finding({ runId: 'OLD', findingId: 'F-OLD' }),
    finding({ runId: 'NEW', findingId: 'F-NEW' }),
  ]
  renderTab([
    run({ id: 'old', runId: 'OLD', recordedAt: '2026-10-01T12:00:00Z' }),
    run({ id: 'new', runId: 'NEW', recordedAt: '2026-10-21T12:00:00Z' }),
  ])

  const drill = screen.getByRole('region', { name: 'Findings for glm-5.3' })
  expect(within(drill).getByText('F-NEW')).toBeInTheDocument()
  expect(within(drill).queryByText('F-OLD')).not.toBeInTheDocument()
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
