// Builds the web image and checks the key-injecting proxy: the admin key reaches the API only for
// requests the SPA made itself, an unset key still starts nginx, and an unembeddable key is refused
// with a message. Needs Docker. Usage: node scripts/web-proxy-smoke.mjs
import { execFileSync } from 'node:child_process'
import { mkdtempSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

const image = 'observatory-web-proxy-smoke'
const name = image + '-' + process.pid
const port = 18080 + (process.pid % 1000)
const run = (args) => execFileSync('docker', args, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] })

const dir = mkdtempSync(join(tmpdir(), 'obs-proxy-'))
const echoConf = join(dir, 'echo.conf')
writeFileSync(echoConf, 'server { listen 8080; location / { return 200 "key=[$http_x_observatory_key]"; } }\n')

const failures = []
const expect = (label, actual, wanted) => {
  if (actual !== wanted) failures.push(label + ': got ' + JSON.stringify(actual) + ', wanted ' + JSON.stringify(wanted))
}

run(['build', '-q', '-t', image, 'src/AiObservatory.Web'])

// Unset key: nginx must still start (the API then answers 401).
try {
  run(['run', '--rm', '--add-host', 'api:127.0.0.1', image, 'nginx', '-t'])
} catch (e) {
  failures.push('unset key: nginx -t failed: ' + e.stderr)
}

// A key with a double quote: refused, naming the setting.
try {
  run(['run', '--rm', '--add-host', 'api:127.0.0.1', '-e', 'OBSERVATORY_API_KEY=ab"cd', image, 'nginx', '-t'])
  failures.push('bad key: container started')
} catch (e) {
  expect('bad key message', /OBSERVATORY_API_KEY contains a character/.test(String(e.stderr) + String(e.stdout)), true)
}

try {
  run([
    'run', '-d', '--rm', '--name', name, '--add-host', 'api:127.0.0.1',
    '-v', echoConf + ':/etc/nginx/conf.d/echo.conf:ro',
    '-e', 'OBSERVATORY_API_KEY=sekret', '-p', port + ':80', image,
  ])
  const post = async (headers) => {
    for (let i = 0; i < 20; i++) {
      try {
        return await (await fetch('http://localhost:' + port + '/api/x', { method: 'POST', headers })).text()
      } catch {
        await new Promise((r) => setTimeout(r, 500))
      }
    }
    throw new Error('proxy never answered')
  }
  expect('no browser headers', await post({}), 'key=[sekret]')
  expect('same-origin', await post({ 'Sec-Fetch-Site': 'same-origin' }), 'key=[sekret]')
  expect('cross-site', await post({ 'Sec-Fetch-Site': 'cross-site', Origin: 'http://evil.example' }), 'key=[]')
  expect('same-site', await post({ 'Sec-Fetch-Site': 'same-site' }), 'key=[]')
  expect('old browser, foreign Origin', await post({ Origin: 'http://evil.example' }), 'key=[]')
  expect('old browser, own Origin', await post({ Origin: 'http://localhost:' + port }), 'key=[sekret]')
  expect('caller key kept', await post({ 'Sec-Fetch-Site': 'cross-site', 'X-Observatory-Key': 'viewer' }), 'key=[viewer]')
} finally {
  try {
    run(['rm', '-f', name])
  } catch (e) {
    // The container is --rm and may already be gone; anything else is worth seeing.
    if (!/No such container/i.test(String(e.stderr))) console.error('cleanup failed: ' + e.stderr)
  }
}

if (failures.length) {
  console.error(failures.join('\n'))
  process.exit(1)
}
console.log('web proxy smoke: ok')
