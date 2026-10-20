import type { AdversarialReviewRun } from '../api/client'

// The vendors on the panel today. Mirror of the enabled seats in the
// adversarial-review skill's reviewers.json; update both together when a seat is swapped.
const ACTIVE_REVIEWER_VENDORS = ['anthropic', 'google', 'openai', 'zai', 'deepseek']

// Seats retired from the panel. Their historical runs still sort among the reviewers but
// do not count toward a complete panel.
const RETIRED_REVIEWER_VENDORS = ['moonshot']

const REVIEWER_ORDER = [...ACTIVE_REVIEWER_VENDORS, ...RETIRED_REVIEWER_VENDORS]

// A "complete" panel is one reviewer per active vendor, plus the adjudicating judge.
// Runs from before a seat joined therefore read as "N of M".
const EXPECTED_REVIEWER_VENDORS = ACTIVE_REVIEWER_VENDORS.length

// A run below this reviewer-vendor count is too thin to carry cross-vendor signal at
// all — distinct from "incomplete" (missing one vendor off the full roster, still a
// meaningful comparison). This is a UI triage default, not a scored business rule.
export const MIN_VALID_REVIEWERS = 2

export interface RunGroup {
  runId: string
  repo: string | null
  summary: string | null
  recordedAt: string
  participants: AdversarialReviewRun[]
  /** costUsd sums known-cost participants only; null when every participant's cost is unknown. */
  totals: {
    raised: number
    accepted: number
    costUsd: number | null
    unknownCostRuns: number
    durationMs: number
  }
  isComplete: boolean
  statusReason: string
  /** false when the panel had too few reviewer vendors (< MIN_VALID_REVIEWERS) to trust. */
  isValid: boolean
  /** Chunks aggregated into this run when it was a batched review; null for a single-diff run. */
  chunkCount: number | null
}

// Banker's rounding (round half to even) to `dp` decimal places.
export function bankersRound(value: number, dp = 0): number {
  const f = 10 ** dp
  const n = value * f
  const floor = Math.floor(n)
  const diff = n - floor
  let rounded: number
  if (Math.abs(diff - 0.5) < 1e-9) {
    rounded = floor % 2 === 0 ? floor : floor + 1 // tie → nearest even
  } else {
    rounded = Math.round(n)
  }
  return rounded / f
}

// Per-panel durations: whole seconds (e.g. "188s").
export function formatSeconds(ms: number): string {
  return `${Math.round(ms / 1000)}s`
}

// Aggregate/average durations: minutes to 1dp (e.g. "3.1m").
export function formatMinutes(ms: number): string {
  return `${(ms / 60000).toFixed(1)}m`
}

function participantRank(p: AdversarialReviewRun): number {
  if (p.role === 'judge') return 99
  const idx = REVIEWER_ORDER.indexOf(p.reviewer)
  // Unknown vendors sort after the known reviewers but before the judge,
  // rather than ahead of everything on indexOf's -1.
  return idx === -1 ? REVIEWER_ORDER.length : idx
}

function sortParticipants(a: AdversarialReviewRun, b: AdversarialReviewRun): number {
  return participantRank(a) - participantRank(b)
}

export function groupRuns(runs: AdversarialReviewRun[]): RunGroup[] {
  const byRun = new Map<string, AdversarialReviewRun[]>()
  for (const r of runs) {
    const list = byRun.get(r.runId) ?? []
    list.push(r)
    byRun.set(r.runId, list)
  }

  const groups: RunGroup[] = []
  for (const [runId, participants] of byRun) {
    // Earliest participant timestamp = the run's time. Derive it before sorting
    // so it never depends on participant display order (ISO-8601 sorts lexically).
    const first = participants[0]
    if (first === undefined) continue
    const recordedAt = participants.reduce(
      (earliest, p) => (p.recordedAt < earliest ? p.recordedAt : earliest),
      first.recordedAt,
    )
    participants.sort(sortParticipants)
    const reviewerVendors = new Set(
      participants.flatMap((p) => (p.role === 'reviewer' ? [p.reviewer] : [])),
    )
    const hasJudge = participants.some((p) => p.role === 'judge')
    const reviewerCount = ACTIVE_REVIEWER_VENDORS.filter((vendor) =>
      reviewerVendors.has(vendor),
    ).length
    // Validity counts every vendor that reviewed, retired seats included: a run is no thinner
    // for a seat having been retired since.
    const validReviewerCount = REVIEWER_ORDER.filter((vendor) => reviewerVendors.has(vendor)).length
    const isComplete = reviewerCount === EXPECTED_REVIEWER_VENDORS && hasJudge
    const statusReason = isComplete
      ? 'complete'
      : `${reviewerCount} of ${EXPECTED_REVIEWER_VENDORS} reviewers${hasJudge ? '' : ' · no judge'}`

    const unknownCostRuns = participants.filter((p) => p.costUnknown).length
    const knownCostTotal = participants.reduce((s, p) => s + (p.costUnknown ? 0 : p.costUsd), 0)

    groups.push({
      runId,
      repo: participants.find((p) => p.repo)?.repo ?? null,
      summary: participants.find((p) => p.summary)?.summary ?? null,
      recordedAt,
      participants,
      totals: {
        raised: participants.reduce((s, p) => s + p.issuesRaised, 0),
        accepted: participants.reduce((s, p) => s + p.issuesAccepted, 0),
        costUsd:
          unknownCostRuns === participants.length ? null : Math.round(knownCostTotal * 1e6) / 1e6,
        unknownCostRuns,
        durationMs: participants.reduce((s, p) => s + p.reviewDurationMs, 0),
      },
      isComplete,
      statusReason,
      isValid: validReviewerCount >= MIN_VALID_REVIEWERS,
      // A batched run carries the same chunk count on every participant; take the
      // first non-null. Null when no participant reported one (single-diff run).
      chunkCount: participants.find((p) => p.chunkCount != null)?.chunkCount ?? null,
    })
  }

  return groups.sort((a, b) => b.recordedAt.localeCompare(a.recordedAt))
}
