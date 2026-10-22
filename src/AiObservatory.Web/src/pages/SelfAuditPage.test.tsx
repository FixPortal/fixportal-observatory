import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, expect, test, vi } from 'vitest'
import type { AuditRun, AuditScope } from '../api/auditTypes'
import { SelfAuditPage } from './SelfAuditPage'

const auth = vi.hoisted(() => ({
  isReadonly: false,
  urlApiKey: '',
  apiKey: '',
  getAccessToken: vi.fn(async () => 'entra'),
}))
vi.mock('../auth/msal', () => auth)
const machine = '11111111-1111-1111-1111-111111111111'
const runId = '22222222-2222-2222-2222-222222222222'
const scope: AuditScope = {
  from: '2026-10-15',
  to: '2026-10-21',
  sources: [{ machineId: machine, sourceId: 'claude-local' }],
  requestedChecks: ['sourceAccounting'],
  excludedChecks: [],
  allowPartial: true,
  predecessorRunId: null,
  evaluationOfRunId: null,
}
const completed: AuditRun = {
  schemaVersion: 1,
  id: runId,
  state: 'completed',
  detailsAvailable: true,
  replayAvailable: true,
  stage: 'completed',
  attempts: 1,
  diagnosticCode: null,
  createdAt: '2026-10-22T12:00:00Z',
  scope,
  verdict: 'incomplete',
  coverage: {
    comparedGroups: '9007199254740993',
    totalGroups: '9007199254740994',
    diagnosticCodes: ['missing_presentation_probe'],
  },
  verifierVersion: '1.0.0',
  ruleVersion: '1',
  inputContentDigest: 'a'.repeat(64),
  serverCutoff: '2026-10-22T12:00:00Z',
  replayExpiresAt: '2027-01-20T12:00:00Z',
  evidenceExpiresAt: '2027-01-20T12:00:00Z',
  summaryExpiresAt: '2027-10-22T12:00:00Z',
  predecessorRunId: null,
  evaluationOfRunId: null,
}
let run = completed
let errorPath = ''
let historyPage = false
const calls: { path: string; init: RequestInit }[] = []

function mount() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  })
  return render(
    <QueryClientProvider client={client}>
      <SelfAuditPage />
    </QueryClientProvider>,
  )
}

beforeEach(() => {
  vi.useRealTimers()
  vi.stubEnv('VITE_DEMO_BANNER', 'false')
  auth.isReadonly = false
  auth.urlApiKey = ''
  auth.getAccessToken.mockClear()
  calls.length = 0
  errorPath = ''
  historyPage = false
  run = completed
  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string, init: RequestInit = {}) => {
      const path = url.replace('/api/audits', '')
      calls.push({ path, init })
      if (errorPath && path.includes(errorPath))
        return new Response('Evidence expired', { status: 410 })
      let data: unknown
      if (path === '/retention') data = { evidenceDays: 90, summaryDays: 365 }
      else if (path === '/capabilities')
        data = {
          schemaVersion: 1,
          enabled: true,
          schemaVersions: [1],
          verifierVersions: ['1.0.0'],
          sources: [
            {
              tool: 'claude',
              supported: true,
              format: 'claude-jsonl',
              version: '1',
              reasonCode: null,
            },
            {
              tool: 'copilot',
              supported: false,
              format: null,
              version: null,
              reasonCode: 'unsupported_copilot_accounting',
            },
          ],
          checks: [
            { code: 'sourceAccounting', supported: true, reasonCode: null },
            { code: 'presentationTotals', supported: true, reasonCode: null },
            {
              code: 'billingReconciliation',
              supported: false,
              reasonCode: 'billing_feed_unqualified',
            },
          ],
        }
      else if (path.startsWith('/readiness'))
        data = {
          schemaVersion: 1,
          sources: [
            {
              sourceId: 'claude-local',
              machineId: null,
              captureIds: ['capture-a'],
              diagnosticCodes: ['source_stale', 'capture_choices_truncated'],
              lastSuccessAt: null,
            },
          ],
          maximumRunWindowDays: 31,
          maximumSnapshotBytes: 536870912,
        }
      else if (path.startsWith('/runs?'))
        data = {
          schemaVersion: 1,
          items: path.includes('cursor=')
            ? [{ ...completed, id: 'older', createdAt: '2026-10-20T12:00:00Z' }]
            : [run],
          nextCursor: historyPage && !path.includes('cursor=') ? 'stable-cursor' : null,
        }
      else if (path.endsWith('/findings'))
        data = {
          schemaVersion: 1,
          items: [
            {
              count: '9007199254740993',
              value: {
                checkCode: 'pricingArithmetic',
                ruleCode: 'price',
                field: 'costUsd',
                expected: { kind: 'decimal', value: '1.005' },
                actual: { kind: 'decimal', value: '2.005' },
                delta: { kind: 'decimal', value: '1' },
                unit: 'money',
                currency: 'USD',
                basis: 'notional',
                diagnosticCode: 'amount_mismatch',
              },
            },
          ],
          nextCursor: null,
        }
      else if (path === '/runs' && init.method === 'POST') data = { schemaVersion: 1, runId }
      else if (path === '/machines' || path.endsWith('/rotate'))
        data = { machineId: machine, credentialId: 'credential', secret: 'one-time-secret' }
      else if (path.endsWith('/cancel') || path.endsWith('/proceed') || init.method === 'DELETE')
        return new Response(null, { status: 204 })
      else if (path.endsWith('/replay')) data = { schemaVersion: 1, runId: 'linked' }
      else data = run
      return Response.json(data)
    }),
  )
})
afterEach(() => {
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
  vi.unstubAllEnvs()
  vi.useRealTimers()
})

test('starts with seven completed UTC days, retains source/check choices and validates inclusive 31-day limit', async () => {
  vi.setSystemTime(new Date('2026-10-22T00:15:00Z'))
  mount()
  expect(await screen.findByText(/2026-10-15.*2026-10-21/)).toBeInTheDocument()
  const user = userEvent.setup()
  await user.click(screen.getByRole('checkbox', { name: /Displayed totals/ }))
  await user.click(screen.getByRole('button', { name: 'Custom' }))
  await user.clear(screen.getByLabelText('From'))
  await user.type(screen.getByLabelText('From'), '2026-09-21')
  await user.click(screen.getByRole('button', { name: 'Apply' }))
  expect(screen.getByRole('checkbox', { name: /Displayed totals/ })).toBeChecked()
  expect(screen.getByRole('checkbox', { name: /claude-local/ })).toBeChecked()
  expect(screen.queryByText(/Choose an inclusive range/)).not.toBeInTheDocument()
  await user.click(screen.getByRole('button', { name: '90d' }))
  expect(screen.getByRole('alert')).toHaveTextContent('31')
  expect(screen.getByRole('button', { name: 'Start audit' })).toBeDisabled()
})

test('retains capture choices and acknowledges offline partial collection explicitly', async () => {
  mount()
  const user = userEvent.setup()
  expect(await screen.findByText('capture_choices_truncated')).toBeInTheDocument()
  expect(
    screen.getByText('More captures are available; only the newest choices are shown.'),
  ).toBeVisible()
  expect(screen.getByText('512 MiB')).toBeVisible()
  expect(screen.getByText('unsupported_copilot_accounting')).toBeInTheDocument()
  expect(screen.getByText('billing_feed_unqualified')).toBeInTheDocument()
  await user.type(screen.getByLabelText('Machine ID for this scope'), machine)
  await user.click(screen.getByRole('checkbox', { name: /Retained capture capture-a/ }))
  await user.click(screen.getByRole('checkbox', { name: /Allow partial evidence/ }))
  await user.click(screen.getByRole('button', { name: 'Start audit' }))
  await waitFor(() => {
    expect(calls.some((c) => c.path === '/runs' && c.init.method === 'POST')).toBe(true)
  })
  const request = JSON.parse(
    calls.find((c) => c.path === '/runs' && c.init.method === 'POST')!.init.body as string,
  )
  expect(request.captureIds).toEqual(['capture-a'])
  expect(request.scope.sources).toEqual([{ machineId: machine, sourceId: 'claude-local' }])
  expect(request.scope.requestedChecks).toEqual(['sourceAccounting'])
  expect(request.scope.allowPartial).toBe(true)
  expect(request.requestFreshCapture).toBe(true)
})

test('uses the explicit configured source instance independently of the extractor family', async () => {
  mount()
  const user = userEvent.setup()
  const source = await screen.findByLabelText('Configured source ID for claude')
  await user.clear(source)
  await user.type(screen.getByLabelText('Machine ID for this scope'), machine)
  expect(screen.getByRole('button', { name: 'Start audit' })).toBeDisabled()
  await user.type(source, 'native-usage@synthetic-machine')
  await user.click(screen.getByRole('button', { name: 'Start audit' }))
  await waitFor(() => {
    expect(calls.some((c) => c.path === '/runs' && c.init.method === 'POST')).toBe(true)
  })
  const request = JSON.parse(
    calls.find((c) => c.path === '/runs' && c.init.method === 'POST')!.init.body as string,
  )
  expect(request.scope.sources).toEqual([
    { machineId: machine, sourceId: 'native-usage@synthetic-machine' },
  ])
  expect(screen.getByRole('checkbox', { name: /claude-local/ })).toBeChecked()
  expect(calls.some((c) => c.path.includes('sources=native-usage%40synthetic-machine'))).toBe(true)
})

test('shows exact coverage and findings with expected/actual/delta, currency and basis', async () => {
  mount()
  await userEvent.click(await screen.findByRole('button', { name: /Open audit/ }))
  expect(
    await screen.findByText('9007199254740993 / 9007199254740994 groups compared'),
  ).toBeInTheDocument()
  const table = await screen.findByRole('table', { name: 'Audit findings' })
  expect(screen.getByRole('heading', { name: 'Audit outcome' })).toHaveFocus()
  for (const value of ['9007199254740993', '1.005', '2.005', 'notional'])
    expect(within(table).getByText(value)).toBeInTheDocument()
  expect(within(table).getByRole('cell', { name: /money.*USD/ })).toBeInTheDocument()
  expect(within(table).getByRole('cell', { name: '1' })).toBeInTheDocument()
  expect(screen.getByText('missing_presentation_probe')).toBeInTheDocument()
})

test('stable history pagination uses the returned opaque cursor and retains earlier rows', async () => {
  historyPage = true
  mount()
  await userEvent.click(await screen.findByRole('button', { name: 'Load older audits' }))
  expect(await screen.findByRole('button', { name: /Open audit older/ })).toBeInTheDocument()
  expect(screen.getAllByRole('button', { name: /Open audit/ })).toHaveLength(2)
  expect(calls.some((c) => c.path.includes('cursor=stable-cursor'))).toBe(true)
})

test('collecting progress requires an acknowledgement before partial proceed and allows cancellation', async () => {
  run = { ...completed, state: 'collectingEvidence', stage: 'collectingEvidence' }
  mount()
  const user = userEvent.setup()
  await user.click(await screen.findByRole('button', { name: /Open audit/ }))
  expect(await screen.findByText(/Waiting for machines/)).toBeInTheDocument()
  expect(screen.getByRole('button', { name: 'Proceed with partial evidence' })).toBeDisabled()
  await user.click(screen.getByRole('checkbox', { name: /I acknowledge missing captures/ }))
  await user.click(screen.getByRole('button', { name: 'Proceed with partial evidence' }))
  await waitFor(() => {
    expect(calls.some((c) => c.path.endsWith('/proceed'))).toBe(true)
  })
  await user.click(screen.getByRole('button', { name: 'Cancel audit' }))
  await waitFor(() => {
    expect(calls.some((c) => c.path.endsWith('/cancel'))).toBe(true)
  })
})

test('expired replay is unavailable and report/export errors remain visible', async () => {
  run = { ...completed, replayExpiresAt: '2020-01-01T00:00:00Z' }
  mount()
  const user = userEvent.setup()
  await user.click(await screen.findByRole('button', { name: /Open audit/ }))
  expect(await screen.findByRole('button', { name: 'Replay retained inputs' })).toBeDisabled()
  errorPath = '/report'
  await user.click(screen.getByRole('button', { name: 'Download report' }))
  expect(await screen.findByRole('alert')).toHaveTextContent('Evidence expired')
  expect(screen.getByRole('button', { name: 'Export evidence bundle' })).toBeDisabled()
})

test('a scope which excluded displayed totals cannot submit a misleading presentation evaluation', async () => {
  mount()
  await userEvent.click(await screen.findByRole('button', { name: /Open audit/ }))
  expect(await screen.findByRole('button', { name: 'Verify displayed totals' })).toBeDisabled()
  expect(screen.getByText(/Include Displayed totals in a new audit scope/)).toBeInTheDocument()
})

test('machine credentials appear once, can be dismissed, rotated and revoked without persistence', async () => {
  mount()
  const user = userEvent.setup()
  await user.click(await screen.findByRole('button', { name: /Machine credentials/ }))
  await user.click(screen.getByRole('button', { name: 'Issue machine credential' }))
  expect(await screen.findByText('one-time-secret')).toBeInTheDocument()
  expect(window.localStorage.getItem('one-time-secret')).toBeNull()
  await user.click(screen.getByRole('button', { name: 'I saved this credential' }))
  expect(screen.queryByText('one-time-secret')).not.toBeInTheDocument()
  await user.click(screen.getByRole('button', { name: 'Rotate credential' }))
  expect(await screen.findByText('one-time-secret')).toBeInTheDocument()
  await user.click(screen.getByRole('button', { name: 'Revoke credential' }))
  await waitFor(() => expect(screen.queryByText('one-time-secret')).not.toBeInTheDocument())
})

test('viewer share authority with an Entra session offers sanitized reports while hiding privileged actions', async () => {
  auth.isReadonly = true
  auth.urlApiKey = 'viewer'
  mount()
  await userEvent.click(await screen.findByRole('button', { name: /Open audit/ }))
  expect(await screen.findByRole('table', { name: 'Audit findings' })).toBeInTheDocument()
  for (const name of [
    'Start audit',
    'Replay retained inputs',
    'Verify displayed totals',
    'Export evidence bundle',
    'Issue machine credential',
    'Cancel audit',
  ])
    expect(screen.queryByRole('button', { name })).not.toBeInTheDocument()
  expect(screen.getByRole('button', { name: 'Download report' })).toBeEnabled()
  expect(
    screen.queryByRole('button', { name: /Detailed sanitized evidence/ }),
  ).not.toBeInTheDocument()
  expect(calls.every((c) => !c.init.method || c.init.method === 'GET')).toBe(true)
})

test('viewer can download a sanitized report after evidence expiry and sees report failures without gaining privileged access', async () => {
  auth.isReadonly = true
  auth.urlApiKey = 'viewer'
  run = {
    ...completed,
    replayExpiresAt: '2020-01-01T00:00:00Z',
    evidenceExpiresAt: '2020-01-01T00:00:00Z',
  }
  const create = vi.fn(() => 'blob:synthetic-viewer-report')
  const revoke = vi.fn()
  vi.stubGlobal(
    'URL',
    class extends URL {
      static override readonly createObjectURL = create
      static override readonly revokeObjectURL = revoke
    },
  )
  const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {})
  mount()
  const user = userEvent.setup()
  await user.click(await screen.findByRole('button', { name: /Open audit/ }))
  await user.click(await screen.findByRole('button', { name: 'Download report' }))
  await waitFor(() => {
    expect(click).toHaveBeenCalledOnce()
  })
  expect(create).toHaveBeenCalledWith(expect.any(Blob))
  expect(revoke).toHaveBeenCalledWith('blob:synthetic-viewer-report')
  expect(calls.find((call) => call.path.endsWith('/report'))?.init.headers).toEqual({
    'X-Observatory-Key': 'viewer',
  })
  expect(auth.getAccessToken).not.toHaveBeenCalled()
  errorPath = '/report'
  await user.click(screen.getByRole('button', { name: 'Download report' }))
  expect(await screen.findByRole('alert')).toHaveTextContent('Evidence expired')
  for (const name of [
    'Export evidence bundle',
    'Verify displayed totals',
    'Replay retained inputs',
    'Start audit',
    'Cancel audit',
    'Issue machine credential',
  ])
    expect(screen.queryByRole('button', { name })).not.toBeInTheDocument()
  expect(
    screen.queryByRole('button', { name: /Detailed sanitized evidence/ }),
  ).not.toBeInTheDocument()
  expect(calls.every((call) => !call.init.method || call.init.method === 'GET')).toBe(true)
  expect(calls.some((call) => /\/(bundle|evidence|presentation-probe)$/.test(call.path))).toBe(
    false,
  )
})

test('demo keeps its synthetic read-only label through discrepancy investigation', async () => {
  vi.stubEnv('VITE_DEMO_BANNER', 'true')
  mount()
  expect(await screen.findByText('Synthetic audit data · read only')).toBeInTheDocument()
  await userEvent.click(await screen.findByRole('button', { name: /Open audit/ }))
  expect(screen.getByText('Synthetic audit data · read only')).toBeInTheDocument()
  expect(screen.queryByRole('button', { name: 'Start audit' })).not.toBeInTheDocument()
})

test('deleted indefinite evidence disables replay, export and probe while retaining the summary report', async () => {
  run = {
    ...completed,
    replayExpiresAt: null,
    evidenceExpiresAt: null,
    detailsAvailable: false,
    replayAvailable: false,
  }
  mount()
  await userEvent.click(await screen.findByRole('button', { name: /Open audit/ }))
  expect(await screen.findByRole('button', { name: 'Replay retained inputs' })).toBeDisabled()
  expect(screen.getByRole('button', { name: 'Export evidence bundle' })).toBeDisabled()
  expect(screen.getByRole('button', { name: 'Verify displayed totals' })).toBeDisabled()
  expect(screen.getByRole('button', { name: 'Download report' })).toBeEnabled()
  expect(screen.getByText(/Detailed evidence is unavailable/)).toBeInTheDocument()
  expect(
    screen.queryByRole('button', { name: /Detailed sanitized evidence/ }),
  ).not.toBeInTheDocument()
  expect(calls.some((c) => c.path.endsWith('/findings'))).toBe(false)
})
