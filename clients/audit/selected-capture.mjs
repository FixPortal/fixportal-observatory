import { randomUUID } from 'node:crypto';
import { performance } from 'node:perf_hooks';
import { isAbsolute, resolve } from 'node:path';
import { open, lstat } from 'node:fs/promises';
import { constants } from 'node:fs';
import { createBudget, validateCaps } from './budget.mjs';
import { createAuditTransport } from './transport.mjs';
import { captureEvidence, checkedRequest } from '../observatory-audit.mjs';
import { canonical, canonicalByteLength, sha256, utcTimestamp } from './protocol.mjs';
import { parseLossless, NumberLexeme } from './lossless-json.mjs';

const GUID = /^(?!00000000-0000-0000-0000-000000000000)[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const tuple = ['schemaVersion', 'runId', 'expectedRevision', 'fullScopeDigest', 'requestId', 'machineId', 'requestScopeDigest'];
function keys(value, required, optional = []) {
  if (!value || Object.getPrototypeOf(value) !== Object.prototype || required.some(name => !Object.hasOwn(value, name)) || Object.keys(value).some(name => ![...required, ...optional].includes(name))) throw new Error('invalid_selected_capture');
}
function time(value) { return Date.parse(utcTimestamp(value)); }
function parse(text) {
  const normalize = value => {
    if (value instanceof NumberLexeme) { const number = Number(value.text); if (!Number.isSafeInteger(number)) throw new Error('invalid_selected_lease'); return number; }
    if (Array.isArray(value)) return value.map(normalize);
    if (value && typeof value === 'object') return Object.fromEntries(Object.entries(value).map(([name, item]) => [name, normalize(item)]));
    return value;
  };
  return normalize(parseLossless(text));
}

export async function readSelectedConfiguration(path) {
  const maximum = 1024 * 1024;
  if (typeof path !== 'string' || !path || Buffer.byteLength(path) > 4096) throw new Error('invalid_selected_configuration');
  const named = await lstat(path);
  if (!named.isFile() || named.isSymbolicLink() || named.size > maximum) throw new Error('invalid_selected_configuration');
  const handle = await open(path, constants.O_RDONLY | (constants.O_NOFOLLOW ?? 0));
  try {
    const info = await handle.stat();
    if (!info.isFile() || info.size > maximum) throw new Error('invalid_selected_configuration');
    const buffer = Buffer.alloc(maximum + 1);
    let length = 0;
    while (length < buffer.length) {
      const { bytesRead } = await handle.read(buffer, length, buffer.length - length, null);
      if (!bytesRead) break;
      length += bytesRead;
    }
    if (length > maximum) throw new Error('invalid_selected_configuration');
    return parse(new TextDecoder('utf-8', { fatal: true }).decode(buffer.subarray(0, length)));
  } finally { await handle.close(); }
}

// Opt-in supervisor contract: one input byte, EOF or pipe error requests stop. No input is interpreted.
export function supervisedStop(stream, controller) {
  const stop = () => { controller.abort(); dispose(); };
  const readable = () => { if (stream.read(1) !== null) stop(); };
  const dispose = () => { stream.off('readable', readable); stream.off('end', stop); stream.off('error', stop); stream.pause(); };
  stream.on('readable', readable); stream.once('end', stop); stream.once('error', stop);
  if (stream.readableEnded || stream.destroyed) stop();
  return dispose;
}

function preflight(configuration, wallNow) {
  keys(configuration, ['origin', 'key', 'approval', 'requestScope', 'identityDirectory', 'sources', 'caps'], ['allowLoopbackHttp', 'includeRawHashes']);
  const caps = validateCaps(configuration.caps), approval = configuration.approval;
  keys(approval, [...tuple, 'authorityExpiresAt'], ['instanceId']);
  if (approval.schemaVersion !== 1 || !Number.isSafeInteger(approval.expectedRevision) || approval.expectedRevision < 0
    || ['runId', 'requestId', 'machineId'].some(name => typeof approval[name] !== 'string' || !GUID.test(approval[name]))
    || ['fullScopeDigest', 'requestScopeDigest'].some(name => typeof approval[name] !== 'string' || !/^[0-9a-f]{64}$/.test(approval[name]))
    || time(approval.authorityExpiresAt) <= wallNow()
    || approval.instanceId !== undefined && (typeof approval.instanceId !== 'string' || !approval.instanceId.length || approval.instanceId.length > 256)) throw new Error('invalid_selected_approval');
  if (!Array.isArray(configuration.sources) || configuration.sources.length !== 2
    || new Set(configuration.sources.map(source => source.tool)).size !== 2
    || !['claude', 'codex'].every(tool => configuration.sources.some(source => source.tool === tool))) throw new Error('invalid_selected_roots');
  const paths = [configuration.identityDirectory];
  for (const source of configuration.sources) {
    if (!Array.isArray(source.roots) || source.roots.length !== 1) throw new Error('invalid_selected_roots');
    paths.push(source.roots[0]);
  }
  if (paths.some(path => typeof path !== 'string' || !isAbsolute(path) || Buffer.byteLength(path) > caps.maxPathBytes)
    || new Set(paths.map(path => process.platform === 'win32' ? resolve(path).toLowerCase() : resolve(path))).size !== paths.length) throw new Error('invalid_selected_roots');
  const local = { identityDirectory: configuration.identityDirectory, sources: configuration.sources };
  if (configuration.includeRawHashes !== undefined) { if (typeof configuration.includeRawHashes !== 'boolean') throw new Error('invalid_selected_capture'); local.includeRawHashes = configuration.includeRawHashes; }
  const scope = checkedRequest({ captureId: approval.runId, requestId: approval.requestId, machineId: approval.machineId, scope: configuration.requestScope }, local);
  if (scope.sources.length !== 2 || new Set(scope.sources.map(source => source.sourceId)).size !== 2 || canonical(scope) !== canonical(configuration.requestScope) || sha256(canonical(scope)) !== approval.requestScopeDigest) throw new Error('invalid_selected_scope');
  const binding = Object.fromEntries(tuple.map(name => [name, approval[name]])), body = canonical(binding);
  if (Buffer.byteLength(body) > 1024) throw new Error('invalid_selected_capture');
  canonicalByteLength(configuration, { maximum: caps.maxMetadataBytes });
  return { approval, scope, local, body };
}

export async function selectedCapture(configuration, dependencies = {}) {
  const now = dependencies.now ?? (() => performance.now());
  const { approval, scope, local, body } = preflight(configuration, dependencies.wallNow ?? Date.now);
  let lease, leaseDeadline = -Infinity;
  const ensureLease = () => { if (lease && now() >= leaseDeadline) throw new Error('selected_lease_expired'); };
  const budget = createBudget(configuration.caps, { now, signal: dependencies.signal, guard: ensureLease });
  const transport = createAuditTransport(configuration, { fetch: dependencies.fetch, sleep: dependencies.sleep });
  const request = async renew => {
    budget.check();
    const started = now();
    const response = await transport.request('/api/audits/capture-requests/selected/' + (renew ? 'renew' : 'claim'), {
      method: 'POST', body, headers: { 'Content-Type': 'application/json', ...(renew ? { 'X-Observatory-Audit-Lease': lease.leaseToken } : {}) },
      budget, signal: budget.signal, ...(renew ? { remainingMs: () => leaseDeadline - now() } : {}),
    });
    const received = now();
    if (response.status !== 200) throw new Error('invalid_selected_lease');
    let next;
    try {
      next = parse(response.body);
      keys(next, [...tuple, 'instanceId', 'leaseToken', 'scope', 'expiresAt', 'serverObservedAt']);
      if (tuple.some(name => next[name] !== approval[name]) || typeof next.instanceId !== 'string' || !next.instanceId.length || next.instanceId.length > 256
        || approval.instanceId !== undefined && next.instanceId !== approval.instanceId
        || !GUID.test(next.leaseToken) || typeof next.leaseToken !== 'string'
        || canonical(next.scope) !== canonical(scope) || sha256(canonical(next.scope)) !== approval.requestScopeDigest
        || renew && (next.leaseToken !== lease.leaseToken || next.instanceId !== lease.instanceId)) throw new Error();
      const expiry = time(next.expiresAt), observed = time(next.serverObservedAt), authority = time(approval.authorityExpiresAt);
      // Date.parse truncates submilliseconds: subtract two ms instead of rounding authority upward.
      const remaining = expiry - observed - (received - started) - 2;
      if (!Number.isFinite(remaining) || received < started || remaining <= 0 || expiry > authority || observed >= authority) throw new Error();
      leaseDeadline = received + remaining;
    } catch { throw new Error('invalid_selected_lease'); }
    lease = next;
    budget.check();
  };
  // Exactly one fresh claim. Any uncertain response exits; no retained package or generic fallback.
  await request(false);
  const capture = await captureEvidence({ captureId: randomUUID(), requestId: approval.requestId, machineId: approval.machineId, scope }, local, {
    ...dependencies, now: () => new Date((dependencies.wallNow ?? Date.now)()), budget, signal: budget.signal, freshIdentity: true,
  });
  const ensureUpload = async () => {
    budget.check();
    if (leaseDeadline - now() <= Math.min(30000, budget.remainingMs())) await request(true);
    budget.check();
  };
  for (const chunk of capture.chunks) {
    await ensureUpload();
    const acknowledgement = await transport.request(`/api/audits/capture-requests/${approval.requestId}/chunks/${chunk.index}`, {
      method: 'PUT', body: chunk.ndjson, headers: { 'Content-Type': 'application/x-ndjson', 'X-Content-SHA256': chunk.sha256, 'X-Observatory-Audit-Lease': lease.leaseToken },
      budget, signal: budget.signal, remainingMs: () => leaseDeadline - now(),
    });
    if (acknowledgement.status !== 204 || acknowledgement.body !== '') throw new Error('invalid_selected_acknowledgement');
  }
  await ensureUpload();
  const manifestBytes = canonicalByteLength(capture.manifest, { budget, maximum: Math.min(budget.limit('maxMetadataBytes'), budget.available('encodedBytes')) });
  const envelopeBytes = '{"schemaVersion":1,"manifest":'.length + ',"sha256":"'.length + 64 + '"}'.length;
  if (manifestBytes + envelopeBytes > budget.limit('maxMetadataBytes')) throw new Error('capture_limit');
  budget.take('encodedBytes', manifestBytes + envelopeBytes);
  const manifest = canonical(capture.manifest), hash = sha256(manifest);
  const acknowledgement = await transport.request(`/api/audits/capture-requests/${approval.requestId}/seal`, {
    body: `{"schemaVersion":1,"manifest":${manifest},"sha256":"${hash}"}`, headers: { 'Content-Type': 'application/json', 'X-Observatory-Audit-Lease': lease.leaseToken, 'Idempotency-Key': hash },
    budget, signal: budget.signal, remainingMs: () => leaseDeadline - now(), sealing: true,
  });
  try {
    if (acknowledgement.status !== 200) throw new Error();
    const result = parse(acknowledgement.body);
    keys(result, ['captureId']);
    if (typeof result.captureId !== 'string' || !GUID.test(result.captureId) || result.captureId !== capture.manifest.captureId) throw new Error();
  } catch { throw new Error('invalid_selected_acknowledgement'); }
  budget.check();
  return { status: 'sealed', capture, budget: budget.snapshot() };
}
