import { Suspense, lazy, useMemo, useState } from 'react'
import { useAdversarialFindings } from '../api/useAdversarialFindings'
import { participantColor } from '../theme/providerColors'
import {
  computeSeatMetrics,
  type FindingRow,
  type Pivot,
  type RunRow,
  type SeatMetrics,
} from './seatInfluence'

interface Props {
  readonly runs: RunRow[]
  readonly runsLoading: boolean
  readonly runsError: boolean
}

interface ScatterPoint {
  key: string
  label: string
  costPerRun: number | null
  influence: number
  runs: number
}

interface TrendSeries {
  key: string
  seatKey: string
  label: string
  color: string
}

interface TrendRow {
  week: string
  [seriesKey: string]: string | number
}

interface ChartData {
  points: ScatterPoint[]
  rows: TrendRow[]
  series: TrendSeries[]
  pivotLabel: string
  isRate: boolean
}

const PIVOT_OPTIONS: { id: Pivot; label: string }[] = [
  { id: 'unique', label: 'Unique accepted' },
  { id: 'corroborated', label: 'Corroborated accepted' },
  { id: 'accepted', label: 'Accepted per run' },
  { id: 'acceptanceRate', label: 'Acceptance rate' },
]

const TEXT_MUTED = 'var(--text-muted)'

function selectedInfluence(seat: SeatMetrics, pivot: Pivot): number {
  if (pivot === 'acceptanceRate') return seat.acceptanceRate ?? 0
  if (pivot === 'accepted') return seat.runs === 0 ? 0 : seat.accepted / seat.runs
  const detailedRuns = seat.runs - seat.countsOnlyRuns
  if (detailedRuns === 0) return 0
  return (pivot === 'unique' ? seat.unique : seat.corroborated) / detailedRuns
}

function pivotHeading(pivot: Pivot): string {
  if (pivot === 'unique') return 'Unique accepted per detailed run'
  if (pivot === 'corroborated') return 'Corroborated accepted per detailed run'
  if (pivot === 'accepted') return 'Accepted per run'
  return 'Acceptance rate'
}

function formatRate(value: number | null): string {
  return value == null ? '—' : `${(value * 100).toFixed(1)}%`
}

function formatInfluence(value: number, pivot: Pivot): string {
  return pivot === 'acceptanceRate' ? formatRate(value) : value.toFixed(2)
}

function formatCost(value: number | null): string {
  return value == null ? '—' : `$${value.toFixed(2)}`
}

function formatKnownCost(seat: SeatMetrics): string {
  return seat.unknownCostRuns >= seat.runs ? 'Unknown' : formatCost(seat.knownCostUsd)
}

function primaryReviewerByKey(runs: RunRow[]): Map<string, string> {
  const reviewers = new Map<string, string>()
  for (const run of runs) {
    if (run.role !== 'reviewer' || reviewers.has(run.model)) continue
    reviewers.set(run.model, run.reviewer)
  }
  return reviewers
}

function buildChartData(metrics: SeatMetrics[], runs: RunRow[], pivot: Pivot): ChartData {
  const reviewers = primaryReviewerByKey(runs)
  const points = metrics.map((seat) => {
    const knownRuns = seat.runs - seat.unknownCostRuns
    return {
      key: seat.key,
      label: seat.label,
      costPerRun: knownRuns <= 0 ? null : seat.knownCostUsd / knownRuns,
      influence: selectedInfluence(seat, pivot),
      runs: seat.runs,
    }
  })

  const weeks = new Set(metrics.flatMap((seat) => seat.byWeek.map((week) => week.week)))
  const series = metrics.map((seat, index) => ({
    key: `seat-${index}`,
    seatKey: seat.key,
    label: seat.label,
    color: participantColor(reviewers.get(seat.key) ?? 'other', 'reviewer'),
  }))
  const rows = [...weeks]
    .toSorted((a, b) => a.localeCompare(b))
    .map((week) => {
      const row: TrendRow = { week }
      for (const seat of metrics) {
        const values = seat.byWeek.find((item) => item.week === week)
        const seriesKey = series.find((item) => item.seatKey === seat.key)?.key
        if (seriesKey) {
          row[seriesKey] = values?.influence ?? 0
          row[`${seriesKey}-cost`] = values?.costUsd ?? 0
        }
      }
      return row
    })

  return {
    points,
    rows,
    series,
    pivotLabel: pivotHeading(pivot),
    isRate: pivot === 'acceptanceRate',
  }
}

const SeatCharts = lazy(() =>
  import('recharts').then(
    ({
      ScatterChart,
      Scatter,
      LineChart,
      Line,
      XAxis,
      YAxis,
      ZAxis,
      Tooltip,
      Legend,
      ResponsiveContainer,
    }) => ({
      default: function Inner({
        data,
        onSelectSeat,
      }: {
        data: ChartData
        onSelectSeat: (key: string) => void
      }) {
        const tooltipStyle = {
          background: 'var(--card-bg)',
          border: '1px solid var(--border)',
          borderRadius: 'var(--r-chip)',
          color: 'var(--text)',
        }
        const axisTick = { fontSize: 10, fill: TEXT_MUTED }
        return (
          <div className="seat-charts">
            <div>
              <p className="seat-chart__heading">Cost vs {data.pivotLabel}</p>
              <ResponsiveContainer width="100%" height={260}>
                <ScatterChart margin={{ top: 8, right: 16, bottom: 24, left: 4 }}>
                  <XAxis
                    type="number"
                    dataKey="costPerRun"
                    name="Known cost per run"
                    tick={axisTick}
                    tickFormatter={(value: number) => `$${value.toFixed(2)}`}
                    label={{
                      value: 'Known cost per run (USD)',
                      position: 'insideBottom',
                      offset: -14,
                      fill: TEXT_MUTED,
                      fontSize: 10,
                    }}
                  />
                  <YAxis
                    type="number"
                    dataKey="influence"
                    name={data.pivotLabel}
                    tick={axisTick}
                    tickFormatter={(value: number) =>
                      data.isRate ? `${Math.round(value * 100)}%` : value.toFixed(1)
                    }
                  />
                  <ZAxis type="number" dataKey="runs" name="Runs" range={[48, 480]} />
                  <Tooltip contentStyle={tooltipStyle} labelStyle={{ color: 'var(--text)' }} />
                  <Scatter
                    data={data.points}
                    name="Seats"
                    fill="var(--brand)"
                    fillOpacity={0.72}
                    onClick={(point: { payload?: { key?: string } }) => {
                      const key = point.payload?.key
                      if (key) onSelectSeat(key)
                    }}
                  />
                </ScatterChart>
              </ResponsiveContainer>
              <p className="panel-note">
                Point area is runs contributed; unknown cost has no X value.
              </p>
            </div>
            <div>
              <p className="seat-chart__heading">Weekly influence and known cost</p>
              <ResponsiveContainer width="100%" height={260}>
                <LineChart data={data.rows} margin={{ top: 8, right: 8, bottom: 4, left: 4 }}>
                  <XAxis dataKey="week" tick={axisTick} />
                  <YAxis
                    yAxisId="influence"
                    tick={axisTick}
                    tickFormatter={(value: number) =>
                      data.isRate ? `${Math.round(value * 100)}%` : value.toFixed(1)
                    }
                  />
                  <YAxis
                    yAxisId="cost"
                    orientation="right"
                    tick={axisTick}
                    tickFormatter={(value: number) => `$${value.toFixed(0)}`}
                  />
                  <Tooltip contentStyle={tooltipStyle} labelStyle={{ color: 'var(--text)' }} />
                  <Legend wrapperStyle={{ fontSize: 11, color: TEXT_MUTED }} />
                  {data.series.map((series) => (
                    <Line
                      key={series.key}
                      yAxisId="influence"
                      dataKey={series.key}
                      name={`${series.label} · ${data.pivotLabel}`}
                      stroke={series.color}
                      dot={{ r: 2 }}
                    />
                  ))}
                  {data.series.map((series) => (
                    <Line
                      key={`${series.key}-cost`}
                      yAxisId="cost"
                      dataKey={`${series.key}-cost`}
                      name={`${series.label} · known cost`}
                      stroke={series.color}
                      strokeDasharray="4 3"
                      dot={false}
                    />
                  ))}
                </LineChart>
              </ResponsiveContainer>
            </div>
          </div>
        )
      },
    }),
  ),
)

interface DrillRow {
  findingId: string
  runId: string
  severity: string
  outcome: string
  coRaisers: string[]
}

function drillRows(runs: RunRow[], findings: FindingRow[], seatKey: string): DrillRow[] {
  const reviewersByRun = new Map<string, Set<string>>()
  for (const run of runs) {
    if (run.role !== 'reviewer' || run.model !== seatKey) continue
    const reviewers = reviewersByRun.get(run.runId) ?? new Set<string>()
    reviewers.add(run.reviewer)
    reviewersByRun.set(run.runId, reviewers)
  }

  return findings
    .filter((finding) =>
      finding.raisedBy.some((raiser) => reviewersByRun.get(finding.runId)?.has(raiser.reviewer)),
    )
    .map((finding) => {
      const ownReviewers = reviewersByRun.get(finding.runId) ?? new Set<string>()
      return {
        findingId: finding.findingId,
        runId: finding.runId,
        severity: finding.severity,
        outcome: finding.outcome,
        coRaisers: finding.raisedBy
          .filter((raiser) => !ownReviewers.has(raiser.reviewer))
          .map((raiser) => `${raiser.reviewer} · ${raiser.model}`),
      }
    })
    .toSorted((a, b) => a.findingId.localeCompare(b.findingId))
}

export function SeatsTab({ runs, runsLoading, runsError }: Props) {
  const { findings, isError: findingsError, isLoading: findingsLoading } = useAdversarialFindings()
  const [pivot, setPivot] = useState<Pivot>('unique')
  const [requestedSeatKey, setRequestedSeatKey] = useState<string | null>(null)
  const metrics = useMemo(() => computeSeatMetrics(runs, findings, pivot), [runs, findings, pivot])
  const selectedKey = metrics.some((seat) => seat.key === requestedSeatKey)
    ? requestedSeatKey
    : metrics[0]?.key
  const selected = metrics.find((seat) => seat.key === selectedKey)
  const chartData = useMemo(() => buildChartData(metrics, runs, pivot), [metrics, runs, pivot])
  const unknownCostRuns = metrics.reduce((total, seat) => total + seat.unknownCostRuns, 0)
  const loading = runsLoading || findingsLoading
  const error = runsError || findingsError

  if (error) {
    return (
      <div className="panel">
        <div className="panel-title">Seat influence</div>
        <p className="panel-empty" role="alert">
          Couldn’t load seat data — try refreshing.
        </p>
      </div>
    )
  }
  if (loading) {
    return (
      <div className="panel">
        <div className="panel-title">Seat influence</div>
        <p className="panel-empty">Loading seat influence...</p>
      </div>
    )
  }
  if (metrics.length === 0) {
    return (
      <div className="panel">
        <div className="panel-title">Seat influence</div>
        <p className="panel-empty">No seat data recorded yet.</p>
      </div>
    )
  }

  return (
    <>
      <section className="panel" aria-labelledby="seat-influence-title">
        <div className="panel-title" id="seat-influence-title">
          Seat influence
        </div>
        <div className="seat-toolbar">
          <div className="chart-toggle" role="group" aria-label="Seat influence pivot">
            {PIVOT_OPTIONS.map((option) => (
              <button
                key={option.id}
                type="button"
                className={`chart-toggle-btn${pivot === option.id ? ' chart-toggle-btn--active' : ''}`}
                aria-pressed={pivot === option.id}
                onClick={() => {
                  setPivot(option.id)
                }}
              >
                {option.label}
              </button>
            ))}
          </div>
          <p className="panel-note">{pivotHeading(pivot)}</p>
          <p className="panel-note">
            {unknownCostRuns} run{unknownCostRuns === 1 ? '' : 's'} unknown cost
          </p>
        </div>
        <Suspense fallback={<div className="chart-skeleton" />}>
          <SeatCharts data={chartData} onSelectSeat={setRequestedSeatKey} />
        </Suspense>
      </section>

      <section className="panel" aria-labelledby="seat-metrics-title">
        <div className="panel-title" id="seat-metrics-title">
          Seat metrics
        </div>
        <div className="model-table-wrap">
          <table className="model-table seat-metrics-table">
            <thead>
              <tr>
                <th>Seat</th>
                <th>Selected influence</th>
                <th>Runs</th>
                <th>Raised</th>
                <th>Accepted</th>
                <th>Unique</th>
                <th>Corroborated</th>
                <th>Peer agreement</th>
                <th>Refuted</th>
                <th>Dropped</th>
                <th>Indeterminate</th>
                <th>False-positive rate</th>
                <th>Acceptance rate</th>
                <th>Known cost</th>
                <th>Cost / unique</th>
                <th>Unknown-cost runs</th>
                <th>Counts-only runs</th>
                <th>Sample</th>
              </tr>
            </thead>
            <tbody>
              {metrics.map((seat) => (
                <tr key={seat.key} aria-selected={seat.key === selectedKey}>
                  <td>
                    <button
                      type="button"
                      className="seat-metrics__seat"
                      title={seat.key}
                      aria-label={`Select findings for ${seat.label}`}
                      onClick={() => {
                        setRequestedSeatKey(seat.key)
                      }}
                    >
                      {seat.label}
                    </button>
                    {seat.mixed && (
                      <span className="model-table__qualification">mixed model row</span>
                    )}
                  </td>
                  <td>{formatInfluence(selectedInfluence(seat, pivot), pivot)}</td>
                  <td>{seat.runs}</td>
                  <td>{seat.raised}</td>
                  <td>{seat.accepted}</td>
                  <td>{seat.unique}</td>
                  <td>{seat.corroborated}</td>
                  <td>{seat.peerAgreement}</td>
                  <td>{seat.refuted}</td>
                  <td>{seat.dropped}</td>
                  <td>{seat.indeterminate}</td>
                  <td>{formatRate(seat.falsePositiveRate)}</td>
                  <td>{formatRate(seat.acceptanceRate)}</td>
                  <td>{formatKnownCost(seat)}</td>
                  <td>{formatCost(seat.costPerUnique)}</td>
                  <td>{seat.unknownCostRuns}</td>
                  <td>{seat.countsOnlyRuns}</td>
                  <td>{seat.smallSample ? 'small sample' : 'sufficient'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </section>

      {selected && (
        <section className="panel" aria-labelledby="seat-findings-title">
          <div className="panel-title" id="seat-findings-title">
            Findings for {selected.label}
          </div>
          <div className="model-table-wrap">
            <table className="model-table">
              <thead>
                <tr>
                  <th>Finding</th>
                  <th>Run</th>
                  <th>Severity</th>
                  <th>Outcome</th>
                  <th>Co-raisers</th>
                </tr>
              </thead>
              <tbody>
                {drillRows(runs, findings, selected.key).map((row) => (
                  <tr key={`${row.runId}-${row.findingId}`}>
                    <td>{row.findingId}</td>
                    <td>{row.runId}</td>
                    <td>{row.severity}</td>
                    <td>{row.outcome}</td>
                    <td>{row.coRaisers.length === 0 ? '—' : row.coRaisers.join(', ')}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <p className="panel-note">IDs only; finding text stays in the run folder.</p>
        </section>
      )}
    </>
  )
}
