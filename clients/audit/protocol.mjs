import { createHash } from 'node:crypto';
import { counter } from './lossless-json.mjs';

export const EXTRACTOR_VERSION = '1.1.0';
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
  const id = (domain, value) => value == null ? null : context.id(domain, value);
  return { machineId: context.machineId, sourceId: context.sourceId, sourceFormat: `${tool}-jsonl`, sourceVersion: '1', tool, kind: 'sessionMetadata', generation: context.generation, fileId: context.fileId, sessionId: id('session', native.sessionId ?? state.sessionId), messageId: null, agentId: id('agent', native.agentId ?? state.agentId), parentId: id('session', native.parentSessionId ?? state.parentSessionId), sequence: BigInt(sequence), timestamp: utcTimestamp(native.timestamp), model: identifier(state.model, true), provider: identifier(state.provider), counters: null, cost: null, currency: null, pricing: pricing({}, state), closure: { state: 'unknown', timestamp: null, sequence: null } };
}

export function diagnosticCounts(codes) {
  const counts = new Map();
  for (const code of codes) counts.set(code, (counts.get(code) ?? 0n) + 1n);
  return [...counts].sort(([a], [b]) => a < b ? -1 : a > b ? 1 : 0).map(([code, count]) => ({ code, count }));
}

export const SAFE_DIAGNOSTICS = new Set(['source_line_limit', 'source_depth_limit', 'malformed_json', 'duplicate_property', 'invalid_counter', 'counter_range', 'invalid_timestamp', 'timestamp_precision', 'invalid_source_identifier', 'unsupported_source_format', 'invalid_usage_shape', 'missing_session_identity', 'invalid_source_money']);

export function extractLines(text, consume) {
  const records = [], codes = [];
  // Buffer bound is enforced by capture; direct extraction is bounded too.
  if (typeof text !== 'string' || Buffer.byteLength(text) > 512 * 1024 * 1024) return { records, diagnostics: [{ code: 'source_file_limit', count: 1n }] };
  const lines = text.split('\n');
  let recognized = false;
  for (let i = 0; i < lines.length; i++) {
    if (!lines[i].trim()) continue;
    try { recognized = consume(lines[i], i + 1, records) || recognized; }
    catch (error) { codes.push(SAFE_DIAGNOSTICS.has(error.message) ? error.message : 'invalid_usage_shape'); }
  }
  if (!recognized) codes.push('unsupported_source_format');
  return { records, diagnostics: diagnosticCounts(codes) };
}
