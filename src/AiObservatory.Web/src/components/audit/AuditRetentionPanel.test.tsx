import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, expect, test, vi } from 'vitest'
import { AuditRetentionPanel } from './AuditRetentionPanel'

vi.mock('../../auth/msal', () => ({
  isReadonly: false,
  urlApiKey: '',
  apiKey: 'admin',
  getAccessToken: async () => null,
}))
const capture = '11111111-1111-1111-1111-111111111111'
const run = '22222222-2222-2222-2222-222222222222'
const calls: { url: string; init: RequestInit }[] = []
let stale = false
beforeEach(() => {
  localStorage.clear()
  calls.length = 0
  stale = false
  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string, init: RequestInit = {}) => {
      calls.push({ url, init })
      if (init.method === 'DELETE')
        return stale
          ? new Response('Impact changed', { status: 412 })
          : new Response(null, { status: 204 })
      if (url.includes('deletion-impact'))
        return Response.json({
          captureId: capture,
          impactDigest: 'a'.repeat(64),
          affectedRunIds: [run],
        })
      return Response.json({ evidenceDays: 90, summaryDays: 365 })
    }),
  )
})
afterEach(() => {
  vi.unstubAllGlobals()
})
function mount() {
  return render(
    <QueryClientProvider
      client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}
    >
      <AuditRetentionPanel />
    </QueryClientProvider>,
  )
}
test('loads future retention, supports explicit indefinite and rejects unsupported days', async () => {
  mount()
  const user = userEvent.setup()
  await user.click(screen.getByRole('button', { name: /Evidence retention/ }))
  const evidence = await screen.findByRole('spinbutton', { name: 'Detailed evidence days' })
  await waitFor(() => {
    expect(evidence).toHaveValue(90)
  })
  await user.clear(evidence)
  await user.type(evidence, '36501')
  expect(screen.getByRole('button', { name: 'Save retention' })).toBeDisabled()
  await user.click(screen.getByRole('checkbox', { name: 'Keep detailed evidence indefinitely' }))
  await user.click(screen.getByRole('button', { name: 'Save retention' }))
  await waitFor(() => {
    expect(calls.find((c) => c.init.method === 'PATCH')?.init.body).toBe(
      JSON.stringify({ evidenceDays: null, summaryDays: 365 }),
    )
  })
  expect(screen.getByText(/future seals and completions/)).toBeInTheDocument()
})
test('shows affected runs and backup limits before explicit deletion, refreshes a stale impact', async () => {
  mount()
  const user = userEvent.setup()
  await user.click(screen.getByRole('button', { name: /Evidence retention/ }))
  await user.type(screen.getByRole('textbox', { name: 'Capture ID to delete' }), capture)
  await user.click(screen.getByRole('button', { name: 'Review deletion impact' }))
  const dialog = await screen.findByRole('dialog', { name: 'Delete retained evidence' })
  expect(dialog).toHaveTextContent(run)
  expect(dialog).toHaveTextContent(/usage and spend records remain/)
  expect(dialog).toHaveTextContent(/backups/)
  expect(screen.getByRole('button', { name: 'Delete evidence permanently' })).toBeDisabled()
  await user.click(screen.getByRole('checkbox', { name: /I understand replay will be lost/ }))
  stale = true
  await user.click(screen.getByRole('button', { name: 'Delete evidence permanently' }))
  expect(await screen.findByRole('alert')).toHaveTextContent(/changed/)
  expect(calls.find((c) => c.init.method === 'DELETE')?.init.headers).toEqual(
    expect.objectContaining({ 'If-Match': '"' + 'a'.repeat(64) + '"' }),
  )
  expect(screen.getByRole('button', { name: 'Delete evidence permanently' })).toBeDisabled()
  await user.click(screen.getByRole('button', { name: 'Refresh impact' }))
  await waitFor(() => {
    expect(calls.filter((c) => c.url.includes('deletion-impact'))).toHaveLength(2)
  })
})
