import { describe, test, expect, vi, beforeEach } from 'vitest'
import { render, screen } from '@testing-library/react'

const postMessage = vi.fn()
const port = {
  postMessage,
  addEventListener: vi.fn(),
  removeEventListener: vi.fn(),
}

const msalMock = vi.hoisted(() => ({
  authEnabled: true,
  urlApiKey: '',
  isEmbedded: true,
  loginRequest: { scopes: [] as string[] },
}))
vi.mock('./auth/msal', () => msalMock)

vi.mock('@azure/msal-react', () => ({
  useIsAuthenticated: () => false,
  useMsal: () => ({
    instance: { loginRedirect: vi.fn().mockResolvedValue(undefined), getActiveAccount: vi.fn(), setActiveAccount: vi.fn() },
    accounts: [],
    inProgress: 'none',
  }),
}))

vi.mock('./pages/Dashboard', () => ({ default: () => <p>dashboard content</p> }))

import App from './App'

describe('App composition for the IDE embed', () => {
  beforeEach(() => {
    postMessage.mockReset()
    msalMock.authEnabled = true
    msalMock.isEmbedded = true
    ;(window as Window & { chrome?: unknown }).chrome = { webview: port }
  })

  // Regression: EmbeddedContext used to sit INSIDE AuthGate, so an embed awaiting
  // sign-in never posted partner.ready. The IDE could not distinguish "needs sign-in"
  // from "partner broken" and sat on Connecting until it timed out.
  test('posts partner.ready to the host even while the sign-in gate is showing', () => {
    render(<App />)

    expect(screen.getByRole('button', { name: /sign in with microsoft/i })).toBeInTheDocument()
    expect(screen.queryByText('dashboard content')).not.toBeInTheDocument()
    expect(postMessage).toHaveBeenCalledWith(
      expect.objectContaining({ contractVersion: '1.0', kind: 'partner.ready', authority: 'workspace' }),
    )
  })
})
