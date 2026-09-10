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
