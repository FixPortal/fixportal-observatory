import { useCallback, useEffect, useState } from 'react'

export const DOCS_ROOT = '/docs'

/** decodeURIComponent throws on malformed escapes, and the address bar is user input. */
export function safeDecode(value: string): string {
  try {
    return decodeURIComponent(value)
  } catch {
    return value
  }
}

export function isDocsPath(pathname: string): boolean {
  return pathname === DOCS_ROOT || pathname.startsWith(`${DOCS_ROOT}/`)
}

/** The chapter slug in a docs path, or null for the bare /docs root. */
export function slugFromPath(pathname: string): string | null {
  if (!pathname.startsWith(`${DOCS_ROOT}/`)) return null
  const slug = safeDecode(pathname.slice(DOCS_ROOT.length + 1)).replace(/\/+$/, '')
  return slug.length > 0 ? slug : null
}

export function docsHref(slug: string, id?: string): string {
  return `${DOCS_ROOT}/${encodeURIComponent(slug)}${id === undefined ? '' : `#${id}`}`
}

/**
 * Carries the current query string onto an app URL (before any fragment). The query holds
 * mode flags such as mbed=fixportal-ide that are read once at page load, so every URL the
 * app writes must keep them or a reload silently changes mode.
 */
export function keepSearch(href: string): string {
  const { search } = window.location
  if (search === '' || href.includes('?')) return href
  const hash = href.indexOf('#')
  return hash < 0 ? href + search : href.slice(0, hash) + search + href.slice(hash)
}

interface Location {
  pathname: string
  hash: string
}

function readLocation(): Location {
  return {
    pathname: window.location.pathname,
    hash: safeDecode(window.location.hash.slice(1)),
  }
}

/** Tracks the browser location so docs links work with back, forward and deep links. */
export function useDocsLocation() {
  const [location, setLocation] = useState<Location>(readLocation)

  useEffect(() => {
    const sync = () => {
      setLocation(readLocation())
    }
    window.addEventListener('popstate', sync)
    return () => {
      window.removeEventListener('popstate', sync)
    }
  }, [])

  const navigate = useCallback((href: string, replace = false) => {
    const target = keepSearch(href)
    const { pathname, search, hash } = window.location
    // Following a link to the page already shown must not stack a duplicate entry.
    if (replace || target === pathname + search + hash)
      window.history.replaceState(null, '', target)
    else window.history.pushState(null, '', target)
    setLocation(readLocation())
  }, [])

  return { pathname: location.pathname, hash: location.hash, navigate }
}
