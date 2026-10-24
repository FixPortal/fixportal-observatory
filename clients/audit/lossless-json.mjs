// Node 24's native parser supplies the *original* numeric lexeme, before Number rounding.
export class NumberLexeme { constructor(text) { this.text = text; } }
export const MAX_LINE_BYTES = 1024 * 1024;

export function parseLossless(text, dependencies = {}) {
  if (Number(process.versions.node.split('.')[0]) < 24 || typeof JSON.rawJSON !== 'function') throw new Error('unsupported_node_runtime');
  if (typeof text !== 'string' || Buffer.byteLength(text) > MAX_LINE_BYTES) throw new Error('source_line_limit');
  // This is only a structural ambiguity/depth preflight; JSON.parse remains the JSON parser.
  const stack = [];
  for (let i = 0; i < text.length; i++) {
    if (i % 1024 === 0) dependencies.budget?.check(true);
    const c = text[i];
    if (c === '"') {
      const start = i++;
      for (; i < text.length; i++) {
        if (i % 1024 === 0) dependencies.budget?.check(true);
        if (text[i] === '\\') i++;
        else if (text[i] === '"') break;
      }
      let next = i + 1;
      while (/\s/.test(text[next] ?? '') && next < text.length) next++;
      if (text[next] === ':' && stack.at(-1) instanceof Set) {
        let key;
        try { key = JSON.parse(text.slice(start, i + 1)); } catch { throw new Error('malformed_json'); }
        if (stack.at(-1).has(key)) throw new Error('duplicate_property');
        stack.at(-1).add(key);
      }
    } else if (c === '{' || c === '[') {
      stack.push(c === '{' ? new Set() : null);
      if (stack.length > 64) throw new Error('source_depth_limit');
    } else if (c === '}' || c === ']') stack.pop();
  }
  try {
    return JSON.parse(text, (_key, value, context) => {
      if (typeof value !== 'number') return value;
      if (typeof context?.source !== 'string') throw new Error('unsupported_node_runtime');
      return new NumberLexeme(context.source);
    });
  } catch (error) {
    if (error.message === 'unsupported_node_runtime') throw error;
    throw new Error('malformed_json');
  }
}

export function counter(value) {
  if (value === undefined || value === null) return null;
  if (!(value instanceof NumberLexeme) || !/^(0|[1-9][0-9]*)$/.test(value.text)) throw new Error('invalid_counter');
  if (value.text.length > 19) throw new Error('counter_range');
  const integer = BigInt(value.text);
  if (integer > 9223372036854775807n) throw new Error('counter_range');
  return integer;
}

export function decimal(value) {
  if (value == null) return null;
  if (!(value instanceof NumberLexeme) || value.text.length > 100) throw new Error('invalid_source_money');
  const match = /^(\d+)(?:\.(\d+))?(?:[eE]([+-]?\d+))?$/.exec(value.text);
  if (!match) throw new Error('invalid_source_money');
  let digits = match[1] + (match[2] ?? ''), scale = (match[2]?.length ?? 0) - Number(match[3] ?? 0);
  if (Math.abs(scale) > 100) throw new Error('invalid_source_money');
  if (scale < 0) { digits += '0'.repeat(-scale); scale = 0; }
  while (scale > 0 && digits.endsWith('0')) { digits = digits.slice(0, -1); scale--; }
  if (scale > 28 || digits.length > 100 || BigInt(digits || '0') > 79228162514264337593543950335n) throw new Error('invalid_source_money');
  digits = BigInt(digits || '0').toString().padStart(scale + 1, '0');
  return scale ? digits.slice(0, -scale) + '.' + digits.slice(-scale) : digits;
}
