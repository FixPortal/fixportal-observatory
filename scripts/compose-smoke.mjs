import assert from 'node:assert/strict'
import { spawnSync } from 'node:child_process'
import { randomUUID } from 'node:crypto'
import { mkdtemp, readFile, rm, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const temporary = await mkdtemp(path.join(tmpdir(), 'observatory-smoke-'))
const project = `observatory-smoke-${randomUUID()}`
const env = { ...process.env }
const composeFile = path.join(root, 'docker-compose.yml')
// Exercise the credential-free quick start even on a configured developer machine.
for (const [, name] of (await readFile(composeFile, 'utf8')).matchAll(/\$\{([A-Z_][A-Z_0-9]*)/g)) {
  delete env[name]
}
const override = path.join(temporary, 'ports.yml')
const dotenv = path.join(temporary, '.env')
await writeFile(dotenv, '')
await writeFile(override, `services:
  db:
    ports: !reset []
  web:
    ports: !override
      - "127.0.0.1::80"
`)
const prefix = ['compose', '--project-name', project, '--env-file', dotenv, '-f', composeFile, '-f', override]

function compose(args, capture = false) {
  const result = spawnSync('docker', [...prefix, ...args], {
    cwd: root, env, encoding: 'utf8', stdio: capture ? 'pipe' : 'inherit', timeout: 600_000,
  })
  if (result.error) throw result.error
  assert.equal(result.status, 0, `docker compose ${args.join(' ')} failed: ${result.stderr ?? ''}`)
  return result.stdout?.trim()
}

try {
  compose(['up', '--build', '--wait', '--wait-timeout', '180'])
  const address = compose(['port', 'web', '80'], true)
  const origin = `http://${address}`
  const page = await fetch(origin, { signal: AbortSignal.timeout(15_000) })
  assert.equal(page.status, 200, 'frontend must respond')
  const html = await page.text()
  assert.match(html, /<div id="root">/, 'frontend must serve the app, not an nginx error page')
  const script = html.match(/src="([^"]+\.js)"/)
  assert.ok(script, 'frontend must reference its built JavaScript')
  const asset = await fetch(new URL(script[1], origin), { signal: AbortSignal.timeout(15_000) })
  assert.equal(asset.status, 200, 'built JavaScript must be served')
  assert.match(asset.headers.get('content-type'), /javascript/, 'asset must not fall back to index.html')
  const response = await fetch(`${origin}/api/aggregates`, {
    headers: { 'X-Observatory-Key': 'change-me' }, signal: AbortSignal.timeout(15_000),
  })
  assert.equal(response.status, 200, 'nginx must proxy authenticated API requests')
  const aggregates = await response.json()
  assert.ok(Array.isArray(aggregates) && aggregates.length > 0, 'quick start must contain seeded data')
  assert.ok(aggregates.some(row => row.sourceId === 'demo-seed'), 'sample data must retain its synthetic provenance')
  const containers = compose(['ps', '--all', '--format', 'json'], true)
    .split(/\r?\n/).filter(Boolean).map(line => JSON.parse(line))
  assert.ok(containers.some(item => item.Service === 'seed' && item.State === 'exited' && item.ExitCode === 0), 'seed must finish successfully')
  assert.ok(containers.some(item => item.Service === 'ingest' && item.Health === 'healthy'), 'ingest must start after seeding and become healthy')
  console.log(`Compose smoke passed: frontend, JavaScript, API, ${aggregates.length} seeded aggregates, ingest health.`)
} catch (error) {
  compose(['logs', '--no-color', '--tail', '80'])
  throw error
} finally {
  // The UUID project owns only this test's containers and synthetic-data volume.
  compose(['down', '--volumes', '--remove-orphans'])
  await rm(temporary, { recursive: true, force: true })
}
