import assert from 'node:assert/strict'
import { spawnSync } from 'node:child_process'
import { createHash } from 'node:crypto'
import { mkdtemp, readFile, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
// Optional explicit installed DLL; never selected by bundle content. No implicit build.
const cli = path.resolve(process.argv[2] ?? path.join(root, 'src/AiObservatory.Audit.Cli/bin/Release/net10.0/AiObservatory.Audit.Cli.dll'))
const fixture = path.join(root, 'tests/fixtures/audit')
const expectedHash = '5a194bb9f85af8731a076e0fbea4baad6a57e0f8a32c5c746f3cea90d9a78f5b'
const expected = await readFile(path.join(fixture, 'golden-v1.result.json'))
const hash = bytes => createHash('sha256').update(bytes).digest('hex')
assert.equal(hash(expected), expectedHash, 'reviewed golden bytes drifted')
const temporary = await mkdtemp(path.join(tmpdir(), 'audit-replay-smoke-'))
try {
  const results = []
  for (const name of ['first', 'second']) {
    const output = path.join(temporary, name)
    const replay = spawnSync('dotnet', [cli, 'replay', path.join(fixture, 'golden-v1.zip'), '--output', output, '--verifier-version', '1.0.0'], {
      cwd: root, encoding: 'utf8', timeout: 30000,
    })
    assert.ifError(replay.error)
    assert.equal(replay.status, 3, `expected honest Incomplete verdict: ${replay.stderr}`)
    const actual = await readFile(path.join(output, 'result.json'))
    assert.deepEqual(actual, expected, 'canonical result differs from committed golden')
    assert.equal(hash(actual), expectedHash)
    assert.equal(await readFile(path.join(output, 'result.sha256'), 'utf8'), expectedHash + '\n')
    results.push(actual)
  }
  assert.deepEqual(results[0], results[1], 'independent replays drifted')
  console.log(`Synthetic replay qualified twice: Incomplete (exit 3); SHA-256 ${expectedHash}`)
} finally {
  await rm(temporary, { recursive: true, force: true })
}
