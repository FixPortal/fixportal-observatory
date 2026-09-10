// localStorage access that never throws. Accessing localStorage raises a SecurityError
// in a storage-blocked browser (private mode with cookies disabled, some embedded
// webviews); an unguarded read in a state initializer would blank the whole app with no
// ErrorBoundary above it. Callers get null / a silent no-op instead.
export const safeStorage = {
  get(key: string): string | null {
    try {
      return localStorage.getItem(key)
    } catch {
      return null
    }
  },
  set(key: string, value: string): void {
    try {
      localStorage.setItem(key, value)
    } catch {
      /* storage blocked — nothing to persist to */
    }
  },
}
