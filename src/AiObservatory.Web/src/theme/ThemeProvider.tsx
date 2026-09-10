import { useCallback, useEffect, useMemo, useState, type ReactNode } from 'react'
import { ThemeContext, type ThemeMode, type ResolvedTheme } from './useTheme'
import { safeStorage } from '../lib/safeStorage'

const STORAGE_KEY = 'aiobs-theme'

function readMode(): ThemeMode {
  const stored = safeStorage.get(STORAGE_KEY)
  if (stored === 'light' || stored === 'dark' || stored === 'system') return stored
  return 'system'
}

function resolve(mode: ThemeMode): ResolvedTheme {
  if (mode === 'system') {
    return typeof window !== 'undefined' && window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light'
  }
  return mode
}

export function ThemeProvider({ children }: { children: ReactNode }) {
  const [mode, setModeState] = useState<ThemeMode>(() => readMode())
  const [systemResolved, setSystemResolved] = useState<ResolvedTheme>(() => resolve('system'))
  const resolved = useMemo<ResolvedTheme>(() => {
    if (mode === 'system') return systemResolved
    return mode
  }, [mode, systemResolved])

  useEffect(() => {
    document.documentElement.dataset.theme = resolved
  }, [resolved])

  useEffect(() => {
    safeStorage.set(STORAGE_KEY, mode)
  }, [mode])

  useEffect(() => {
    if (mode !== 'system') return () => {}
    if (typeof window === 'undefined' || !window.matchMedia) return () => {}
    const mq = window.matchMedia('(prefers-color-scheme: dark)')
    // eslint-disable-next-line react-hooks/set-state-in-effect
    setSystemResolved(mq.matches ? 'dark' : 'light')
    const onChange = () => setSystemResolved(mq.matches ? 'dark' : 'light')
    mq.addEventListener('change', onChange)
    return () => mq.removeEventListener('change', onChange)
  }, [mode])

  const setMode = useCallback((next: ThemeMode) => setModeState(next), [])

  const ctxValue = useMemo(() => ({ mode, setMode, resolved }), [mode, setMode, resolved])
  return (
    <ThemeContext.Provider value={ctxValue}>
      {children}
    </ThemeContext.Provider>
  )
}
