/// <reference types="node" />
import { afterAll, beforeAll, describe, expect, it } from 'vitest'
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { createRequire } from 'node:module'
import { tmpdir } from 'node:os'
import { join, relative, resolve } from 'node:path'

type ParsedFile = { name: string; diagrams: unknown[] }
type Patterns = string | string[]
const require = createRequire(import.meta.url)
const consumerRequire = createRequire(require.resolve('archunit/package.json'))
// Exercise ArchUnit's installed transitive CommonJS consumer, not a second direct copy.
const parser = consumerRequire('plantuml-parser') as {
  parseFile(patterns: Patterns, options?: object,
    callback?: (error: Error | null, files: ParsedFile[]) => void): ParsedFile[]
}
const roots: string[] = []
let absoluteRoot: string
let relativeRoot: string
const slash = (value: string) => value.replaceAll('\\', '/')

beforeAll(() => {
  absoluteRoot = mkdtempSync(join(tmpdir(), 'observatory-plantuml-glob-'))
  relativeRoot = mkdtempSync(join(process.cwd(), '.tmp-plantuml-glob-'))
  roots.push(absoluteRoot, relativeRoot)
  for (const root of roots) {
    mkdirSync(join(root, 'nested'))
    for (const file of ['one.puml', 'two.puml', 'nested/three.puml', '.hidden.puml']) {
      writeFileSync(join(root, file), '@startuml\nclass Example\n@enduml\n')
    }
  }
})

afterAll(() => {
  for (const root of roots) {
    const target = resolve(root)
    const allowed = [join(tmpdir(), 'observatory-plantuml-glob-'),
      join(process.cwd(), '.tmp-plantuml-glob-')]
    if (!allowed.some(prefix => target.startsWith(resolve(prefix)))) {
      throw new Error('Refusing to remove a fixture outside its generated root')
    }
    rmSync(target, { recursive: true, force: true })
  }
})

const scenarios: Array<[string, () => Patterns, () => string[]]> = [
  ['absolute file (including Windows cross-drive paths)',
    () => slash(join(absoluteRoot, 'one.puml')), () => [join(absoluteRoot, 'one.puml')]],
  ['relative wildcard',
    () => slash(relative(process.cwd(), relativeRoot)) + '/*.puml',
    () => ['one.puml', 'two.puml'].map(file => join(relativeRoot, file))],
  ['absolute wildcard',
    () => slash(absoluteRoot) + '/*.puml',
    () => ['one.puml', 'two.puml'].map(file => join(absoluteRoot, file))],
  ['brace alternatives',
    () => slash(absoluteRoot) + '/{one,two}.puml',
    () => ['one.puml', 'two.puml'].map(file => join(absoluteRoot, file))],
  ['array of absolute and relative files',
    () => [slash(join(absoluteRoot, 'one.puml')),
      slash(relative(process.cwd(), join(relativeRoot, 'nested/three.puml')))],
    () => [join(absoluteRoot, 'one.puml'), join(relativeRoot, 'nested/three.puml')]],
  ['recursive wildcard with exclusion',
    () => [slash(absoluteRoot) + '/**/*.puml', '!' + slash(absoluteRoot) + '/nested/**'],
    () => ['one.puml', 'two.puml'].map(file => join(absoluteRoot, file))],
  ['directory does not expand into its children', () => slash(absoluteRoot), () => []],
  ['missing file', () => slash(join(absoluteRoot, 'missing.puml')), () => []],
]

describe('installed PlantUML file parser glob compatibility', () => {
  it.each(scenarios)('%s', (_label, patterns, expected) => {
    const files = parser.parseFile(patterns())
    expect(files.map(file => file.name).sort()).toEqual(
      expected().map(file => relative(process.cwd(), file)).sort())
    for (const file of files) expect(file.diagrams).toHaveLength(1)
  })

  it('retains its asynchronous callback API', async () => {
    const files = await new Promise<ParsedFile[]>((fulfil, reject) => {
      parser.parseFile(slash(join(absoluteRoot, 'one.puml')), {},
        (error, values) => error ? reject(error) : fulfil(values))
    })
    expect(files.map(file => file.name)).toEqual([
      relative(process.cwd(), join(absoluteRoot, 'one.puml')),
    ])
    expect(files[0].diagrams).toHaveLength(1)
  })
})


