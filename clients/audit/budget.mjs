import { performance } from 'node:perf_hooks';

const ceilings = { maxFiles: 10000, maxRecords: 1000000, maxFileBytes: 512 * 1024 * 1024, maxLineBytes: 1024 * 1024, maxChunkBytes: 8 * 1024 * 1024, maxEncodedBytes: 512 * 1024 * 1024, maxMetadataBytes: 1024 * 1024, maxDurationMs: 2147483647 };
const counters = { directoryEntries: 'maxDirectoryEntries', queuedPaths: 'maxQueuedPaths', files: 'maxFiles', readBytes: 'maxReadBytes', lines: 'maxLines', records: 'maxRecords', diagnostics: 'maxDiagnostics', encodedBytes: 'maxEncodedBytes', httpAttempts: 'maxHttpAttempts', uploadBytes: 'maxUploadBytes', ownedWriteBytes: 'maxOwnedWriteBytes', ownedEntries: 'maxOwnedEntries' };
const names = [...Object.values(counters), 'maxDepth', 'maxFileBytes', 'maxLineBytes', 'maxChunkBytes', 'maxDurationMs', 'sealReserveAttempts', 'sealReserveBytes', 'sealReserveMs', 'maxPathBytes', 'maxMetadataBytes'];

export class CaptureLimit extends Error {
  constructor() { super('capture_limit'); this.captureLimit = true; }
}

export function validateCaps(caps) {
  if (!caps || Object.getPrototypeOf(caps) !== Object.prototype || Object.keys(caps).length !== names.length
    || names.some(name => !Object.hasOwn(caps, name) || !Number.isSafeInteger(caps[name]) || caps[name] < (name === 'maxDepth' ? 0 : 1) || caps[name] > (ceilings[name] ?? Number.MAX_SAFE_INTEGER))
    || caps.sealReserveAttempts >= caps.maxHttpAttempts || caps.sealReserveBytes >= caps.maxUploadBytes || caps.sealReserveMs >= caps.maxDurationMs
    || caps.maxChunkBytes > caps.maxEncodedBytes || caps.maxLineBytes > caps.maxFileBytes) throw new Error('invalid_capture_caps');
  return Object.freeze({ ...caps });
}

export function createBudget(configuration, dependencies = {}) {
  const caps = validateCaps(configuration), now = dependencies.now ?? (() => performance.now());
  const started = now(), deadline = started + caps.maxDurationMs, counts = Object.fromEntries(Object.keys(counters).map(name => [name, 0]));
  let nativeStopped = false;
  const timeout = AbortSignal.timeout(caps.maxDurationMs);
  const signal = dependencies.signal ? AbortSignal.any([timeout, dependencies.signal]) : timeout;
  const fail = () => { nativeStopped = true; throw new CaptureLimit(); };
  const budget = {
    signal,
    limit(name) { if (!Object.hasOwn(caps, name)) throw new Error('invalid_budget_counter'); return caps[name]; },
    remainingMs() { return Math.max(0, deadline - now()); },
    check(native = false) {
      if (signal.aborted) throw new Error('capture_stopped');
      dependencies.guard?.();
      if (budget.remainingMs() <= 0 || native && (nativeStopped || budget.remainingMs() <= caps.sealReserveMs)) fail();
    },
    room(name, amount = 1, native = false, sealing = false) {
      budget.check(native);
      if (!Object.hasOwn(counters, name) || !Number.isSafeInteger(amount) || amount < 0) throw new Error('invalid_budget_counter');
      const reserve = sealing ? 0 : name === 'httpAttempts' ? caps.sealReserveAttempts : name === 'uploadBytes' ? caps.sealReserveBytes : 0;
      if (amount > caps[counters[name]] - reserve - counts[name]) fail();
    },
    available(name) {
      budget.check();
      if (!Object.hasOwn(counters, name)) throw new Error('invalid_budget_counter');
      return caps[counters[name]] - counts[name];
    },
    take(name, amount = 1, native = false, sealing = false) {
      budget.room(name, amount, native, sealing);
      counts[name] += amount;
    },
    stopNative() { nativeStopped = true; },
    ownedWrite(path, data, entry = true) {
      budget.check();
      if (typeof path !== 'string' || Buffer.byteLength(path) > caps.maxPathBytes) fail();
      if (entry) budget.take('ownedEntries');
      budget.take('ownedWriteBytes', data == null ? 0 : typeof data === 'number' ? data : Buffer.byteLength(data));
    },
    snapshot() { return { ...counts, nativeStopped, elapsedMs: now() - started }; },
  };
  return budget;
}
