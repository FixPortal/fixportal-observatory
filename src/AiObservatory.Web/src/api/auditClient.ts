import { ApiError, requestApi } from './client'
import { isReadonly } from '../auth/msal'
import { summarizeFrozenPresentation } from './auditPresentation'
import type {
  AuditCapabilities,
  AuditRetention,
  AuditDeletionImpact,
  AuditCreated,
  AuditCredential,
  AuditEvidencePage,
  AuditFinding,
  AuditPage,
  AuditReadinessDto,
  AuditRun,
  AuditScope,
  PresentationProbe,
} from './auditTypes'

function writable() {
  if (isReadonly || import.meta.env.VITE_DEMO_BANNER === 'true')
    throw new ApiError(403, 'This audit view is read only.')
}
async function json<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await requestApi(`/audits${path}`, init)
  return response.json() as Promise<T>
}
function query(params: Record<string, string | null>) {
  return (
    '?' +
    new URLSearchParams(
      Object.entries(params).filter((pair): pair is [string, string] => pair[1] !== null),
    ).toString()
  )
}
function post<T>(path: string, body: unknown, idempotencyKey = crypto.randomUUID()): Promise<T> {
  writable()
  return json<T>(path, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'Idempotency-Key': idempotencyKey },
    body: JSON.stringify(body),
  })
}
export async function getAuditCapabilities(): Promise<AuditCapabilities | null> {
  try {
    return await json<AuditCapabilities>('/capabilities')
  } catch (error) {
    if (error instanceof ApiError && error.status === 404) return null
    throw error
  }
}
export const getAuditReadiness = (scope: AuditScope) =>
  json<AuditReadinessDto>(
    '/readiness' +
      query({
        from: scope.from,
        to: scope.to,
        sources: [...new Set(scope.sources.map((s) => s.sourceId))].sort().join(','),
      }),
  )
export const getAuditRuns = (cursor: string | null = null) =>
  json<AuditPage<AuditRun>>('/runs' + query({ limit: '50', cursor }))
export const getAuditRun = (id: string) => json<AuditRun>(`/runs/${encodeURIComponent(id)}`)
export const getAuditFindings = (id: string, cursor: string | null = null) =>
  json<AuditPage<AuditFinding>>(
    `/runs/${encodeURIComponent(id)}/findings` + (cursor ? query({ cursor }) : ''),
  )
export const getAuditEvidence = (
  id: string,
  cursor: string | null = null,
  signal?: AbortSignal,
) => {
  writable()
  return json<AuditEvidencePage>(
    `/runs/${encodeURIComponent(id)}/evidence` + query({ limit: '200', cursor }),
    signal ? { signal } : undefined,
  )
}
export const createAuditRun = (request: {
  scope: AuditScope
  captureIds: string[]
  requestFreshCapture: boolean
}) => post<AuditCreated>('/runs', { schemaVersion: 1, ...request })
export async function cancelAuditRun(id: string): Promise<void> {
  writable()
  await requestApi(`/audits/runs/${encodeURIComponent(id)}/cancel`, { method: 'POST' })
}
export async function proceedAuditRun(id: string): Promise<void> {
  writable()
  await requestApi(`/audits/runs/${encodeURIComponent(id)}/proceed`, { method: 'POST' })
}
export const replayAuditRun = (id: string) =>
  post<AuditCreated>(`/runs/${encodeURIComponent(id)}/replay`, undefined)
export const submitAuditPresentation = (id: string, probe: PresentationProbe) =>
  post<AuditCreated>(`/runs/${encodeURIComponent(id)}/presentation-probe`, {
    schemaVersion: 1,
    probe,
  })
export const issueAuditCredential = () => post<AuditCredential>('/machines', { schemaVersion: 1 })
export const rotateAuditCredential = (id: string) =>
  post<AuditCredential>(`/machines/${encodeURIComponent(id)}/rotate`, { schemaVersion: 1 })
export async function revokeAuditCredential(id: string): Promise<void> {
  writable()
  await requestApi(`/audits/machines/${encodeURIComponent(id)}/credential`, { method: 'DELETE' })
}
export async function downloadAudit(id: string, kind: 'report' | 'bundle'): Promise<void> {
  if (kind === 'bundle') writable()
  const response = await requestApi(`/audits/runs/${encodeURIComponent(id)}/${kind}`)
  const href = URL.createObjectURL(await response.blob())
  const anchor = document.createElement('a')
  anchor.href = href
  anchor.download = `audit-${id}.${kind === 'report' ? 'json' : 'zip'}`
  anchor.click()
  URL.revokeObjectURL(href)
}

/** Drain every stable page and require the same snapshot/input binding before any observation. */
export async function verifyAuditPresentation(id: string, signal?: AbortSignal) {
  writable()
  const items: AuditEvidencePage['items'] = []
  const cursors = new Set<string>()
  let cursor: string | null = null
  let binding: { frozenSnapshotHash: string; summaryInputDigest: string } | undefined
  do {
    const page = await getAuditEvidence(id, cursor, signal)
    if (
      !/^[a-f0-9]{64}$/.test(page.frozenSnapshotHash) ||
      !/^[a-f0-9]{64}$/.test(page.summaryInputDigest)
    )
      throw new Error('Missing frozen evidence binding; presentation remains incomplete.')
    if (
      binding &&
      (binding.frozenSnapshotHash !== page.frozenSnapshotHash ||
        binding.summaryInputDigest !== page.summaryInputDigest)
    )
      throw new Error('Frozen evidence changed between pages; presentation remains incomplete.')
    binding = {
      frozenSnapshotHash: page.frozenSnapshotHash,
      summaryInputDigest: page.summaryInputDigest,
    }
    items.push(...page.items)
    cursor = page.nextCursor
    if (cursor && cursors.has(cursor))
      throw new Error('Repeated evidence cursor; presentation remains incomplete.')
    if (cursor) cursors.add(cursor)
  } while (cursor)
  const summary = summarizeFrozenPresentation(items)
  if (!summary.values.length)
    throw new Error(`Presentation coverage incomplete: ${summary.diagnosticCodes.join(', ')}`)
  if (typeof __AUDIT_BUILD_ID__ !== 'string' || typeof __AUDIT_SOURCE_ID__ !== 'string')
    throw new Error('Frontend build identity unavailable; presentation remains incomplete.')
  const evaluation = await submitAuditPresentation(id, {
    ...binding,
    frontendBuildId: __AUDIT_BUILD_ID__,
    frontendSourceId: __AUDIT_SOURCE_ID__,
    values: summary.values,
  })
  return { ...evaluation, diagnosticCodes: summary.diagnosticCodes }
}

export function getAuditRetention() {
  writable()
  return json<AuditRetention>('/retention')
}
export function updateAuditRetention(value: AuditRetention) {
  writable()
  return json<AuditRetention>('/retention', {
    method: 'PATCH',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(value),
  })
}
export function getAuditDeletionImpact(id: string) {
  writable()
  return json<AuditDeletionImpact>(`/captures/${encodeURIComponent(id)}/deletion-impact`)
}
export async function deleteAuditCapture(impact: AuditDeletionImpact) {
  writable()
  await requestApi(`/audits/captures/${encodeURIComponent(impact.captureId)}`, {
    method: 'DELETE',
    headers: { 'If-Match': '"' + impact.impactDigest + '"' },
  })
}
export async function deleteAuditRun(id: string) {
  writable()
  await requestApi(`/audits/runs/${encodeURIComponent(id)}`, { method: 'DELETE' })
}
