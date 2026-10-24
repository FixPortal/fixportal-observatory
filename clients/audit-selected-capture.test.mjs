import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, mkdir, writeFile, rm, readdir } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { canonical, sha256 } from './audit/protocol.mjs';
import { loadIdentity } from './audit/identity.mjs';
import { PassThrough } from 'node:stream';

const selectedCapture = async (...args) => (await import('./audit/selected-capture.mjs')).selectedCapture(...args);
const id = n => String(n).repeat(8) + '-' + String(n).repeat(4) + '-' + String(n).repeat(4) + '-' + String(n).repeat(4) + '-' + String(n).repeat(12);
const caps = (overrides = {}) => ({ maxDirectoryEntries: 100, maxDepth: 4, maxQueuedPaths: 30, maxFiles: 20, maxFileBytes: 100000, maxReadBytes: 500000, maxLines: 100, maxLineBytes: 10000, maxRecords: 50, maxDiagnostics: 20, maxEncodedBytes: 100000, maxChunkBytes: 10000, maxHttpAttempts: 20, maxUploadBytes: 500000, maxDurationMs: 60000, sealReserveAttempts: 1, sealReserveBytes: 10000, sealReserveMs: 1000, maxOwnedWriteBytes: 100000, maxOwnedEntries: 100, maxPathBytes: 4096, maxMetadataBytes: 10000, ...overrides });

async function fixture(t) {
  const dir = await mkdtemp(join(tmpdir(), 'selected-capture-owned-'));
  t.after(() => rm(dir, { recursive: true, force: true }));
  const roots = [join(dir, 'claude'), join(dir, 'codex')], identityDirectory = join(dir, 'identity');
  for (const path of [...roots, identityDirectory]) await mkdir(path, { mode: 0o700 });
  await writeFile(join(roots[0], 'session.jsonl'), JSON.stringify({ type: 'assistant', sessionId: 'synthetic-a', timestamp: '2026-10-20T12:00:00Z', message: { id: 'synthetic-message', model: 'synthetic-model', usage: { input_tokens: 12, output_tokens: 2 } } }));
  await writeFile(join(roots[1], 'session.jsonl'), JSON.stringify({ type: 'session_meta', timestamp: '2026-10-18T12:00:00Z', payload: { id: 'synthetic-b', model: 'synthetic-model' } }) + '\n' + JSON.stringify({ type: 'event_msg', timestamp: '2026-10-20T12:00:00Z', payload: { type: 'token_count', info: { total_token_usage: { input_tokens: 20, output_tokens: 3 } } } }));
  const scope = { from: '2026-10-19', to: '2026-10-21', sources: [{ machineId: id(1), sourceId: 'claude-local' }, { machineId: id(1), sourceId: 'codex-local' }], requestedChecks: ['sourceAccounting'], excludedChecks: [], allowPartial: true, predecessorRunId: null, evaluationOfRunId: null };
  const observed = '2026-10-24T01:00:00.000Z';
  const approval = { schemaVersion: 1, runId: id(2), expectedRevision: 0, fullScopeDigest: 'ab'.repeat(32), requestId: id(3), machineId: id(1), requestScopeDigest: sha256(canonical(scope)), authorityExpiresAt: '2026-10-24T01:10:00.000Z', instanceId: 'synthetic-instance' };
  const config = { origin: 'https://example.test', key: id(4) + '.' + 'ab'.repeat(32), approval, requestScope: scope, identityDirectory, sources: roots.map((root, index) => ({ sourceId: index ? 'codex-local' : 'claude-local', tool: index ? 'codex' : 'claude', roots: [root] })), caps: caps() };
  const { authorityExpiresAt: _expiry, ...binding } = approval;
  const lease = { ...binding, leaseToken: id(5), scope: structuredClone(scope), expiresAt: '2026-10-24T01:05:00.000Z', serverObservedAt: observed };
  const calls = []; let identities = 0, monotonic = 0;
  const dependencies = { wallNow: () => Date.parse(observed), now: () => monotonic, loadIdentity: async (...args) => { identities++; return loadIdentity(...args); }, fetch: async (url, options) => {
    calls.push({ url, options });
    if (url.endsWith('/claim') || url.endsWith('/renew')) return new Response(JSON.stringify(lease), { status: 200 });
    if (url.includes('/chunks/')) return new Response(null, { status: 204 });
    return new Response(JSON.stringify({ captureId: JSON.parse(options.body).manifest.captureId }), { status: 200 });
  } };
  return { dir, roots, config, lease, calls, dependencies, identities: () => identities, advance: ms => { monotonic += ms; } };
}

test('valid exact binding enters two owned roots and seals without generic claim', async t => {
  const f = await fixture(t);
  const result = await selectedCapture(f.config, f.dependencies);
  assert.equal(result.status, 'sealed');
  assert.equal(result.capture.complete, true);
  assert.equal(result.capture.manifest.readFiles, 2n);
  assert.equal(f.identities(), 1);
  assert.ok(result.capture.records.some(record => record.timestamp.startsWith('2026-10-18')), 'pre-window Codex session metadata retained');
  assert.equal(f.calls.filter(call => call.url.endsWith('/selected/claim')).length, 1);
  assert.ok(f.calls.every(call => !call.url.endsWith('/capture-requests/claim')));
  assert.deepEqual(Object.keys(JSON.parse(f.calls[0].options.body)).sort(), ['schemaVersion', 'runId', 'expectedRevision', 'fullScopeDigest', 'requestId', 'machineId', 'requestScopeDigest'].sort());
});

test('preflight closed approval roots expiry and caps reject before any fetch or identity', async t => {
  for (const mutate of [c => { delete c.approval.runId; }, c => { c.approval.authorityExpiresAt = '2026-10-23T00:00:00Z'; }, c => { c.sources[0].roots = ['relative']; }, c => { c.sources[0].roots.push(c.sources[1].roots[0]); }, c => { c.sources[1].tool = 'claude'; }, c => { delete c.caps.maxReadBytes; }, c => { c.approval.command = 'foreign'; }, c => { c.requestScope.sources[0].machineId = id(9); }]) {
    const f = await fixture(t); mutate(f.config);
    await assert.rejects(selectedCapture(f.config, f.dependencies));
    assert.equal(f.calls.length, 0); assert.equal(f.identities(), 0);
    assert.deepEqual(await readdir(f.config.identityDirectory), []);
  }
});

test('HTTPS preflight rejects nonboolean allowLoopbackHttp before fetch or native identity work', async t => {
  for (const [label, value] of [['string', 'true'], ['number', 1], ['null', null]]) {
    await t.test(label, async t => {
      const f = await fixture(t); f.config.allowLoopbackHttp = value;
      await assert.rejects(selectedCapture(f.config, f.dependencies), /invalid_selected_capture/);
      assert.equal(f.calls.length, 0);
      assert.equal(f.identities(), 0);
      assert.deepEqual(await readdir(f.config.identityDirectory), []);
      for (const root of f.roots) assert.deepEqual(await readdir(root), ['session.jsonl']);
    });
  }
});

test('HTTPS preflight preserves omitted and boolean allowLoopbackHttp configurations', async t => {
  for (const value of [undefined, false, true]) {
    await t.test(value === undefined ? 'omitted' : String(value), async t => {
      const f = await fixture(t);
      if (value !== undefined) f.config.allowLoopbackHttp = value;
      const result = await selectedCapture(f.config, f.dependencies);
      assert.equal(result.status, 'sealed');
      assert.equal(result.capture.complete, true);
      assert.equal(f.identities(), 1);
    });
  }
});
test('foreign malformed scope token instance and expiry refuse before identity or upload', async t => {
  for (const mutate of [l => { l.machineId = id(9); }, l => { l.runId = id(9); }, l => { l.requestId = id(9); }, l => { l.expectedRevision++; }, l => { l.fullScopeDigest = 'cd'.repeat(32); }, l => { l.requestScopeDigest = 'cd'.repeat(32); }, l => { l.scope.to = '2026-10-22'; }, l => { l.leaseToken = ''; }, l => { l.instanceId = 'foreign'; }, l => { l.expiresAt = '2026-10-24T01:11:00Z'; }, l => { l.expiresAt = l.serverObservedAt; }, l => { l.serverObservedAt = 'malformed'; }, l => { l.roots = ['foreign']; }]) {
    const f = await fixture(t); mutate(f.lease);
    await assert.rejects(selectedCapture(f.config, f.dependencies));
    assert.equal(f.calls.length, 1); assert.equal(f.identities(), 0);
    assert.deepEqual(await readdir(f.config.identityDirectory), []);
  }
});

test('full slow claim RTT and timestamp rounding consume lease before native work', async t => {
  const f = await fixture(t); f.lease.expiresAt = '2026-10-24T01:00:01.000Z';
  const fetch = f.dependencies.fetch;
  f.dependencies.fetch = async (...args) => { const result = await fetch(...args); f.advance(1000); return result; };
  await assert.rejects(selectedCapture(f.config, f.dependencies));
  assert.equal(f.identities(), 0); assert.equal(f.calls.length, 1);
});

test('supervised stdin cooperatively aborts on one byte or EOF without interpreting input', async () => {
  const { supervisedStop } = await import('./audit/selected-capture.mjs');
  for (const eof of [false, true]) {
    const stream = new PassThrough(), controller = new AbortController();
    const dispose = supervisedStop(stream, controller);
    if (eof) stream.end(); else stream.write('!');
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(controller.signal.aborted, true);
    dispose(); stream.destroy();
  }
});

test('selected bootstrap rejects overlarge explicit metadata without parsing it', async t => {
  const f = await fixture(t), path = join(f.dir, 'oversized.json');
  await writeFile(path, ' '.repeat(1024 * 1024 + 1));
  const { readSelectedConfiguration } = await import('./audit/selected-capture.mjs');
  await assert.rejects(readSelectedConfiguration(path), /invalid_selected_configuration/);
});

test('selected native record cap seals truthful incomplete facts while lease and reserves permit', async t => {
  const f = await fixture(t); f.config.caps.maxRecords = 1;
  const result = await selectedCapture(f.config, f.dependencies);
  assert.equal(result.status, 'sealed'); assert.equal(result.capture.complete, false);
  assert.equal(result.capture.manifest.recordCount, 1n);
  assert.ok(result.capture.manifest.diagnostics.some(item => item.code === 'capture_limit'));
  assert.equal(result.capture.manifest.discoveredFiles, 2n);
  assert.equal(result.capture.manifest.readFiles, 2n);
  assert.equal(f.calls.at(-1).url.endsWith('/seal'), true);
});

test('expired lease during owned native work stops unsealed without fresh claim', async t => {
  const f = await fixture(t); f.lease.expiresAt = '2026-10-24T01:00:01Z';
  const load = f.dependencies.loadIdentity;
  f.dependencies.loadIdentity = async (...args) => { const result = await load(...args); f.advance(1000); return result; };
  await assert.rejects(selectedCapture(f.config, f.dependencies), /selected_lease_expired/);
  assert.equal(f.calls.length, 1);
});

test('renew uses only selected tuple and token and rejects mismatch before upload', async t => {
  const f = await fixture(t); f.lease.expiresAt = '2026-10-24T01:00:10Z';
  const fetch = f.dependencies.fetch;
  f.dependencies.fetch = async (url, options) => {
    if (url.endsWith('/renew')) f.lease.requestId = id(9);
    return fetch(url, options);
  };
  await assert.rejects(selectedCapture(f.config, f.dependencies), /invalid_selected_lease/);
  assert.deepEqual(f.calls.map(call => new URL(call.url).pathname.split('/').at(-1)), ['claim', 'renew']);
  assert.equal(f.calls[1].options.headers['X-Observatory-Audit-Lease'], id(5));
  assert.equal(f.calls[1].options.body, f.calls[0].options.body);
});

test('wall clock jump cannot enlarge a lease and successful renewal cannot reset operation deadline', async t => {
  const f = await fixture(t); f.lease.expiresAt = '2026-10-24T01:00:01Z';
  f.config.caps.maxDurationMs = 5000; f.config.caps.sealReserveMs = 100;
  let wall = Date.parse(f.lease.serverObservedAt);
  f.dependencies.wallNow = () => wall;
  const fetch = f.dependencies.fetch;
  f.dependencies.fetch = async (url, options) => {
    if (url.endsWith('/renew')) { wall -= 86400000; f.lease.expiresAt = '2026-10-24T01:05:00Z'; }
    if (url.includes('/chunks/')) f.advance(5001);
    return fetch(url, options);
  };
  await assert.rejects(selectedCapture(f.config, f.dependencies));
  assert.equal(f.calls.length, 3, 'renew succeeded inside old lease before fixed operation deadline expired during upload');
  assert.ok(f.calls.every(call => !call.url.endsWith('/seal')));
});

test('oversized or duplicate-key claim response remains one attempt with zero identity work', async t => {
  for (const content of [' '.repeat(10001), '{"schemaVersion":1,"schemaVersion":1}']) {
    const f = await fixture(t);
    f.dependencies.fetch = async (url, options) => { f.calls.push({ url, options }); return new Response(content); };
    await assert.rejects(selectedCapture(f.config, f.dependencies));
    assert.equal(f.calls.length, 1); assert.equal(f.identities(), 0);
  }
});

test('fresh identity writes are bounded and existing material is never loaded or overwritten', async t => {
  const f = await fixture(t); f.config.caps.maxOwnedWriteBytes = 1;
  await assert.rejects(selectedCapture(f.config, f.dependencies), /capture_limit/);
  assert.deepEqual(await readdir(f.config.identityDirectory), []);
  const g = await fixture(t), path = join(g.config.identityDirectory, 'identity.json');
  await writeFile(path, 'synthetic-existing-material');
  await assert.rejects(selectedCapture(g.config, g.dependencies), /identity_unavailable/);
  assert.equal(g.calls.length, 1);
});

test('encoded output capacity includes seal metadata and refuses it before sending a seal', async t => {
  const f = await fixture(t), first = await selectedCapture(f.config, f.dependencies);
  const g = await fixture(t);
  g.config.caps.maxEncodedBytes = first.capture.chunks.reduce((sum, chunk) => sum + chunk.ndjson.length, 0);
  g.config.caps.maxChunkBytes = Math.min(g.config.caps.maxChunkBytes, g.config.caps.maxEncodedBytes);
  await assert.rejects(selectedCapture(g.config, g.dependencies), /capture_limit/);
  assert.ok(g.calls.every(call => !call.url.endsWith('/seal')));
});

for (const [name, operation, status, content] of [
  ['chunk200', 'chunks', 200, ''],
  ['chunk202', 'chunks', 202, '{}'],
  ['seal202', 'seal', 202, '{}'],
  ['seal204', 'seal', 204, null],
  ['sealHTML', 'seal', 200, '<html>accepted</html>'],
  ['sealMissing', 'seal', 200, '{}'],
  ['sealForeign', 'seal', 200, JSON.stringify({ captureId: id(9) })],
  ['sealDuplicate', 'seal', 200, '{"captureId":"' + id(9) + '","captureId":"' + id(9) + '"}'],
  ['sealExtra', 'seal', 200, JSON.stringify({ captureId: id(9), extra: true })],
]) test(`invalid selected acknowledgement ${name} stops FAILED/UNCERTAIN without a new claim`, async t => {
  const f = await fixture(t), fetch = f.dependencies.fetch;
  f.dependencies.fetch = async (url, options) => {
    const response = await fetch(url, options);
    if (operation === 'chunks' ? url.includes('/chunks/') : url.endsWith('/seal')) {
      const captureId = operation === 'seal' ? JSON.parse(options.body).manifest.captureId : null;
      const body = name === 'sealDuplicate' ? `{"captureId":"${captureId}","captureId":"${captureId}"}`
        : name === 'sealExtra' ? JSON.stringify({ captureId, extra: true }) : content;
      return new Response(body, { status });
    }
    return response;
  };
  await assert.rejects(selectedCapture(f.config, f.dependencies), /invalid_selected_acknowledgement/);
  assert.equal(f.calls.filter(call => call.url.endsWith('/claim')).length, 1);
  assert.equal(f.calls.filter(call => operation === 'chunks' ? call.url.includes('/chunks/') : call.url.endsWith('/seal')).length, 1);
  if (operation === 'chunks') assert.ok(f.calls.every(call => !call.url.endsWith('/seal')));
});
