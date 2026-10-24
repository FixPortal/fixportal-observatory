import { parseLossless, counter } from './lossless-json.mjs';
import { extractLines, recordBase, pricing, identifier, utcTimestamp, canonical } from './protocol.mjs';
import { CaptureLimit } from './budget.mjs';

// Only complete, representable cumulative vectors can corroborate a ledger.
// The sixth producer counter is unsupported by this evidence version: never hide it.
function corroborationVector(usage) {
  try {
    const counters = usageCounters(usage);
    if (Object.values(counters).some(value => value === null)) return null;
    if (Object.keys(usage).some(key => !Object.hasOwn(counters, key) && key !== 'cache_write_input_tokens')) return null;
    if (Object.hasOwn(usage, 'cache_write_input_tokens') && counter(usage.cache_write_input_tokens) !== 0n) return null;
    if (counters.cached_input_tokens > counters.input_tokens || counters.reasoning_output_tokens > counters.output_tokens || counters.total_tokens !== counters.input_tokens + counters.output_tokens) return null;
    return Object.values(counters).join(',');
  } catch { return null; }
}

function usageCounters(usage) {
  if (!usage || typeof usage !== 'object' || Array.isArray(usage)) throw new Error('invalid_usage_shape');
  const result = Object.fromEntries(['input_tokens', 'cached_input_tokens', 'output_tokens', 'reasoning_output_tokens', 'total_tokens'].map(key => [key, counter(usage[key])]));
  if (Object.values(result).every(value => value === null)) throw new Error('invalid_usage_shape');
  return result;
}

export function extractCodexEvidence(text, identityContext, dependencies = {}) {
  identityContext = { ...identityContext, budget: dependencies.budget };
  let state = {};
  let boundSession = false, boundContext = false;
  const corroborationKey = (timestamp, vector) => canonical([utcTimestamp(timestamp).slice(0, 10), identifier(state.model, true), identifier(state.provider), vector]);
  const legacyVectors = new Set(), ledgerVectors = new Map();
  const boundedRecord = (sequence, native, facts) => {
    dependencies.budget?.room('records', ledgerVectors.size + 1, true);
    return recordBase('codex', identityContext, sequence, native, facts);
  };
  const finishSession = codes => {
    for (const [vector, count] of ledgerVectors) {
      dependencies.budget?.check(true);
      if (!legacyVectors.has(vector)) codes.add('unsupported_source_format', count);
    }
    ledgerVectors.clear(); legacyVectors.clear();
  };
  return extractLines(text, (line, sequence, records, codes) => {
    const native = parseLossless(line, dependencies);
    if (!native || typeof native !== 'object' || Array.isArray(native)) throw new Error('unsupported_source_format');
    const payload = native.payload;
    if (native.type === 'session_meta') {
      // A replacement starts a new epoch even when it repeats the same session ID.
      finishSession(codes);
      // Even a rejected replacement declares a session boundary: old identities/context cannot survive it.
      state = {};
      boundSession = false; boundContext = false;
      if (!payload || !payload.id) throw new Error('missing_session_identity');
      const next = { sessionId: payload.id, parentSessionId: payload.parent_session_id ?? payload.source?.subagent?.thread_spawn?.parent_thread_id, model: payload.model, provider: payload.model_provider };
      const record = boundedRecord(sequence, native, next);
      state = next; records.push(record);
      boundSession = (native.sessionId ?? state.sessionId) === state.sessionId;
      boundContext = boundSession;
      return true;
    }
    if (native.type === 'token_usage_record') {
      // Producer order permits this ledger to precede its token_count event.
      // Retain only a vector/count until this file/session epoch ends; emit no usage.
      const vector = corroborationVector(payload?.thread_token_usage);
      const usage = corroborationVector(payload?.usage), turn = corroborationVector(payload?.turn_token_usage);
      if (!boundContext || !state.sessionId || payload?.thread_id !== state.sessionId || vector === null ||
        ['thread_id', 'turn_id', 'session_id', 'root_turn_id', 'response_id'].some(key => !identifier(payload?.[key])) ||
        usage === null || turn === null) throw new Error('unsupported_source_format');
      const responseCounters = usage.split(',').map(BigInt), turnCounters = turn.split(',').map(BigInt);
      if (!vector.split(',').map(BigInt).every((total, index) => responseCounters[index] <= turnCounters[index] && turnCounters[index] <= total)) throw new Error('unsupported_source_format');
      const key = corroborationKey(native.timestamp, vector);
      if (!ledgerVectors.has(key)) {
        // Pending unique vectors share the existing record allocation ceiling.
        if (dependencies.budget) dependencies.budget.room('records', ledgerVectors.size + 1, true);
        else if (ledgerVectors.size >= 1000000) throw new CaptureLimit();
      }
      ledgerVectors.set(key, (ledgerVectors.get(key) ?? 0n) + 1n);
      return true;
    }
    if (native.type === 'turn_context') {
      const previousProvider = state.provider;
      boundContext = false;
      // Session identities remain trustworthy, but a rejected turn makes its model/provider/pricing unknown.
      state = { sessionId: state.sessionId, parentSessionId: state.parentSessionId };
      if (!payload || typeof payload !== 'object') throw new Error('invalid_usage_shape');
      const next = { ...state, model: payload.model, provider: payload.model_provider ?? previousProvider, service_tier: payload.service_tier, speed: payload.speed, inference_geo: payload.inference_geo, region: payload.region, model_context_window: payload.model_context_window };
      const record = boundedRecord(sequence, native, next);
      state = next; records.push(record);
      boundContext = boundSession && (native.sessionId ?? state.sessionId) === state.sessionId;
      return true;
    }
    if (native.type === 'session_end') {
      const record = boundedRecord(sequence, native, state);
      record.kind = 'sessionClosure'; record.closure = { state: 'explicitlyClosed', timestamp: record.timestamp, sequence: record.sequence };
      records.push(record); return true;
    }
    if (native.type === 'response_item' || native.type === 'compacted' || native.type === 'world_state') return true;
    if (native.type !== 'event_msg') throw new Error('unsupported_source_format');
    if (payload?.type !== 'token_count') return true;
    // A token_count event with no info is a rate-limit observation, not usage.
    if (payload.info === null) return true;
    const info = payload.info;
    if (!info || typeof info !== 'object') throw new Error('invalid_usage_shape');
    const record = boundedRecord(sequence, native, state);
    record.kind = 'cumulativeUsage';
    record.counters = { counterKind: 'codex', ...usageCounters(info.total_token_usage), last_token_usage: info.last_token_usage == null ? null : usageCounters(info.last_token_usage) };
    record.pricing = pricing(info, state);
    records.push(record);
    const vector = corroborationVector(info.total_token_usage);
    if (vector !== null && boundContext && state.sessionId && (native.sessionId ?? state.sessionId) === state.sessionId) legacyVectors.add(corroborationKey(record.timestamp, vector));
    return true;
  }, dependencies, finishSession);
}
