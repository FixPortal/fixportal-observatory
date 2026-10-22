export interface Chapter {
  slug: string
  title: string
  group: string
  markdown: string
}

export interface ChapterGroup {
  title: string
  slugs: readonly string[]
}

/**
 * Reading order and grouping. A chapter file is content/<slug>.md, titled by its first
 * `# ` heading; the docs tests fail if a file is missing from this list or listed here
 * without a file. Add the slug here when you add a chapter.
 */
export const CHAPTER_GROUPS: readonly ChapterGroup[] = [
  { title: 'Getting started', slugs: ['introduction', 'concepts', 'access'] },
  {
    title: 'Using the dashboard',
    slugs: [
      'overview',
      'subscriptions',
      'usage-insights',
      'data-sources',
      'suggestions',
      'adversarial-review',
      'reporting',
      'self-audit',
      'spend',
      'activity',
      'github',
    ],
  },
  {
    title: 'Setup and operations',
    slugs: [
      'providers',
      'local-producers',
      'pricing',
      'configuration',
      'authentication',
      'api',
      'deployment',
      'troubleshooting',
      'glossary',
    ],
  },
]

const FILES = import.meta.glob<string>('./content/*.md', {
  query: '?raw',
  import: 'default',
  eager: true,
})

function titleOf(markdown: string, fallback: string): string {
  const match = /^#\s+(.+?)\s*$/m.exec(markdown)
  return match?.[1] ?? fallback
}

export function buildChapters(
  files: Readonly<Record<string, string>>,
  groups: readonly ChapterGroup[],
): Chapter[] {
  const bySlug = new Map<string, string>()
  for (const [path, markdown] of Object.entries(files)) {
    const slug = /([^/\\]+)\.md$/.exec(path)?.[1]
    if (slug !== undefined) bySlug.set(slug, markdown)
  }
  return groups.flatMap((group) =>
    group.slugs.flatMap((slug) => {
      const markdown = bySlug.get(slug)
      return markdown === undefined
        ? []
        : [{ slug, title: titleOf(markdown, slug), group: group.title, markdown }]
    }),
  )
}

export function fileSlugs(files: Readonly<Record<string, string>>): string[] {
  return Object.keys(files).flatMap((path) => {
    const slug = /([^/\\]+)\.md$/.exec(path)?.[1]
    return slug === undefined ? [] : [slug]
  })
}

export const CHAPTER_FILES: Readonly<Record<string, string>> = FILES
export const CHAPTERS: readonly Chapter[] = buildChapters(FILES, CHAPTER_GROUPS)
