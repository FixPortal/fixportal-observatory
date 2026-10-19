import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { expect, test } from 'vitest'
import type { AdversarialReviewRun } from '../api/client'
import { computeSeatMetrics, type FindingRow, type Pivot, type RunRow } from './seatInfluence'

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
    costUsd: 1,
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
    severity: 'medium',
    outcome: 'accepted',
    raisedBy: [{ reviewer: 'zai', model: 'glm-5.3' }],
    assessments: [],
    ...p,
  }
}

const seat = (metrics: Awaited<ReturnType<typeof computeSeatMetrics>>, key: string) =>
  metrics.find((item) => item.key === key)

test('groups reviewer rows by the run model and labels a plus-separated row mixed', () => {
  const metrics = computeSeatMetrics(
    [
      run({ reviewer: 'anthropic', model: 'claude-sonnet-5-5+claude-opus-5-5' }),
      run({ reviewer: 'anthropic', model: 'judge-model', role: 'judge' }),
    ],
    [
      finding({
        raisedBy: [{ reviewer: 'anthropic', model: 'claude-sonnet-5-5+claude-opus-5-5' }],
      }),
    ],
    'unique',
  )

  expect(metrics).toHaveLength(1)
  expect(seat(metrics, 'claude-sonnet-5-5+claude-opus-5-5')).toMatchObject({
    label: 'mixed: claude-sonnet-5-5+claude-opus-5-5',
    mixed: true,
    runs: 1,
  })
})

test('credits a seat when it is among the raisers of an accepted finding', () => {
  const metrics = computeSeatMetrics(
    [run({ reviewer: 'zai' }), run({ id: 'openai', reviewer: 'openai', model: 'gpt-6-astra' })],
    [
      finding({ findingId: 'F1', raisedBy: [{ reviewer: 'zai', model: 'glm-5.3' }] }),
      finding({
        findingId: 'F2',
        raisedBy: [
          { reviewer: 'zai', model: 'glm-5.3' },
          { reviewer: 'openai', model: 'gpt-6-astra' },
        ],
      }),
    ],
    'unique',
  )

  expect(seat(metrics, 'glm-5.3')).toMatchObject({
    raised: 2,
    accepted: 2,
    unique: 1,
    corroborated: 1,
  })
  expect(seat(metrics, 'gpt-6-astra')).toMatchObject({
    raised: 1,
    accepted: 1,
    unique: 0,
    corroborated: 1,
  })
})

test('peer agreement counts agree verdicts on raised findings but never self-assessment', () => {
  const metrics = computeSeatMetrics(
    [run({ reviewer: 'zai' }), run({ id: 'openai', reviewer: 'openai', model: 'gpt-6-astra' })],
    [
      finding({
        assessments: [
          { reviewer: 'zai', model: 'glm-5.3', verdict: 'agree' },
          { reviewer: 'openai', model: 'gpt-6-astra', verdict: 'agree' },
          { reviewer: 'openai', model: 'gpt-6-astra', verdict: 'false-positive' },
        ],
      }),
      finding({
        findingId: 'F2',
        outcome: 'refuted',
        assessments: [{ reviewer: 'openai', model: 'gpt-6-astra', verdict: 'agree' }],
      }),
      finding({
        findingId: 'F3',
        raisedBy: [{ reviewer: 'openai', model: 'gpt-6-astra' }],
        assessments: [{ reviewer: 'zai', model: 'glm-5.3', verdict: 'agree' }],
      }),
    ],
    'unique',
  )

  expect(seat(metrics, 'glm-5.3')?.peerAgreement).toBe(2)
  expect(seat(metrics, 'gpt-6-astra')?.peerAgreement).toBe(1)
})

test('computes settled-outcome and acceptance rates with null empty denominators', () => {
  const settled = [
    finding({ findingId: 'A1' }),
    finding({ findingId: 'A2', raisedBy: [{ reviewer: 'zai', model: 'glm-5.3' }] }),
    finding({ findingId: 'R1', outcome: 'refuted' }),
    finding({ findingId: 'D1', outcome: 'dropped' }),
    finding({ findingId: 'I1', outcome: 'indeterminate' }),
  ]
  const metrics = computeSeatMetrics([run({})], settled, 'unique')
  expect(seat(metrics, 'glm-5.3')).toMatchObject({
    raised: 5,
    accepted: 2,
    refuted: 1,
    dropped: 1,
    indeterminate: 1,
    falsePositiveRate: 1 / 3,
    acceptanceRate: 2 / 5,
  })

  const unsettled = computeSeatMetrics(
    [run({ runId: 'R2' })],
    [finding({ runId: 'R2', findingId: 'D1', outcome: 'dropped' })],
    'unique',
  )
  expect(unsettled[0]).toMatchObject({ falsePositiveRate: null, acceptanceRate: 0 })

  const noRaised = computeSeatMetrics([run({ runId: 'R3' })], [], 'unique')
  expect(noRaised[0]).toMatchObject({ falsePositiveRate: null, acceptanceRate: null })
})

test('excludes unknown-cost runs from cost totals and ratios while counting them visibly', () => {
  const metrics = computeSeatMetrics(
    [
      run({ runId: 'R1', costUsd: 2 }),
      run({
        id: 'unknown',
        runId: 'R2',
        costUsd: 0,
        costUnknown: true,
        recordedAt: '2026-10-20T12:00:00Z',
      }),
    ],
    [
      finding({ runId: 'R1' }),
      finding({
        runId: 'R2',
        findingId: 'F2',
        raisedBy: [
          { reviewer: 'zai', model: 'glm-5.3' },
          { reviewer: 'openai', model: 'gpt-6-astra' },
        ],
      }),
    ],
    'unique',
  )

  expect(metrics[0]).toMatchObject({
    runs: 2,
    knownCostUsd: 2,
    unknownCostRuns: 1,
    costPerUnique: 2,
  })

  const allUnknown = computeSeatMetrics(
    [run({ runId: 'R1', costUsd: 0, costUnknown: true })],
    [finding({ runId: 'R1' })],
    'unique',
  )
  expect(allUnknown[0]).toMatchObject({ knownCostUsd: 0, unknownCostRuns: 1, costPerUnique: null })
})

// The server computes the same metrics in C# (suggestion rules); both implementations are held
// to this one fixture so they cannot drift apart. Change it only with the C# test beside it.
test('matches the shared seat-metrics contract fixture', () => {
  // vitest runs with the web project as its root (jsdom has no file: import.meta.url).
  const dir = resolve(process.cwd(), '../../docs/superpowers/contracts/seat-metrics')
  const input = JSON.parse(readFileSync(resolve(dir, 'input.json'), 'utf8')) as {
    runs: RunRow[]
    findings: FindingRow[]
  }
  const expected: unknown = JSON.parse(readFileSync(resolve(dir, 'expected.json'), 'utf8'))

  expect(computeSeatMetrics(input.runs, input.findings, 'unique')).toEqual(expected)
})

test('the false-positive rate counts finding-row catches only, not counts-only accepted counts', () => {
  const metrics = computeSeatMetrics(
    [
      run({ runId: 'R1', issuesRaised: 5, issuesAccepted: 4 }),
      run({ id: 'detailed', runId: 'R2', recordedAt: '2026-10-20T12:00:00Z' }),
    ],
    [
      finding({ runId: 'R2', findingId: 'F1' }),
      finding({ runId: 'R2', findingId: 'F2', outcome: 'refuted' }),
      finding({ runId: 'R2', findingId: 'F3', outcome: 'refuted' }),
    ],
    'unique',
  )

  expect(metrics[0]).toMatchObject({ accepted: 5, unique: 1, refuted: 2 })
  // Settled findings are the finding rows only: 1 accepted + 2 refuted. The 4 accepted from
  // the counts-only run have no refuted counterpart and must not dilute the rate.
  expect(metrics[0]?.falsePositiveRate).toBeCloseTo(2 / 3, 10)
})

test('a counts-only run contributes counts to acceptance but not unique or corroborated', () => {
  const metrics = computeSeatMetrics(
    [
      run({
        runId: 'R1',
        issuesRaised: 0,
        issuesAccepted: 0,
        costUsd: 2,
        recordedAt: '2026-10-18T23:59:59Z',
      }),
      run({
        id: 'counts-only',
        runId: 'R2',
        issuesRaised: 4,
        issuesAccepted: 3,
        costUsd: 8,
        recordedAt: '2026-10-20T12:00:00Z',
      }),
    ],
    [finding({ runId: 'R1' })],
    'unique',
  )

  expect(metrics[0]).toMatchObject({
    runs: 2,
    countsOnlyRuns: 1,
    raised: 5,
    accepted: 4,
    unique: 1,
    corroborated: 0,
    acceptanceRate: 4 / 5,
    knownCostUsd: 10,
    // Only the detailed run's spend (2) is divided by its one unique catch, not the 8 spent on the counts-only run.
    costPerUnique: 2,
  })
  expect(metrics[0]?.byWeek.map((week) => [week.week, week.influence])).toEqual([
    ['2026-W42', 1],
    ['2026-W43', 0],
  ])
})

test('uses the selected pivot as per-run influence, excluding counts-only from unique pivots', () => {
  const runs = [
    run({ runId: 'R1', costUsd: 2 }),
    run({
      id: 'counts-only',
      runId: 'R2',
      issuesRaised: 2,
      issuesAccepted: 1,
      recordedAt: '2026-10-20T12:00:00Z',
    }),
  ]
  const findings = [
    finding({ runId: 'R1' }),
    finding({
      runId: 'R1',
      findingId: 'F2',
      raisedBy: [
        { reviewer: 'zai', model: 'glm-5.3' },
        { reviewer: 'openai', model: 'gpt-6-astra' },
      ],
      outcome: 'accepted',
    }),
  ]

  const pivots = (['unique', 'corroborated', 'accepted', 'acceptanceRate'] as Pivot[]).map(
    (pivot) => {
      const [item] = computeSeatMetrics(runs, findings, pivot)
      if (!item) throw new Error('pivot fixture must produce a seat')
      return [pivot, item] as const
    },
  )

  expect(new Map(pivots.map(([pivot, item]) => [pivot, item.byWeek[0]?.influence]))).toEqual(
    new Map([
      ['unique', 1],
      ['corroborated', 1],
      ['accepted', 1.5],
      ['acceptanceRate', 0.75],
    ]),
  )
})

test('buckets weekly known cost and influence by UTC ISO week from run timestamps', () => {
  const metrics = computeSeatMetrics(
    [
      run({ runId: 'R1', recordedAt: '2026-10-18T23:59:59Z', costUsd: 2 }),
      run({ id: 'next-week', runId: 'R2', recordedAt: '2026-10-19T00:00:01Z', costUsd: 3 }),
    ],
    [finding({ runId: 'R1' }), finding({ runId: 'R2', findingId: 'F2' })],
    'unique',
  )

  expect(metrics[0]?.byWeek).toEqual([
    { week: '2026-W42', costUsd: 2, influence: 1 },
    { week: '2026-W43', costUsd: 3, influence: 1 },
  ])
})

test('marks fewer than five runs as a small sample and five runs as sufficient', () => {
  const four = computeSeatMetrics(
    Array.from({ length: 4 }, (_, i) => run({ id: `r${i}`, runId: `R${i}` })),
    [],
    'unique',
  )
  const five = computeSeatMetrics(
    Array.from({ length: 5 }, (_, i) => run({ id: `r${i}`, runId: `R${i}` })),
    [],
    'unique',
  )

  expect(four[0]?.smallSample).toBe(true)
  expect(five[0]?.smallSample).toBe(false)
})
