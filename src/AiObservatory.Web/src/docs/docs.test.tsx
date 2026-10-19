import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { CHAPTER_FILES, CHAPTER_GROUPS, CHAPTERS, buildChapters, fileSlugs } from './chapters'
import { DocsPage } from './DocsPage'
import { isDocsPath, slugFromPath } from './docsRoute'
import { slugify } from './rehypeDocs'
import { READING_LINE, pickActiveIndex } from './useActiveSection'

const FIXTURE = [
  {
    slug: 'one',
    title: 'One',
    group: 'Basics',
    markdown: [
      '# One',
      '',
      'Intro with a [jump](#second) and a [next](/docs/two) link.',
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

  it('says so when the chapter does not exist', () => {
    window.history.replaceState(null, '', '/docs/nope')
    render(<DocsPage chapters={FIXTURE} groups={GROUPS} />)
    expect(screen.getByRole('alert')).toHaveTextContent('Page not found')
  })
})
