import { useQuery } from '@tanstack/react-query'
import { useMemo } from 'react'
import { ApiError } from './client'
import { apiKey, getAccessToken, urlApiKey } from '../auth/msal'
import type { Clock } from '../lib/clock'
import { useClock } from '../lib/useClock'

const BASE = (import.meta.env.VITE_API_BASE ?? '') + '/api'

export type FindingSeverity = 'critical' | 'high' | 'medium' | 'low' | 'unknown'
export type FindingOutcome = 'accepted' | 'refuted' | 'dropped' | 'indeterminate'
export type AssessmentVerdict =
  'agree' | 'false-positive' | 'needs-evidence' | 'needs-repo' | 'abstained'

export interface FindingParticipant {
  reviewer: string
  model: string
}

export interface Assessment extends FindingParticipant {
  verdict: AssessmentVerdict
}

export interface FindingRow {
  runId: string
  repo: string
  chunkId: string
  findingId: string
  severity: FindingSeverity
  outcome: FindingOutcome
  raisedBy: FindingParticipant[]
  assessments: Assessment[]
}

// Same auth priority as client.ts. The findings contract lives with its hook so this
// package adds one API module without widening the shared client surface; the copy of
// authHeaders below is that deliberate trade (it predates this pass).
async function authHeaders(): Promise<Record<string, string>> {
  if (urlApiKey) return { 'X-Observatory-Key': urlApiKey }
  const token = await getAccessToken()
  if (token) return { Authorization: `Bearer ${token}` }
  if (apiKey) return { 'X-Observatory-Key': apiKey }
  return {}
}

export const FINDINGS_WINDOW_DAYS = 180

/** The UTC date (YYYY-MM-DD) the findings window opens on; the Seats tab applies it to runs too. */
export function findingsWindowStart(clock: Clock): string {
  return new Date(clock.now().getTime() - FINDINGS_WINDOW_DAYS * 86_400_000)
    .toISOString()
    .slice(0, 10)
}

async function getAdversarialFindings(since: string): Promise<FindingRow[]> {
  const response = await fetch(`${BASE}/adversarial-review/findings?since=${since}`, {
    headers: await authHeaders(),
  })
  if (!response.ok) {
    throw new ApiError(
      response.status,
      `GET /adversarial-review/findings failed: ${response.status}`,
    )
  }
  return (await response.json()) as FindingRow[]
}

export function useAdversarialFindings(): {
  findings: FindingRow[]
  since: string
  isError: boolean
  isLoading: boolean
} {
  const clock = useClock()
  const since = useMemo(() => findingsWindowStart(clock), [clock])
  const {
    data = [],
    isError,
    isPending,
  } = useQuery({
    queryKey: ['adversarial-review-findings', since],
    queryFn: () => getAdversarialFindings(since),
  })
  return { findings: data, since, isError, isLoading: isPending }
}
