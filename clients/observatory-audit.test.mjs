import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, mkdir, writeFile, readFile, rm, unlink, symlink, stat } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawnSync, execFileSync } from 'node:child_process';
import { extractClaudeEvidence } from './audit/claude-evidence.mjs';
import { extractCodexEvidence } from './audit/codex-evidence.mjs';
import { loadIdentity, identityContext } from './audit/identity.mjs';
import { parseLossless } from './audit/lossless-json.mjs';
import { canonical, utcTimestamp, sha256 } from './audit/protocol.mjs';
import * as protocol from './audit/protocol.mjs';
import { pollOnce } from './audit/poll.mjs';
import { captureEvidence, writeCapture } from './observatory-audit.mjs';

const machineId = '11111111-1111-1111-1111-111111111111';
const secret = 'PLANTED private prompt sk-secret /home/private/history';
const context = (file = 'file-a') => identityContext({ key: Buffer.alloc(32, 7), generation: 'generation-a' }, { machineId, sourceId: 'claude-local', fileId: file });
const claude = (extra = {}) => JSON.stringify({ type: 'assistant', sessionId: 'session-a', uuid: 'row-a', parentUuid: 'parent-a', timestamp: '2026-10-20T12:00:00.1234567Z', message: { id: 'message-a', model: 'claude-sonnet-4-6', content: secret, usage: { input_tokens: 12, output_tokens: 3, cache_creation_input_tokens: 8, cache_read_input_tokens: 4, cache_creation: { ephemeral_5m_input_tokens: 5, ephemeral_1h_input_tokens: 3 }, service_tier: 'standard', inference_geo: 'us' } }, ...extra });
const codex = () => [
  { type: 'session_meta', timestamp: '2026-10-18T23:00:00Z', payload: { id: 'session-x', model_provider: 'openai', parent_session_id: 'session-parent', instructions: secret } },
  { type: 'turn_context', timestamp: '2026-10-18T23:00:00Z', payload: { model: 'gpt-5-codex', service_tier: 'priority', cwd: secret } },
  { type: 'event_msg', timestamp: '2026-10-18T23:59:00Z', payload: { type: 'token_count', info: { total_token_usage: { input_tokens: 100, cached_input_tokens: 20, output_tokens: 30, reasoning_output_tokens: 10, total_tokens: 130 }, last_token_usage: { input_tokens: 100, cached_input_tokens: 20, output_tokens: 30, reasoning_output_tokens: 10, total_tokens: 130 }, model_context_window: 200000 }, text: secret } },
  { type: 'event_msg', timestamp: '2026-10-20T01:00:00Z', payload: { type: 'token_count', info: { total_token_usage: { input_tokens: 150, cached_input_tokens: 30, output_tokens: 50, reasoning_output_tokens: 20, total_tokens: 200 }, last_token_usage: { input_tokens: 50, cached_input_tokens: 10, output_tokens: 20, reasoning_output_tokens: 10, total_tokens: 70 } } } },
].map(JSON.stringify).join('\n');

async function workspace(t) {
  const dir = await mkdtemp(join(tmpdir(), 'observatory-audit-synthetic-'));
  t.after(() => rm(dir, { recursive: true, force: true }));
  const root = join(dir, 'source'); await mkdir(root);
  const config = { identityDirectory: join(dir, 'identity'), sources: [{ sourceId: 'claude-local', tool: 'claude', roots: [root] }] };
  const request = { captureId: '22222222-2222-2222-2222-222222222222', requestId: '33333333-3333-3333-3333-333333333333', machineId, scope: { from: '2026-10-19', to: '2026-10-21', sources: [{ machineId, sourceId: 'claude-local' }], requestedChecks: ['sourceAccounting'], excludedChecks: [], allowPartial: false, predecessorRunId: null, evaluationOfRunId: null } };
  return { dir, root, config, request };
}

test('Claude copies retain native inclusive/subset counters and exact integer lexemes', () => {
  const line = claude().replace('"input_tokens":12', '"input_tokens":9007199254740993');
  const result = extractClaudeEvidence(`${line}\n${line}\n`, context());
  assert.equal(result.records.length, 2);
  assert.equal(result.records[0].counters.input_tokens, 9007199254740993n);
  assert.equal(result.records[0].counters.ephemeral_5m_input_tokens, 5n);
  assert.equal(result.records[0].counters.cache_creation_input_tokens, 8n);
  assert.equal(result.records[1].messageId, result.records[0].messageId);
  assert.equal(result.records[1].sequence, 2n);
  assert.equal(result.records[0].timestamp, '2026-10-20T12:00:00.1234567Z');
  assert.equal(result.records[0].pricing.sourceServiceTier, 'standard');
  assert.equal(result.records[0].pricing.inferenceGeo, 'us');
  assert.equal(result.records[0].closure.state, 'unknown');
  assert.doesNotMatch(canonical(result), /PLANTED|sk-secret|home\/private/);
});

test('source version map fixes the public producer contract', () => {
  assert.deepEqual(protocol.SOURCE_VERSIONS, { claude: '2', codex: '1' });
  assert.equal(Object.isFrozen(protocol.SOURCE_VERSIONS), true);
  assert.equal(extractClaudeEvidence(claude(), context()).records[0].sourceVersion, protocol.SOURCE_VERSIONS.claude);
  assert.equal(extractCodexEvidence(codex(), context()).records[0].sourceVersion, protocol.SOURCE_VERSIONS.codex);
});

test('prospective captures advertise exact emitted versions and remain facts only', async t => {
  const { root, config, request } = await workspace(t);
  await writeFile(join(root, 'native.jsonl'), claude());
  const capture = await captureEvidence(request, config);
  assert.equal(capture.manifest.extractorVersion, '1.1.0');
  assert.deepEqual(capture.manifest.sourceFormats, [{ sourceId: 'claude-local', format: 'claude-jsonl', version: '2' }]);
  assert.ok(capture.records.every(r => r.sourceVersion === '2'));
  assert.equal(capture.records[0].pricing.context, undefined);
  assert.deepEqual(Object.keys(capture.records[0].counters).sort(), ['counterKind', 'input_tokens', 'output_tokens', 'cache_creation_input_tokens', 'cache_read_input_tokens', 'ephemeral_5m_input_tokens', 'ephemeral_1h_input_tokens', 'thinking_tokens', 'thinking_output_tokens'].sort());
  const closure = extractClaudeEvidence(JSON.stringify({ type: 'session_end', timestamp: '2026-10-21T02:00:00Z' }), context());
  assert.equal(closure.records[0].sourceVersion, '2');
  config.sources[0].tool = 'codex';
  await writeFile(join(root, 'native.jsonl'), codex());
  const codexCapture = await captureEvidence(request, config);
  assert.deepEqual(codexCapture.manifest.sourceFormats, [{ sourceId: 'claude-local', format: 'codex-jsonl', version: '1' }]);
  assert.ok(codexCapture.records.every(r => r.sourceVersion === '1'));
});

test('restart retains a sealed v1 package without upgrading version or bytes', async t => {
  const { dir, root, config, request } = await workspace(t);
  await writeFile(join(root, 'native.jsonl'), claude());
  const capture = await captureEvidence(request, config);
  const records = capture.records.map(r => ({ ...r, sourceVersion: '1' }));
  const ndjson = Buffer.from(records.map(canonical).join('\n') + '\n');
  const manifest = { ...capture.manifest, extractorVersion: '1.0.0', sourceFormats: [{ sourceId: 'claude-local', format: 'claude-jsonl', version: '1' }], chunkHashes: [sha256(ndjson)] };
  const token = '44444444-4444-4444-4444-444444444444';
  const stateDirectory = join(dir, 'retained'); await mkdir(stateDirectory);
  const directory = join(stateDirectory, request.requestId + '-' + token);
  await writeCapture({ ...capture, manifest, records, chunks: [{ index: 0, sha256: sha256(ndjson), ndjson }] }, directory);
  const before = await readFile(join(directory, 'manifest.json'));
  await writeFile(join(root, 'native.jsonl'), 'changed invalid native input');
  const uploads = [];
  const transport = { async request(path, options) {
    if (path.endsWith('/claim')) return { status: 200, body: JSON.stringify({ schemaVersion: 1, requestId: request.requestId, machineId, leaseToken: token, expiresAt: new Date(Date.now() + 300000).toISOString(), scope: request.scope }) };
    uploads.push(options.body); return { status: 200, body: '{}' };
  } };
  assert.equal(await pollOnce({ ...config, machineId, stateDirectory }, { transport }), 'sealed');
  assert.deepEqual(uploads[0], ndjson);
  const seal = JSON.parse(uploads[1]);
  assert.equal(seal.manifest.extractorVersion, '1.0.0');
  assert.equal(seal.manifest.sourceFormats[0].version, '1');
  assert.equal(canonical(seal.manifest), before.toString());
  assert.deepEqual(await readFile(join(directory, 'manifest.json')), before);
  assert.deepEqual(await readFile(join(directory, 'chunks', '000000.ndjson')), ndjson);
});

test('session and parent identities share domains across files without merging message and session domains', () => {
  const parent = extractClaudeEvidence(claude({ sessionId: 'parent-a' }), context('file-a')).records[0];
  const child = extractClaudeEvidence(claude({ sessionId: 'child', parentSessionId: 'parent-a', agentId: 'agent' }), context('file-b')).records[0];
  assert.equal(child.parentId, parent.sessionId);
  assert.equal(child.messageId, parent.messageId);
  assert.notEqual(child.sessionId, child.agentId);
});

test('Codex keeps cumulative and last counters, pre-window observations, metadata and parent identity', () => {
  const result = extractCodexEvidence(codex(), context());
  const usage = result.records.filter(r => r.counters);
  assert.equal(usage.length, 2);
  assert.equal(usage[0].timestamp, '2026-10-18T23:59:00.0000000Z');
  assert.equal(usage[1].counters.input_tokens, 150n);
  assert.equal(usage[1].counters.last_token_usage.input_tokens, 50n);
  assert.equal(usage[1].counters.reasoning_output_tokens, 20n);
  assert.equal(usage[0].pricing.contextTokens, 200000n);
  assert.equal(usage[1].provider, 'openai');
  assert.equal(usage[1].pricing.sourceServiceTier, 'priority');
  assert.equal(usage[1].closure.state, 'unknown');
  assert.doesNotMatch(canonical(result), /PLANTED|sk-secret/);
});

test('explicit session end is retained, turn completion alone never closes a session', () => {
  const turn = { type: 'event_msg', timestamp: '2026-10-21T01:00:00Z', payload: { type: 'task_complete' } };
  const result = extractCodexEvidence(`${codex()}\n${JSON.stringify(turn)}`, context());
  assert.ok(result.records.every(r => r.closure.state === 'unknown'));
  const end = { type: 'session_end', timestamp: '2026-10-21T02:00:00Z' };
  const closed = extractCodexEvidence(`${codex()}\n${JSON.stringify(end)}`, context());
  assert.equal(closed.records.at(-1).kind, 'sessionClosure');
  assert.equal(closed.records.at(-1).closure.state, 'explicitlyClosed');
});

for (const bad of ['-1', '1.5', '1e3', '9223372036854775808', `"${secret}"`, '{}']) {
  test(`invalid counter category ${['-1','1.5','1e3','9223372036854775808'].includes(bad) ? bad : 'private-shape'} is sanitized`, () => {
    const result = extractClaudeEvidence(claude().replace('"input_tokens":12', `"input_tokens":${bad}`), context());
    assert.equal(result.records.length, 0);
    assert.ok(result.diagnostics.length > 0);
    assert.doesNotMatch(canonical(result), /PLANTED|sk-secret|private/);
  });
}

test('duplicate properties, malformed JSON, deep/oversized values and unsupported signatures are incomplete', () => {
  for (const text of [claude().replace('"input_tokens":12', '"input_tokens":12,"input_tokens":99'), `{"${secret}":`, '['.repeat(66) + '0' + ']'.repeat(66), ' '.repeat(1024 * 1024 + 1), '{"type":"future_usage","usage":{"input_tokens":4}}']) {
    const result = extractClaudeEvidence(text, context());
    assert.equal(result.records.length, 0);
    assert.ok(result.diagnostics.length > 0);
    assert.doesNotMatch(canonical(result), /PLANTED|sk-secret/);
  }
  assert.throws(() => parseLossless('{"x":1,"x":2}'), { message: 'duplicate_property' });
});

test('timestamp canonicalization preserves seven digits and qualifies excess precision', () => {
  assert.equal(utcTimestamp('2026-10-20T13:00:00.1234567+01:00'), '2026-10-20T12:00:00.1234567Z');
  assert.throws(() => utcTimestamp('2026-10-20T12:00:00.12345678Z'), { message: 'timestamp_precision' });
  assert.throws(() => utcTimestamp('2026-02-30T12:00:00Z'), { message: 'invalid_timestamp' });
});

test('local key persists privately and key loss creates a new identity generation', async t => {
  const { config } = await workspace(t);
  const first = await loadIdentity(config.identityDirectory);
  const again = await loadIdentity(config.identityDirectory);
  assert.equal(again.generation, first.generation);
  assert.equal(identityContext(first, { machineId, sourceId: 'x', fileId: 'a' }).id('session', 'one'), identityContext(again, { machineId, sourceId: 'x', fileId: 'b' }).id('session', 'one'));
  await unlink(join(config.identityDirectory, 'identity.json'));
  const next = await loadIdentity(config.identityDirectory);
  assert.notEqual(first.generation, next.generation);
  assert.notEqual(identityContext(first, { machineId, sourceId: 'x', fileId: 'a' }).id('session', 'one'), identityContext(next, { machineId, sourceId: 'x', fileId: 'a' }).id('session', 'one'));
  if (process.platform !== 'win32') assert.equal((await stat(join(config.identityDirectory, 'identity.json'))).mode & 0o777, 0o600);
});

test('capture retains private-free chunks, native baselines, unknown closure and manifest hashes', async t => {
  const { dir, root, config, request } = await workspace(t);
  await writeFile(join(root, 'synthetic.jsonl'), claude());
  const result = await captureEvidence(request, config);
  assert.equal(result.manifest.recordCount, 1n);
  assert.equal(result.manifest.readFiles, 1n);
  assert.equal(result.records[0].closure.state, 'unknown');
  assert.equal(result.chunks.length, 1);
  assert.match(result.chunks[0].ndjson.toString(), /900|input_tokens/);
  assert.ok(result.chunks[0].ndjson.toString().endsWith('\n'));
  const output = join(dir, 'capture'); await writeCapture(result, output);
  const wire = await readFile(join(output, 'manifest.json'), 'utf8');
  assert.doesNotMatch(wire + result.chunks[0].ndjson, /PLANTED|sk-secret|synthetic\.jsonl|source\\|source\//);
  assert.equal(JSON.parse(wire).recordCount, 1);
});

test('empty scan is explicitly incomplete', async t => {
  const { config, request } = await workspace(t);
  const result = await captureEvidence(request, config);
  assert.equal(result.complete, false);
  assert.ok(result.manifest.diagnostics.some(d => d.code === 'no_usage_records'));
});

test('changing file is retried twice and produces no authoritative partial records', async t => {
  const { root, config, request } = await workspace(t);
  const file = join(root, 'changing.jsonl'); await writeFile(file, claude());
  let reads = 0;
  const result = await captureEvidence(request, config, { readFile: async path => { const text = await readFile(path, 'utf8'); await writeFile(path, text + '\n'); reads++; return text; } });
  assert.equal(reads, 3);
  assert.equal(result.manifest.changedFiles, 1n);
  assert.equal(result.manifest.recordCount, 0n);
  assert.equal(result.complete, false);
});

test('outside-root symlink or junction never supplies records', async t => {
  const { dir, root, config, request } = await workspace(t);
  const outside = join(dir, 'outside'); await mkdir(outside); await writeFile(join(outside, 'private.jsonl'), claude());
  await symlink(outside, join(root, 'escape'), process.platform === 'win32' ? 'junction' : 'dir');
  const result = await captureEvidence(request, config);
  assert.equal(result.records.length, 0);
  assert.equal(result.complete, false);
  assert.ok(result.manifest.diagnostics.some(d => d.code === 'unsafe_source_path'));
});

test('CLI rejects errors without echoing paths or request contents', t => {
  const result = spawnSync(process.execPath, ['clients/observatory-audit.mjs', 'capture', '--request', secret, '--output', secret], { encoding: 'utf8' });
  assert.equal(result.status, 4);
  assert.doesNotMatch(result.stdout + result.stderr, /PLANTED|sk-secret|home\/private/);
});

test('native Claude cost is a lossless canonical decimal string', () => {
  const text = claude().replace('"type":"assistant"', '"costUSD":1.230000000000000001,"type":"assistant"');
  const record = extractClaudeEvidence(text, context()).records[0];
  assert.equal(record.cost, '1.230000000000000001');
  assert.equal(record.currency, 'USD');
});

test('malformed known subset object is rejected even alongside valid main counters', () => {
  const text = claude().replace('"cache_creation":{"ephemeral_5m_input_tokens":5,"ephemeral_1h_input_tokens":3}', '"cache_creation":5');
  const result = extractClaudeEvidence(text, context());
  assert.equal(result.records.length, 0);
  assert.ok(result.diagnostics.some(d => d.code === 'invalid_usage_shape'));
});

test('records sort by numeric source sequence and capture retains pre-window Codex baseline', async t => {
  const { root, config, request } = await workspace(t);
  config.sources[0].tool = 'codex';
  await writeFile(join(root, 'baseline.jsonl'), codex());
  const result = await captureEvidence(request, config);
  assert.equal(result.records.find(r => r.counters).timestamp, '2026-10-18T23:59:00.0000000Z');
  config.sources[0].tool = 'claude';
  await writeFile(join(root, 'baseline.jsonl'), Array.from({ length: 12 }, () => claude()).join('\n'));
  const ordered = await captureEvidence(request, config);
  assert.deepEqual(ordered.records.map(r => r.sequence), [1n, 2n, 3n, 4n, 5n, 6n, 7n, 8n, 9n, 10n, 11n, 12n]);
});

test('source selection is strict and limit failures retain visibly incomplete captures', async t => {
  const { root, config, request } = await workspace(t);
  await assert.rejects(() => captureEvidence({ ...request, unknown: secret }, config));
  await assert.rejects(() => captureEvidence(request, { ...config, limits: { maxRecords: 1000001 } }));
  await writeFile(join(root, 'limited.jsonl'), [claude(), claude()].join('\n'));
  const capture = await captureEvidence(request, { ...config, limits: { maxRecords: 1 } });
  assert.equal(capture.complete, false);
  assert.equal(capture.records.length, 1);
  assert.ok(capture.manifest.diagnostics.some(d => d.code === 'capture_limit'));
});

test('chunk boundaries and local raw hashes bind retained bytes without leaking source paths', async t => {
  const { root, config, request } = await workspace(t);
  await writeFile(join(root, 'chunked.jsonl'), [claude(), claude(), claude()].join('\n'));
  const result = await captureEvidence(request, { ...config, includeRawHashes: true, limits: { maxChunkBytes: 2000 } });
  assert.equal(result.chunks.length, 3);
  assert.equal(result.local.rawHashes.length, 1);
  assert.doesNotMatch(canonical(result.local), /chunked|source|PLANTED/);
  const hashes = await import('node:crypto');
  for (const chunk of result.chunks) assert.equal(chunk.sha256, hashes.createHash('sha256').update(chunk.ndjson).digest('hex'));
});

test('key permissions remove an existing broad explicit ACL on Windows', async t => {
  const { dir, config } = await workspace(t);
  if (process.platform !== 'win32') return; // POSIX mode is checked by the cross-platform key test.
  await mkdir(config.identityDirectory);
  execFileSync('icacls.exe', [config.identityDirectory, '/grant', '*S-1-1-0:(OI)(CI)R'], { stdio: 'pipe', windowsHide: true });
  await loadIdentity(config.identityDirectory);
  const acl = join(dir, 'synthetic-acl.txt');
  execFileSync('icacls.exe', [join(config.identityDirectory, 'identity.json'), '/save', acl], { stdio: 'pipe', windowsHide: true });
  assert.doesNotMatch((await readFile(acl)).toString('utf16le'), /;;;WD\)/);
});

test('optional local raw-file hash includes original UTF-8 BOM bytes', async t => {
  const { root, config, request } = await workspace(t);
  const bytes = Buffer.concat([Buffer.from([0xef, 0xbb, 0xbf]), Buffer.from(claude())]);
  await writeFile(join(root, 'bom.jsonl'), bytes);
  const result = await captureEvidence(request, { ...config, includeRawHashes: true });
  const { createHash } = await import('node:crypto');
  assert.equal(result.records.length, 1);
  assert.equal(result.local.rawHashes[0].sha256, createHash('sha256').update(bytes).digest('hex'));
});

for (const [name, value] of [['null', null], ['omitted', undefined], ['empty', '']]) {
  for (const target of ['request', 'configuration', 'both']) {
    test(`required source ID ${name} in ${target} is rejected with sanitized validation`, async t => {
      const { config, request, root } = await workspace(t);
      await writeFile(join(root, 'synthetic.jsonl'), claude());
      if (target !== 'configuration') request.scope.sources[0].sourceId = value;
      if (target !== 'request') config.sources[0].sourceId = value;
      if (value === undefined) {
        if (target !== 'configuration') delete request.scope.sources[0].sourceId;
        if (target !== 'request') delete config.sources[0].sourceId;
      }
      const expected = target === 'configuration' ? 'invalid_capture_configuration' : 'invalid_capture_request';
      await assert.rejects(() => captureEvidence(request, config), { message: expected });
    });
  }
}

for (const failedType of ['turn_context', 'session_meta']) {
  test(`rejected Codex ${failedType} invalidates stale facts until valid context recovers`, () => {
    const native = codex().split('\n').map(line => JSON.parse(line));
    const originalTurn = { ...native[1], payload: { ...native[1].payload, speed: 'fast', inference_geo: 'us', region: 'us', model_context_window: 200000 } };
    const usage = native[2];
    const rejected = { type: failedType, timestamp: '2026-10-20T02:00:00Z', payload: { id: 'session-rejected', model: secret, model_provider: 'deepseek', service_tier: 'flex' } };
    const recovered = { type: failedType, timestamp: '2026-10-20T03:00:00Z', payload: { id: 'session-recovered', parent_session_id: 'parent-recovered', model: 'glm-4.6', model_provider: 'zai', service_tier: 'flex', speed: 'fast', inference_geo: 'eu', region: 'eu', model_context_window: 100000 } };
    const withoutInlineDimensions = { ...usage, payload: { ...usage.payload, info: { ...usage.payload.info } } };
    delete withoutInlineDimensions.payload.info.model_context_window;
    const result = extractCodexEvidence([native[0], originalTurn, withoutInlineDimensions, rejected, withoutInlineDimensions, recovered, withoutInlineDimensions].map(JSON.stringify).join('\n'), context());
    const facts = result.records.filter(record => record.counters);
    assert.equal(facts.length, 3);
    assert.equal(facts[0].model, 'gpt-5-codex');
    assert.equal(facts[0].provider, 'openai');
    assert.equal(facts[0].pricing.sourceServiceTier, 'priority');
    assert.equal(facts[1].model, null);
    assert.equal(facts[1].provider, null);
    assert.deepEqual(facts[1].pricing, { tier: 'unknown', batch: false, region: null, contextTokens: null, sourceServiceTier: null, speed: null, inferenceGeo: null });
    if (failedType === 'session_meta') {
      assert.equal(facts[1].sessionId, null);
      assert.equal(facts[1].parentId, null);
      assert.notEqual(facts[2].sessionId, facts[0].sessionId);
    } else {
      assert.equal(facts[1].sessionId, facts[0].sessionId);
      assert.equal(facts[1].parentId, facts[0].parentId);
      assert.equal(facts[2].sessionId, facts[0].sessionId);
      assert.equal(facts[2].pricing.sourceServiceTier, 'flex');
      assert.equal(facts[2].pricing.region, 'eu');
      assert.equal(facts[2].pricing.contextTokens, 100000n);
    }
    assert.equal(facts[2].model, 'glm-4.6');
    assert.equal(facts[2].provider, 'zai');
    assert.ok(result.diagnostics.some(diagnostic => diagnostic.code === 'invalid_source_identifier'));
    assert.doesNotMatch(canonical(result), /PLANTED|sk-secret|home\/private/);
  });
}
