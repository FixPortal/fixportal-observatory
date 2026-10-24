import { createHash } from 'node:crypto';
import { counter } from './lossless-json.mjs';
import { CaptureLimit } from './budget.mjs';

export const EXTRACTOR_VERSION = '1.1.0';
export const SOURCE_VERSIONS = Object.freeze({ claude: '2', codex: '1' });
export const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');

function quote(value) {
  let output = '"';
  for (let i = 0; i < value.length; i++) {
    const c = value[i], n = value.charCodeAt(i);
    const short = { '\b': '\\b', '\t': '\\t', '\n': '\\n', '\f': '\\f', '\r': '\\r', '\\': '\\\\' }[c];
    if (short) output += short;
    else if (n < 32 || n > 126 || '"\'&+<>`'.includes(c)) output += '\\u' + n.toString(16).toUpperCase().padStart(4, '0');
    else output += c;
  }
  return output + '"';
}

export function canonical(value) {
  if (value === null) return 'null';
  if (typeof value === 'bigint') return JSON.stringify(JSON.rawJSON(value.toString()));
  if (typeof value === 'string') return quote(value);
  if (typeof value === 'boolean') return String(value);
  if (typeof value === 'number' && Number.isSafeInteger(value)) return String(value);
  if (Array.isArray(value)) return '[' + value.map(canonical).join(',') + ']';
  if (value && Object.getPrototypeOf(value) === Object.prototype) return '{' + Object.keys(value).sort().map(key => quote(key) + ':' + canonical(value[key])).join(',') + '}';
  throw new Error('invalid_wire_value');
}

// Count the canonical ASCII wire representation without allocating that representation.
const shortEscape = new Set([8, 9, 10, 12, 13, 92]);
const longEscape = new Set([34, 39, 38, 43, 60, 62, 96]);
export function canonicalByteLength(value, { budget, maximum = 512 * 1024 * 1024 } = {}) {
  let length = 0;
  const add = count => { if (count > maximum - length) throw new CaptureLimit(); length += count; };
  const string = text => {
    add(2);
    for (let index = 0; index < text.length; index++) {
      if (index % 512 === 0) budget?.check();
      const code = text.charCodeAt(index);
      add(shortEscape.has(code) ? 2 : code < 32 || code > 126 || longEscape.has(code) ? 6 : 1);
    }
  };
  const visit = (item, depth = 0) => {
    budget?.check();
    if (depth > 64) throw new Error('invalid_wire_value');
    if (item === null) add(4);
    else if (typeof item === 'string') string(item);
    else if (typeof item === 'boolean') add(item ? 4 : 5);
    else if (typeof item === 'bigint' || typeof item === 'number' && Number.isSafeInteger(item)) add(String(item).length);
    else if (Array.isArray(item)) {
      add(2);
      for (let index = 0; index < item.length; index++) { if (index) add(1); visit(item[index], depth + 1); }
    } else if (item && Object.getPrototypeOf(item) === Object.prototype) {
      add(2); let first = true;
      // Ordering cannot change length; avoid sorting/allocation of encoded key/value strings.
      for (const name of Object.keys(item)) { if (!first) add(1); first = false; string(name); add(1); visit(item[name], depth + 1); }
    } else throw new Error('invalid_wire_value');
  };
  visit(value);
  return length;
}

export function utcTimestamp(text) {
  if (typeof text !== 'string') throw new Error('invalid_timestamp');
  const match = /^(\d{4}-\d{2}-\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.(\d+))?(Z|[+-]\d{2}:\d{2})$/.exec(text);
  if (!match) throw new Error('invalid_timestamp');
  const [, day, hour, minute, second, fraction = '', offset] = match;
  if (fraction.length > 7) throw new Error('timestamp_precision');
  if (day.startsWith('0000') || +hour > 23 || +minute > 59 || +second > 59 || new Date(day + 'T00:00:00Z').toISOString().slice(0, 10) !== day) throw new Error('invalid_timestamp');
  if (offset !== 'Z' && (+offset.slice(1, 3) > 14 || +offset.slice(4) > 59 || +offset.slice(1, 3) === 14 && +offset.slice(4) !== 0)) throw new Error('invalid_timestamp');
  const date = new Date(`${day}T${hour}:${minute}:${second}${offset}`);
  if (!Number.isFinite(date.getTime()) || date.getUTCFullYear() < 1 || date.getUTCFullYear() > 9999) throw new Error('invalid_timestamp');
  return date.toISOString().slice(0, 19) + '.' + fraction.padEnd(7, '0') + 'Z';
}

export function identifier(value, model = false) {
  if (value === undefined || value === null) return null;
  if (typeof value !== 'string' || !value.length || value.length > 256 || !(model ? /^[A-Za-z0-9_:.@|=/-]+$/ : /^[A-Za-z0-9_:.@|=-]+$/).test(value) || value.startsWith('/') || value.includes(':/') || value.includes('..')) throw new Error('invalid_source_identifier');
  return value;
}

export function pricing(usage = {}, metadata = {}) {
  const tier = identifier(usage.service_tier ?? metadata.service_tier);
  const speed = identifier(usage.speed ?? metadata.speed);
  return { tier: ['standard', 'priority', 'flex', 'fast'].includes(tier) ? tier : 'unknown', batch: usage.batch === true || metadata.batch === true, region: identifier(usage.region ?? metadata.region), contextTokens: counter(usage.model_context_window ?? metadata.model_context_window), sourceServiceTier: tier, speed, inferenceGeo: identifier(usage.inference_geo ?? metadata.inference_geo) };
}

export function recordBase(tool, context, sequence, native, state = {}) {
  context.budget?.take('records', 1, true);
  const id = (domain, value) => value == null ? null : context.id(domain, value);
  return { machineId: context.machineId, sourceId: context.sourceId, sourceFormat: `${tool}-jsonl`, sourceVersion: SOURCE_VERSIONS[tool], tool, kind: 'sessionMetadata', generation: context.generation, fileId: context.fileId, sessionId: id('session', native.sessionId ?? state.sessionId), messageId: null, agentId: id('agent', native.agentId ?? state.agentId), parentId: id('session', native.parentSessionId ?? state.parentSessionId), sequence: BigInt(sequence), timestamp: utcTimestamp(native.timestamp), model: identifier(state.model, true), provider: identifier(state.provider), counters: null, cost: null, currency: null, pricing: pricing({}, state), closure: { state: 'unknown', timestamp: null, sequence: null } };
}

// Extraction admits one retained group per file/code, not one allocation per occurrence.
// Terminal limit markers can still describe exhaustion after ordinary admission stops.
export function countedDiagnostics(budget) {
  const counts = new Map();
  return {
    add(code, count = 1n) {
      if (typeof code !== 'string' || typeof count !== 'bigint' || count < 1n) throw new Error('invalid_diagnostic_count');
      if (code !== 'capture_limit') budget?.check(true);
      if (!counts.has(code) && code !== 'capture_limit') budget?.take('diagnostics', 1, true);
      counts.set(code, (counts.get(code) ?? 0n) + count);
    },
    has(code) { return counts.has(code); },
    get size() { return counts.size; },
    toArray() { return [...counts].sort(([a], [b]) => a < b ? -1 : a > b ? 1 : 0).map(([code, count]) => ({ code, count })); },
  };
}

export function diagnosticCounts(codes) {
  const counts = countedDiagnostics();
  for (const code of codes) counts.add(code);
  return counts.toArray();
}

export const SAFE_DIAGNOSTICS = new Set(['source_line_limit', 'source_depth_limit', 'malformed_json', 'duplicate_property', 'invalid_counter', 'counter_range', 'invalid_timestamp', 'timestamp_precision', 'invalid_source_identifier', 'unsupported_source_format', 'invalid_usage_shape', 'missing_session_identity', 'invalid_source_money']);

export function extractLines(text, consume, dependencies = {}) {
  const budget = dependencies.budget;
  const records = [], codes = countedDiagnostics(budget);
  // Buffer bound is enforced by capture; direct extraction is bounded too.
  if (typeof text !== 'string' || Buffer.byteLength(text) > 512 * 1024 * 1024) return { records, diagnostics: [{ code: 'source_file_limit', count: 1n }] };
  if (budget) Object.defineProperty(records, 'push', { value(...items) {
    for (const item of items) {
      budget.check(true);
      const maximum = Math.min(budget.limit('maxLineBytes'), budget.limit('maxChunkBytes'));
      const bytes = canonicalByteLength(item, { budget, maximum: maximum - 1 }) + 1;
      budget.take('encodedBytes', bytes, true);
      Array.prototype.push.call(records, item);
    }
    return records.length;
  } });
  let recognized = false;
  const stopped = error => {
    if (!error.captureLimit) throw error;
    budget?.stopNative();
    codes.add('capture_limit');
  };
  let start = 0, sequence = 0;
  for (; start <= text.length;) {
    try {
      budget?.take('lines', 1, true);
      const newline = text.indexOf('\n', start), end = newline < 0 ? text.length : newline;
      const lineStart = start;
      start = end + 1; sequence++;
      const maxLine = budget?.limit('maxLineBytes') ?? 1024 * 1024;
      if (end - lineStart > maxLine) throw Object.assign(new Error(budget ? 'capture_limit' : 'source_line_limit'), { captureLimit: !!budget });
      const line = text.slice(lineStart, end);
      if (Buffer.byteLength(line) > maxLine) throw Object.assign(new Error(budget ? 'capture_limit' : 'source_line_limit'), { captureLimit: !!budget });
      if (!line.trim()) continue;
      budget?.room('records', 1, true);
      recognized = consume(line, sequence, records) || recognized;
    } catch (error) {
      if (error.captureLimit) { stopped(error); break; }
      try {
        budget?.check(true);
        codes.add(SAFE_DIAGNOSTICS.has(error.message) ? error.message : 'invalid_usage_shape');
      } catch (limit) { stopped(limit); break; }
    }
  }
  if (!recognized && !codes.has('capture_limit')) {
    try { codes.add('unsupported_source_format'); }
    catch (error) { stopped(error); }
  }
  return { records, diagnostics: codes.toArray() };
}
