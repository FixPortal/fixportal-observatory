import type { Element, ElementContent, Root, RootContent } from 'hast'

const HEADING_TAGS = new Set(['h2', 'h3', 'h4'])
const REFERENCE_TITLE = /^(references|sources)$/i

export function slugify(text: string): string {
  const slug = text
    .toLowerCase()
    .normalize('NFKD')
    .replaceAll(/[^a-z0-9]+/g, '-')
    .replaceAll(/^-+|-+$/g, '')
  return slug.length > 0 ? slug : 'section'
}

function textOf(node: ElementContent | RootContent): string {
  if (node.type === 'text') return node.value
  if (node.type === 'element') return node.children.map(textOf).join('')
  return ''
}

function isElement(node: RootContent): node is Element {
  return node.type === 'element'
}

function wrapReferences(children: RootContent[]): RootContent[] {
  const out: RootContent[] = []
  let current: Element | null = null
  for (const node of children) {
    if (isElement(node) && node.tagName === 'h2') {
      current = null
      if (REFERENCE_TITLE.test(textOf(node).trim())) {
        const summary: Element = {
          type: 'element',
          tagName: 'summary',
          properties: {},
          children: [node],
        }
        current = {
          type: 'element',
          tagName: 'details',
          properties: { className: ['docs-refs'] },
          children: [summary],
        }
        out.push(current)
        continue
      }
    }
    if (current === null) out.push(node)
    else current.children.push(node as ElementContent)
  }
  return out
}

/**
 * Gives h2-h4 stable unique ids and wraps a References or Sources section in a collapsed
 * disclosure. Must run after rehype-sanitize, which would otherwise prefix the ids.
 * Each call to the returned transformer starts a fresh duplicate counter, so ids depend only
 * on the document.
 */
export function rehypeDocs() {
  return (tree: Root) => {
    const seen = new Map<string, number>()
    const visit = (node: Root | Element) => {
      for (const child of node.children) {
        if (!isElement(child)) continue
        if (HEADING_TAGS.has(child.tagName)) {
          const base = slugify(textOf(child))
          const count = seen.get(base) ?? 0
          seen.set(base, count + 1)
          child.properties = { ...child.properties, id: count === 0 ? base : `${base}-${count}` }
        }
        visit(child)
      }
    }
    visit(tree)
    tree.children = wrapReferences(tree.children)
  }
}
