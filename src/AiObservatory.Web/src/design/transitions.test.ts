import { readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'

// Read from disk, not via `?raw`: under Vitest's default css handling a `?raw` CSS import
// resolves to an EMPTY STRING (measured: typeof css === 'string', css.length === 0), so the
// previous negative regex passed vacuously against nothing.
const stylesheets: Record<string, string> = Object.fromEntries(
  ['../index.css', './components.css', './tokens.css'].map((path) => [
    path,
    readFileSync(new URL(path, import.meta.url), 'utf8'),
  ]),
)

// Every layer of every `transition` / `transition-property` declaration, comments removed.
// A comma-separated list is split so `color 1s, all 2s` is judged layer by layer.
function transitionLayers(css: string): string[] {
  const withoutComments = css.replace(/\/\*[\s\S]*?\*\//g, '')
  const layers: string[] = []
  for (const match of withoutComments.matchAll(
    /(?:^|[;{\s])(?:--[\w-]*transition[\w-]*|transition(?:-property)?)\s*:\s*([^;}]+)/g,
  )) {
    layers.push(...splitTopLevel(match[1]!))
  }
  return layers
}

// Splits on commas outside parentheses, so cubic-bezier(0.4, 0, 0.2, 1) stays in one layer.
function splitTopLevel(value: string): string[] {
  const parts: string[] = []
  let depth = 0
  let start = 0
  for (let i = 0; i < value.length; i++) {
    const ch = value[i]
    if (ch === '(') depth++
    else if (ch === ')') depth--
    else if (ch === ',' && depth === 0) {
      parts.push(value.slice(start, i).trim())
      start = i + 1
    }
  }
  parts.push(value.slice(start).trim())
  return parts
}

// Shorthand components come in any order, so 150ms all animates everything too.
const animatesAll = (layer: string) => /(^|\s)all(\s|$)/.test(layer)

describe('control transitions', () => {
  it('reads real stylesheets that declare transitions', () => {
    // Guards the test against passing vacuously on empty input or a parser that matches nothing.
    for (const css of Object.values(stylesheets)) {
      expect(css.length).toBeGreaterThan(0)
    }
    expect(Object.values(stylesheets).flatMap(transitionLayers).length).toBeGreaterThan(10)
  })

  it('catches the keyword all in any position and through a transition custom property', () => {
    const css = [
      'a { transition: color 1s, 150ms all; }',
      'b { transition: opacity 1s cubic-bezier(0.4, 0, 0.2, 1), all 2s; }',
      ':root { --transition-x: 0.2s all; }',
      'c { transition: color 1s ease, opacity 1s; }',
    ].join('\n')
    expect(transitionLayers(css).filter(animatesAll)).toEqual(['150ms all', 'all 2s', '0.2s all'])
  })

  it('names the properties that controls animate', () => {
    for (const [file, css] of Object.entries(stylesheets)) {
      const offending = transitionLayers(css).filter(animatesAll)
      expect(offending, `${file} animates "all"`).toEqual([])
    }
  })
})
