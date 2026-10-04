const { isAbsolute } = require('node:path');
const { globSync } = require('tinyglobby');

// plantuml-parser@0.4.0 calls only sync(patterns), without glob options.
// Keep this adapter consumer-scoped; it is not a general fast-glob replacement.
exports.sync = (patterns, options) => {
  const values = typeof patterns === 'string' ? [patterns] : patterns;
  if (!Array.isArray(values) || values.some(value => typeof value !== 'string') ||
      options !== undefined) {
    throw new TypeError('Unsupported PlantUML glob API; review the consumer upgrade');
  }
  const exclusions = values.filter(value => value.startsWith('!'));
  const positives = values.filter(value => !value.startsWith('!'));
  // Preserve each positive pattern's path form, including Windows cross-drive
  // absolute patterns. Do not recursively expand a plain directory argument.
  return [...new Set([false, true].flatMap(absolute => {
    const group = positives.filter(value => isAbsolute(value) === absolute);
    if (!group.length) return [];
    return globSync([...group, ...exclusions], { absolute, expandDirectories: false });
  }))];
};
