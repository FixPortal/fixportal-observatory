import { describe, test, expect, vi, beforeEach } from 'vitest'
import { render, screen } from '@testing-library/react'

const useIsAuthenticated = vi.fn()
const useMsal = vi.fn()

vi.mock('@azure/msal-react', () => ({
  useIsAuthenticated: () => useIsAuthenticated(),
  useMsal: () => useMsal(),
}))

const msalMock = vi.hoisted(() => ({
  authEnabled: false,
  urlApiKey: '',
  isEmbedded: false,
  loginRequest: { scopes: [] as string[] },
}))
vi.mock('./msal', () => msalMock)

import AuthGate from './AuthGate'

describe('AuthGate', () => {
  beforeEach(() => {
    msalMock.authEnabled = false
    msalMock.urlApiKey = ''
    msalMock.isEmbedded = false
    useIsAuthenticated.mockReset().mockReturnValue(false)
    useMsal.mockReset().mockReturnValue({
      instance: { loginRedirect: vi.fn().mockResolvedValue(undefined), getActiveAccount: vi.fn(), setActiveAccount: vi.fn() },
      accounts: [],
      inProgress: 'none',
    })
  })

  test('renders children directly when auth is disabled (local dev, no client id baked)', () => {
    msalMock.authEnabled = false
    render(<AuthGate><p>dashboard content</p></AuthGate>)
    expect(screen.getByText('dashboard content')).toBeInTheDocument()
  })

  test('renders children directly when a viewer share-link key is present, bypassing Entra entirely', () => {
    msalMock.authEnabled = true
    msalMock.urlApiKey = 'viewer-key-123'
    render(<AuthGate><p>dashboard content</p></AuthGate>)
    expect(screen.getByText('dashboard content')).toBeInTheDocument()
    // useIsAuthenticated must not even be consulted on the viewer-key bypass path.
    expect(useIsAuthenticated).not.toHaveBeenCalled()
  })

  test('shows the sign-in screen when auth is enabled, no viewer key, and not authenticated', () => {
    msalMock.authEnabled = true
    msalMock.urlApiKey = ''
    useIsAuthenticated.mockReturnValue(false)

    render(<AuthGate><p>dashboard content</p></AuthGate>)

    expect(screen.queryByText('dashboard content')).not.toBeInTheDocument()
    expect(screen.getByRole('main')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /sign in with microsoft/i })).toBeInTheDocument()
  })

  test('embedded mode requires Entra even when a viewer key is present', () => {
    msalMock.authEnabled = true
    msalMock.urlApiKey = 'viewer-key-123'
    msalMock.isEmbedded = true

    render(<AuthGate><p>dashboard content</p></AuthGate>)

    expect(screen.queryByText('dashboard content')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: /sign in with microsoft/i })).toBeInTheDocument()
  })

  test('renders children once authenticated via Entra', () => {
    msalMock.authEnabled = true
    msalMock.urlApiKey = ''
    useIsAuthenticated.mockReturnValue(true)

    render(<AuthGate><p>dashboard content</p></AuthGate>)

    expect(screen.getByText('dashboard content')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /sign in with microsoft/i })).not.toBeInTheDocument()
  })

  test('the sign-in button is disabled while a redirect is already in progress', () => {
    msalMock.authEnabled = true
    msalMock.urlApiKey = ''
    useIsAuthenticated.mockReturnValue(false)
    useMsal.mockReturnValue({
      instance: { loginRedirect: vi.fn(), getActiveAccount: vi.fn(), setActiveAccount: vi.fn() },
      accounts: [],
      inProgress: 'login',
    })

    render(<AuthGate><p>dashboard content</p></AuthGate>)

    expect(screen.getByRole('button', { name: /signing in/i })).toBeDisabled()
  })
})
