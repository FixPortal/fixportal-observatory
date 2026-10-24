import { mkdir, writeFile, readFile } from 'node:fs/promises';
import { pathToFileURL } from 'node:url';
import { join, resolve } from 'node:path';
import { discoverSources, readStable } from './audit/discovery.mjs';
import { loadIdentity, identityContext } from './audit/identity.mjs';
import { extractClaudeEvidence } from './audit/claude-evidence.mjs';
import { extractCodexEvidence } from './audit/codex-evidence.mjs';
import { canonical, canonicalByteLength, utcTimestamp, identifier, countedDiagnostics, sha256, EXTRACTOR_VERSION, SOURCE_VERSIONS } from './audit/protocol.mjs';

const GUID = /^(?!00000000-0000-0000-0000-000000000000)[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const CHECKS = new Set(['sourceAccounting', 'dailyAttribution', 'eventAggregation', 'pricingArithmetic', 'presentationTotals', 'billingReconciliation']);

function onlyKeys(object, keys) {
  if (!object || typeof object !== 'object' || Array.isArray(object) || Object.keys(object).some(key => !keys.includes(key))) throw new Error('invalid_capture_request');
}

export function checkedRequest(request, configuration) {
  onlyKeys(request, ['captureId', 'requestId', 'machineId', 'scope']);
  if (![request.captureId, request.requestId, request.machineId].every(id => typeof id === 'string' && GUID.test(id))) throw new Error('invalid_capture_request');
  const scope = request.scope;
  onlyKeys(scope, ['from', 'to', 'sources', 'requestedChecks', 'excludedChecks', 'allowPartial', 'predecessorRunId', 'evaluationOfRunId']);
  const date = value => typeof value === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(value) && utcTimestamp(value + 'T00:00:00Z').startsWith(value);
  if (!date(scope.from) || !date(scope.to) || scope.from > scope.to || (Date.parse(scope.to) - Date.parse(scope.from)) / 86400000 >= 31 || typeof scope.allowPartial !== 'boolean') throw new Error('invalid_capture_request');
  if (!Array.isArray(scope.sources) || !scope.sources.length || !Array.isArray(scope.requestedChecks) || !scope.requestedChecks.length || !Array.isArray(scope.excludedChecks)) throw new Error('invalid_capture_request');
  if ([...scope.requestedChecks, ...scope.excludedChecks].some(code => !CHECKS.has(code)) || scope.requestedChecks.some(code => scope.excludedChecks.includes(code))) throw new Error('invalid_capture_request');
  for (const source of scope.sources) {
    onlyKeys(source, ['machineId', 'sourceId']);
    if (source.machineId !== request.machineId || typeof source.sourceId !== 'string' || !source.sourceId.length) throw new Error('invalid_capture_request');
    identifier(source.sourceId);
  }
  for (const name of ['predecessorRunId', 'evaluationOfRunId']) if (scope[name] != null && !GUID.test(scope[name])) throw new Error('invalid_capture_request');
  onlyKeys(configuration, ['identityDirectory', 'sources', 'limits', 'includeRawHashes']);
  if (typeof configuration.identityDirectory !== 'string' || !configuration.identityDirectory || !Array.isArray(configuration.sources)) throw new Error('invalid_capture_configuration');
  const configured = new Set();
  for (const source of configuration.sources) {
    onlyKeys(source, ['sourceId', 'tool', 'roots']);
    if (typeof source.sourceId !== 'string' || !source.sourceId.length) throw new Error('invalid_capture_configuration');
    identifier(source.sourceId);
    if (configured.has(source.sourceId) || !['claude', 'codex'].includes(source.tool) || !Array.isArray(source.roots) || !source.roots.length || source.roots.some(root => typeof root !== 'string' || !root)) throw new Error('invalid_capture_configuration');
    configured.add(source.sourceId);
  }
  if (scope.sources.some(source => !configured.has(source.sourceId))) throw new Error('unconfigured_source');
  const sources = [...new Map(scope.sources.map(source => [source.sourceId, source])).values()].sort((a, b) => a.sourceId < b.sourceId ? -1 : a.sourceId > b.sourceId ? 1 : 0);
  return { ...scope, sources, requestedChecks: [...new Set(scope.requestedChecks)].sort(), excludedChecks: [...new Set(scope.excludedChecks)].sort(), predecessorRunId: scope.predecessorRunId ?? null, evaluationOfRunId: scope.evaluationOfRunId ?? null };
}

function limits(configuration) {
  const values = { maxChunkBytes: 8 * 1024 * 1024, maxCaptureBytes: 512 * 1024 * 1024, maxRecords: 1000000, maxFiles: 10000 };
  if (configuration.limits) {
    onlyKeys(configuration.limits, Object.keys(values));
    for (const [name, value] of Object.entries(configuration.limits)) {
      if (!Number.isSafeInteger(value) || value <= 0 || value > values[name]) throw new Error('invalid_capture_limits');
      values[name] = value;
    }
  }
  return values;
}

export async function captureEvidence(request, configuration, dependencies = {}) {
  const budget = dependencies.budget;
  const scope = checkedRequest(request, configuration), bounds = limits(configuration);
  budget?.check(true);
  const now = () => utcTimestamp((dependencies.now?.() ?? new Date()).toISOString());
  const scanStartedAt = now();
  const identity = await (dependencies.loadIdentity ?? loadIdentity)(configuration.identityDirectory, { budget, fresh: dependencies.freshIdentity === true });
  budget?.check(true);
  const selected = new Set(scope.sources.map(source => source.sourceId));
  const sources = configuration.sources.filter(source => selected.has(source.sourceId));
  const discovery = await discoverSources(sources, Math.min(bounds.maxFiles, budget?.limit('maxFiles') ?? bounds.maxFiles), dependencies);
  const codes = countedDiagnostics(), records = [], rawHashes = [];
  for (const code of discovery.codes) codes.add(code);
  let readFiles = 0, changedFiles = 0, skippedFiles = discovery.skippedFiles, sourceBytes = 0, wireBytes = 0;
  for (const file of discovery.files) {
    try { budget?.check(true); } catch (error) { if (!error.captureLimit) throw error; codes.add('capture_limit'); break; }
    const stable = await readStable(file, bounds.maxCaptureBytes - sourceBytes, dependencies);
    if (stable.code) {
      if (stable.code === 'source_changed') changedFiles++; else skippedFiles++;
      try { if (stable.code !== 'capture_limit') budget?.take('diagnostics', 1, true); }
      catch (error) { if (!error.captureLimit) throw error; codes.add('capture_limit'); break; }
      codes.add(stable.code);
      if (stable.code === 'capture_limit') break;
      continue;
    }
    sourceBytes += stable.byteLength;
    const context = identityContext(identity, { machineId: request.machineId, sourceId: file.sourceId });
    context.fileId = context.id('file', resolve(file.path));
    const extracted = (file.tool === 'claude' ? extractClaudeEvidence : extractCodexEvidence)(stable.text, context, dependencies);
    readFiles++;
    for (const diagnostic of extracted.diagnostics) codes.add(diagnostic.code, diagnostic.count);
    for (const record of extracted.records) {
      const length = canonicalByteLength(record, { budget }) + 1;
      if (records.length >= bounds.maxRecords || wireBytes + length > bounds.maxCaptureBytes || length > bounds.maxChunkBytes || length > 1024 * 1024) { codes.add('capture_limit'); skippedFiles++; break; }
      records.push(record); wireBytes += length;
    }
    if (configuration.includeRawHashes) rawHashes.push({ fileId: context.fileId, sha256: stable.rawHash });
    if (extracted.diagnostics.some(item => item.code === 'capture_limit')) break;
  }
  budget?.check();
  records.sort((a, b) => {
    for (const field of ['sourceId', 'generation', 'fileId']) { if (a[field] < b[field]) return -1; if (a[field] > b[field]) return 1; }
    return a.sequence < b.sequence ? -1 : a.sequence > b.sequence ? 1 : 0;
  });
  const chunks = []; let lines = [], length = 0;
  const maxChunkBytes = Math.min(bounds.maxChunkBytes, budget?.limit('maxChunkBytes') ?? bounds.maxChunkBytes);
  const flush = () => { if (lines.length) { const ndjson = Buffer.from(lines.join('')); chunks.push({ index: chunks.length, sha256: sha256(ndjson), ndjson }); lines = []; length = 0; } };
  for (const record of records) { budget?.check(); const line = canonical(record) + '\n', bytes = Buffer.byteLength(line); if (length + bytes > maxChunkBytes) flush(); lines.push(line); length += bytes; }
  flush();
  if (!records.some(record => record.counters && record.timestamp.slice(0, 10) >= scope.from && record.timestamp.slice(0, 10) <= scope.to) && !codes.has('capture_limit')) {
    try { budget?.take('diagnostics'); codes.add('no_usage_records'); }
    catch (error) { if (!error.captureLimit) throw error; codes.add('capture_limit'); }
  }
  const dates = records.map(record => record.timestamp.slice(0, 10)).sort();
  const scanEndedAt = now();
  if (budget) {
    // The hash array alone is an exact lower bound; refuse before allocating its entries.
    const hashArrayBytes = 2 + chunks.length * 67 - (chunks.length ? 1 : 0);
    if (hashArrayBytes > budget.limit('maxMetadataBytes')) throw Object.assign(new Error('capture_limit'), { captureLimit: true });
    budget.room('encodedBytes', hashArrayBytes);
  }
  const manifest = { schemaVersion: 1, captureId: request.captureId, requestId: request.requestId, machineId: request.machineId, generation: identity.generation, extractorVersion: EXTRACTOR_VERSION, scanStartedAt, scanEndedAt, scope, sourceFormats: sources.map(source => ({ sourceId: source.sourceId, format: `${source.tool}-jsonl`, version: SOURCE_VERSIONS[source.tool] })).sort((a, b) => a.sourceId < b.sourceId ? -1 : a.sourceId > b.sourceId ? 1 : 0), discoveredFiles: BigInt(discovery.files.length + discovery.skippedFiles), readFiles: BigInt(readFiles), skippedFiles: BigInt(skippedFiles), changedFiles: BigInt(changedFiles), recordCount: BigInt(records.length), observedFrom: dates[0] ?? null, observedTo: dates.at(-1) ?? null, diagnostics: codes.toArray(), chunkHashes: chunks.map(chunk => chunk.sha256), sealedAt: now() };
  return { manifest, records, chunks, complete: codes.size === 0, local: { rawHashes } };
}

export async function writeCapture(capture, outputDirectory, dependencies = {}) {
  const budget = dependencies.budget;
  // Existing output directories are refused; capture bytes are never silently replaced.
  budget?.ownedWrite(outputDirectory, null);
  await mkdir(outputDirectory, { mode: 0o700 });
  budget?.ownedWrite(join(outputDirectory, 'chunks'), null);
  await mkdir(join(outputDirectory, 'chunks'), { mode: 0o700 });
  for (const chunk of capture.chunks) {
    const path = join(outputDirectory, 'chunks', String(chunk.index).padStart(6, '0') + '.ndjson');
    budget?.ownedWrite(path, chunk.ndjson);
    await writeFile(path, chunk.ndjson, { flag: 'wx', mode: 0o600 });
  }
  for (const [name, value] of [['manifest.json', capture.manifest], ['local.json', capture.local]]) {
    const path = join(outputDirectory, name);
    const bytes = canonicalByteLength(value, { budget, maximum: budget ? Math.min(budget.limit('maxMetadataBytes'), budget.available('ownedWriteBytes'), budget.available('encodedBytes')) : 512 * 1024 * 1024 });
    budget?.ownedWrite(path, bytes);
    budget?.take('encodedBytes', bytes);
    const data = canonical(value);
    await writeFile(path, data, { flag: 'wx', mode: 0o600 });
  }
}

async function main() {
  const args = process.argv.slice(2);
  if (args[0] === 'selected-capture') {
    if (args.length < 3 || args.length > 4 || args[1] !== '--config' || args.length === 4 && args[3] !== '--supervised-stdin') throw new Error('invalid_cli_arguments');
    const { selectedCapture, readSelectedConfiguration, supervisedStop } = await import('./audit/selected-capture.mjs');
    const controller = new AbortController();
    const stop = () => controller.abort();
    process.once('SIGINT', stop); process.once('SIGTERM', stop);
    const supervised = args.includes('--supervised-stdin');
    const dispose = supervised ? supervisedStop(process.stdin, controller) : () => {};
    try {
      const configuration = await readSelectedConfiguration(args[2]);
      const result = await selectedCapture(configuration, { signal: controller.signal });
      process.stdout.write(result.capture.complete ? 'capture_complete\n' : 'capture_incomplete\n');
      process.exitCode = result.capture.complete ? 0 : 3;
    } finally {
      dispose();
      if (supervised) process.stdin.destroy();
      process.off('SIGINT', stop); process.off('SIGTERM', stop);
    }
    return;
  }
  if (args[0] === 'poll') {
    if (args.length < 3 || args.length > 4 || args[1] !== '--config' || args.length === 4 && args[3] !== '--once') throw new Error('invalid_cli_arguments');
    const buffer = await readFile(args[2]);
    if (buffer.length > 1024 * 1024) throw new Error('invalid_capture_configuration');
    const configuration = JSON.parse(buffer.toString('utf8'));
    const { poll } = await import('./audit/poll.mjs');
    const controller = new AbortController();
    process.once('SIGINT', () => controller.abort()); process.once('SIGTERM', () => controller.abort());
    await poll(configuration, { once: args.includes('--once'), signal: controller.signal, log: message => process.stdout.write(message + '\n') });
    return;
  }
  if (args.length !== 5 || args[0] !== 'capture' || args[1] !== '--request' || args[3] !== '--output') throw new Error('invalid_cli_arguments');
  const buffer = await readFile(args[2]);
  if (buffer.length > 1024 * 1024) throw new Error('invalid_capture_request');
  const local = JSON.parse(buffer.toString('utf8'));
  onlyKeys(local, ['request', 'configuration']);
  const capture = await captureEvidence(local.request, local.configuration);
  await writeCapture(capture, args[4]);
  process.stdout.write(capture.complete ? 'capture_complete\n' : 'capture_incomplete\n');
  process.exitCode = capture.complete ? 0 : 3;
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) main().catch(() => { process.stderr.write('audit_capture_failed\n'); process.exitCode = 4; });
