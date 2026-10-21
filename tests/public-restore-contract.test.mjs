import assert from 'node:assert/strict'
import { mkdtemp, readFile, readdir, rm } from 'node:fs/promises'
import { spawnSync } from 'node:child_process'
import os from 'node:os'
import path from 'node:path'
import test from 'node:test'
import { fileURLToPath } from 'node:url'

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const ignored = new Set(['.git', 'bin', 'obj', 'node_modules'])
const privatePlumbingPatterns = [
  new RegExp(['nuget', 'pkg', 'github', 'com'].join('\\.'), 'i'),
  new RegExp(['npm', 'pkg', 'github', 'com'].join('\\.'), 'i'),
  new RegExp(['GITHUB', 'PACKAGES', 'TOKEN'].join('_'), 'i'),
  new RegExp(['FIXPORTAL', 'PACKAGES', 'TOKEN'].join('_'), 'i'),
  new RegExp(['NODE', 'AUTH', 'TOKEN'].join('_'), 'i'),
  new RegExp(['package', 'SourceCredentials'].join(''), 'i'),
]
const privatePackageIds = [
  ['FixPortal', 'CodeStyle', 'ArchRules'].join('.'),
]
const privateArchitectureRule = ['FixPortal', 'ArchRules'].join('')

function trackedFiles() {
  const result = spawnSync('git', ['ls-files', '-z'], { cwd: root, encoding: 'utf8' })
  assert.equal(result.status, 0, result.stderr)
  return result.stdout.split('\0').filter(Boolean)
}

function isLive(pathname) {
  const parts = pathname.split('/')
  return !pathname.startsWith('docs/superpowers/') && !parts.some(part => ignored.has(part))
}

function assertNoPrivatePlumbing(pathname, text) {
  for (const pattern of privatePlumbingPatterns) {
    assert.doesNotMatch(text, pattern, `${pathname} retains private package or credential plumbing`)
  }
}

function hasPrivatePackageReference(pathname, text) {
  if (!/\.(?:csproj|props|targets)$/.test(pathname)) {
    return false
  }

  return [...text.matchAll(/<Package(?:Reference|Version)\b[^>]*>/gi)].some(element => {
    const packageId = element[0].match(/\b(?:Include|Update)\s*=\s*(['"])(.*?)\1/i)?.[2]
    return privatePackageIds.some(privatePackageId => privatePackageId.toLowerCase() === packageId?.toLowerCase())
  })
}

// Parses <PackageReference> elements into attribute maps with a quote-aware scanner, so
// assertions hold for any attribute order, whitespace or quoting the XML grammar allows —
// the same forms the private package matcher above accepts.
function packageReferenceAttributes(xml) {
  const elements = []
  let cursor = 0
  for (;;) {
    const start = xml.indexOf('<PackageReference', cursor)
    if (start === -1) return elements
    let index = start + '<PackageReference'.length
    const attributes = {}
    let selfClosed = false
    let closed = false
    while (!closed) {
      while (index < xml.length && /\s/.test(xml[index])) index++
      if (xml.startsWith('/>', index)) {
        index += 2
        closed = true
        selfClosed = true
      } else if (xml[index] === '>') {
        index++
        closed = true
      } else {
        const name = /^[^\s=/<>]+/.exec(xml.slice(index))?.[0]
        assert.ok(name, `unparseable attribute at offset ${index} of a PackageReference element`)
        index += name.length
        while (index < xml.length && /\s/.test(xml[index])) index++
        assert.equal(xml[index], '=', `attribute ${name} has no value at offset ${index}`)
        index++
        const quote = xml[index]
        assert.ok(quote === '"' || quote === "'", `attribute ${name} is not quoted at offset ${index}`)
        const end = xml.indexOf(quote, index + 1)
        assert.notEqual(end, -1, `attribute ${name} is never closed`)
        attributes[name] = xml.slice(index + 1, end)
        index = end + 1
      }
    }
    if (!selfClosed) {
      const close = xml.indexOf('</PackageReference>', index)
      assert.notEqual(close, -1, 'PackageReference element is never closed')
      index = close
    }
    elements.push(attributes)
    cursor = index
  }
}

async function runWithRecovery(operation, recovery, cleanup) {
  let primaryFailure
  try {
    await operation()
  } catch (error) {
    primaryFailure = error
  }

  try {
    await recovery()
  } catch (recoveryFailure) {
    if (primaryFailure) {
      throw new AggregateError([primaryFailure, recoveryFailure], 'operation and recovery both failed')
    }
    throw recoveryFailure
  }

  await cleanup()
  if (primaryFailure) {
    throw primaryFailure
  }
}

test('private plumbing scan includes test sources', () => {
  const token = ['GITHUB', 'PACKAGES', 'TOKEN'].join('_')

  assert.throws(() => assertNoPrivatePlumbing('tests/live-fixture.test.mjs', token), /retains private package or credential plumbing/)
})

test('private plumbing scan rejects mixed-case feed spellings', () => {
  const feed = ['NuGeT', 'PkG', 'GiThUb', 'CoM'].join('.')
  assert.throws(() => assertNoPrivatePlumbing('tests/live-fixture.test.mjs', feed), /retains private package or credential plumbing/)
})

test('private package matcher detects supported MSBuild attribute forms', () => {
  const packageId = privatePackageIds[0]
  for (const [pathname, element] of [
    ['sample.csproj', `<PackageReference Include="${packageId}" />`],
    ['sample.props', `<PackageVersion Version="0.1.11" Update='${packageId}' />`],
    ['sample.targets', `<PackageReference PrivateAssets="all" Include='${packageId}' />`],
    ['lowercase.csproj', `<PackageReference Include='${packageId.toLowerCase()}' />`],
    ['mixed-case.targets', `<PackageReference Update='${packageId.toLowerCase()}' />`],
  ]) {
    assert.equal(hasPrivatePackageReference(pathname, element), true, `${pathname} must detect ${element}`)
  }
})

test('failed recovery retains the isolated cache and reports both failures', async () => {
  const primaryFailure = new Error('cold restore failed')
  const recoveryFailure = new Error('live restore failed')
  let cleaned = false

  await assert.rejects(
    runWithRecovery(
      async () => { throw primaryFailure },
      async () => {
        assert.equal(cleaned, false)
        throw recoveryFailure
      },
      async () => { cleaned = true },
    ),
    error => error instanceof AggregateError
      && error.errors[0] === primaryFailure
      && error.errors[1] === recoveryFailure,
  )
  assert.equal(cleaned, false)
})

test('successful recovery cleans the isolated cache without masking the primary failure', async () => {
  const primaryFailure = new Error('cold restore failed')
  const events = []

  await assert.rejects(
    runWithRecovery(
      async () => {
        events.push('primary')
        throw primaryFailure
      },
      async () => { events.push('recovery') },
      async () => { events.push('cleanup') },
    ),
    error => error === primaryFailure,
  )
  assert.deepEqual(events, ['primary', 'recovery', 'cleanup'])
})

test('public restore contract excludes private package plumbing and leaves live build assets usable', async () => {
  const buildProps = await readFile(path.join(root, 'Directory.Build.props'), 'utf8')
  const codeStyle = packageReferenceAttributes(buildProps).filter(
    (attributes) => attributes.Include === 'FixPortal.CodeStyle'
  )
  assert.equal(codeStyle.length, 1, 'Directory.Build.props must reference FixPortal.CodeStyle exactly once')
  assert.equal(codeStyle[0].PrivateAssets, 'all', 'the FixPortal.CodeStyle reference must stay private')
  assert.doesNotMatch(buildProps, /GlobalAnalyzerConfigFiles|SonarAnalyzer\.CSharp/)

  for (const pathname of trackedFiles().filter(isLive)) {
    const text = await readFile(path.join(root, pathname), 'utf8')
    assertNoPrivatePlumbing(pathname, text)
    if (hasPrivatePackageReference(pathname, text)) {
      assert.fail(`${pathname} retains a private package reference`)
    }
    if (pathname.endsWith('.cs')) {
      assert.doesNotMatch(text, new RegExp(privateArchitectureRule), `${pathname} retains a private architecture rule call`)
    }
  }

  const packages = await mkdtemp(path.join(os.tmpdir(), 'aiobservatory-public-restore-'))
  const githubPackagesToken = ['GITHUB', 'PACKAGES', 'TOKEN'].join('_')
  const fixportalPackagesToken = ['FIXPORTAL', 'PACKAGES', 'TOKEN'].join('_')
  await runWithRecovery(
    async () => {
      assert.deepEqual(await readdir(packages), [], 'isolated NuGet package directory must start empty')
      const environment = { ...process.env, NUGET_PACKAGES: packages }
      delete environment[githubPackagesToken]
      delete environment[fixportalPackagesToken]
      assert.equal(Object.hasOwn(environment, githubPackagesToken), false, `${githubPackagesToken} must be absent during restore`)
      assert.equal(Object.hasOwn(environment, fixportalPackagesToken), false, `${fixportalPackagesToken} must be absent during restore`)
      const restore = spawnSync('dotnet', ['restore', 'AiObservatory.slnx', '--configfile', 'nuget.config'], {
        cwd: root,
        encoding: 'utf8',
        env: environment,
        timeout: 300_000,
      })
      assert.equal(restore.status, 0, `dotnet restore failed:\n${restore.stdout}\n${restore.stderr}`)
    },
    async () => {
      const recoveryEnvironment = { ...process.env }
      delete recoveryEnvironment[githubPackagesToken]
      delete recoveryEnvironment[fixportalPackagesToken]
      const recovery = spawnSync('dotnet', ['restore', 'AiObservatory.slnx', '--configfile', 'nuget.config', '--force'], {
        cwd: root,
        encoding: 'utf8',
        env: recoveryEnvironment,
        timeout: 300_000,
      })
      assert.equal(recovery.status, 0, `failed to restore live build assets:\n${recovery.stdout}\n${recovery.stderr}`)
    },
    async () => rm(packages, { recursive: true, force: true }),
  )

  const build = spawnSync('dotnet', ['build', 'AiObservatory.slnx', '--configuration', 'Release', '--no-restore'], {
    cwd: root,
    encoding: 'utf8',
    timeout: 300_000,
  })
  assert.equal(build.status, 0, `isolated restore contaminated live build assets:\n${build.stdout}\n${build.stderr}`)
})
