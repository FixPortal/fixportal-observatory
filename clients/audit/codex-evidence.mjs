import { parseLossless, counter } from './lossless-json.mjs';
import { extractLines, recordBase, pricing } from './protocol.mjs';

function usageCounters(usage) {
  if (!usage || typeof usage !== 'object' || Array.isArray(usage)) throw new Error('invalid_usage_shape');
  const result = Object.fromEntries(['input_tokens', 'cached_input_tokens', 'output_tokens', 'reasoning_output_tokens', 'total_tokens'].map(key => [key, counter(usage[key])]));
  if (Object.values(result).every(value => value === null)) throw new Error('invalid_usage_shape');
  return result;
}

export function extractCodexEvidence(text, identityContext) {
  let state = {};
  return extractLines(text, (line, sequence, records) => {
    const native = parseLossless(line);
    if (!native || typeof native !== 'object' || Array.isArray(native)) throw new Error('unsupported_source_format');
    const payload = native.payload;
    if (native.type === 'session_meta') {
      if (!payload || !payload.id) throw new Error('missing_session_identity');
      const next = { sessionId: payload.id, parentSessionId: payload.parent_session_id ?? payload.source?.subagent?.thread_spawn?.parent_thread_id, model: payload.model, provider: payload.model_provider };
      const record = recordBase('codex', identityContext, sequence, native, next);
      state = next; records.push(record); return true;
    }
    if (native.type === 'turn_context') {
      if (!payload || typeof payload !== 'object') throw new Error('invalid_usage_shape');
      const next = { ...state, model: payload.model, provider: payload.model_provider ?? state.provider, service_tier: payload.service_tier, speed: payload.speed, inference_geo: payload.inference_geo, region: payload.region, model_context_window: payload.model_context_window };
      const record = recordBase('codex', identityContext, sequence, native, next);
      state = next; records.push(record); return true;
    }
    if (native.type === 'session_end') {
      const record = recordBase('codex', identityContext, sequence, native, state);
      record.kind = 'sessionClosure'; record.closure = { state: 'explicitlyClosed', timestamp: record.timestamp, sequence: record.sequence };
      records.push(record); return true;
    }
    if (native.type === 'response_item' || native.type === 'compacted') return true;
    if (native.type !== 'event_msg') throw new Error('unsupported_source_format');
    if (payload?.type !== 'token_count') return true;
    // A token_count event with no info is a rate-limit observation, not usage.
    if (payload.info === null) return true;
    const info = payload.info;
    if (!info || typeof info !== 'object') throw new Error('invalid_usage_shape');
    const record = recordBase('codex', identityContext, sequence, native, state);
    record.kind = 'cumulativeUsage';
    record.counters = { counterKind: 'codex', ...usageCounters(info.total_token_usage), last_token_usage: info.last_token_usage == null ? null : usageCounters(info.last_token_usage) };
    record.pricing = pricing(info, state);
    records.push(record); return true;
  });
}
