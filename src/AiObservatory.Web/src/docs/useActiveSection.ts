import { useCallback, useEffect, useState } from 'react'

/** Distance below the top of the viewport at which a heading counts as reached. */
export const READING_LINE = 96

/**
 * Index of the active section for headings at the given viewport offsets.
 * The last heading at or above the reading line wins; before the first heading the first
 * section is active. At the bottom of a scrolled page the first section still below the
 * line wins if its heading is on screen, so short closing sections that can never reach
 * the line are not skipped.
 */
export function pickActiveIndex(
  tops: readonly number[],
  viewportHeight: number,
  atBottom: boolean,
  line: number = READING_LINE,
): number {
  if (tops.length === 0) return -1
  let index = 0
  tops.forEach((top, i) => {
    if (top <= line) index = i
  })
  if (atBottom) {
    const remaining = tops.findIndex((top) => top > line)
    const top = tops[remaining]
    if (top !== undefined && top < viewportHeight) index = remaining
  }
  return index
}

interface Selection {
  headings: readonly HTMLElement[]
  id: string | null
}

const USER_SCROLL_KEYS = new Set(['ArrowUp', 'ArrowDown', 'PageUp', 'PageDown', 'Home', 'End', ' '])

/**
 * Tracks the heading being read. An explicit selection (a rail click) holds until the reader
 * resumes scrolling, then scrolling takes over again. State is keyed to the heading list,
 * so a chapter change never shows the previous chapter's highlight.
 */
export function useActiveSection(headings: readonly HTMLElement[]) {
  const [scrolled, setScrolled] = useState<Selection>({ headings, id: null })
  const [explicit, setExplicit] = useState<Selection>({ headings, id: null })

  useEffect(() => {
    if (headings.length === 0) return undefined
    const update = () => {
      const root = document.documentElement
      const atBottom =
        window.scrollY > 0 && window.scrollY + window.innerHeight >= root.scrollHeight - 1
      const tops = headings.map((h) => h.getBoundingClientRect().top)
      const index = pickActiveIndex(tops, window.innerHeight, atBottom)
      const id = headings[index]?.id ?? null
      setScrolled((prev) =>
        prev.headings === headings && prev.id === id ? prev : { headings, id },
      )
    }
    const onUserInput = (event: Event) => {
      if (event instanceof KeyboardEvent && !USER_SCROLL_KEYS.has(event.key)) return
      setExplicit({ headings, id: null })
      update()
    }
    const frame = requestAnimationFrame(update)
    const observer = typeof ResizeObserver === 'undefined' ? undefined : new ResizeObserver(update)
    observer?.observe(document.body)
    window.addEventListener('scroll', update, { passive: true })
    window.addEventListener('resize', update)
    window.addEventListener('toggle', update, true)
    window.addEventListener('wheel', onUserInput, { passive: true })
    window.addEventListener('touchmove', onUserInput, { passive: true })
    window.addEventListener('keydown', onUserInput)
    return () => {
      cancelAnimationFrame(frame)
      observer?.disconnect()
      window.removeEventListener('scroll', update)
      window.removeEventListener('resize', update)
      window.removeEventListener('toggle', update, true)
      window.removeEventListener('wheel', onUserInput)
      window.removeEventListener('touchmove', onUserInput)
      window.removeEventListener('keydown', onUserInput)
    }
  }, [headings])

  const select = useCallback(
    (id: string) => {
      setExplicit({ headings, id })
    },
    [headings],
  )

  const heldId = explicit.headings === headings ? explicit.id : null
  const scrolledId = scrolled.headings === headings ? scrolled.id : null
  const activeId = heldId ?? scrolledId ?? headings[0]?.id ?? null
  return { activeId, select }
}
