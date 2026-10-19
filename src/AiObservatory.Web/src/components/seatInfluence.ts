import type { AdversarialReviewRun } from '../api/client'
import type { FindingRow } from '../api/useAdversarialFindings'

export type {
  Assessment,
  AssessmentVerdict,
  FindingOutcome,
  FindingParticipant,
  FindingRow,
  FindingSeverity,
} from '../api/useAdversarialFindings'

export type RunRow = AdversarialReviewRun
export type Pivot = 'unique' | 'corroborated' | 'accepted' | 'acceptanceRate'

export interface SeatMetrics {
  key: string
  label: string
  mixed: boolean
  runs: number
  raised: number
  accepted: number
  unique: number
  corroborated: number
  peerAgreement: number
  refuted: number
  dropped: number
  indeterminate: number
  falsePositiveRate: number | null
  acceptanceRate: number | null
  knownCostUsd: number
  unknownCostRuns: number
  costPerUnique: number | null
  countsOnlyRuns: number
  smallSample: boolean
  byWeek: { week: string; costUsd: number; influence: number }[]
}

interface FindingAggregates {
  raised: number
  accepted: number
  unique: number
  corroborated: number
  refuted: number
  dropped: number
  indeterminate: number
}

interface WeekAggregates extends FindingAggregates {
  costUsd: number
  runs: number
  detailedRuns: number
}

interface SeatAggregates extends FindingAggregates {
  key: string
  label: string
  mixed: boolean
  runs: number
  peerAgreement: number
  knownCostRuns: number
  knownCostUsd: number
  // Known cost of runs that carry finding detail only: unique catches exist for those runs
  // alone, so cost per unique catch must not be divided over counts-only runs' spend.
  detailedKnownCostUsd: number
  unknownCostRuns: number
  countsOnlyRuns: number
  byWeek: Map<string, WeekAggregates>
}

const runKey = (runId: string, reviewer: string) => `${runId}\u0000${reviewer}`

function isoWeekUtc(iso: string): string {
  const instant = new Date(iso)
  const day = instant.getUTCDay() || 7
  const thursday = new Date(instant)
  thursday.setUTCDate(instant.getUTCDate() + 4 - day)
  const yearStart = Date.UTC(thursday.getUTCFullYear(), 0, 1)
  const week = Math.ceil(((thursday.getTime() - yearStart) / 86_400_000 + 1) / 7)
  return `${thursday.getUTCFullYear()}-W${String(week).padStart(2, '0')}`
}

function emptyFindingAggregates(): FindingAggregates {
  return {
    raised: 0,
    accepted: 0,
    unique: 0,
    corroborated: 0,
    refuted: 0,
    dropped: 0,
    indeterminate: 0,
  }
}

function emptyWeek(): WeekAggregates {
  return {
    ...emptyFindingAggregates(),
    costUsd: 0,
    runs: 0,
    detailedRuns: 0,
  }
}

function emptySeat(model: string): SeatAggregates {
  return {
    ...emptyFindingAggregates(),
    key: model,
    label: model.includes('+') ? `mixed: ${model}` : model,
    mixed: model.includes('+'),
    runs: 0,
    peerAgreement: 0,
    knownCostUsd: 0,
    detailedKnownCostUsd: 0,
    unknownCostRuns: 0,
    knownCostRuns: 0,
    countsOnlyRuns: 0,
    byWeek: new Map(),
  }
}

function weekFor(seat: SeatAggregates, recordedAt: string): WeekAggregates {
  const week = isoWeekUtc(recordedAt)
  const existing = seat.byWeek.get(week)
  if (existing) return existing
  const created = emptyWeek()
  seat.byWeek.set(week, created)
  return created
}

function influenceFor(
  seat: SeatAggregates,
  week: WeekAggregates | undefined,
  pivot: Pivot,
): number {
  if (pivot === 'acceptanceRate') {
    const raised = week?.raised ?? seat.raised
    return raised === 0 ? 0 : (week?.accepted ?? seat.accepted) / raised
  }
  if (pivot === 'accepted') return (week?.accepted ?? seat.accepted) / (week?.runs ?? seat.runs)
  const value =
    pivot === 'unique' ? (week?.unique ?? seat.unique) : (week?.corroborated ?? seat.corroborated)
  const detailedRuns = week?.detailedRuns ?? seat.runs - seat.countsOnlyRuns
  return detailedRuns === 0 ? 0 : value / detailedRuns
}

function accumulateRun(seat: SeatAggregates, row: RunRow, detailedRunIds: Set<string>): void {
  const countsOnly = !detailedRunIds.has(row.runId)
  seat.runs += 1
  if (countsOnly) {
    seat.countsOnlyRuns += 1
    seat.raised += row.issuesRaised
    seat.accepted += row.issuesAccepted
  }
  if (row.costUnknown) {
    seat.unknownCostRuns += 1
  } else {
    seat.knownCostRuns += 1
    seat.knownCostUsd += row.costUsd
    if (!countsOnly) seat.detailedKnownCostUsd += row.costUsd
  }

  const week = weekFor(seat, row.recordedAt)
  week.runs += 1
  if (countsOnly) {
    week.raised += row.issuesRaised
    week.accepted += row.issuesAccepted
  } else {
    week.detailedRuns += 1
  }
  if (!row.costUnknown) week.costUsd += row.costUsd
}

function accumulateFinding(target: FindingAggregates, finding: FindingRow): void {
  target.raised += 1
  if (finding.outcome === 'accepted') {
    target.accepted += 1
    if (finding.raisedBy.length === 1) target.unique += 1
    else target.corroborated += 1
    return
  }
  if (finding.outcome === 'refuted') target.refuted += 1
  else if (finding.outcome === 'dropped') target.dropped += 1
  else target.indeterminate += 1
}

function peerAgreementReceived(finding: FindingRow, reviewers: Set<string>): number {
  return finding.assessments.reduce(
    (total, assessment) =>
      assessment.verdict === 'agree' && !reviewers.has(assessment.reviewer) ? total + 1 : total,
    0,
  )
}

interface FindingTarget {
  row: RunRow
  reviewers: Set<string>
}

function targetsForFinding(
  finding: FindingRow,
  reviewerRows: Map<string, RunRow>,
): Map<string, FindingTarget> {
  const targets = new Map<string, FindingTarget>()
  for (const raiser of finding.raisedBy) {
    const row = reviewerRows.get(runKey(finding.runId, raiser.reviewer))
    if (row?.role !== 'reviewer') continue
    const target = targets.get(row.model)
    if (target) {
      target.reviewers.add(raiser.reviewer)
    } else {
      targets.set(row.model, { row, reviewers: new Set([raiser.reviewer]) })
    }
  }
  return targets
}

function accumulateFindingForSeat(
  seat: SeatAggregates,
  finding: FindingRow,
  target: FindingTarget,
): void {
  accumulateFinding(seat, finding)
  seat.peerAgreement += peerAgreementReceived(finding, target.reviewers)
  const week = weekFor(seat, target.row.recordedAt)
  accumulateFinding(week, finding)
}

export function computeSeatMetrics(
  runs: RunRow[],
  findings: FindingRow[],
  pivot: Pivot,
): SeatMetrics[] {
  const seats = new Map<string, SeatAggregates>()
  const reviewerRows = new Map<string, RunRow>()
  const detailedRunIds = new Set(findings.map((finding) => finding.runId))

  const seatFor = (model: string): SeatAggregates => {
    const existing = seats.get(model)
    if (existing) return existing
    const created = emptySeat(model)
    seats.set(model, created)
    return created
  }

  for (const row of runs) {
    if (row.role !== 'reviewer') continue
    const key = runKey(row.runId, row.reviewer)
    if (!reviewerRows.has(key)) reviewerRows.set(key, row)

    accumulateRun(seatFor(row.model), row, detailedRunIds)
  }

  for (const finding of findings) {
    const targets = targetsForFinding(finding, reviewerRows)
    for (const [model, target] of targets) {
      accumulateFindingForSeat(seatFor(model), finding, target)
    }
  }

  return [...seats.values()]
    .toSorted((a, b) => a.label.localeCompare(b.label))
    .map((seat) => {
      const settled = seat.accepted + seat.refuted
      return {
        key: seat.key,
        label: seat.label,
        mixed: seat.mixed,
        runs: seat.runs,
        raised: seat.raised,
        accepted: seat.accepted,
        unique: seat.unique,
        corroborated: seat.corroborated,
        peerAgreement: seat.peerAgreement,
        refuted: seat.refuted,
        dropped: seat.dropped,
        indeterminate: seat.indeterminate,
        falsePositiveRate: settled === 0 ? null : seat.refuted / settled,
        acceptanceRate: seat.raised === 0 ? null : seat.accepted / seat.raised,
        knownCostUsd: seat.knownCostUsd,
        unknownCostRuns: seat.unknownCostRuns,
        costPerUnique:
          seat.unique === 0 || seat.detailedKnownCostUsd === 0
            ? null
            : seat.detailedKnownCostUsd / seat.unique,
        countsOnlyRuns: seat.countsOnlyRuns,
        smallSample: seat.runs < 5,
        byWeek: [...seat.byWeek.entries()]
          .toSorted(([a], [b]) => a.localeCompare(b))
          .map(([week, values]) => ({
            week,
            costUsd: values.costUsd,
            influence: influenceFor(seat, values, pivot),
          })),
      }
    })
}
