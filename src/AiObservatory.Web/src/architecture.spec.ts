/**
 * ArchUnitTS architecture spec (https://github.com/LukasNiessen/ArchUnitTS).
 *
 * File/folder-level architecture rules. Scope: layer isolation and cycle freedom.
 * (Naming and size-metric rules were trialled and dropped: naming overlaps lint,
 * and ArchUnitTS's metrics are class-oriented, of little use in a function-
 * component codebase.)
 *
 * Layer diagram (low -> high):
 *
 *   config/*   provider metadata, pure constants; no internal deps
 *   auth/*     Entra/MSAL wiring; no internal deps
 *   lib/*      pure helpers (currency, billing); no internal deps
 *   theme/*    design tokens, context; may depend on config
 *   api/*      data fetching, query hooks; may depend on auth
 *   components/* presentational panels; may depend on api, lib, theme, config
 *   pages/*    composition layer; may depend on all of the above
 *
 * Assertion style: we call `.check()` (every condition implements Checkable) and
 * assert on the returned Violation[] with plain `expect`. ArchUnitTS's
 * `toPassAsync` matcher only auto-registers under Vitest `globals: true`, which
 * this scaffold opts out of -- `projectFiles` therefore comes through the local
 * wrapper `./architecture.archunit`, which isolates the dist-internal deep import
 * and the exact-version pin it needs. See that file's header for the full story.
 */
import { describe, it, expect } from 'vitest'
import { projectFiles } from './architecture.archunit'

const TS_CONFIG = 'tsconfig.app.json'

// Test files reach across layers by design; exclude them from layering rules.
const EXCEPT_TESTS = { except: { withName: '*.test.*' } }

// Each row asserts: nothing in `from` may import from `to`.
// Derived from the actual import hierarchy in src/ as of 2026-06-15.
const FORBIDDEN_EDGES: ReadonlyArray<{
  from: string
  fromGlob: string
  to: string
  toGlob: string
}> = [
  // config is the lowest layer -- must not reach up into anything
  { from: 'config', fromGlob: '**/config/**', to: 'lib',        toGlob: '**/lib/**'        },
  { from: 'config', fromGlob: '**/config/**', to: 'auth',       toGlob: '**/auth/**'       },
  { from: 'config', fromGlob: '**/config/**', to: 'theme',      toGlob: '**/theme/**'      },
  { from: 'config', fromGlob: '**/config/**', to: 'api',        toGlob: '**/api/**'        },
  { from: 'config', fromGlob: '**/config/**', to: 'components', toGlob: '**/components/**' },
  { from: 'config', fromGlob: '**/config/**', to: 'pages',      toGlob: '**/pages/**'      },
  // lib is a pure-helper layer -- must not import any other internal layer
  { from: 'lib',    fromGlob: '**/lib/**',    to: 'config',     toGlob: '**/config/**'     },
  { from: 'lib',    fromGlob: '**/lib/**',    to: 'auth',       toGlob: '**/auth/**'       },
  { from: 'lib',    fromGlob: '**/lib/**',    to: 'theme',      toGlob: '**/theme/**'      },
  { from: 'lib',    fromGlob: '**/lib/**',    to: 'api',        toGlob: '**/api/**'        },
  { from: 'lib',    fromGlob: '**/lib/**',    to: 'components', toGlob: '**/components/**' },
  { from: 'lib',    fromGlob: '**/lib/**',    to: 'pages',      toGlob: '**/pages/**'      },
  // auth must not reach up into api, components or pages
  { from: 'auth',   fromGlob: '**/auth/**',   to: 'api',        toGlob: '**/api/**'        },
  { from: 'auth',   fromGlob: '**/auth/**',   to: 'components', toGlob: '**/components/**' },
  { from: 'auth',   fromGlob: '**/auth/**',   to: 'pages',      toGlob: '**/pages/**'      },
  // theme must not reach up into api, components or pages
  { from: 'theme',  fromGlob: '**/theme/**',  to: 'api',        toGlob: '**/api/**'        },
  { from: 'theme',  fromGlob: '**/theme/**',  to: 'components', toGlob: '**/components/**' },
  { from: 'theme',  fromGlob: '**/theme/**',  to: 'pages',      toGlob: '**/pages/**'      },
  // api must not reach up into components or pages
  { from: 'api',    fromGlob: '**/api/**',    to: 'components', toGlob: '**/components/**' },
  { from: 'api',    fromGlob: '**/api/**',    to: 'pages',      toGlob: '**/pages/**'      },
  // components must not import pages (composition layer)
  { from: 'components', fromGlob: '**/components/**', to: 'pages', toGlob: '**/pages/**'  },
]

describe('architecture / layer isolation', () => {
  for (const edge of FORBIDDEN_EDGES) {
    it(`${edge.from} must not depend on ${edge.to}`, async () => {
      const violations = await projectFiles(TS_CONFIG)
        .inFolder(edge.fromGlob, EXCEPT_TESTS)
        .shouldNot()
        .dependOnFiles()
        .inFolder(edge.toGlob)
        .check()
      expect(violations).toEqual([])
    }, 15_000)
  }
})

describe('architecture / cycles', () => {
  it('the whole src tree is free of import cycles', async () => {
    const violations = await projectFiles(TS_CONFIG)
      .inFolder('**/src/**')
      .should()
      .haveNoCycles()
      .check()
    expect(violations).toEqual([])
  }, 15_000)
})

