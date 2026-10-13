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
    /(?:^|[;{\s])transition(?:-property)?\s*:\s*([^;}]+)/g,
  )) {
    layers.push(...match[1]!.split(',').map((layer) => layer.trim()))
  }
  return layers
}

describe('control transitions', () => {
  it('reads real stylesheets that declare transitions', () => {
    // Guards the test against passing vacuously on empty input or a parser that matches nothing.
    for (const css of Object.values(stylesheets)) {
      expect(css.length).toBeGreaterThan(0)
    }
    expect(Object.values(stylesheets).flatMap(transitionLayers).length).toBeGreaterThan(10)
  })

  it('names the properties that controls animate', () => {
    for (const [file, css] of Object.entries(stylesheets)) {
      const offending = transitionLayers(css).filter((layer) => /^all\b/.test(layer))
      expect(offending, `${file} animates "all"`).toEqual([])
    }
  })
})
