import { afterEach, beforeEach, expect, test, vi } from 'vitest'
import {
  getAuditCapabilities,
  verifyAuditPresentation,
  downloadAudit,
  issueAuditCredential,
} from './auditClient'
import type { AuditEvidencePage } from './auditTypes'

const auth = vi.hoisted(() => ({
  isReadonly: false,
  urlApiKey: '',
  apiKey: 'configured',
  getAccessToken: vi.fn(async () => 'entra'),
}))
vi.mock('../auth/msal', () => auth)
const hash = 'a'.repeat(64)
const digest = 'b'.repeat(64)
const aggregate = {
  kind: 'aggregate',
  record: {
    key: {
      date: '2026-10-20',
      provider: 'provider',
      model: '',
      sourceId: 'local',
      sourceKind: 'localTelemetry',
      usageScope: 'subscription',
      costBasis: 'notional',
    },
    inputTokens: '10',
    outputTokens: '20',
    cacheReadTokens: '3',
    cacheWriteTokens: '0',
    cacheWrite1hTokens: '0',
    costUsd: '4',
    unknownCostCount: '0',
    cacheSavingsUsd: '0.125',
    unknownCacheSavingsCount: '0',
    requestCount: '1',
  },
} as const
const page = (
  items: AuditEvidencePage['items'],
  nextCursor: string | null = null,
): AuditEvidencePage => ({
  schemaVersion: 1,
  frozenSnapshotHash: hash,
  summaryInputDigest: digest,
  items,
  nextCursor,
})
let second = page([{ kind: 'spend', record: { amountGbp: '10.65' } }])
let failSecond = false
const requests: { url: string; init: RequestInit }[] = []
beforeEach(() => {
  auth.isReadonly = false
  auth.urlApiKey = ''
  auth.getAccessToken.mockClear()
  requests.length = 0
  failSecond = false
  second = page([{ kind: 'spend', record: { amountGbp: '10.65' } }])
  vi.stubGlobal('__AUDIT_BUILD_ID__', 'synthetic-test-build')
  vi.stubGlobal('__AUDIT_SOURCE_ID__', 'synthetic-test-source')
  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string, init: RequestInit = {}) => {
      requests.push({ url, init })
      if (url.includes('cursor='))
        return failSecond
          ? new Response('Evidence expired', { status: 410 })
          : Response.json(second)
      if (url.includes('/evidence')) return Response.json(page([aggregate], 'frozen-next'))
      if (url.endsWith('/presentation-probe'))
        return Response.json({ schemaVersion: 1, runId: 'linked-evaluation' })
      return Response.json({ enabled: true })
    }),
  )
})
afterEach(() => {
  vi.unstubAllGlobals()
  vi.unstubAllEnvs()
})

test('probe drains frozen pages, calls production summary and sends the exact server digests plus actual build identities', async () => {
  expect(await verifyAuditPresentation('original')).toEqual({
    schemaVersion: 1,
    runId: 'linked-evaluation',
    diagnosticCodes: ['missing_presentation_value:billedNative'],
  })
  expect(requests.map((request) => request.url)).toEqual([
    '/api/audits/runs/original/evidence?limit=200',
    '/api/audits/runs/original/evidence?limit=200&cursor=frozen-next',
    '/api/audits/runs/original/presentation-probe',
  ])
  const posted = JSON.parse(requests[2]!.init.body as string)
  expect(posted.probe.frozenSnapshotHash).toBe(hash)
  expect(posted.probe.summaryInputDigest).toBe(digest)
  expect(posted.probe.frontendBuildId).toBe('synthetic-test-build')
  expect(posted.probe.frontendSourceId).toBe('synthetic-test-source')
  expect(posted.probe.values).toContainEqual({
    field: 'billedGbp',
    groupKey: 'all',
    unroundedValue: '10.65',
    formattedValue: '10.65',
    currency: 'GBP',
  })
  expect(posted.probe.values).toContainEqual({
    field: 'notionalUsd',
    groupKey: 'all',
    unroundedValue: '4',
    formattedValue: '4.00',
    currency: 'USD',
  })
  expect(
    posted.probe.values.some((value: { field: string }) => value.field === 'billedNative'),
  ).toBe(false)
  expect(requests[2]!.init.headers).toEqual(
    expect.objectContaining({
      Authorization: 'Bearer entra',
      'Idempotency-Key': expect.any(String),
    }),
  )
})

test.each(['changed', 'loop', 'expired', 'unsafe'])(
  'incomplete or unbound evidence (%s) never submits a partial probe',
  async (kind) => {
    if (kind === 'changed') second = { ...second, summaryInputDigest: 'c'.repeat(64) }
    if (kind === 'loop') second = { ...second, nextCursor: 'frozen-next' }
    if (kind === 'expired') failSecond = true
    if (kind === 'unsafe')
      second = page([
        { ...aggregate, record: { ...aggregate.record, requestCount: '9007199254740993' } },
      ])
    await expect(verifyAuditPresentation('original')).rejects.toThrow()
    expect(requests.every((request) => request.init.method !== 'POST')).toBe(true)
  },
)

test('share-key requests outrank Entra, and readonly authority refuses all privileged clients before fetching', async () => {
  auth.urlApiKey = 'viewer'
  await getAuditCapabilities()
  expect(requests[0]!.init.headers).toEqual({ 'X-Observatory-Key': 'viewer' })
  expect(auth.getAccessToken).not.toHaveBeenCalled()
  auth.isReadonly = true
  await expect(verifyAuditPresentation('original')).rejects.toThrow('read only')
  await expect(downloadAudit('original', 'bundle')).rejects.toThrow('read only')
  expect(() => issueAuditCredential()).toThrow('read only')
  expect(requests).toHaveLength(1)
})

test('report downloads use authenticated blob responses without credentials in URLs', async () => {
  const create = vi.fn(() => 'blob:synthetic-report')
  const revoke = vi.fn()
  vi.stubGlobal(
    'URL',
    class extends URL {
      static override createObjectURL = create
      static override revokeObjectURL = revoke
    },
  )
  const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {})
  await downloadAudit('original', 'report')
  expect(requests[0]!.url).toBe('/api/audits/runs/original/report')
  expect(requests[0]!.init.headers).toEqual({ Authorization: 'Bearer entra' })
  expect(create).toHaveBeenCalledWith(expect.any(Blob))
  expect(click).toHaveBeenCalledOnce()
  expect(revoke).toHaveBeenCalledWith('blob:synthetic-report')
  click.mockRestore()
})
