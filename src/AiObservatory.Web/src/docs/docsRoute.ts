import { useCallback, useEffect, useState } from 'react'

export const DOCS_ROOT = '/docs'

export function isDocsPath(pathname: string): boolean {
  return pathname === DOCS_ROOT || pathname.startsWith(`${DOCS_ROOT}/`)
}

/** The chapter slug in a docs path, or null for the bare /docs root. */
export function slugFromPath(pathname: string): string | null {
  if (!pathname.startsWith(`${DOCS_ROOT}/`)) return null
  const slug = decodeURIComponent(pathname.slice(DOCS_ROOT.length + 1)).replace(/\/+$/, '')
  return slug.length > 0 ? slug : null
}

export function docsHref(slug: string, id?: string): string {
  return `${DOCS_ROOT}/${encodeURIComponent(slug)}${id === undefined ? '' : `#${id}`}`
}

interface Location {
  pathname: string
  hash: string
}

function readLocation(): Location {
  return {
    pathname: window.location.pathname,
    hash: decodeURIComponent(window.location.hash.slice(1)),
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
    if (replace) window.history.replaceState(null, '', href)
    else window.history.pushState(null, '', href)
    setLocation(readLocation())
  }, [])

  return { pathname: location.pathname, hash: location.hash, navigate }
}
