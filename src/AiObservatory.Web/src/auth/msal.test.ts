import { describe, test, expect, vi, beforeEach, afterEach } from 'vitest'

const VIEWER_KEY_STORAGE = 'observatory:viewer-key'

describe('readViewerKey / URL strip', () => {
  beforeEach(() => {
    vi.resetModules()
    window.sessionStorage.clear()
    delete (window as Window & { chrome?: unknown }).chrome
  })

  test('captures ?key= from the URL, persists to sessionStorage, and strips it from the visible URL', async () => {
    window.history.pushState({}, '', '/dashboard?key=secret123&other=1')

    const mod = await import('./msal')

    expect(mod.urlApiKey).toBe('secret123')
    expect(window.sessionStorage.getItem(VIEWER_KEY_STORAGE)).toBe('secret123')
    expect(window.location.search).not.toContain('key=secret123')
    expect(window.location.search).toContain('other=1')
  })

  test('falls back to sessionStorage when no ?key= in the URL (reload / tab-restore)', async () => {
    window.sessionStorage.setItem(VIEWER_KEY_STORAGE, 'from-storage')
    window.history.pushState({}, '', '/dashboard')

    const mod = await import('./msal')

    expect(mod.urlApiKey).toBe('from-storage')
  })

  test('is empty when neither the URL nor sessionStorage carries a key', async () => {
    window.history.pushState({}, '', '/dashboard')

    const mod = await import('./msal')

    expect(mod.urlApiKey).toBe('')
  })

  test('isReadonly is true iff a viewer key is present', async () => {
    window.history.pushState({}, '', '/?key=abc')
    const withKey = await import('./msal')
    expect(withKey.isReadonly).toBe(true)

    vi.resetModules()
    window.sessionStorage.clear()
    window.history.pushState({}, '', '/')
    const withoutKey = await import('./msal')
    expect(withoutKey.isReadonly).toBe(false)
  })

  test('a URL without ?key= does not touch the visible URL at all', async () => {
    window.history.pushState({}, '', '/dashboard?other=1')

    await import('./msal')

    expect(window.location.search).toBe('?other=1')
  })

  test('embedded mode ignores URL and stored viewer keys', async () => {
    stubWebViewHost()
    window.sessionStorage.setItem(VIEWER_KEY_STORAGE, 'from-storage')
    window.history.pushState({}, '', '/?embed=fixportal-ide&key=secret123')
    const setItem = vi.spyOn(Storage.prototype, 'setItem')

    const mod = await import('./msal')

    expect(mod.isEmbedded).toBe(true)
    expect(mod.urlApiKey).toBe('')
    expect(mod.isReadonly).toBe(false)
    expect(setItem).not.toHaveBeenCalled()
  })

  test('?embed=fixportal-ide without a WebView2 host does not enter embedded mode and keeps the viewer key', async () => {
    window.history.pushState({}, '', '/?embed=fixportal-ide&key=secret123')

    const mod = await import('./msal')

    expect(mod.isEmbedded).toBe(false)
    expect(mod.urlApiKey).toBe('secret123')
    expect(mod.isReadonly).toBe(true)
  })
})

function stubWebViewHost() {
  const host = window as Window & { chrome?: { webview?: object } }
  host.chrome = { webview: {} }
}

describe('getAccessToken', () => {
  beforeEach(() => {
    vi.resetModules()
    vi.unstubAllEnvs()
  })

  afterEach(() => {
    vi.doUnmock('@azure/msal-browser')
    vi.unstubAllEnvs()
    vi.useRealTimers()
  })

  test('returns null without constructing an MSAL instance when auth is disabled (no client id baked)', async () => {
    vi.stubEnv('VITE_AAD_CLIENT_ID', '')

    const mod = await import('./msal')

    expect(mod.authEnabled).toBe(false)
    expect(mod.msalInstance).toBeNull()
    await expect(mod.getAccessToken()).resolves.toBeNull()
  })

  test('single-flights an InteractionRequiredAuthError redirect across concurrent callers', async () => {
    class MockInteractionRequiredAuthError extends Error {}
    const account = { homeAccountId: 'acc-1' }
    let resolveRedirect!: () => void
    const acquireTokenRedirect = vi.fn().mockReturnValue(
      new Promise<void>((resolve) => { resolveRedirect = resolve })
    )
    const instance = {
      getActiveAccount: vi.fn().mockReturnValue(account),
      getAllAccounts: vi.fn().mockReturnValue([account]),
      acquireTokenSilent: vi.fn().mockRejectedValue(new MockInteractionRequiredAuthError('needs interaction')),
      acquireTokenRedirect,
    }
    vi.doMock('@azure/msal-browser', () => ({
      PublicClientApplication: vi.fn().mockImplementation(function () { return instance }),
      InteractionRequiredAuthError: MockInteractionRequiredAuthError,
    }))
    vi.stubEnv('VITE_AAD_CLIENT_ID', 'test-client-id')

    const mod = await import('./msal')

    // The dashboard mounts many queries at once — every concurrent caller must share
    // ONE redirect instead of each kicking off its own (which throws
    // BrowserAuthError: interaction_in_progress for every caller after the first).
    const first = mod.getAccessToken()
    const second = mod.getAccessToken()
    resolveRedirect()
    const [a, b] = await Promise.all([first, second])

    expect(a).toBeNull()
    expect(b).toBeNull()
    expect(acquireTokenRedirect).toHaveBeenCalledTimes(1)
  })

  test('rethrows a transient (non-interaction) error instead of redirecting', async () => {
    class MockInteractionRequiredAuthError extends Error {}
    const transientError = new Error('network blip')
    const account = { homeAccountId: 'acc-1' }
    const acquireTokenRedirect = vi.fn()
    const instance = {
      getActiveAccount: vi.fn().mockReturnValue(account),
      getAllAccounts: vi.fn().mockReturnValue([account]),
      acquireTokenSilent: vi.fn().mockRejectedValue(transientError),
      acquireTokenRedirect,
    }
    vi.doMock('@azure/msal-browser', () => ({
      PublicClientApplication: vi.fn().mockImplementation(function () { return instance }),
      InteractionRequiredAuthError: MockInteractionRequiredAuthError,
    }))
    vi.stubEnv('VITE_AAD_CLIENT_ID', 'test-client-id')

    const mod = await import('./msal')

    await expect(mod.getAccessToken()).rejects.toBe(transientError)
    expect(acquireTokenRedirect).not.toHaveBeenCalled()
  })

  test('rejects when silent token acquisition does not settle', async () => {
    vi.useFakeTimers()
    const account = { homeAccountId: 'acc-1' }
    const instance = {
      getActiveAccount: vi.fn().mockReturnValue(account),
      getAllAccounts: vi.fn().mockReturnValue([account]),
      acquireTokenSilent: vi.fn().mockReturnValue(new Promise(() => {})),
      acquireTokenRedirect: vi.fn(),
    }
    vi.doMock('@azure/msal-browser', () => ({
      PublicClientApplication: vi.fn().mockImplementation(function () { return instance }),
      InteractionRequiredAuthError: class extends Error {},
    }))
    vi.stubEnv('VITE_AAD_CLIENT_ID', 'test-client-id')

    const mod = await import('./msal')
    const result = mod.getAccessToken().then(
      () => 'resolved',
      error => error instanceof Error ? error.message : String(error),
    )

    await vi.advanceTimersByTimeAsync(10_000)
    await expect(result).resolves.toBe('Sign-in timed out')
  })

  test('returns null when no account is signed in', async () => {
    const instance = {
      getActiveAccount: vi.fn().mockReturnValue(null),
      getAllAccounts: vi.fn().mockReturnValue([]),
      acquireTokenSilent: vi.fn(),
      acquireTokenRedirect: vi.fn(),
    }
    vi.doMock('@azure/msal-browser', () => ({
      PublicClientApplication: vi.fn().mockImplementation(function () { return instance }),
      InteractionRequiredAuthError: class extends Error {},
    }))
    vi.stubEnv('VITE_AAD_CLIENT_ID', 'test-client-id')

    const mod = await import('./msal')

    await expect(mod.getAccessToken()).resolves.toBeNull()
    expect(instance.acquireTokenSilent).not.toHaveBeenCalled()
  })
})
