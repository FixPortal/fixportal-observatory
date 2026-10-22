import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import path from 'node:path'
import test from 'node:test'
import { fileURLToPath } from 'node:url'

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const read = name => readFile(path.join(root, name), 'utf8')

test('public CI builds the CLI and qualifies replay and explicit Node audit suites', async () => {
  const ci = await read('.github/workflows/ci.yml')
  assert.match(ci, /dotnet build AiObservatory\.slnx/)
  assert.match(ci, /dotnet test --solution AiObservatory\.slnx/)
  for (const command of ['node scripts/audit-replay-smoke.mjs', 'tests/audit-release-contract.test.mjs', 'clients/observatory-audit.test.mjs', 'clients/audit-poll.test.mjs']) {
    assert.ok(ci.includes(command), `CI must run ${command}`)
  }
})

test('release workflow packages all supported framework-dependent runners without deployment authority', async () => {
  const workflow = await read('.github/workflows/audit-release.yml')
  assert.match(workflow, /release:\s*\n\s+types: \[published\]/)
  assert.match(workflow, /workflow_dispatch:/)
  assert.match(workflow, /git merge-base --is-ancestor/)
  assert.match(workflow, /dotnet publish[\s\S]*--self-contained false/)
  assert.match(workflow, /UseAppHost=false/)
  for (const rid of ['win-x64', 'linux-x64', 'osx-arm64']) assert.ok(workflow.includes(rid))
  assert.match(workflow, /python3 scripts\/package-audit-runner.py/)
  assert.match(workflow, /actions\/upload-artifact@v7/)
  assert.match(workflow, /if-no-files-found: error/)
  assert.doesNotMatch(workflow, /secrets[.\[]|id-token:|contents: write|deploy\.yml|gh release|azure\//i)
})

test('replay smoke locks the reviewed golden verdict, canonical bytes and SHA-256', async () => {
  const smoke = await read('scripts/audit-replay-smoke.mjs')
  assert.match(smoke, /5a194bb9f85af8731a076e0fbea4baad6a57e0f8a32c5c746f3cea90d9a78f5b/)
  assert.match(smoke, /golden-v1\.result\.json/)
  assert.match(smoke, /status, 3/)
  assert.match(smoke, /deepEqual/)
  assert.match(smoke, /\['first', 'second'\]/)
  assert.doesNotMatch(smoke, /fetch\(|https?:|curl|wget|dotnet run|dotnet build|dotnet restore/)
})

test('packaging records source and verifier identities and uses an explicit payload allowlist', async () => {
  const script = await read('scripts/package-audit-runner.py')
  for (const key of ['sourceCommit', 'schemaVersion', 'verifierVersion', 'ruleVersion', 'sha256', 'SHA256SUMS', 'AuditVerifierIdentity.cs']) assert.ok(script.includes(key))
  assert.match(script, /AiObservatory\.Audit\.Cli\.runtimeconfig\.json/)
  assert.doesNotMatch(script, /requests|urllib|curl|wget|extractall|\.pdb["']/)
})

test('operator documentation exposes setup, authority, retention and offline replay contracts', async () => {
  const guide = await read('docs/self-audit.md')
  for (const contract of ['Audit__Enabled', 'false', '/api/audits/machines', '/rotate', '/credential', 'X-Observatory-Audit-Key', 'observatory-audit.mjs poll', '90 days', '365 days', 'two minutes', '512 MiB', '1 GiB', '.NET 10', 'replay', '--reevaluate', 'six', 'billing', 'backup']) assert.ok(guide.includes(contract), `guide must explain ${contract}`)
  assert.match(guide, /two concurrent exports per instance/i)
  assert.match(guide, /pricing.*provenance.*disabled/i)
  assert.match(guide, /viewer[\s\S]*report[\s\S]*admin[\s\S]*bundle/i)
  assert.ok((await read('README.md')).includes('(docs/self-audit.md)'))
  assert.ok((await read('clients/README.md')).includes('../docs/self-audit.md'))
  assert.ok((await read('src/AiObservatory.Web/src/docs/chapters.ts')).includes("'self-audit'"))
})

test('public automation and examples contain no installation paths or credentials', async () => {
  const files = ['.github/workflows/audit-release.yml', 'scripts/audit-replay-smoke.mjs', 'scripts/package-audit-runner.py', 'docs/self-audit.md']
  for (const file of files) {
    const text = await read(file)
    assert.doesNotMatch(text, /(?:^|[\s"'])[A-Z]:[\\/]|\/Users\/|\/home\/|sk-ant-|sk-[A-Za-z0-9]{12,}|TEST_DB_CONNECTION|ANTHROPIC_API_KEY|OPENAI_API_KEY/i, file)
  }
})
