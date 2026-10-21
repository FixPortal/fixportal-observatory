import { readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'

// jsdom applies no cascade, so a component test cannot see a specificity loss. The shared table
// rule `:is(.spend-ledger, ...) th|td` has specificity (0,1,1); a bare `.spend-ledger__num*`
// rule is (0,1,0) and silently loses to it (refund colour, Amount header alignment).
// Not a literal inside new URL(...): Vite would rewrite that into an asset URL.
const path = '../index.css'
const css = readFileSync(new URL(path, import.meta.url), 'utf8').replace(/\/\*[\s\S]*?\*\//g, '')

describe('spend ledger cascade', () => {
  it('scopes every number-cell rule under .spend-ledger so it outranks the shared table rule', () => {
    const selectors = [...css.matchAll(/([^{}]+)\{/g)]
      .map((m) => m[1]!.trim())
      .filter((selector) => selector.includes('spend-ledger__num'))
    expect(selectors.length).toBeGreaterThanOrEqual(2)
    for (const selector of selectors) {
      expect(selector, 'rule would lose to the shared table rule').toMatch(/^\.spend-ledger\s/)
    }
  })
})
