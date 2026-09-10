import type { AdversarialReviewRun } from '../api/client'

const REVIEWER_ORDER = ['anthropic', 'google', 'openai', 'moonshot']

// A "complete" panel is one reviewer per vendor across the full cross-vendor
// roster, plus the adjudicating judge. The v2 panel spans four vendors
// (Anthropic, Google, OpenAI, Moonshot); pre-Moonshot runs therefore read as
// "N of 4".
const EXPECTED_REVIEWER_VENDORS = REVIEWER_ORDER.length

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
  totals: { raised: number; accepted: number; costUsd: number; durationMs: number }
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
    const recordedAt = participants.reduce(
      (earliest, p) => (p.recordedAt < earliest ? p.recordedAt : earliest),
      participants[0].recordedAt,
    )
    participants.sort(sortParticipants)
    const reviewerVendors = new Set(participants.flatMap(p => (p.role === 'reviewer' ? [p.reviewer] : [])))
    const hasJudge = participants.some(p => p.role === 'judge')
    const reviewerCount = REVIEWER_ORDER.filter(vendor => reviewerVendors.has(vendor)).length
    const isComplete = reviewerCount === EXPECTED_REVIEWER_VENDORS && hasJudge
    const statusReason = isComplete
      ? 'complete'
      : `${reviewerCount} of ${EXPECTED_REVIEWER_VENDORS} reviewers${hasJudge ? '' : ' · no judge'}`

    groups.push({
      runId,
      repo: participants.find(p => p.repo)?.repo ?? null,
      summary: participants.find(p => p.summary)?.summary ?? null,
      recordedAt,
      participants,
      totals: {
        raised: participants.reduce((s, p) => s + p.issuesRaised, 0),
        accepted: participants.reduce((s, p) => s + p.issuesAccepted, 0),
        costUsd: Math.round(participants.reduce((s, p) => s + p.costUsd, 0) * 1e6) / 1e6,
        durationMs: participants.reduce((s, p) => s + p.reviewDurationMs, 0),
      },
      isComplete,
      statusReason,
      isValid: reviewerCount >= MIN_VALID_REVIEWERS,
      // A batched run carries the same chunk count on every participant; take the
      // first non-null. Null when no participant reported one (single-diff run).
      chunkCount: participants.find(p => p.chunkCount != null)?.chunkCount ?? null,
    })
  }

  return groups.sort((a, b) => b.recordedAt.localeCompare(a.recordedAt))
}
