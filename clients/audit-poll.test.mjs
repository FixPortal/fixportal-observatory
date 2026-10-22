import test from 'node:test';
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { once } from 'node:events';
import { spawn } from 'node:child_process';
import { mkdtemp, mkdir, writeFile, rm, readFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createAuditTransport } from './audit/transport.mjs';
import { pollOnce, poll } from './audit/poll.mjs';

const key = '11111111-1111-1111-1111-111111111111.' + 'ab'.repeat(32);
const machineId = '11111111-1111-1111-1111-111111111111';
const requestId = '22222222-2222-2222-2222-222222222222';
const leaseToken = '33333333-3333-3333-3333-333333333333';
const lease = () => ({ schemaVersion: 1, requestId, machineId, leaseToken, expiresAt: new Date(Date.now() + 300000).toISOString(), scope: { from: '2026-10-19', to: '2026-10-21', sources: [{ machineId, sourceId: 'claude-local' }], requestedChecks: ['sourceAccounting'], excludedChecks: [], allowPartial: false, predecessorRunId: null, evaluationOfRunId: null } });

async function server(t, handler) {
  const app = createServer(handler); app.listen(0, '127.0.0.1'); await once(app, 'listening');
  t.after(() => new Promise(resolve => app.close(resolve)));
  return `http://127.0.0.1:${app.address().port}`;
}

test('transport sends scoped header only to configured origin and rejects redirects', async t => {
  let requests = 0;
  const origin = await server(t, (req, res) => { requests++; assert.equal(req.headers['x-observatory-audit-key'], key); res.writeHead(302, { location: '/private' }); res.end('PLANTED_SECRET'); });
  const transport = createAuditTransport({ origin, key, allowLoopbackHttp: true });
  await assert.rejects(transport.request('https://evil.test/api/audits/capture-requests/claim'), /invalid_audit_path/);
  await assert.rejects(transport.request('//evil.test/api/audits/capture-requests/claim'), /invalid_audit_path/);
  await assert.rejects(transport.request('/api/audits/capture-requests/claim'), /^Error: audit_redirect_blocked$/);
  assert.equal(requests, 1);
});

test('transport requires TLS except explicit loopback development configuration', () => {
  for (const origin of ['http://example.test', 'http://127.0.0.1', 'https://user:secret@example.test', 'https://example.test/path', 'https://example.test?key=secret']) assert.throws(() => createAuditTransport({ origin, key }), /invalid_audit_transport/);
  assert.throws(() => createAuditTransport({ origin: 'http://example.test', key, allowLoopbackHttp: true }), /invalid_audit_transport/);
});

test('transport retries transient failures at most three times and exposes only sanitized errors', async t => {
  let count = 0;
  const origin = await server(t, (_req, res) => { count++; res.writeHead(503); res.end('PLANTED_SECRET'); });
  const transport = createAuditTransport({ origin, key, allowLoopbackHttp: true }, { sleep: async () => {} });
  await assert.rejects(transport.request('/api/audits/capture-requests/claim'), /^Error: audit_http_503$/);
  assert.equal(count, 3);
});

test('transport bounds responses and does not retry authentication failures', async t => {
  let count = 0;
  const origin = await server(t, (_req, res) => { count++; res.writeHead(401); res.end('PLANTED_SECRET'); });
  await assert.rejects(createAuditTransport({ origin, key, allowLoopbackHttp: true }).request('/api/audits/capture-requests/claim'), /^Error: audit_http_401$/);
  assert.equal(count, 1);
  const large = await server(t, (_req, res) => res.end('a'.repeat(1024 * 1024 + 1)));
  await assert.rejects(createAuditTransport({ origin: large, key, allowLoopbackHttp: true }).request('/api/audits/capture-requests/claim'), /audit_response_limit/);
});

async function workspace(t) {
  const dir = await mkdtemp(join(tmpdir(), 'audit-poll-synthetic-')); t.after(() => rm(dir, { force: true, recursive: true }));
  const root = join(dir, 'source'); await mkdir(root);
  await writeFile(join(root, 'source.jsonl'), JSON.stringify({ type: 'assistant', sessionId: 's', uuid: 'm', timestamp: '2026-10-20T12:00:00Z', message: { id: 'm', model: 'claude', content: 'PLANTED_SECRET', usage: { input_tokens: 1, output_tokens: 2 } } }) + '\n');
  return { root, configuration: { machineId, stateDirectory: join(dir, 'state'), identityDirectory: join(dir, 'identity'), sources: [{ sourceId: 'claude-local', tool: 'claude', roots: [root] }] } };
}

test('poll retains exact package before upload and restart reuses it after local source changes', async t => {
  const { root, configuration } = await workspace(t); let failSeal = true; const sent = [];
  const origin = await server(t, async (req, res) => {
    const parts = []; for await (const part of req) parts.push(part); const body = Buffer.concat(parts);
    if (req.url.endsWith('/claim')) { res.setHeader('content-type', 'application/json'); res.end(JSON.stringify(lease())); }
    else if (req.method === 'PUT') { sent.push(body); res.writeHead(204); res.end(); }
    else { assert.doesNotMatch(body.toString(), /PLANTED_SECRET|rawHashes/); if (failSeal) { res.writeHead(409); res.end(); } else res.end('{"captureId":"22222222-2222-2222-2222-222222222222"}'); }
  });
  const transport = createAuditTransport({ origin, key, allowLoopbackHttp: true });
  await assert.rejects(pollOnce(configuration, { transport }), /audit_http_409/);
  await writeFile(join(root, 'source.jsonl'), 'changed invalid source'); failSeal = false;
  assert.equal(await pollOnce(configuration, { transport }), 'sealed');
  assert.equal(sent.length, 2); assert.deepEqual(sent[0], sent[1]);
  assert.equal(await readFile(join(configuration.stateDirectory, requestId + '-' + leaseToken, 'chunks', '000000.ndjson'), 'utf8'), sent[0].toString());
});

test('poll rejects server supplied roots URL command and cross-machine scope before capture', async t => {
  const { configuration } = await workspace(t);
  for (const change of [{ roots: ['/private'] }, { url: 'https://evil.test' }, { command: 'private' }, { machineId: '44444444-4444-4444-4444-444444444444' }]) {
    const origin = await server(t, (_req, res) => res.end(JSON.stringify({ ...lease(), ...change })));
    await assert.rejects(pollOnce(configuration, { transport: createAuditTransport({ origin, key, allowLoopbackHttp: true }) }), /invalid_audit_lease/);
  }
});

test('one-shot poll with no assignment does not capture local evidence', async t => {
  const origin = await server(t, (_req, res) => { res.writeHead(204); res.end(); });
  assert.equal(await pollOnce({ machineId, stateDirectory: 'unused', identityDirectory: 'unused', sources: [] }, { transport: createAuditTransport({ origin, key, allowLoopbackHttp: true }) }), 'idle');
});

test('continuous polling uses bounded sixty second jitter and one-shot never waits', async t => {
  const origin = await server(t, (_req, res) => { res.writeHead(204); res.end(); });
  const configuration = { machineId, stateDirectory: 'unused', identityDirectory: 'unused', sources: [] };
  const transport = createAuditTransport({ origin, key, allowLoopbackHttp: true });
  for (const [random, expected] of [[0, 55000], [0.5, 60000], [1, 65000]]) {
    const controller = new AbortController(); const delays = [], logs = [];
    await poll(configuration, { transport, signal: controller.signal, random: () => random, log: message => logs.push(message), sleep: async ms => { delays.push(ms); controller.abort(); } });
    assert.deepEqual(delays, [expected]); assert.deepEqual(logs, ['audit_poll_idle']);
  }
  await poll(configuration, { transport, once: true, sleep: async () => { assert.fail('one-shot waited'); } });
});

test('active upload renews the same lease every minute and refuses changed ownership before seal', async t => {
  t.mock.timers.enable({ apis: ['setInterval'] });
  const { configuration } = await workspace(t); let claims = 0, seals = 0;
  const origin = await server(t, async (req, res) => {
    for await (const _part of req) { /* Drain the synthetic request. */ }
    if (req.url.endsWith('/claim')) { claims++; res.end(JSON.stringify({ ...lease(), leaseToken: claims > 1 ? '44444444-4444-4444-4444-444444444444' : leaseToken })); }
    else if (req.method === 'PUT') { t.mock.timers.tick(60000); res.writeHead(204); res.end(); }
    else { seals++; res.end('{}'); }
  });
  await assert.rejects(pollOnce(configuration, { transport: createAuditTransport({ origin, key, allowLoopbackHttp: true }) }), /audit_lease_lost/);
  assert.equal(claims, 2); assert.equal(seals, 0);
});

test('poll CLI once exits after one claim and logs no credential or response body', async t => {
  const { configuration } = await workspace(t); let claims = 0;
  const origin = await server(t, (_req, res) => { claims++; res.writeHead(204); res.end(); });
  const file = join(configuration.stateDirectory + '-config.json');
  t.after(() => rm(file, { force: true }));
  await writeFile(file, JSON.stringify({ ...configuration, origin, key, allowLoopbackHttp: true }));
  const child = spawn(process.execPath, ['clients/observatory-audit.mjs', 'poll', '--config', file, '--once'], { windowsHide: true });
  let output = '', errors = '';
  child.stdout.on('data', value => { output += value; }); child.stderr.on('data', value => { errors += value; });
  const [code] = await once(child, 'exit');
  assert.equal(code, 0); assert.equal(claims, 1); assert.equal(output, 'audit_poll_idle\n'); assert.equal(errors, '');
});
