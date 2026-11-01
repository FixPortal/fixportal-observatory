import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import path from 'node:path'
import test from 'node:test'
import { fileURLToPath } from 'node:url'

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const read = relativePath => readFile(path.join(root, relativePath), 'utf8')

// Deploy-ordering and recovery invariants, re-homed here from the estate-shared
// workflow-hygiene checker, which dropped them when it became a generic asset.
// They guard the class written up as the ~25-minute production outage: the API
// deploy stops the ingest worker to run migrations, and anything that reorders
// the stop, or unwires the job that starts the worker again, passed every gate
// silently. The contract runs over the real workflow file, so the assertion and
// the thing asserted cannot drift apart.
function jobBlock(text, jobId) {
  const start = text.search(new RegExp(`^  ${jobId}:$`, 'm'))
  assert.notEqual(start, -1, `deploy.yml is missing the '${jobId}' job`)
  const rest = text.slice(start)
  const next = rest.slice(1).search(/^ {2}[A-Za-z_][\w-]*:$/m)
  return next === -1 ? rest : rest.slice(0, next + 1)
}

// Evaluates the real `$same_origin_request` map from the web image's nginx template the way
// nginx does (first matching row wins, `default` otherwise), so the admin-key rule is asserted
// against the shipped file rather than a copy of it.
function sameOriginDecision(template, request) {
  const map = /map\s+"([^"]+)"\s+\$same_origin_request\s*\{([^}]*)\}/.exec(template)
  assert.ok(map, 'nginx.conf.template must define the $same_origin_request map')
  const variables = {
    $request_method: request.method,
    $http_sec_fetch_site: request.site ?? '',
    $http_origin: request.origin ?? '',
    $http_host: request.host ?? 'observatory.example',
  }
  const input = map[1].replace(/\$\w+/g, name => variables[name] ?? '')
  let fallback = '0'
  for (const row of map[2].split(';')) {
    const parts = /^\s*(?:"([^"]*)"|(\S+))\s+(\S+)\s*$/.exec(row)
    if (!parts) continue
    const pattern = parts[1] ?? parts[2]
    if (pattern === 'default') { fallback = parts[3]; continue }
    if (pattern.startsWith('~') && new RegExp(pattern.slice(1)).test(input)) { return parts[3] }
  }
  return fallback
}

test('the web proxy injects the admin key only for same-origin browsers or a bodyless read', async () => {
  const template = await read('src/AiObservatory.Web/nginx.conf.template')
  const host = 'observatory.example'
  const cases = [
    [{ method: 'POST', site: 'same-origin' }, '1'],
    [{ method: 'POST', site: 'none' }, '1'],
    [{ method: 'POST', site: 'cross-site' }, '0'],
    [{ method: 'POST', origin: `https://${host}`, host }, '1'],
    [{ method: 'POST', origin: 'https://evil.example', host }, '0'],
    // A browser that sends no Origin or Sec-Fetch-Site (Firefox before 70 on a cross-site form POST)
    // is indistinguishable from a non-browser client, so the empty case may only carry reads.
    [{ method: 'POST' }, '0'],
    [{ method: 'PUT' }, '0'],
    [{ method: 'DELETE' }, '0'],
    [{ method: 'GET' }, '1'],
    [{ method: 'HEAD' }, '1'],
    [{ method: 'GET', site: 'cross-site' }, '0'],
  ]
  for (const [request, expected] of cases) {
    assert.equal(sameOriginDecision(template, request), expected, JSON.stringify(request))
  }
})

test('deploy-ingest deploys only after the API deploy', async () => {
  const ingest = jobBlock(await read('.github/workflows/deploy.yml'), 'deploy-ingest')
  // The API deploy stops the ingest worker for its migrations; deploying the worker
  // without that ordering races new worker code against a migration in flight.
  assert.ok(
    /^ {4}needs:[^\n]*\bdeploy-api\b/m.test(ingest) || /^ {4}needs:\s*\r?\n\s+-\s*deploy-api\b/m.test(ingest),
    "deploy-ingest must declare `needs: deploy-api` -- the API deploy stops the ingest worker for its migrations, and an unsequenced ingest deploy races them",
  )
})

test('the ingest worker is stopped before the API package is deployed', async () => {
  const api = jobBlock(await read('.github/workflows/deploy.yml'), 'deploy-api')
  const stop = api.indexOf('az webapp stop')
  const publish = api.indexOf('azure/webapps-deploy')
  assert.notEqual(stop, -1, 'deploy-api no longer stops the ingest worker before running API migrations')
  assert.notEqual(publish, -1, 'deploy-api no longer deploys via azure/webapps-deploy')
  assert.ok(stop < publish, 'az webapp stop must precede azure/webapps-deploy -- migrating under a live worker is the outage class this ordering exists to prevent')
})

test('ensure-ingest-running is wired with always()', async () => {
  const ensure = jobBlock(await read('.github/workflows/deploy.yml'), 'ensure-ingest-running')
  // Without always() the job is SKIPPED when either deploy fails, leaving the worker
  // stopped by the API deploy's migration step -- a green-looking failed deploy and
  // a silently dead worker. always() is what restarts it in the failure lane too.
  assert.match(ensure, /^ {4}if:.*\balways\(\)/m, "ensure-ingest-running must carry `always()` in its `if:` -- without it a failed deploy skips the restart and leaves the ingest worker stopped")
})

test('both deploy entry points acquire the production lock without re-entering it', async () => {
  const ci = await read('.github/workflows/ci.yml')
  const deploy = await read('.github/workflows/deploy.yml')
  const caller = jobBlock(ci, 'deploy')
  assert.match(caller, /concurrency:\s*\r?\n\s+group: deploy-production\s*\r?\n\s+cancel-in-progress: false/)
  assert.match(caller, /if: github.event_name == 'push' && github.ref == 'refs\/heads\/main'/)
  assert.match(caller, /uses: \.\/\.github\/workflows\/deploy.yml/)
  assert.match(deploy, /group: \$\{\{ github.event_name == 'workflow_dispatch' && 'deploy-production' \|\| format\('deploy-called-\{0\}', github.run_id\) \}\}/)
  assert.match(deploy, /^  cancel-in-progress: false$/m)
  assert.match(ci, /^  cancel-in-progress: false$/m)
})
