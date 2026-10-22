import assert from 'node:assert/strict'
import { spawnSync } from 'node:child_process'
import { createHash } from 'node:crypto'
import { mkdtemp, readFile, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
// Optional explicit installed DLL; never selected by bundle content. No implicit build.
const cli = path.resolve(
    process.argv[2] ??
        path.join(
            root,
            'src/AiObservatory.Audit.Cli/bin/Release/net10.0/AiObservatory.Audit.Cli.dll',
        ),
)
const fixture = path.join(root, 'tests/fixtures/audit')
const hash = (bytes) => createHash('sha256').update(bytes).digest('hex')
const oldHash = '5a194bb9f85af8731a076e0fbea4baad6a57e0f8a32c5c746f3cea90d9a78f5b'
const oldResult = await readFile(path.join(fixture, 'golden-v1.result.json'))
const oldBundle = await readFile(path.join(fixture, 'golden-v1.zip'))
assert.equal(hash(oldResult), oldHash, 'reviewed v1 result drifted')
assert.equal(hash(oldBundle), 'adbf69fa7d1b54d9752ab6fd15cb7b7d62f8ac552603ef35e6cb04dbd841f014')
const expected = await readFile(path.join(fixture, 'golden-v2.result.json'))
assert.equal(hash(expected), '5469f42f0643b857f3427dec79ddcb762abe54793404cfa4423bf94046db9677')
assert.equal(
    hash(await readFile(path.join(fixture, 'golden-v2.zip'))),
    '2eb839718c9ad11798d587cd0185c99549d2c5cc790b3f4c3cb8b3733997f4f4',
)
const temporary = await mkdtemp(path.join(tmpdir(), 'audit-replay-smoke-'))
function run(name, version, extra = []) {
    const output = path.join(temporary, name)
    const replay = spawnSync(
        'dotnet',
        [cli, 'replay', path.join(fixture, `golden-v${version}.zip`), '--output', output, ...extra],
        {
            cwd: root,
            encoding: 'utf8',
            timeout: 30000,
        },
    )
    assert.ifError(replay.error)
    return { output, replay }
}
try {
    const rejected = run('rejected', 1)
    assert.equal(rejected.replay.status, 4, 'old identity must require explicit reevaluation')
    await assert.rejects(readFile(path.join(rejected.output, 'result.json')), { code: 'ENOENT' })
    const reevaluated = run('reevaluated', 1, ['--reevaluate'])
    assert.equal(reevaluated.replay.status, 3)
    const retained = await readFile(path.join(reevaluated.output, 'recorded-result.json'))
    assert.deepEqual(retained, oldResult)
    assert.equal(hash(retained), oldHash)
    const evaluation = JSON.parse(
        await readFile(path.join(reevaluated.output, 'evaluation.json'), 'utf8'),
    )
    assert.equal(evaluation.mode, 'reevaluation')
    assert.equal(evaluation.recordedResultSha256, oldHash)
    assert.equal(evaluation.recordedInputDigest, JSON.parse(oldResult).inputContentDigest)
    assert.equal(evaluation.verifierVersion, '1.1.0')
    assert.equal(evaluation.ruleVersion, '2')
    assert.notEqual(evaluation.evaluationInputDigest, evaluation.recordedInputDigest)
    assert.equal(
        evaluation.evaluationIdentity,
        hash(await readFile(path.join(reevaluated.output, 'result.json'))),
    )
    const results = []
    for (const name of ['first', 'second']) {
        const { output, replay } = run(name, 2)
        assert.equal(replay.status, 0, `expected scoped Matched verdict: ${replay.stderr}`)
        const actual = await readFile(path.join(output, 'result.json'))
        assert.deepEqual(actual, expected, 'canonical v2 result differs from committed fixture')
        assert.equal(
            await readFile(path.join(output, 'result.sha256'), 'utf8'),
            hash(expected) + '\n',
        )
        results.push(actual)
    }
    assert.deepEqual(results[0], results[1], 'independent replays drifted')
    assert.deepEqual(await readFile(path.join(fixture, 'golden-v1.zip')), oldBundle)
    assert.deepEqual(await readFile(path.join(fixture, 'golden-v1.result.json')), oldResult)
    console.log(
        `V1 rejected by default; linked reevaluation retains original bytes. Synthetic v2 replay qualified twice: Matched (exit 0); SHA-256 ${hash(expected)}`,
    )
} finally {
    await rm(temporary, { recursive: true, force: true })
}
