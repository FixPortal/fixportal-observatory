import test from 'node:test';
import assert from 'node:assert/strict';
import { createAuditTransport } from './audit/transport.mjs';
const key = '11111111-1111-1111-1111-111111111111.' + 'ab'.repeat(32);
const chunk = '/api/audits/capture-requests/22222222-2222-2222-2222-222222222222/chunks/0';

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
