import {
  useCallback,
  useEffect,
  useMemo,
  useState,
  useSyncExternalStore,
  type MouseEvent,
  type ReactNode,
} from 'react'
import ReactMarkdown, { type Components } from 'react-markdown'
import rehypeSanitize from 'rehype-sanitize'
import remarkGfm from 'remark-gfm'
import { CHAPTER_GROUPS, CHAPTERS, type Chapter, type ChapterGroup } from './chapters'
import { DOCS_ROOT, docsHref, slugFromPath, useDocsLocation } from './docsRoute'
import { rehypeDocs } from './rehypeDocs'
import { useActiveSection } from './useActiveSection'
import './docs.css'

const WIDE_RAIL = '(min-width: 1100px)'
const WIDE_NAV = '(min-width: 820px)'

function useMediaQuery(query: string): boolean {
  return useSyncExternalStore(
    (notify) => {
      if (typeof window.matchMedia !== 'function') return () => undefined
      const list = window.matchMedia(query)
      list.addEventListener('change', notify)
      return () => {
        list.removeEventListener('change', notify)
      }
    },
    () => typeof window.matchMedia !== 'function' || window.matchMedia(query).matches,
  )
}

function isPlainLeftClick(event: MouseEvent): boolean {
  return (
    !event.defaultPrevented &&
    event.button === 0 &&
    !event.metaKey &&
    !event.ctrlKey &&
    !event.shiftKey &&
    !event.altKey
  )
}

function openAncestors(element: HTMLElement) {
  for (
    let details = element.closest('details');
    details !== null;
    details = details.parentElement?.closest('details') ?? null
  ) {
    details.open = true
  }
}

const REMARK_PLUGINS = [remarkGfm]
const REHYPE_PLUGINS = [rehypeSanitize, rehypeDocs]

interface DocsLinkProps {
  href: string
  className?: string | undefined
  current?: 'page' | 'location' | undefined
  onFollow: (href: string) => void
  children: ReactNode
}

function DocsLink({ href, className, current, onFollow, children }: DocsLinkProps) {
  return (
    <a
      href={href}
      className={className}
      aria-current={current}
      onClick={(event) => {
        if (!isPlainLeftClick(event)) return
        event.preventDefault()
        onFollow(href)
      }}
    >
      {children}
    </a>
  )
}

function ChapterNav({
  chapters,
  groups,
  current,
  onFollow,
}: {
  chapters: readonly Chapter[]
  groups: readonly ChapterGroup[]
  current: string | undefined
  onFollow: (href: string) => void
}) {
  const wide = useMediaQuery(WIDE_NAV)
  const list = (
    <nav aria-label="Documentation chapters" className="docs-nav">
      {groups.map((group) => {
        const items = chapters.filter((c) => c.group === group.title)
        if (items.length === 0) return null
        return (
          <div key={group.title} className="docs-nav__group">
            <p className="docs-nav__heading">{group.title}</p>
            <ul>
              {items.map((c) => (
                <li key={c.slug}>
                  <DocsLink
                    href={docsHref(c.slug)}
                    className="docs-nav__link"
                    current={c.slug === current ? 'page' : undefined}
                    onFollow={onFollow}
                  >
                    {c.title}
                  </DocsLink>
                </li>
              ))}
            </ul>
          </div>
        )
      })}
    </nav>
  )
  if (wide) return <aside className="docs-sidebar">{list}</aside>
  return (
    <details className="docs-disclosure docs-sidebar">
      <summary>Chapters</summary>
      {list}
    </details>
  )
}

function SectionRail({
  headings,
  activeId,
  slug,
  onFollow,
}: {
  headings: readonly HTMLElement[]
  activeId: string | null
  slug: string
  onFollow: (href: string) => void
}) {
  const wide = useMediaQuery(WIDE_RAIL)
  if (headings.length === 0) return null
  const list = (
    <nav aria-label="On this page" className="docs-rail__nav">
      <ul>
        {headings.map((h) => (
          <li key={h.id} className={h.tagName === 'H3' ? 'docs-rail__sub' : undefined}>
            <DocsLink
              href={docsHref(slug, h.id)}
              className="docs-rail__link"
              current={h.id === activeId ? 'location' : undefined}
              onFollow={onFollow}
            >
              {h.textContent}
            </DocsLink>
          </li>
        ))}
      </ul>
    </nav>
  )
  if (wide) {
    return (
      <aside className="docs-rail">
        <p className="docs-rail__title">On this page</p>
        {list}
      </aside>
    )
  }
  return (
    <details className="docs-disclosure docs-rail">
      <summary>On this page</summary>
      {list}
    </details>
  )
}

function useBeforePrint() {
  useEffect(() => {
    const reopen: HTMLDetailsElement[] = []
    const onBefore = () => {
      for (const d of document.querySelectorAll<HTMLDetailsElement>('.docs-article details')) {
        if (!d.open) {
          d.open = true
          reopen.push(d)
        }
      }
    }
    const onAfter = () => {
      for (const d of reopen.splice(0)) d.open = false
    }
    window.addEventListener('beforeprint', onBefore)
    window.addEventListener('afterprint', onAfter)
    return () => {
      window.removeEventListener('beforeprint', onBefore)
      window.removeEventListener('afterprint', onAfter)
    }
  }, [])
}

function DocsArticle({
  chapter,
  onFollow,
  onHeadings,
}: {
  chapter: Chapter
  onFollow: (href: string) => void
  onHeadings: (headings: HTMLElement[]) => void
}) {
  const components = useMemo<Components>(
    () => ({
      a({ href, children }) {
        if (href === undefined) return <>{children}</>
        if (href.startsWith('#')) {
          return (
            <DocsLink href={docsHref(chapter.slug, href.slice(1))} onFollow={onFollow}>
              {children}
            </DocsLink>
          )
        }
        if (href === DOCS_ROOT || href.startsWith(`${DOCS_ROOT}/`)) {
          return (
            <DocsLink href={href} onFollow={onFollow}>
              {children}
            </DocsLink>
          )
        }
        return (
          <a href={href} target="_blank" rel="noopener noreferrer">
            {children}
          </a>
        )
      },
      table({ children }) {
        return (
          <div
            className="docs-table"
            tabIndex={0}
            role="region"
            aria-label="Table, scrolls sideways"
          >
            <table>{children}</table>
          </div>
        )
      },
    }),
    [chapter.slug, onFollow],
  )
  const collect = useCallback(
    (article: HTMLElement | null) => {
      onHeadings(
        article === null ? [] : Array.from(article.querySelectorAll<HTMLElement>('h2[id], h3[id]')),
      )
    },
    [onHeadings],
  )
  return (
    <article className="docs-article" ref={collect}>
      <ReactMarkdown
        remarkPlugins={REMARK_PLUGINS}
        rehypePlugins={REHYPE_PLUGINS}
        components={components}
      >
        {chapter.markdown}
      </ReactMarkdown>
    </article>
  )
}

function AdjacentChapters({
  previous,
  next,
  onFollow,
}: {
  previous: Chapter | undefined
  next: Chapter | undefined
  onFollow: (href: string) => void
}) {
  if (previous === undefined && next === undefined) return null
  return (
    <nav aria-label="Adjacent chapters" className="docs-adjacent">
      {previous !== undefined && (
        <DocsLink
          href={docsHref(previous.slug)}
          className="docs-adjacent__link docs-adjacent__link--previous"
          onFollow={onFollow}
        >
          <span className="docs-adjacent__label">Previous</span>
          {previous.title}
        </DocsLink>
      )}
      {next !== undefined && (
        <DocsLink
          href={docsHref(next.slug)}
          className="docs-adjacent__link docs-adjacent__link--next"
          onFollow={onFollow}
        >
          <span className="docs-adjacent__label">Next</span>
          {next.title}
        </DocsLink>
      )}
    </nav>
  )
}

export function DocsPage({
  chapters = CHAPTERS,
  groups = CHAPTER_GROUPS,
}: {
  chapters?: readonly Chapter[]
  groups?: readonly ChapterGroup[]
}) {
  const { pathname, hash, navigate } = useDocsLocation()
  const requested = slugFromPath(pathname)
  const index = requested === null ? 0 : chapters.findIndex((c) => c.slug === requested)
  const chapter = chapters[index]
  const [headings, setHeadings] = useState<HTMLElement[]>([])
  const { activeId, select } = useActiveSection(headings)
  useBeforePrint()

  const bareRoot = requested === null
  const firstSlug = chapters[0]?.slug
  useEffect(() => {
    if (bareRoot && firstSlug !== undefined) {
      window.history.replaceState(null, '', docsHref(firstSlug))
    }
  }, [bareRoot, firstSlug])

  const scrollToHash = useCallback(
    (id: string): boolean => {
      const target = document.getElementById(id)
      if (target === null) return false
      openAncestors(target)
      target.scrollIntoView()
      select(id)
      return true
    },
    [select],
  )

  const slug = chapter?.slug
  useEffect(() => {
    if (slug === undefined) return
    if (hash.length > 0) scrollToHash(hash)
    else window.scrollTo(0, 0)
  }, [slug, hash, scrollToHash])

  const follow = useCallback(
    (href: string) => {
      navigate(href)
      const id = href.split('#')[1]
      if (id !== undefined) scrollToHash(decodeURIComponent(id))
    },
    [navigate, scrollToHash],
  )

  if (chapter === undefined) {
    return (
      <div className="docs">
        <ChapterNav chapters={chapters} groups={groups} current={undefined} onFollow={follow} />
        <div className="docs-main">
          <div className="docs-article" role="alert">
            <h1>Page not found</h1>
            <p>There is no documentation page at this address. Pick a chapter from the list.</p>
          </div>
        </div>
      </div>
    )
  }

  return (
    <div className="docs">
      <ChapterNav chapters={chapters} groups={groups} current={chapter.slug} onFollow={follow} />
      <div className="docs-main">
        <SectionRail
          headings={headings}
          activeId={activeId}
          slug={chapter.slug}
          onFollow={follow}
        />
        <DocsArticle
          key={chapter.slug}
          chapter={chapter}
          onFollow={follow}
          onHeadings={setHeadings}
        />
        <AdjacentChapters
          previous={chapters[index - 1]}
          next={chapters[index + 1]}
          onFollow={follow}
        />
      </div>
    </div>
  )
}
