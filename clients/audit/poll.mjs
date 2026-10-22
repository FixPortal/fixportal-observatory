import { randomUUID } from 'node:crypto';
import { lstat, readFile, rename, rm } from 'node:fs/promises';
import { join } from 'node:path';
import { setTimeout as sleep } from 'node:timers/promises';
import { captureEvidence, writeCapture } from '../observatory-audit.mjs';
import { privateDirectory } from './identity.mjs';
import { createAuditTransport } from './transport.mjs';
import { canonical, sha256, utcTimestamp } from './protocol.mjs';
import { parseLossless, NumberLexeme } from './lossless-json.mjs';

const GUID = /^(?!00000000-0000-0000-0000-000000000000)[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
function keys(value, allowed) {
  if (!value || typeof value !== 'object' || Array.isArray(value) || Object.keys(value).some(name => !allowed.includes(name))) throw new Error('invalid_audit_lease');
}
function parse(text) {
  const normalize = value => {
    if (value instanceof NumberLexeme) { const number = Number(value.text); if (!Number.isSafeInteger(number)) throw new Error('invalid_audit_lease'); return number; }
    if (Array.isArray(value)) return value.map(normalize);
    if (value && typeof value === 'object') return Object.fromEntries(Object.entries(value).map(([key, item]) => [key, normalize(item)]));
    return value;
  };
  return normalize(parseLossless(text));
}
function checkedLease(text, machineId) {
  try {
    const lease = parse(text);
    keys(lease, ['schemaVersion', 'requestId', 'machineId', 'leaseToken', 'expiresAt', 'scope']);
    if (lease.schemaVersion !== 1 || lease.machineId !== machineId || ![lease.requestId, lease.machineId, lease.leaseToken].every(id => typeof id === 'string' && GUID.test(id))) throw new Error();
    utcTimestamp(lease.expiresAt);
    keys(lease.scope, ['from', 'to', 'sources', 'requestedChecks', 'excludedChecks', 'allowPartial', 'predecessorRunId', 'evaluationOfRunId']);
    if (!Array.isArray(lease.scope.sources) || lease.scope.sources.some(source => { keys(source, ['machineId', 'sourceId']); return source.machineId !== machineId; })) throw new Error();
    return lease;
  } catch { throw new Error('invalid_audit_lease'); }
}
async function retained(directory, lease) {
  try {
    const info = await lstat(directory);
    if (!info.isDirectory() || info.isSymbolicLink()) throw new Error();
    const file = join(directory, 'manifest.json');
    if ((await lstat(file)).size > 1024 * 1024 || (await lstat(file)).isSymbolicLink()) throw new Error();
    const manifestBytes = await readFile(file), manifest = parse(manifestBytes.toString('utf8'));
    if (manifest.requestId !== lease.requestId || manifest.machineId !== lease.machineId || canonical(manifest.scope) !== canonical(lease.scope)
      || !Array.isArray(manifest.chunkHashes) || manifest.chunkHashes.length > 10000 || canonical(manifest) !== manifestBytes.toString('utf8')) throw new Error();
    const chunks = []; let bytes = manifestBytes.length;
    for (const [index, hash] of manifest.chunkHashes.entries()) {
      const path = join(directory, 'chunks', String(index).padStart(6, '0') + '.ndjson');
      const stat = await lstat(path);
      if (!stat.isFile() || stat.isSymbolicLink() || stat.size > 8 * 1024 * 1024 || (bytes += stat.size) > 512 * 1024 * 1024) throw new Error();
      const ndjson = await readFile(path);
      if (sha256(ndjson) !== hash) throw new Error();
      chunks.push({ index, sha256: hash, ndjson });
    }
    return { manifestBytes, chunks };
  } catch (error) { if (error.code === 'ENOENT') throw error; throw new Error('audit_retained_package_invalid'); }
}

export async function pollOnce(configuration, dependencies = {}) {
  keys(configuration, ['origin', 'key', 'allowLoopbackHttp', 'machineId', 'stateDirectory', 'identityDirectory', 'sources', 'limits', 'includeRawHashes']);
  if (!GUID.test(configuration.machineId) || typeof configuration.stateDirectory !== 'string' || !configuration.stateDirectory) throw new Error('invalid_audit_configuration');
  const transport = dependencies.transport ?? createAuditTransport(configuration);
  const claim = () => transport.request('/api/audits/capture-requests/claim', { method: 'POST', body: '{"schemaVersion":1}', headers: { 'Content-Type': 'application/json' }, signal: dependencies.signal });
  const response = await claim();
  if (response.status === 204) return 'idle';
  const lease = checkedLease(response.body, configuration.machineId);
  let renewalFailure, renewing = Promise.resolve();
  const timer = setInterval(() => {
    renewing = renewing.then(async () => {
      const renewed = await claim();
      if (renewed.status !== 200) throw new Error('audit_lease_lost');
      const next = checkedLease(renewed.body, configuration.machineId);
      if (next.requestId !== lease.requestId || next.leaseToken !== lease.leaseToken) throw new Error('audit_lease_lost');
      lease.expiresAt = next.expiresAt;
    }).catch(() => { renewalFailure = new Error('audit_lease_lost'); });
  }, 60000);
  const ensureLive = async () => { await renewing; if (renewalFailure) throw renewalFailure; if (dependencies.signal?.aborted) throw new Error('audit_poll_stopped'); };
  try {
    await privateDirectory(configuration.stateDirectory);
    const directory = join(configuration.stateDirectory, lease.requestId + '-' + lease.leaseToken);
    let capture;
    try { capture = await retained(directory, lease); }
    catch (error) {
      // Only an entirely absent package may start a scan. An incomplete/corrupt retained package fails closed.
      try { await lstat(directory); } catch (missing) { if (missing.code !== 'ENOENT') throw new Error('audit_retained_package_invalid'); }
      if (error.code !== 'ENOENT') throw error;
      let exists = true; try { await lstat(directory); } catch { exists = false; }
      if (exists) throw new Error('audit_retained_package_invalid');
      const local = { identityDirectory: configuration.identityDirectory, sources: configuration.sources };
      for (const name of ['limits', 'includeRawHashes']) if (configuration[name] !== undefined) local[name] = configuration[name];
      const scanned = await captureEvidence({ captureId: randomUUID(), requestId: lease.requestId, machineId: lease.machineId, scope: lease.scope }, local);
      const staging = directory + '-' + randomUUID();
      try {
        await writeCapture(scanned, staging); await ensureLive();
        try { await rename(staging, directory); }
        catch (renameError) { if (!['EEXIST', 'ENOTEMPTY', 'EPERM'].includes(renameError.code)) throw renameError; }
      } finally { await rm(staging, { recursive: true, force: true }); }
      capture = await retained(directory, lease);
    }
    for (const chunk of capture.chunks) {
      await ensureLive();
      await transport.request(`/api/audits/capture-requests/${lease.requestId}/chunks/${chunk.index}`, { method: 'PUT', body: chunk.ndjson, headers: { 'Content-Type': 'application/x-ndjson', 'X-Content-SHA256': chunk.sha256, 'X-Observatory-Audit-Lease': lease.leaseToken }, signal: dependencies.signal, deadline: Date.parse(lease.expiresAt) });
    }
    await ensureLive();
    await transport.request(`/api/audits/capture-requests/${lease.requestId}/seal`, { body: `{"schemaVersion":1,"manifest":${capture.manifestBytes.toString('utf8')},"sha256":"${sha256(capture.manifestBytes)}"}`, headers: { 'Content-Type': 'application/json', 'X-Observatory-Audit-Lease': lease.leaseToken, 'Idempotency-Key': sha256(capture.manifestBytes) }, signal: dependencies.signal, deadline: Date.parse(lease.expiresAt) });
    return 'sealed';
  } finally { clearInterval(timer); await renewing; }
}

export async function poll(configuration, dependencies = {}) {
  do {
    try { const status = await pollOnce(configuration, dependencies); dependencies.log?.(`audit_poll_${status}`); }
    catch { dependencies.log?.('audit_poll_failed'); if (dependencies.once) throw new Error('audit_poll_failed'); }
    if (dependencies.once || dependencies.signal?.aborted) return;
    const jitter = Math.min(10000, Math.floor(Math.max(0, Math.min(1, (dependencies.random ?? Math.random)())) * 10001)) - 5000;
    await (dependencies.sleep ?? sleep)(60000 + jitter, undefined, { signal: dependencies.signal });
  } while (!dependencies.signal?.aborted);
}
