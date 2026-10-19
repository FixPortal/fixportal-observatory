import { useQuery } from '@tanstack/react-query'
import { ApiError } from './client'
import { apiKey, getAccessToken, urlApiKey } from '../auth/msal'

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
// package adds one API module without widening the shared client surface.
async function authHeaders(): Promise<Record<string, string>> {
  if (urlApiKey) return { 'X-Observatory-Key': urlApiKey }
  const token = await getAccessToken()
  if (token) return { Authorization: `Bearer ${token}` }
  if (apiKey) return { 'X-Observatory-Key': apiKey }
  return {}
}

async function getAdversarialFindings(): Promise<FindingRow[]> {
  const response = await fetch(`${BASE}/adversarial-review/findings`, {
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
  isError: boolean
  isLoading: boolean
} {
  const {
    data = [],
    isError,
    isPending,
  } = useQuery({
    queryKey: ['adversarial-review-findings'],
    queryFn: getAdversarialFindings,
  })
  return { findings: data, isError, isLoading: isPending }
}
