import { describe, test, expect, vi, beforeEach, afterEach } from 'vitest'

const msalMock = vi.hoisted(() => ({
  getAccessToken: vi.fn(),
  apiKey: '',
  urlApiKey: '',
}))
vi.mock('../auth/msal', () => msalMock)

import {
  getAggregates, getSourceStatuses, ApiError,
  getBilledReporting, getSpendEntries,
  createSpendCategory, patchSpendCategory, createSpendVendor, patchSpendVendor,
  SPEND_SOURCE_VALUES,
} from './client'
import type { DailyAggregate } from './client'

function mockFetchOnce(status: number, body: unknown = []) {
  const fetchMock = vi.fn().mockResolvedValue({
    ok: status >= 200 && status < 300,
    status,
    json: () => Promise.resolve(body),
  })
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

// Catalog writes read the body on failure (created entities always come back via
// json()), so this mock carries both json() and text() for the success path.
function mockFetchOnceWithText(status: number, body: unknown, text = '') {
  const fetchMock = vi.fn().mockResolvedValue({
    ok: status >= 200 && status < 300,
    status,
    json: () => Promise.resolve(body),
    text: () => Promise.resolve(text),
  })
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

// The real failure shape: Results.BadRequest(object?)/Results.Conflict(object?) JSON-encode
// whatever they're given, with an application/json Content-Type -- verified against the
// running API (see task-9-report.md): a duplicate-key Conflict body on the wire is
// `"Category key already exists: <key>"`, quotes included, not the bare sentence.
function mockFetchOnceJsonError(status: number, value: unknown) {
  const bodyText = JSON.stringify(value)
  const fetchMock = vi.fn().mockResolvedValue({
    ok: false,
    status,
    text: () => Promise.resolve(bodyText),
    headers: { get: (name: string) => (name.toLowerCase() === 'content-type' ? 'application/json; charset=utf-8' : null) },
  })
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

// A non-JSON failure body -- covers the raw-text fallback path (not produced by the spend
// catalog endpoints today, but request() must not assume every failure is JSON).
function mockFetchOnceRawTextError(status: number, text: string, contentType = 'text/plain') {
  const fetchMock = vi.fn().mockResolvedValue({
    ok: false,
    status,
    text: () => Promise.resolve(text),
    headers: { get: (name: string) => (name.toLowerCase() === 'content-type' ? contentType : null) },
  })
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

// No body at all -- e.g. Results.NotFound().
function mockFetchOnceEmptyError(status: number) {
  const fetchMock = vi.fn().mockResolvedValue({
    ok: false,
    status,
    text: () => Promise.resolve(''),
    headers: { get: () => null },
  })
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

beforeEach(() => {
  msalMock.getAccessToken.mockReset().mockResolvedValue(null)
  msalMock.apiKey = ''
  msalMock.urlApiKey = ''
})

afterEach(() => {
  vi.unstubAllGlobals()
})

test('spend-source values match the backend enum wire contract', () => {
  expect(SPEND_SOURCE_VALUES).toEqual(['manual', 'csv', 'portal', 'api'])
})

test('gets the source-status wire response and preserves unknown source and aggregate strings', async () => {
  const response = [{ sourceId: 'new-source', status: 'fresh', isConfigured: true, lastAttemptAt: null, lastSuccessAt: '2026-08-24T12:00:00Z', latestObservationAt: null, consecutiveFailureCount: 0, lastError: null }]
  const fetchMock = mockFetchOnce(200, response)

  await expect(getSourceStatuses()).resolves.toEqual(response)
  expect(fetchMock.mock.calls[0][0]).toContain('/sources/status')
})

test('gets the authoritative billed-reporting projection for the complete date range', async () => {
  const response = {
    entryCount: 5003,
    totalGbp: 5011,
    dailyAverageGbp: 2505.5,
    projectedMonthlyGbp: 75165,
    topVendorName: 'Anthropic',
    topVendorGbp: 4991,
    dailySeries: [{ date: '2026-08-01', amountGbp: 5001 }],
    vendorSeries: [{ vendorId: 'a', name: 'Anthropic', amountGbp: 4991 }],
    categorySeries: [{ categoryId: 'c', name: 'Credits', amountGbp: 4991 }],
  }
  const fetchMock = mockFetchOnce(200, response)

  await expect(getBilledReporting('2026-08-01', '2026-08-02')).resolves.toEqual(response)
  expect(fetchMock.mock.calls[0][0]).toContain('/spend/reporting?from=2026-08-01&to=2026-08-02')
})

test('passes filters to spend entries before the server-side cap', async () => {
  const fetchMock = mockFetchOnce(200, [])

  await getSpendEntries('2026-08-01', '2026-08-31', 'azure-id', 'cloud-id')

  expect(fetchMock.mock.calls[0][0]).toContain(
    '/spend/entries?from=2026-08-01&to=2026-08-31&vendorId=azure-id&categoryId=cloud-id',
  )
})

test('keeps unknown aggregate provenance strings and every source-aware field from the wire', async () => {
  const response: DailyAggregate[] = [{
    date: '2026-08-24', provider: 'new-oss-provider', model: 'model-x', sourceId: 'new-source',
    sourceKind: 'futureSource', usageScope: 'futureScope', costBasis: 'futureBasis', inputTokens: 1,
    outputTokens: 2, cacheReadTokens: 3, cacheWriteTokens: 4, cacheWrite1hTokens: 5, costUsd: 6,
    unknownCostCount: 7, cacheSavingsUsd: 8, unknownCacheSavingsCount: 9, requestCount: 10,
  }]
  mockFetchOnce(200, response)

  await expect(getAggregates()).resolves.toEqual(response)
})

describe('authHeaders precedence: Entra > URL viewer key > self-host key > none', () => {
  test('uses the Entra bearer token when available, even if a viewer/self-host key is also set', async () => {
    msalMock.getAccessToken.mockResolvedValue('entra-token')
    msalMock.urlApiKey = 'viewer-key'
    msalMock.apiKey = 'self-host-key'
    const fetchMock = mockFetchOnce(200)

    await getAggregates()

    const headers = fetchMock.mock.calls[0][1].headers as Record<string, string>
    expect(headers.Authorization).toBe('Bearer entra-token')
    expect(headers['X-Observatory-Key']).toBeUndefined()
  })

  test('falls back to the URL viewer key when there is no Entra token', async () => {
    msalMock.getAccessToken.mockResolvedValue(null)
    msalMock.urlApiKey = 'viewer-key'
    msalMock.apiKey = 'self-host-key'
    const fetchMock = mockFetchOnce(200)

    await getAggregates()

    const headers = fetchMock.mock.calls[0][1].headers as Record<string, string>
    expect(headers['X-Observatory-Key']).toBe('viewer-key')
    expect(headers.Authorization).toBeUndefined()
  })

  test('falls back to the self-host VITE_API_KEY when there is no Entra token and no viewer key', async () => {
    msalMock.getAccessToken.mockResolvedValue(null)
    msalMock.urlApiKey = ''
    msalMock.apiKey = 'self-host-key'
    const fetchMock = mockFetchOnce(200)

    await getAggregates()

    const headers = fetchMock.mock.calls[0][1].headers as Record<string, string>
    expect(headers['X-Observatory-Key']).toBe('self-host-key')
  })

  test('sends no auth header at all when nothing is configured (local dev)', async () => {
    const fetchMock = mockFetchOnce(200)

    await getAggregates()

    const headers = fetchMock.mock.calls[0][1].headers as Record<string, string>
    expect(headers.Authorization).toBeUndefined()
    expect(headers['X-Observatory-Key']).toBeUndefined()
  })
})

describe('ApiError', () => {
  test('carries the HTTP status so the UI can tell auth failures from outages', async () => {
    mockFetchOnce(401)

    const error = await getAggregates().catch((e: unknown) => e)

    expect(error).toBeInstanceOf(ApiError)
    expect((error as ApiError).status).toBe(401)
  })

  test('distinguishes a 403 from a 5xx by status code', async () => {
    mockFetchOnce(500)

    const error = await getAggregates().catch((e: unknown) => e)

    expect(error).toBeInstanceOf(ApiError)
    expect((error as ApiError).status).toBe(500)
  })

  test('a rejected fetch (network outage) propagates rather than being swallowed', async () => {
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new TypeError('Failed to fetch')))

    await expect(getAggregates()).rejects.toThrow('Failed to fetch')
  })

  test('decodes a JSON-string error body instead of leaving the wire quotes in the message', async () => {
    // Results.Conflict($"...") sends `"Category key already exists: credits"` on the wire
    // (quotes included) -- this is the assertion that fails against a raw res.text().
    mockFetchOnceJsonError(409, 'Category key already exists: credits')

    const error = await createSpendCategory(
      { key: 'credits', displayName: 'Credits', colorVar: null, sortOrder: 0 },
    ).catch((e: unknown) => e)

    expect(error).toBeInstanceOf(ApiError)
    expect((error as ApiError).message).toBe('Category key already exists: credits')
    expect((error as ApiError).message).not.toContain('"')
  })

  test('picks the detail/title out of a ProblemDetails-shaped object rather than stringifying it', async () => {
    mockFetchOnceJsonError(400, { title: 'One or more validation errors occurred.', status: 400 })

    const error = await createSpendCategory(
      { key: 'x', displayName: 'X', colorVar: null, sortOrder: 0 },
    ).catch((e: unknown) => e)

    expect((error as ApiError).message).toBe('One or more validation errors occurred.')
    expect((error as ApiError).message).not.toContain('[object Object]')
  })

  test('prefers detail over title when a ProblemDetails object has both', async () => {
    mockFetchOnceJsonError(400, { title: 'Bad Request', detail: 'DisplayName is required' })

    const error = await createSpendCategory(
      { key: 'x', displayName: 'X', colorVar: null, sortOrder: 0 },
    ).catch((e: unknown) => e)

    expect((error as ApiError).message).toBe('DisplayName is required')
  })

  test('falls back to the raw text for a non-JSON error body', async () => {
    mockFetchOnceRawTextError(400, 'from must be yyyy-MM-dd')

    const error = await createSpendCategory(
      { key: 'x', displayName: 'X', colorVar: null, sortOrder: 0 },
    ).catch((e: unknown) => e)

    expect((error as ApiError).message).toBe('from must be yyyy-MM-dd')
  })

  test('falls back to a generic status line when the body is empty', async () => {
    mockFetchOnceEmptyError(404)

    const error = await patchSpendCategory('missing-id', { archived: true }).catch((e: unknown) => e)

    expect(error).toBeInstanceOf(ApiError)
    expect((error as ApiError).message).toMatch(/failed: 404/)
  })
})

describe('spend catalog writes', () => {
  test('creates a category and returns the created entity', async () => {
    const created = { id: 'c1', key: 'credits', displayName: 'Credits', colorVar: '', sortOrder: 0, archivedAt: null }
    const fetchMock = mockFetchOnceWithText(201, created)

    const result = await createSpendCategory({ key: 'credits', displayName: 'Credits', colorVar: null, sortOrder: 0 })

    expect(result).toEqual(created)
    const [, init] = fetchMock.mock.calls[0]
    expect(init.method).toBe('POST')
    expect(JSON.parse(init.body as string)).toEqual({ key: 'credits', displayName: 'Credits', colorVar: null, sortOrder: 0 })
  })

  test('patches a category with only the changed field', async () => {
    const patched = { id: 'c1', key: 'credits', displayName: 'Credits', colorVar: '', sortOrder: 0, archivedAt: '2026-07-26T00:00:00Z' }
    const fetchMock = mockFetchOnceWithText(200, patched)

    const result = await patchSpendCategory('c1', { archived: true })

    expect(result).toEqual(patched)
    const [path, init] = fetchMock.mock.calls[0]
    expect(path).toContain('/spend/categories/c1')
    expect(JSON.parse(init.body as string)).toEqual({ archived: true })
  })

  test('creates a vendor with no provider by sending null, not an empty string', async () => {
    const created = { id: 'v1', key: 'gha', displayName: 'GitHub Actions', provider: null, defaultCategoryId: null, archivedAt: null }
    const fetchMock = mockFetchOnceWithText(201, created)

    const result = await createSpendVendor({ key: 'gha', displayName: 'GitHub Actions', provider: null, defaultCategoryId: null })

    expect(result).toEqual(created)
    const [, init] = fetchMock.mock.calls[0]
    expect(JSON.parse(init.body as string).provider).toBeNull()
  })

  test('patches a vendor display name (rename)', async () => {
    const patched = { id: 'v1', key: 'anthropic', displayName: 'Anthropic Inc', provider: 'anthropic', defaultCategoryId: null, archivedAt: null }
    const fetchMock = mockFetchOnceWithText(200, patched)

    const result = await patchSpendVendor('v1', { displayName: 'Anthropic Inc' })

    expect(result).toEqual(patched)
    const [path, init] = fetchMock.mock.calls[0]
    expect(path).toContain('/spend/vendors/v1')
    expect(JSON.parse(init.body as string)).toEqual({ displayName: 'Anthropic Inc' })
  })
})
