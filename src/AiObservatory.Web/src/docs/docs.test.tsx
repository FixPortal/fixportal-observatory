import { useEffect } from 'react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { act, fireEvent, render, renderHook, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { CHAPTER_FILES, CHAPTER_GROUPS, CHAPTERS, buildChapters, fileSlugs } from './chapters'
import { DocsPage } from './DocsPage'
import { isDocsPath, safeDecode, slugFromPath } from './docsRoute'
import { slugify } from './rehypeDocs'
import { READING_LINE, pickActiveIndex, useActiveSection } from './useActiveSection'

const FIXTURE = [
  {
    slug: 'one',
    title: 'One',
    group: 'Basics',
    markdown: [
      '# One',
      '',
      'Intro with a [jump](#second), a [next](/docs/two) and a [dead](/docs/two#nope) link.',
      '',
      '## First',
      '',
      'text',
      '',
      '## Second',
      '',
      '### Detail',
      '',
      '| a | b |',
      '| - | - |',
      '| 1 | 2 |',
      '',
      '## Second',
      '',
      '## References',
      '',
      'A source line.',
    ].join('\n'),
  },
  { slug: 'two', title: 'Two', group: 'Basics', markdown: '# Two\n\n## Only\n' },
] as const

const GROUPS = [{ title: 'Basics', slugs: ['one', 'two'] }]

describe('chapter manifest', () => {
  it('lists every content file and every listed chapter has a file', () => {
    const listed = CHAPTER_GROUPS.flatMap((g) => g.slugs).sort()
    expect(fileSlugs(CHAPTER_FILES).sort()).toEqual(listed)
    expect(CHAPTERS.map((c) => c.slug).sort()).toEqual(listed)
  })

  it('titles a chapter by its first heading and skips unknown slugs', () => {
    const built = buildChapters({ './content/a.md': '# Alpha\n' }, [
      { title: 'G', slugs: ['a', 'missing'] },
    ])
    expect(built).toEqual([{ slug: 'a', title: 'Alpha', group: 'G', markdown: '# Alpha\n' }])
  })
})

describe('docs paths', () => {
  it.each([
    ['/docs', true, null],
    ['/docs/spend', true, 'spend'],
    ['/docs/spend/', true, 'spend'],
    ['/documents', false, null],
    ['/', false, null],
  ])('%s', (path, docs, slug) => {
    expect(isDocsPath(path)).toBe(docs)
    expect(slugFromPath(path)).toBe(slug)
  })

  it('survives malformed percent-escapes in the address', () => {
    expect(slugFromPath('/docs/%E0%A4%A')).toBe('%E0%A4%A')
    expect(safeDecode('%zz')).toBe('%zz')
    expect(safeDecode('a%20b')).toBe('a b')
  })

  it('slugifies headings', () => {
    expect(slugify('Billed spend (31 days)')).toBe('billed-spend-31-days')
    expect(slugify('***')).toBe('section')
  })
})

describe('pickActiveIndex', () => {
  const line = READING_LINE
  const height = 800

  it('selects the first section before any heading is reached', () => {
    expect(pickActiveIndex([300, 900], height, false)).toBe(0)
  })

  it('selects the last heading that has crossed the reading line', () => {
    expect(pickActiveIndex([-400, 50, line + 1, 900], height, false)).toBe(1)
  })

  it('does not skip a short section when the page bottoms out', () => {
    // A and B crossed; C and D share the final viewport but cannot reach the line.
    const tops = [-900, -300, 400, 600]
    expect(pickActiveIndex(tops, height, false)).toBe(1)
    expect(pickActiveIndex(tops, height, true)).toBe(2)
  })

  it('keeps the last section when it crossed the line at the bottom', () => {
    expect(pickActiveIndex([-900, -300, 40], height, true)).toBe(2)
  })

  it('ignores a remaining heading that is below the viewport', () => {
    expect(pickActiveIndex([-300, 1200], height, true)).toBe(0)
  })

  it('reports no section for no headings', () => {
    expect(pickActiveIndex([], height, true)).toBe(-1)
  })
})

describe('DocsPage', () => {
  beforeEach(() => {
    window.history.replaceState(null, '', '/docs/one')
    window.HTMLElement.prototype.scrollIntoView = vi.fn()
    window.scrollTo = vi.fn()
  })
  afterEach(() => {
    window.history.replaceState(null, '', '/')
  })

  it('renders the chapter with one title and unique heading ids', () => {
    render(<DocsPage chapters={FIXTURE} groups={GROUPS} />)
    expect(screen.getAllByRole('heading', { level: 1 })).toHaveLength(1)
    const ids = screen.getAllByRole('heading', { level: 2 }).map((h) => h.id)
    expect(ids).toEqual(['first', 'second', 'second-1', 'references'])
  })

  it('lists sections in document order under On this page', () => {
    render(<DocsPage chapters={FIXTURE} groups={GROUPS} />)
    const rail = screen.getByRole('navigation', { name: 'On this page' })
    expect(
      within(rail)
        .getAllByRole('link')
        .map((a) => a.textContent),
    ).toEqual(['First', 'Second', 'Detail', 'Second', 'References'])
    expect(within(rail).getAllByRole('link')[0]).toHaveAttribute('aria-current', 'location')
  })

  it('marks the current chapter and offers previous and next', () => {
    render(<DocsPage chapters={FIXTURE} groups={GROUPS} />)
    const nav = screen.getByRole('navigation', { name: 'Documentation chapters' })
    expect(within(nav).getByRole('link', { name: 'One' })).toHaveAttribute('aria-current', 'page')
    const adjacent = screen.getByRole('navigation', { name: 'Adjacent chapters' })
    expect(within(adjacent).queryByText('Previous')).toBeNull()
    expect(within(adjacent).getByRole('link', { name: /Two/ })).toHaveAttribute('href', '/docs/two')
  })

  it('moves to another chapter and rebuilds the rail', async () => {
    const user = userEvent.setup()
    render(<DocsPage chapters={FIXTURE} groups={GROUPS} />)
    await user.click(
      within(screen.getByRole('navigation', { name: 'Adjacent chapters' })).getByRole('link'),
    )
    expect(window.location.pathname).toBe('/docs/two')
    const rail = screen.getByRole('navigation', { name: 'On this page' })
    expect(
      within(rail)
        .getAllByRole('link')
        .map((a) => a.textContent),
    ).toEqual(['Only'])
    expect(window.scrollTo).toHaveBeenCalledWith(0, 0)
  })

  it('keeps a same-page anchor link as a real link and scrolls to it', async () => {
    const user = userEvent.setup()
    render(<DocsPage chapters={FIXTURE} groups={GROUPS} />)
    const jump = screen.getByRole('link', { name: 'jump' })
    expect(jump).toHaveAttribute('href', '/docs/one#second')
    await user.click(jump)
    expect(window.location.hash).toBe('#second')
    expect(window.HTMLElement.prototype.scrollIntoView).toHaveBeenCalled()
    const rail = screen.getByRole('navigation', { name: 'On this page' })
    expect(within(rail).getAllByRole('link')[1]).toHaveAttribute('aria-current', 'location')
  })

  it('starts references collapsed and opens them for a citation link', () => {
    window.history.replaceState(null, '', '/docs/one#references')
    const { container } = render(<DocsPage chapters={FIXTURE} groups={GROUPS} />)
    const details = container.querySelector('details.docs-refs')
    expect(details).not.toBeNull()
    expect(details).toHaveProperty('open', true)
  })

  it('collapses references when no deep link points at them', () => {
    const { container } = render(<DocsPage chapters={FIXTURE} groups={GROUPS} />)
    expect(container.querySelector('details.docs-refs')).toHaveProperty('open', false)
  })

  it('wraps tables so wide ones scroll locally', () => {
    render(<DocsPage chapters={FIXTURE} groups={GROUPS} />)
    const region = screen.getByRole('region', { name: /Table/ })
    expect(within(region).getByRole('table')).toBeInTheDocument()
  })

  it('redirects the bare root to the first chapter', () => {
    window.history.replaceState(null, '', '/docs')
    render(<DocsPage chapters={FIXTURE} groups={GROUPS} />)
    expect(screen.getByRole('heading', { level: 1, name: 'One' })).toBeInTheDocument()
    expect(window.location.pathname).toBe('/docs/one')
  })

  it('keeps the fragment and query string when redirecting the bare root', () => {
    window.history.replaceState(null, '', '/docs?embed=fixportal-ide#second')
    render(<DocsPage chapters={FIXTURE} groups={GROUPS} />)
    expect(window.location.pathname).toBe('/docs/one')
    expect(window.location.search).toBe('?embed=fixportal-ide')
    expect(window.location.hash).toBe('#second')
  })

  it('keeps the query string on chapter links', async () => {
    window.history.replaceState(null, '', '/docs/one?embed=fixportal-ide')
    const user = userEvent.setup()
    render(<DocsPage chapters={FIXTURE} groups={GROUPS} />)
    await user.click(
      within(screen.getByRole('navigation', { name: 'Adjacent chapters' })).getByRole('link'),
    )
    expect(window.location.pathname).toBe('/docs/two')
    expect(window.location.search).toBe('?embed=fixportal-ide')
  })

  it('returns to the top when a link names a fragment the new chapter lacks', async () => {
    const user = userEvent.setup()
    render(<DocsPage chapters={FIXTURE} groups={GROUPS} />)
    vi.mocked(window.scrollTo).mockClear()
    await user.click(screen.getByRole('link', { name: 'dead' }))
    expect(window.location.pathname).toBe('/docs/two')
    expect(window.scrollTo).toHaveBeenCalledWith(0, 0)
  })

  it('replaces rather than stacks history when the current chapter is clicked again', async () => {
    const user = userEvent.setup()
    render(<DocsPage chapters={FIXTURE} groups={GROUPS} />)
    const before = window.history.length
    vi.mocked(window.scrollTo).mockClear()
    const nav = screen.getByRole('navigation', { name: 'Documentation chapters' })
    await user.click(within(nav).getByRole('link', { name: 'One' }))
    expect(window.history.length).toBe(before)
    expect(window.scrollTo).toHaveBeenCalledWith(0, 0)
  })

  it('gives a heading whose slug ends in a number a distinct id from a duplicate', () => {
    window.history.replaceState(null, '', '/docs/dup')
    const dup = [
      {
        slug: 'dup',
        title: 'Dup',
        group: 'Basics',
        markdown: '# Dup\n\n## Base\n\n## Base\n\n## Base 1\n',
      },
    ] as const
    render(<DocsPage chapters={dup} groups={[{ title: 'Basics', slugs: ['dup'] }]} />)
    const ids = screen.getAllByRole('heading', { level: 2 }).map((h) => h.id)
    expect(new Set(ids).size).toBe(ids.length)
  })

  it('says so when the chapter does not exist', () => {
    window.history.replaceState(null, '', '/docs/nope')
    render(<DocsPage chapters={FIXTURE} groups={GROUPS} />)
    expect(screen.getByRole('alert')).toHaveTextContent('Page not found')
  })
})

describe('useActiveSection', () => {
  function headingAt(id: string, top: { value: number }) {
    const h = document.createElement('h2')
    h.id = id
    h.getBoundingClientRect = () => ({ top: top.value }) as DOMRect
    document.body.append(h)
    return h
  }
  afterEach(() => {
    document.body.replaceChildren()
  })

  it('does not re-render on wheel input when nothing is held', async () => {
    const headings = [headingAt('a', { value: 0 }), headingAt('b', { value: 500 })]
    // Count commits, not function calls: React may re-invoke a component to bail out of an
    // unchanged state, which re-parses nothing.
    let renders = 0
    renderHook(() => {
      useEffect(() => {
        renders += 1
      })
      return useActiveSection(headings)
    })
    await act(async () => {
      await new Promise((resolve) => requestAnimationFrame(resolve))
    })
    const settled = renders
    act(() => {
      fireEvent.wheel(window)
      fireEvent.wheel(window)
      fireEvent.wheel(window)
    })
    expect(renders).toBe(settled)
  })

  it('releases a held selection when the page is scrolled without wheel or keys', () => {
    const aTop = { value: 0 }
    const bTop = { value: 500 }
    const headings = [headingAt('a', aTop), headingAt('b', bTop)]
    const { result } = renderHook(() => useActiveSection(headings))
    act(() => {
      result.current.select('b')
    })
    expect(result.current.activeId).toBe('b')

    // A scrollbar drag moves the page: heading b leaves where the click scrolled it.
    aTop.value = -400
    bTop.value = 100
    act(() => {
      fireEvent.scroll(window)
    })
    expect(result.current.activeId).toBe('a')
  })

  it('keeps a held selection through scroll events that do not move the page', () => {
    const headings = [headingAt('a', { value: 0 }), headingAt('b', { value: 500 })]
    const { result } = renderHook(() => useActiveSection(headings))
    act(() => {
      result.current.select('b')
    })
    act(() => {
      fireEvent.scroll(window)
    })
    expect(result.current.activeId).toBe('b')
  })
})

describe('shipped chapters', () => {
  it('every in-chapter and cross-chapter fragment link resolves to a heading id', () => {
    window.HTMLElement.prototype.scrollIntoView = vi.fn()
    window.scrollTo = vi.fn()
    const ids = new Map<string, Set<string>>()
    const links: { from: string; href: string }[] = []
    for (const chapter of CHAPTERS) {
      window.history.replaceState(null, '', `/docs/${chapter.slug}`)
      const { container, unmount } = render(<DocsPage />)
      ids.set(
        chapter.slug,
        new Set(Array.from(container.querySelectorAll('.docs-article [id]'), (el) => el.id)),
      )
      for (const a of container.querySelectorAll<HTMLAnchorElement>('.docs-article a')) {
        const href = a.getAttribute('href') ?? ''
        if (href.startsWith('/docs/') && href.includes('#'))
          links.push({ from: chapter.slug, href })
      }
      unmount()
    }
    window.history.replaceState(null, '', '/')
    expect(links.length).toBeGreaterThan(0)
    const broken = links.filter(({ href }) => {
      const [path = '', id = ''] = href.split('#')
      return !ids.get(decodeURIComponent(path.slice('/docs/'.length)))?.has(decodeURIComponent(id))
    })
    expect(broken).toEqual([])
  })
})
