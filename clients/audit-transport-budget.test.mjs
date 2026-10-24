import test from 'node:test';
import assert from 'node:assert/strict';
import { createAuditTransport } from './audit/transport.mjs';
import { createBudget } from './audit/budget.mjs';
const caps = (overrides = {}) => ({ maxDirectoryEntries: 100, maxDepth: 4, maxQueuedPaths: 30, maxFiles: 20, maxFileBytes: 100000, maxReadBytes: 500000, maxLines: 100, maxLineBytes: 10000, maxRecords: 50, maxDiagnostics: 20, maxEncodedBytes: 100000, maxChunkBytes: 10000, maxHttpAttempts: 4, maxUploadBytes: 1000, maxDurationMs: 60000, sealReserveAttempts: 1, sealReserveBytes: 100, sealReserveMs: 1000, maxOwnedWriteBytes: 100000, maxOwnedEntries: 100, maxPathBytes: 4096, maxMetadataBytes: 10000, ...overrides });
const key = '11111111-1111-1111-1111-111111111111.' + 'ab'.repeat(32);
const chunk = '/api/audits/capture-requests/22222222-2222-2222-2222-222222222222/chunks/0';

test('one budget debits failed retry bytes before every fetch', async t => {
  let calls = 0;
  t.mock.method(globalThis, 'fetch', async () => { calls++; throw new Error('network'); });
  const budget = createBudget(caps());
  const transport = createAuditTransport({ origin: 'https://example.test', key }, { sleep: async () => {} });
  const failure = await transport.request(chunk, { method: 'PUT', body: Buffer.alloc(500), budget }).catch(error => error);
  assert.equal(calls, 1);
  assert.match(failure.message, /capture_limit/);
  assert.equal(budget.snapshot().uploadBytes, 500);
});

test('selected claim uncertainty never emits a second fresh claim', async t => {
  let calls = 0;
  t.mock.method(globalThis, 'fetch', async () => { calls++; throw new Error('lost response'); });
  const transport = createAuditTransport({ origin: 'https://example.test', key }, { sleep: async () => {} });
  await assert.rejects(transport.request('/api/audits/capture-requests/selected/claim', { body: '{}', budget: createBudget(caps()) }), /audit_transport_failed/);
  assert.equal(calls, 1);
});

test('whole monotonic operation deadline stops failed upload before retry despite wall clock rollback', async t => {
  let monotonic = 0, calls = 0;
  t.mock.method(Date, 'now', () => -86400000);
  t.mock.method(globalThis, 'fetch', async () => { calls++; monotonic = 1001; throw new Error('lost upload'); });
  const budget = createBudget(caps({ maxDurationMs: 1000, sealReserveMs: 100 }), { now: () => monotonic });
  const transport = createAuditTransport({ origin: 'https://example.test', key }, { sleep: async () => {} });
  const failure = await transport.request(chunk, { method: 'PUT', body: 'abc', budget }).catch(error => error);
  assert.equal(calls, 1);
  assert.match(failure.message, /capture_limit/);
});

test('seal uses counted attempt and byte reserves after ordinary attempt capacity is spent', async t => {
  let calls = 0;
  t.mock.method(globalThis, 'fetch', async () => { calls++; return new Response('{}'); });
  const budget = createBudget(caps({ maxHttpAttempts: 2 }));
  const transport = createAuditTransport({ origin: 'https://example.test', key });
  await transport.request(chunk, { body: 'abc', budget });
  await assert.rejects(transport.request(chunk, { body: 'abc', budget }), /capture_limit/);
  await transport.request(chunk.replace('chunks/0', 'seal'), { body: 'abc', budget, sealing: true });
  assert.equal(calls, 2); assert.equal(budget.snapshot().httpAttempts, 2); assert.equal(budget.snapshot().uploadBytes, 6);
});

test('large uploads receive a larger bounded budget while claims stay quick', async t => {
  const budgets = [];
  t.mock.method(AbortSignal, 'timeout', ms => { budgets.push(ms); return new AbortController().signal; });
  t.mock.method(globalThis, 'fetch', async () => new Response(null, { status: 204 }));
  const transport = createAuditTransport({ origin: 'https://example.test', key });
  await transport.request('/api/audits/capture-requests/claim', { body: '{}' });
  await transport.request(chunk, { method: 'PUT', body: Buffer.alloc(8 * 1024 * 1024) });
  assert.equal(budgets[0], 10000);
  assert.ok(budgets[1] > budgets[0], 'large upload must not inherit the short claim timeout');
  assert.ok(budgets[1] <= 90000);
});

test('upload attempts and retry waits stay inside the remaining lease budget', async t => {
  let now = 0; const budgets = []; let calls = 0;
  t.mock.method(Date, 'now', () => now);
  t.mock.method(AbortSignal, 'timeout', ms => { budgets.push(ms); return new AbortController().signal; });
  t.mock.method(globalThis, 'fetch', async () => { calls++; now = 4900; throw new Error('network'); });
  const transport = createAuditTransport({ origin: 'https://example.test', key }, { sleep: async ms => { now += ms; } });
  await assert.rejects(transport.request(chunk, { method: 'PUT', body: Buffer.alloc(8 * 1024 * 1024), deadline: 5000 }), /audit_transport_failed/);
  assert.ok(budgets[0] < 5000, 'leave room before the lease boundary');
  assert.equal(calls, 1, 'no retry may start after its deadline');
});

test('caller cancellation prevents retries with a large body', async t => {
  const caller = new AbortController(); let calls = 0;
  t.mock.method(globalThis, 'fetch', async (_url, options) => { calls++; caller.abort(); options.signal.throwIfAborted(); });
  const transport = createAuditTransport({ origin: 'https://example.test', key }, { sleep: async () => assert.fail('cancelled retry') });
  await assert.rejects(transport.request(chunk, { method: 'PUT', body: Buffer.alloc(8 * 1024 * 1024), signal: caller.signal }), /audit_transport_failed/);
  assert.equal(calls, 1);
});
