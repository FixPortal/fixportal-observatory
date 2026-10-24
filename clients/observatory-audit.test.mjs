import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, mkdir, writeFile, readFile, rm, unlink, symlink, stat, readdir, open, opendir } from 'node:fs/promises';
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
import { discoverSources, readStable } from './audit/discovery.mjs';
import { createBudget } from './audit/budget.mjs';

const pilotCaps = (overrides = {}) => ({ maxDirectoryEntries: 100, maxDepth: 4, maxQueuedPaths: 30, maxFiles: 20, maxFileBytes: 100000, maxReadBytes: 500000, maxLines: 100, maxLineBytes: 10000, maxRecords: 50, maxDiagnostics: 20, maxEncodedBytes: 100000, maxChunkBytes: 10000, maxHttpAttempts: 20, maxUploadBytes: 500000, maxDurationMs: 60000, sealReserveAttempts: 1, sealReserveBytes: 10000, sealReserveMs: 1000, maxOwnedWriteBytes: 100000, maxOwnedEntries: 100, maxPathBytes: 4096, maxMetadataBytes: 10000, ...overrides });

// These exercise native failures inside the real try block, followed by the
// catch's budget check; throwing CaptureLimit directly misses that boundary.
for (const boundary of ['seal reserve', 'native stopped']) {
  test(`discovery native failure at ${boundary} returns admitted files and one terminal marker`, async t => {
    const { root, config } = await workspace(t);
    await writeFile(join(root, 'admitted.jsonl'), claude());
    let now = 0, reads = 0;
    const budget = createBudget(pilotCaps(), { now: () => now });
    const directory = await opendir(root), prototype = Object.getPrototypeOf(directory), original = prototype.read;
    await directory.close();
    t.mock.method(prototype, 'read', async function(...args) {
      reads++;
      if (reads === 2) {
        if (boundary === 'seal reserve') now = 59000; else budget.stopNative();
        throw Object.assign(new Error('synthetic native directory failure'), { code: 'EIO' });
      }
      assert.ok(reads < 2, 'no directory IO follows the terminal failure');
      return original.apply(this, args);
    });
    const result = await discoverSources(config.sources, 100, { budget });
    assert.deepEqual(result.files.map(file => file.path), [join(root, 'admitted.jsonl')]);
    assert.deepEqual(result.codes, ['capture_limit']);
    assert.equal(result.skippedFiles, 0);
    assert.equal(reads, 2);
    assert.equal(budget.snapshot().directoryEntries, 2);
    assert.equal(budget.snapshot().nativeStopped, true);
  });

  test(`read native failure at ${boundary} retains prior capture facts without more IO`, async t => {
    const { root, config, request } = await workspace(t);
    await writeFile(join(root, 'a.jsonl'), claude());
    await writeFile(join(root, 'b.jsonl'), claude());
    await writeFile(join(root, 'c.jsonl'), claude());
    let now = 0, reads = 0;
    const budget = createBudget(pilotCaps(), { now: () => now });
    const handle = await open(join(root, 'a.jsonl'), 'r'), prototype = Object.getPrototypeOf(handle), original = prototype.read;
    await handle.close();
    t.mock.method(prototype, 'read', async function(...args) {
      reads++;
      if (reads === 3) {
        if (boundary === 'seal reserve') now = 59000; else budget.stopNative();
        throw Object.assign(new Error('synthetic native read failure'), { code: 'EIO' });
      }
      assert.ok(reads < 3, 'no read or retry follows the terminal failure');
      return original.apply(this, args);
    });
    const result = await captureEvidence(request, config, { budget });
    assert.equal(result.complete, false);
    assert.equal(result.records.length, 1);
    assert.equal(result.records[0].counters.input_tokens, 12n);
    assert.equal(result.manifest.discoveredFiles, 3n);
    assert.equal(result.manifest.readFiles, 1n);
    assert.equal(result.manifest.skippedFiles, 1n);
    assert.deepEqual(result.manifest.diagnostics, [{ code: 'capture_limit', count: 1n }]);
    assert.equal(reads, 3);
    assert.equal(budget.snapshot().nativeStopped, true);
  });
}

for (const operation of ['discovery', 'read']) {
  test(`${operation} native failure preserves a non-limit catch guard error unchanged`, async t => {
    const { root, config } = await workspace(t), path = join(root, 'a.jsonl');
    await writeFile(path, claude());
    const guardFailure = new Error('selected_lease_expired');
    let failed = false, calls = 0;
    const budget = createBudget(pilotCaps(), { guard: () => { if (failed) throw guardFailure; } });
    const resource = operation === 'discovery' ? await opendir(root) : await open(path, 'r');
    const prototype = Object.getPrototypeOf(resource);
    await resource.close();
    t.mock.method(prototype, 'read', async function() {
      calls++; failed = true;
      throw Object.assign(new Error('synthetic native failure'), { code: 'EIO' });
    });
    await assert.rejects(operation === 'discovery'
      ? discoverSources(config.sources, 100, { budget })
      : readStable({ root, path }, 100000, { budget }), error => error === guardFailure);
    assert.equal(calls, 1);
    assert.equal(budget.snapshot().nativeStopped, false);
  });
}

test('discovery debits before native directory entry work', async t => {
  const { config } = await workspace(t);
  await writeFile(join(config.sources[0].roots[0], 'one.jsonl'), claude());
  await writeFile(join(config.sources[0].roots[0], 'two.jsonl'), claude());
  const budget = createBudget(pilotCaps({ maxDirectoryEntries: 1 }));
  const result = await discoverSources(config.sources, 100, { budget });
  assert.equal(budget.snapshot().directoryEntries, 1);
  assert.equal(result.files.length, 1);
  assert.ok(result.codes.includes('capture_limit'));
});

test('extractors enforce shared line admission before parse and record allocation', () => {
  const budget = createBudget(pilotCaps({ maxLines: 1 }));
  const result = extractClaudeEvidence(claude() + '\n' + claude(), context(), { budget });
  assert.equal(result.records.length, 1);
  assert.ok(result.diagnostics.some(item => item.code === 'capture_limit'));
});

test('distinct extraction diagnostic groups exhaust the admission budget', () => {
  const budget = createBudget(pilotCaps({ maxDiagnostics: 1 }));
  const result = extractClaudeEvidence(claude() + '\ninvalid\n{"type":"unsupported"}\n' + claude(), context(), { budget });
  assert.equal(result.records.length, 1);
  assert.deepEqual(result.diagnostics, [{ code: 'capture_limit', count: 1n }, { code: 'malformed_json', count: 1n }]);
});

test('repeated unsupported rows retain counts and allow valid Claude and later Codex usage', async t => {
  const { dir, root, config, request } = await workspace(t);
  const codexRoot = join(dir, 'codex'); await mkdir(codexRoot);
  config.sources.push({ sourceId: 'codex-local', tool: 'codex', roots: [codexRoot] });
  request.scope.sources.push({ machineId, sourceId: 'codex-local' });
  const noise = JSON.stringify({ type: 'new_unsupported_type', private_payload: secret });
  await writeFile(join(root, 'a.jsonl'), Array(30).fill(noise).join('\n') + '\n' + claude());
  await writeFile(join(codexRoot, 'a.jsonl'), codex());
  const budget = createBudget(pilotCaps({ maxDiagnostics: 1 }));
  const result = await captureEvidence(request, config, { budget });
  assert.equal(result.complete, false, 'unknown rows still make the capture incomplete');
  assert.equal(result.manifest.readFiles, 2n);
  assert.deepEqual(result.manifest.diagnostics, [{ code: 'unsupported_source_format', count: 30n }]);
  assert.equal(result.records.filter(r => r.sourceId === 'claude-local' && r.counters).length, 1);
  assert.equal(result.records.filter(r => r.sourceId === 'codex-local' && r.counters).length, 2);
  assert.equal(budget.snapshot().diagnostics, 1);
  assert.equal(budget.snapshot().nativeStopped, false);
  assert.doesNotMatch(result.chunks.map(chunk => chunk.ndjson.toString()).join(''), /PLANTED|private_payload|new_unsupported_type/);
});

test('diagnostic admission groups reset for each file', async t => {
  const { root, config, request } = await workspace(t);
  for (const name of ['a.jsonl', 'b.jsonl']) await writeFile(join(root, name), '{"type":"unsupported"}\n' + claude());
  const budget = createBudget(pilotCaps({ maxDiagnostics: 1 }));
  const result = await captureEvidence(request, config, { budget });
  assert.equal(result.records.length, 1);
  assert.deepEqual(result.manifest.diagnostics, [{ code: 'capture_limit', count: 1n }, { code: 'unsupported_source_format', count: 1n }]);
  assert.equal(budget.snapshot().diagnostics, 1);
  assert.equal(budget.snapshot().nativeStopped, true);
});

test('repeated diagnostics cannot bypass the shared line cap', () => {
  const budget = createBudget(pilotCaps({ maxDiagnostics: 1, maxLines: 5 }));
  const result = extractClaudeEvidence(Array(20).fill('{"type":"unsupported"}').join('\n'), context(), { budget });
  assert.deepEqual(result.diagnostics, [{ code: 'capture_limit', count: 1n }, { code: 'unsupported_source_format', count: 5n }]);
  assert.equal(budget.snapshot().lines, 5);
});

test('repeated diagnostic groups still check the monotonic time cap', () => {
  let now = 0;
  const budget = createBudget(pilotCaps({ maxDiagnostics: 1, maxDurationMs: 1000, sealReserveMs: 100 }), { now: () => now });
  const result = protocol.extractLines('x\nx\nx', () => { now += 400; throw new Error('unsupported_source_format'); }, { budget });
  assert.deepEqual(result.diagnostics, [{ code: 'capture_limit', count: 1n }, { code: 'unsupported_source_format', count: 2n }]);
  assert.equal(budget.snapshot().nativeStopped, true);
});

test('counted diagnostics merge large occurrence counts without expanding them', () => {
  const counts = protocol.countedDiagnostics();
  counts.add('unsupported_source_format', 1000000000000n);
  counts.add('unsupported_source_format', 7n);
  assert.equal(counts.size, 1);
  assert.deepEqual(counts.toArray(), [{ code: 'unsupported_source_format', count: 1000000000007n }]);
});

test('an already admitted group still observes expiry while terminal markers remain reportable', () => {
  let now = 0;
  const budget = createBudget(pilotCaps({ maxDurationMs: 1000, sealReserveMs: 100 }), { now: () => now });
  const counts = protocol.countedDiagnostics(budget);
  counts.add('malformed_json');
  now = 1000;
  assert.throws(() => counts.add('malformed_json'), /capture_limit/);
  counts.add('capture_limit');
  assert.deepEqual(counts.toArray(), [{ code: 'capture_limit', count: 1n }, { code: 'malformed_json', count: 1n }]);
});

test('huge line and record floods are bounded before parse and retain admitted facts', () => {
  const lineBudget = createBudget(pilotCaps({ maxLineBytes: 50 }));
  const oversized = extractClaudeEvidence(' '.repeat(100000), context(), { budget: lineBudget });
  assert.deepEqual(oversized.diagnostics, [{ code: 'capture_limit', count: 1n }]);
  assert.equal(lineBudget.snapshot().records, 0);
  const recordBudget = createBudget(pilotCaps({ maxRecords: 2 }));
  const records = extractClaudeEvidence(Array(1000).fill(claude()).join('\n'), context(), { budget: recordBudget });
  assert.equal(records.records.length, 2);
  assert.equal(recordBudget.snapshot().lines, 3);
  assert.ok(records.diagnostics.some(item => item.code === 'capture_limit'));
});

test('changed file rereads and growth sentinels share physical requested-byte ceiling', async t => {
  const { root } = await workspace(t), path = join(root, 'changing.jsonl');
  await writeFile(path, 'abc');
  const budget = createBudget(pilotCaps({ maxReadBytes: 11 }));
  let reads = 0;
  const owned = await open(path, 'r'), prototype = Object.getPrototypeOf(owned), original = prototype.read;
  await owned.close();
  t.mock.method(prototype, 'read', async function(...args) {
    const result = await original.apply(this, args);
    if (!result.bytesRead) { reads++; await writeFile(path, 'abc' + 'x'.repeat(reads)); }
    return result;
  });
  const result = await readStable({ root, path }, 100000, { budget });
  assert.equal(result.code, 'capture_limit');
  assert.equal(reads, 2);
  assert.equal(budget.snapshot().readBytes, 11, 'initial requests4+EOF1 and retry5+EOF1 remain charged');
});

test('stable native read charges the EOF request beyond file size plus sentinel', async t => {
  const { root } = await workspace(t), path = join(root, 'stable.jsonl');
  await writeFile(path, 'abc');
  const budget = createBudget(pilotCaps({ maxReadBytes: 4 }));
  const result = await readStable({ root, path }, 100000, { budget });
  assert.equal(result.code, 'capture_limit', 'N+1 admitted bytes cannot pay the subsequent EOF request');
  assert.equal(budget.snapshot().readBytes, 4);
  const exact = createBudget(pilotCaps({ maxReadBytes: 5 }));
  assert.equal((await readStable({ root, path }, 100000, { budget: exact })).text, 'abc');
  assert.equal(exact.snapshot().readBytes, 5);
});

test('native short reads debit every requested remainder before each call without refunds', async t => {
  const { root } = await workspace(t), path = join(root, 'short.jsonl');
  await writeFile(path, 'abc');
  const owned = await open(path, 'r'), prototype = Object.getPrototypeOf(owned), original = prototype.read;
  await owned.close();
  const requests = [];
  t.mock.method(prototype, 'read', async function(buffer, offset, length, position) {
    requests.push(length);
    return original.call(this, buffer, offset, Math.min(1, length), position);
  });
  const budget = createBudget(pilotCaps({ maxReadBytes: 9 }));
  const result = await readStable({ root, path }, 100000, { budget });
  assert.equal(result.code, 'capture_limit');
  assert.deepEqual(requests, [4, 3, 2], 'EOF call must be refused before invoking the handle');
  assert.equal(budget.snapshot().readBytes, 9);
  requests.length = 0;
  const exact = createBudget(pilotCaps({ maxReadBytes: 10 }));
  assert.equal((await readStable({ root, path }, 100000, { budget: exact })).text, 'abc');
  assert.deepEqual(requests, [4, 3, 2, 1]);
  assert.equal(exact.snapshot().readBytes, 10);
});

test('two source roots share directory queue file read and record budgets', async t => {
  const { root, config, request, dir } = await workspace(t), second = join(dir, 'second');
  await mkdir(second);
  const text = claude();
  await writeFile(join(root, 'a.jsonl'), text); await writeFile(join(second, 'b.jsonl'), text);
  config.sources.push({ sourceId: 'claude-second', tool: 'claude', roots: [second] });
  request.scope.sources.push({ machineId, sourceId: 'claude-second' });
  const budget = createBudget(pilotCaps({ maxReadBytes: Buffer.byteLength(text) + 2 }));
  const result = await captureEvidence(request, config, { budget });
  assert.equal(result.manifest.readFiles, 1n);
  assert.equal(result.manifest.discoveredFiles, 2n);
  assert.equal(result.records.length, 1);
  assert.equal(result.complete, false);
  assert.ok(result.manifest.diagnostics.some(item => item.code === 'capture_limit'));
});

test('bounded enumeration stops before visiting a large owned directory tail', async t => {
  const { config, root } = await workspace(t);
  for (let index = 0; index < 200; index++) await writeFile(join(root, `fixture-${index}.jsonl`), '');
  const budget = createBudget(pilotCaps({ maxDirectoryEntries: 3 }));
  const result = await discoverSources(config.sources, 10000, { budget });
  assert.equal(result.files.length, 3);
  assert.equal(budget.snapshot().directoryEntries, 3);
  assert.equal(result.skippedFiles, 0, 'unvisited files are never invented as skipped');
  assert.deepEqual(result.codes, ['capture_limit']);
});

test('owned package byte boundary prevents the next file and exposes only prior actual output', async t => {
  const { dir, root, config, request } = await workspace(t);
  await writeFile(join(root, 'a.jsonl'), claude());
  const capture = await captureEvidence(request, config), output = join(dir, 'bounded-output');
  const budget = createBudget(pilotCaps({ maxOwnedWriteBytes: capture.chunks[0].ndjson.length }));
  await assert.rejects(writeCapture(capture, output, { budget }), /capture_limit/);
  assert.deepEqual(await readdir(output), ['chunks']);
  assert.equal(budget.snapshot().ownedWriteBytes, capture.chunks[0].ndjson.length);
});

test('closed mandatory caps refuse omitted unknown raised and invalid reserve settings', () => {
  for (const mutate of [c => { delete c.maxLines; }, c => { c.unknown = 1; }, c => { c.maxFiles = 10001; }, c => { c.maxRecords = 1000001; }, c => { c.maxChunkBytes = 8 * 1024 * 1024 + 1; }, c => { c.maxEncodedBytes = 512 * 1024 * 1024 + 1; }, c => { c.sealReserveMs = c.maxDurationMs; }, c => { c.maxReadBytes = 0; }, c => { c.maxOwnedEntries = -1; }]) {
    const values = pilotCaps(); mutate(values); assert.throws(() => createBudget(values), /invalid_capture_caps/);
  }
});

test('directory depth queued admissions and file caps stop actual owned traversal', async t => {
  const { root, config } = await workspace(t);
  const nested = join(root, 'nested'); await mkdir(nested); await writeFile(join(nested, 'a.jsonl'), claude());
  for (const overrides of [{ maxDepth: 0 }, { maxQueuedPaths: 1 }]) {
    const result = await discoverSources(config.sources, 10000, { budget: createBudget(pilotCaps(overrides)) });
    assert.equal(result.files.length, 0); assert.ok(result.codes.includes('capture_limit'));
  }
  await writeFile(join(root, 'one.jsonl'), claude()); await writeFile(join(root, 'two.jsonl'), claude());
  const result = await discoverSources(config.sources, 10000, { budget: createBudget(pilotCaps({ maxFiles: 1 })) });
  assert.equal(result.files.length, 1); assert.ok(result.codes.includes('capture_limit'));
});

test('per-file read and encoded record bounds stop work at the corresponding boundary', async t => {
  const { root } = await workspace(t), path = join(root, 'a.jsonl'); await writeFile(path, claude());
  const budget = createBudget(pilotCaps({ maxFileBytes: 100, maxLineBytes: 100 }));
  assert.equal((await readStable({ root, path }, 100000, { budget })).code, 'capture_limit');
  assert.equal(budget.snapshot().readBytes, 0);
  const encoded = createBudget(pilotCaps({ maxEncodedBytes: 100, maxChunkBytes: 100 }));
  const result = extractClaudeEvidence(claude(), context(), { budget: encoded });
  assert.equal(result.records.length, 0); assert.ok(result.diagnostics.some(item => item.code === 'capture_limit'));
});

test('owned metadata cap is checked before allocating its encoding or visiting later fields', async t => {
  const { dir } = await workspace(t);
  let visited = false;
  const manifest = { a: 'x'.repeat(100), get z() { visited = true; throw new Error('encoded_before_admission'); } };
  const output = join(dir, 'metadata-output'), budget = createBudget(pilotCaps({ maxOwnedWriteBytes: 10 }));
  await assert.rejects(writeCapture({ chunks: [], manifest, local: {} }, output, { budget }), /capture_limit/);
  assert.equal(visited, false);
  assert.deepEqual(await readdir(output), ['chunks']);
});

test('nonallocating canonical sizing exactly matches escaped ASCII Unicode keys arrays and lossless numbers', () => {
  const text = Array.from({ length: 256 }, (_, index) => String.fromCharCode(index)).join('') + '\u2028😀\ud800';
  for (const value of [text, { [text]: [null, true, false, -0, 123, -7n, 9007199254740993n, { b: text, a: '"\\\n' }] }]) {
    const size = Buffer.byteLength(canonical(value));
    assert.equal(protocol.canonicalByteLength(value, { maximum: size }), size);
    assert.throws(() => protocol.canonicalByteLength(value, { maximum: size - 1 }), /capture_limit/);
  }
});

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

test('Claude administrative and request-replay kinds neither duplicate usage nor imply closure', () => {
  const kinds = ['ai-title', 'agent-name', 'mode', 'permission-mode', 'atis-latch', 'pr-link', 'last-prompt', 'file-history-delta', 'attachment', 'api-request', 'api-request-shape', 'api-request-blob'];
  // Replay blobs can contain an old assistant message. Only the native assistant row is usage.
  const lines = kinds.map(type => JSON.stringify({ type, sessionId: 'session-a', message: JSON.parse(claude()).message, content: secret, costUSD: '999' }));
  const result = extractClaudeEvidence([...lines, claude()].join('\n'), context());
  assert.deepEqual(result.diagnostics, []);
  assert.equal(result.records.length, 1);
  assert.equal(result.records[0].kind, 'messageUsage');
  assert.equal(result.records[0].counters.input_tokens, 12n);
  assert.equal(result.records[0].sequence, 13n);
  assert.ok(!canonical(result).includes(secret));
  const unknown = extractClaudeEvidence('{"type":"api-request-future"}', context());
  assert.ok(unknown.diagnostics.some(item => item.code === 'unsupported_source_format'));
});

test('Codex usage-bearing rollout records remain diagnosed until reconciliation is qualified', () => {
  const result = extractCodexEvidence(JSON.stringify({ type: 'token_usage_record', payload: { usage: { input_tokens: 12 }, thread_token_usage: { input_tokens: 12 } } }), context());
  assert.equal(result.records.length, 0);
  assert.ok(result.diagnostics.some(item => item.code === 'unsupported_source_format'));
});

const completeCodexUsage = (input = 100) => ({ input_tokens: input, cached_input_tokens: 20, output_tokens: 30, reasoning_output_tokens: 10, total_tokens: input + 30 });
const codexLedger = (usage = completeCodexUsage(), thread = 'session-x') => ({ type: 'token_usage_record', timestamp: '2026-10-18T23:59:00Z', payload: { thread_id: thread, turn_id: 'turn-x', session_id: 'process-x', root_turn_id: 'turn-x', response_id: 'response-x', usage, turn_token_usage: usage, thread_token_usage: usage } });
const codexLegacy = (usage = completeCodexUsage()) => ({ type: 'event_msg', timestamp: '2026-10-18T23:59:00Z', payload: { type: 'token_count', info: { total_token_usage: usage, last_token_usage: usage } } });
const codexSession = (id = 'session-x') => ({ type: 'session_meta', timestamp: '2026-10-18T23:00:00Z', payload: { id, model_provider: 'openai' } });
const codexText = (...rows) => rows.map(JSON.stringify).join('\n');

test('Codex ledgers corroborate complete same-session legacy totals in either order without new usage', () => {
  const explicitZero = { ...completeCodexUsage(), cache_write_input_tokens: 0 };
  for (const rows of [[codexLedger(), { type: 'response_item', payload: { private: secret } }, codexLegacy()], [codexLegacy(), codexLedger(), codexLedger()], [codexLedger(explicitZero), codexLegacy(explicitZero)]]) {
    const result = extractCodexEvidence(codexText(codexSession(), ...rows), context());
    assert.deepEqual(result.diagnostics, []);
    assert.deepEqual(result.records.map(record => record.kind), ['sessionMetadata', 'cumulativeUsage']);
    assert.equal(result.records[1].counters.input_tokens, 100n);
    assert.ok(!canonical(result).includes(secret));
  }
});

test('Codex unmatched partial contradictory foreign-session and cache-write ledgers remain diagnosed', () => {
  const partial = completeCodexUsage(); delete partial.total_tokens;
  for (const ledger of [codexLedger(completeCodexUsage(101)), codexLedger(partial), codexLedger(completeCodexUsage(), 'other-session'), codexLedger({ ...completeCodexUsage(), cache_write_input_tokens: 1 }), codexLedger({ ...completeCodexUsage(), cache_write_input_tokens: null })]) {
    const result = extractCodexEvidence(codexText(codexSession(), ledger, codexLegacy()), context());
    assert.deepEqual(result.diagnostics, [{ code: 'unsupported_source_format', count: 1n }]);
    assert.equal(result.records.filter(record => record.kind === 'cumulativeUsage').length, 1);
  }
  const result = extractCodexEvidence(codexText(codexSession(), codexLedger(), codexSession(), codexLegacy()), context());
  assert.deepEqual(result.diagnostics, [{ code: 'unsupported_source_format', count: 1n }]);
});

test('Codex deferred unmatched ledger diagnostics retain occurrence counts and group admission', () => {
  const budget = createBudget(pilotCaps({ maxDiagnostics: 1 }));
  const result = extractCodexEvidence(codexText(codexSession(), ...Array.from({ length: 20 }, () => codexLedger())), context(), { budget });
  assert.deepEqual(result.diagnostics, [{ code: 'unsupported_source_format', count: 20n }]);
  assert.equal(budget.snapshot().diagnostics, 1);
});

test('Codex impossible response and turn totals cannot be corroborated by a matching thread total', () => {
  for (const field of Object.keys(completeCodexUsage())) {
    for (const lane of ['usage', 'turn_token_usage']) {
      const ledger = codexLedger(); ledger.payload[lane] = { ...ledger.payload[lane], [field]: 1000 };
      const result = extractCodexEvidence(codexText(codexSession(), ledger, codexLegacy()), context());
      assert.deepEqual(result.diagnostics, [{ code: 'unsupported_source_format', count: 1n }]);
    }
  }
});

test('Codex corroboration cannot cross files, rejected metadata, or incomplete legacy vectors', () => {
  const partial = completeCodexUsage(); delete partial.cached_input_tokens;
  for (const rows of [[codexLedger()], [codexLedger(), { type: 'session_meta', payload: {} }, codexLegacy()], [codexLedger(), codexLegacy(partial)]]) {
    const result = extractCodexEvidence(codexText(codexSession(), ...rows), context());
    assert.ok(result.diagnostics.some(item => item.code === 'unsupported_source_format'));
  }
  assert.deepEqual(extractCodexEvidence(codexText(codexSession(), codexLegacy()), context('other-file')).diagnostics, []);
});

test('Codex corroboration compares lossless integer vectors beyond floating point precision', () => {
  const text = codexText(codexSession(), codexLedger(), codexLegacy()).replaceAll('"input_tokens":100', '"input_tokens":9007199254740993').replaceAll('"total_tokens":130', '"total_tokens":9007199254741023');
  assert.deepEqual(extractCodexEvidence(text, context()).diagnostics, []);
  const ledgerText = codexText(codexSession(), codexLedger()).replaceAll('"input_tokens":100', '"input_tokens":9007199254740993').replaceAll('"total_tokens":130', '"total_tokens":9007199254741023');
  const legacyText = JSON.stringify(codexLegacy()).replaceAll('"input_tokens":100', '"input_tokens":9007199254740992').replaceAll('"total_tokens":130', '"total_tokens":9007199254741022');
  const contradictory = ledgerText + '\n' + legacyText;
  assert.deepEqual(extractCodexEvidence(contradictory, context()).diagnostics, [{ code: 'unsupported_source_format', count: 1n }]);
});

test('deferred extraction findings obey diagnostic and deadline budgets', () => {
  const exhausted = createBudget(pilotCaps({ maxDiagnostics: 1 }));
  const result = protocol.extractLines('valid', () => true, { budget: exhausted }, codes => { codes.add('malformed_json'); codes.add('unsupported_source_format'); });
  assert.deepEqual(result.diagnostics, [{ code: 'capture_limit', count: 1n }, { code: 'malformed_json', count: 1n }]);
  let now = 0;
  const expired = createBudget(pilotCaps(), { now: () => now });
  const timed = protocol.extractLines('valid', () => { now = 59000; return true; }, { budget: expired }, codes => codes.add('unsupported_source_format'));
  assert.deepEqual(timed.diagnostics, [{ code: 'capture_limit', count: 1n }]);
});

test('Codex world state is recognized without copying model-visible context', () => {
  const result = extractCodexEvidence(codexText(codexSession(), { type: 'world_state', payload: { full: true, state: { private: secret } } }), context());
  assert.deepEqual(result.diagnostics, []);
  assert.equal(result.records.length, 1);
  assert.ok(!canonical(result).includes(secret));
});

test('Codex ledger corroboration respects UTC usage day and validated model/provider context', () => {
  const midnight = codexLedger(); midnight.timestamp = '2026-10-18T23:59:59Z';
  const nextDay = codexLegacy(); nextDay.timestamp = '2026-10-19T00:00:01Z';
  assert.deepEqual(extractCodexEvidence(codexText(codexSession(), midnight, nextDay), context()).diagnostics, [{ code: 'unsupported_source_format', count: 1n }]);
  const offset = codexLedger(); offset.timestamp = '2026-10-19T01:59:00+02:00';
  assert.deepEqual(extractCodexEvidence(codexText(codexSession(), offset, codexLegacy()), context()).diagnostics, []);
  for (const replacement of [{ model: 'other-model' }, { model_provider: 'other-provider' }]) {
    const result = extractCodexEvidence(codexText(codexSession(), codexLedger(), { type: 'turn_context', timestamp: '2026-10-18T23:59:00Z', payload: replacement }, codexLegacy()), context());
    assert.deepEqual(result.diagnostics, [{ code: 'unsupported_source_format', count: 1n }]);
  }
});

test('Codex ledger corroboration rejects inconsistent subsets totals and unknown token meters', () => {
  const malformed = [{ cached_input_tokens: 101 }, { reasoning_output_tokens: 31 }, { total_tokens: 129 }, { future_meter: 1 }];
  for (const lane of ['usage', 'turn_token_usage', 'thread_token_usage']) {
    for (const fields of malformed) {
      const ledger = codexLedger(); ledger.payload[lane] = { ...ledger.payload[lane], ...fields };
      const result = extractCodexEvidence(codexText(codexSession(), ledger, codexLegacy()), context());
      assert.deepEqual(result.diagnostics, [{ code: 'unsupported_source_format', count: 1n }]);
    }
  }
});

test('Codex pending unique ledger vectors share the existing record memory ceiling', () => {
  const budget = createBudget(pilotCaps({ maxRecords: 3 }));
  const result = extractCodexEvidence(codexText(codexSession(), codexLedger(completeCodexUsage(101)), codexLedger(completeCodexUsage(102)), codexLedger(completeCodexUsage(103)), codexLegacy()), context(), { budget });
  assert.deepEqual(result.diagnostics, [{ code: 'capture_limit', count: 1n }]);
  assert.equal(result.records.length, 1);
});

test('Codex pending ledgers also reserve room before later normalized metadata records', () => {
  const budget = createBudget(pilotCaps({ maxRecords: 3 }));
  const turn = { type: 'turn_context', timestamp: '2026-10-18T23:59:00Z', payload: { model: 'model-x' } };
  const result = extractCodexEvidence(codexText(codexSession(), codexLedger(), turn, turn), context(), { budget });
  assert.deepEqual(result.diagnostics, [{ code: 'capture_limit', count: 1n }]);
  assert.equal(result.records.length, 2);
});

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
