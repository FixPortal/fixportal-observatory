// Builds the web image and checks the key-injecting proxy: the admin key reaches the API only for
// requests the SPA made itself, a Host outside the allow-list is dropped, an unset key still starts
// nginx, and an unembeddable key is refused with a message. Needs Docker.
// Usage: node scripts/web-proxy-smoke.mjs
// It removes only what this run created: the container whose ID `docker run -d` printed and its
// own temp directory. The image is built untagged and run by ID, so concurrent checkouts do not
// test each other's image, and it is left to Docker's layer cache.
import { execFileSync } from 'node:child_process'
import { randomBytes } from 'node:crypto'
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { request } from 'node:http'
import { tmpdir } from 'node:os'
import { basename, join, resolve, sep } from 'node:path'

const port = 18080 + (process.pid % 1000)
const run = (args) => execFileSync('docker', args, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] })

const dir = mkdtempSync(join(tmpdir(), 'obs-proxy-'))
const failures = []
const expect = (label, actual, wanted) => {
  if (actual !== wanted) failures.push(label + ': got ' + JSON.stringify(actual) + ', wanted ' + JSON.stringify(wanted))
}

// Sends a request with an exact Host header (fetch will not). A dropped connection (nginx `return 444`)
// resolves as null.
const rawGet = (host, headers = {}) =>
  new Promise((done, fail) => {
    const req = request({ host: '127.0.0.1', port, path: '/api/x', method: 'GET', headers: { ...headers, Host: host } }, (res) => {
      let body = ''
      res.on('data', (c) => (body += c))
      res.on('end', () => done(body))
    })
    req.on('error', (e) => (e.code === 'ECONNRESET' ? done(null) : fail(e)))
    req.end()
  })

let containerId = ''
try {
  const echoConf = join(dir, 'echo.conf')
  writeFileSync(echoConf, 'server { listen 8080; location / { return 200 "key=[$http_x_observatory_key]"; } }\n')
  const iidFile = join(dir, 'image.id')
  run(['build', '-q', '--iidfile', iidFile, 'src/AiObservatory.Web'])
  const image = readFileSync(iidFile, 'utf8').trim()
  console.log('image ' + image)

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

  // An allow-list that cannot sit in server_name: refused, naming the setting.
  try {
    run(['run', '--rm', '--add-host', 'api:127.0.0.1', '-e', 'OBSERVATORY_ALLOWED_HOSTS=a;b', image, 'nginx', '-t'])
    failures.push('bad host list: container started')
  } catch (e) {
    expect('bad host list message', /OBSERVATORY_ALLOWED_HOSTS must be/.test(String(e.stderr) + String(e.stdout)), true)
  }

  const name = 'observatory-web-proxy-smoke-' + randomBytes(6).toString('hex')
  containerId = run([
    'run', '-d', '--rm', '--name', name, '--add-host', 'api:127.0.0.1',
    '-v', echoConf + ':/etc/nginx/conf.d/echo.conf:ro',
    '-e', 'OBSERVATORY_API_KEY=sekret', '-p', '127.0.0.1:' + port + ':80', image,
  ]).trim()
  console.log('container ' + containerId)
  const post = async (headers, method = 'POST') => {
    for (let i = 0; i < 20; i++) {
      try {
        return await (await fetch('http://localhost:' + port + '/api/x', { method, headers })).text()
      } catch {
        await new Promise((r) => setTimeout(r, 500))
      }
    }
    throw new Error('proxy never answered')
  }
  // No Sec-Fetch-Site and no Origin: trusted for reads only, never a write (nginx.conf.template).
  expect('no browser headers, GET', await post({}, 'GET'), 'key=[sekret]')
  expect('no browser headers, POST', await post({}), 'key=[]')
  expect('same-origin', await post({ 'Sec-Fetch-Site': 'same-origin' }), 'key=[sekret]')
  expect('none, POST', await post({ 'Sec-Fetch-Site': 'none' }), 'key=[]')
  expect('none, GET', await post({ 'Sec-Fetch-Site': 'none' }, 'GET'), 'key=[sekret]')
  expect('cross-site', await post({ 'Sec-Fetch-Site': 'cross-site', Origin: 'http://evil.example' }), 'key=[]')
  expect('same-site', await post({ 'Sec-Fetch-Site': 'same-site' }), 'key=[]')
  expect('old browser, foreign Origin', await post({ Origin: 'http://evil.example' }), 'key=[]')
  expect('old browser, own Origin', await post({ Origin: 'http://localhost:' + port }), 'key=[sekret]')
  expect('old browser, own Origin over TLS', await post({ Origin: 'https://localhost:' + port }), 'key=[sekret]')
  expect('caller key kept', await post({ 'Sec-Fetch-Site': 'cross-site', 'X-Observatory-Key': 'viewer' }), 'key=[viewer]')
  // DNS rebinding: the page's own Host re-resolved to loopback looks same-origin, so the Host is refused.
  expect('127.0.0.1 allowed', await rawGet('127.0.0.1:' + port, { 'Sec-Fetch-Site': 'same-origin' }), 'key=[sekret]')
  expect('foreign Host, same-origin', await rawGet('attacker.example:' + port, { 'Sec-Fetch-Site': 'same-origin' }), null)
  expect('foreign Host, own Origin', await rawGet('attacker.example:' + port, { Origin: 'http://attacker.example:' + port }), null)
  expect('foreign Host, no headers', await rawGet('attacker.example'), null)
} finally {
  if (containerId) {
    try {
      run(['rm', '-f', containerId])
    } catch (e) {
      // The container is --rm and may already be gone; anything else is worth seeing.
      if (!/No such container/i.test(String(e.stderr))) console.error('cleanup failed: ' + e.stderr)
    }
  }
  // Delete only the directory mkdtempSync returned for this run: inside the OS temp dir and named by us.
  const owned = resolve(dir)
  if (owned.startsWith(resolve(tmpdir()) + sep) && basename(owned).startsWith('obs-proxy-')) {
    console.log('removing ' + owned)
    rmSync(owned, { recursive: true, force: true })
  } else {
    console.error('refusing to remove unexpected path ' + owned)
  }
}

if (failures.length) {
  console.error(failures.join('\n'))
  process.exit(1)
}
console.log('web proxy smoke: ok')
