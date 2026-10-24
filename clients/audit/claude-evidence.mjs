import { parseLossless, counter, decimal } from './lossless-json.mjs';
import { extractLines, recordBase, identifier, pricing } from './protocol.mjs';

export function extractClaudeEvidence(text, identityContext, dependencies = {}) {
  identityContext = { ...identityContext, budget: dependencies.budget };
  return extractLines(text, (line, sequence, records) => {
    const native = parseLossless(line, dependencies);
    if (!native || typeof native !== 'object' || Array.isArray(native)) throw new Error('unsupported_source_format');
    // Conversation-only records are recognized but never copied into evidence.
    if (['user', 'system', 'progress', 'file-history-snapshot', 'queue-operation', 'summary'].includes(native.type)) return true;
    if (native.type === 'session_end') {
      const record = recordBase('claude', identityContext, sequence, native);
      record.kind = 'sessionClosure'; record.closure = { state: 'explicitlyClosed', timestamp: record.timestamp, sequence: record.sequence };
      records.push(record); return true;
    }
    if (native.type !== 'assistant' || native.message?.role && native.message.role !== 'assistant') throw new Error('unsupported_source_format');
    const usage = native.message?.usage;
    if (!usage || typeof usage !== 'object' || Array.isArray(usage)) throw new Error('invalid_usage_shape');
    if (usage.cache_creation != null && Object.getPrototypeOf(usage.cache_creation) !== Object.prototype) throw new Error('invalid_usage_shape');
    const record = recordBase('claude', identityContext, sequence, native, { model: native.message.model, provider: 'anthropic' });
    record.kind = 'messageUsage';
    record.messageId = native.message.id == null ? null : identityContext.id('message', native.message.id);
    record.model = identifier(native.message.model, true);
    record.counters = { counterKind: 'claude', input_tokens: counter(usage.input_tokens), output_tokens: counter(usage.output_tokens), cache_creation_input_tokens: counter(usage.cache_creation_input_tokens), cache_read_input_tokens: counter(usage.cache_read_input_tokens), ephemeral_5m_input_tokens: counter(usage.cache_creation?.ephemeral_5m_input_tokens), ephemeral_1h_input_tokens: counter(usage.cache_creation?.ephemeral_1h_input_tokens), thinking_tokens: counter(usage.thinking_tokens), thinking_output_tokens: counter(usage.thinking_output_tokens) };
    if (Object.entries(record.counters).every(([key, value]) => key === 'counterKind' || value === null)) throw new Error('invalid_usage_shape');
    record.pricing = pricing(usage, native.message);
    record.cost = decimal(native.costUSD);
    record.currency = record.cost == null ? null : 'USD';
    records.push(record); return true;
  }, dependencies);
}
