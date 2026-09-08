#!/usr/bin/env node
// AI Observatory local usage sweeper (drop-in).
//
// Rebuilds cumulative daily/model snapshots from six local CLI stores, then
// POSTs them to `/api/events`. Source ids carry a per-machine suffix
// (`codex-local@<host>`) so machines never share a namespace or tombstone each
// other's history. The state file caches parsed files by path + mtime; server
// inventory makes losing it harmless.
//
// Zero dependencies: Node 24+ only (global fetch, fs/promises, sqlite).

import { readFile, readdir, mkdir, writeFile, stat } from 'node:fs/promises'
import { homedir, hostname } from 'node:os'
import { join, dirname, basename } from 'node:path'
import { pathToFileURL } from 'node:url'

const ALL_LOCAL_SOURCES = ['codex', 'copilot', 'claude', 'kimi', 'gemini', 'antigravity']
const LOCAL_SOURCE_IDS = {
  codex: 'codex-local',
  copilot: 'copilot-local',
  claude: 'claude-local',
  kimi: 'kimi-local',
  gemini: 'gemini-review-local',
  antigravity: 'antigravity-local',
}
// Bump whenever a parser change invalidates cached records; the wipe re-parses
// every file. Never bump alone: after the wipe an unreadable file has no cached
// records, so scanRecords flags its source incomplete and main withholds that
// source's corrections and tombstones until a complete scan succeeds.
const PARSE_CACHE_VERSION = 3
// Token fields compared against server inventory to skip unchanged re-posts.
const SNAPSHOT_TOKEN_FIELDS = [
  'inputTokens', 'outputTokens', 'cacheReadTokens',
  'cacheWriteTokens', 'cacheWrite1hTokens', 'thoughtTokens',
]
// Gemini Developer API standard-tier pricing changes above this documented prompt-token threshold.
const GEMINI_LONG_CONTEXT_THRESHOLD = 200_000

// --- Pure helpers -----------------------------------------------------------

function token(value) {
  return Math.max(0, Number.isFinite(Number(value)) ? Number(value) : 0)
}

function isoTimestamp(value) {
  if (value === null || value === undefined || value === '') { return null }
  const date = new Date(value)
  return Number.isNaN(date.valueOf()) ? null : date.toISOString()
}

export function observatoryUrl(value) {
  let url
  try { url = new URL(value) } catch { throw new Error('OBSERVATORY_URL must be an absolute HTTP(S) URL') }
  if (!['http:', 'https:'].includes(url.protocol)) {
    throw new Error('OBSERVATORY_URL must use HTTP or HTTPS')
  }
  if (url.username || url.password || url.search || url.hash) {
    throw new Error('OBSERVATORY_URL must not contain credentials, a query, or a fragment')
  }
  const loopback = ['localhost', '127.0.0.1', '[::1]'].includes(url.hostname)
  if (url.protocol === 'http:' && !loopback) {
    throw new Error('OBSERVATORY_URL must use HTTPS unless it targets loopback')
  }
  return url.href.replace(/\/+$/, '')
}

export async function observatoryFetch(url, apiKey, options = {}) {
  for (let attempt = 1; attempt <= 3; attempt++) {
    let response
    try {
      response = await fetch(url, {
        ...options,
        headers: { ...options.headers, 'X-Observatory-Key': apiKey },
        redirect: 'error',
        signal: AbortSignal.timeout(10_000),
      })
    } catch (error) {
      if (attempt === 3) { throw error }
      await new Promise(resolve => setTimeout(resolve, 1_000))
      continue
    }
    const retryable = response.status === 429 || response.status >= 500
    if (!retryable || attempt === 3) { return response }
    const retryAfter = response.headers.get('Retry-After')
    const fallbackDelayMs = response.status === 429 ? 61_000 : 1_000
    // Retry-After is honored but capped: an uncapped server hint can rate-limit
    // the collector into overlapping scheduled runs, which is worse than retrying
    // early and being refused again.
    const delayMs = retryAfter && /^\d+$/.test(retryAfter)
      ? Math.min(Number(retryAfter) * 1_000, 61_000)
      : fallbackDelayMs
    await response.body?.cancel()
    await new Promise(resolve => setTimeout(resolve, delayMs))
  }
}

/**
 * Parse a Codex rollout into its last cumulative token_count.
 *
 * ponytail: a session that switches model mid-flight is attributed wholly to the
 * last turn_context model, and the whole cumulative total is bucketed on the day
 * of the final token_count — the cumulative total isn't broken down per model or
 * per day. Upgrade path: bucket token_count deltas by the preceding turn_context
 * and each row's timestamp if/when mixed-model or midnight-crossing Codex
 * sessions become common.
 */
export function parseCodex(content) {
  let model = null
  let total = null
  let reasoning = 0
  let endedAt = null
  for (const line of content.split('\n')) {
    if (!line) { continue }
    let row
    try { row = JSON.parse(line) } catch { continue }
    if (!row || typeof row !== 'object') { continue }
    const payload = row.payload
    if (row.type === 'turn_context' && payload?.model) { model = payload.model }
    if (payload?.type === 'token_count' && payload.info?.total_token_usage && isoTimestamp(row.timestamp)) {
      total = payload.info.total_token_usage
      reasoning = token(total.reasoning_output_tokens)
      endedAt = row.timestamp
    }
  }
  if (!total) { return null }
  const cacheRead = token(total.cached_input_tokens)
  return {
    model: model ?? 'gpt-5',
    cum: {
      input: Math.max(0, token(total.input_tokens) - cacheRead),
      output: token(total.output_tokens),
      cacheRead,
      cacheWrite: 0,
    },
    reasoning,
    endedAt,
  }
}

/**
 * Parse the last cumulative per-model Copilot session.shutdown event.
 *
 * ponytail: the whole session's cumulative per-model totals are bucketed on the
 * shutdown event's day — a session still running across UTC midnight moves its
 * entire total from yesterday's key into today's. Per-day bucketing would need
 * per-event usage rows, which the session-state log does not carry.
 */
export function parseCopilot(content) {
  let shutdown = null
  for (const line of content.split('\n')) {
    if (!line || !line.includes('"session.shutdown"')) { continue }
    try { shutdown = JSON.parse(line) } catch { /* keep last parseable */ }
  }
  const metrics = shutdown?.data?.modelMetrics
  const endedAt = isoTimestamp(shutdown?.timestamp)
  if (!metrics || !endedAt) { return null }
  const perModel = {}
  for (const [model, value] of Object.entries(metrics)) {
    const usage = value?.usage
    if (!usage) { continue }
    const cacheRead = token(usage.cacheReadTokens)
    perModel[model] = {
      input: Math.max(0, token(usage.inputTokens) - cacheRead),
      output: token(usage.outputTokens),
      cacheRead,
      cacheWrite: token(usage.cacheWriteTokens),
      reasoning: token(usage.reasoningTokens),
    }
  }
  return { endedAt, perModel }
}

/** Parse Claude assistant-message usage for global message.id selection. */
export function parseClaude(content) {
  const records = []
  for (const line of content.split('\n')) {
    if (!line) { continue }
    let row
    try { row = JSON.parse(line) } catch { continue }
    if (!row || typeof row !== 'object') { continue }
    const message = row.type === 'assistant' ? row.message : null
    const usage = message?.usage
    const occurredAtUtc = isoTimestamp(row.timestamp)
    if (!usage || !occurredAtUtc) { continue }

    const creation = usage.cache_creation ?? {}
    const cacheWrite5mTokens = token(creation.ephemeral_5m_input_tokens)
    const cacheWrite1hTokens = token(creation.ephemeral_1h_input_tokens)
    const cacheWriteTokens = Math.max(
      token(usage.cache_creation_input_tokens),
      cacheWrite5mTokens + cacheWrite1hTokens,
    )
    records.push({
      tool: 'claude',
      ...(message.id ? { messageId: message.id } : {}),
      date: occurredAtUtc.slice(0, 10),
      model: message.model ?? 'unknown',
      occurredAtUtc,
      inputTokens: token(usage.input_tokens),
      outputTokens: token(usage.output_tokens),
      cacheReadTokens: token(usage.cache_read_input_tokens),
      cacheWriteTokens,
      ...(Object.hasOwn(creation, 'ephemeral_1h_input_tokens') ? { cacheWrite1hTokens } : {}),
      ...(Object.hasOwn(creation, 'ephemeral_5m_input_tokens') ? { cacheWrite5mTokens } : {}),
      ...(Object.hasOwn(usage, 'thinking_tokens') || Object.hasOwn(usage, 'thinking_output_tokens')
        ? { thoughtTokens: token(usage.thinking_tokens ?? usage.thinking_output_tokens) }
        : {}),
      ...(typeof usage.service_tier === 'string' && usage.service_tier ? { serviceTier: usage.service_tier } : {}),
      ...(typeof usage.speed === 'string' && usage.speed ? { speed: usage.speed } : {}),
      ...(typeof usage.inference_geo === 'string' && usage.inference_geo ? { inferenceGeo: usage.inference_geo } : {}),
    })
  }
  return records
}

/** Parse only Kimi usage.record rows; step.end mirrors are intentionally ignored. */
export function parseKimi(content) {
  const records = []
  for (const line of content.split('\n')) {
    if (!line) { continue }
    let row
    try { row = JSON.parse(line) } catch { continue }
    if (!row || typeof row !== 'object') { continue }
    if (row.type !== 'usage.record' || !row.usage) { continue }
    const occurredAtUtc = isoTimestamp(row.time)
    if (!occurredAtUtc) { continue }
    records.push({
      tool: 'kimi',
      date: occurredAtUtc.slice(0, 10),
      model: row.model ?? 'kimi-code/kimi-for-coding',
      occurredAtUtc,
      inputTokens: token(row.usage.inputOther),
      outputTokens: token(row.usage.output),
      cacheReadTokens: token(row.usage.inputCacheRead),
      cacheWriteTokens: token(row.usage.inputCacheCreation),
    })
  }
  return records
}

/** Parse PAYG Gemini review transcripts written by gemini-review.ps1. */
export function parseGeminiReview(content) {
  const records = []
  for (const line of content.split('\n')) {
    // Cheap prefilter on the key name only. Filtering on one exact serialisation
    // ('"type":"gemini"') would silently drop valid rows whose producer writes
    // whitespace ('"type": "gemini"'); the parsed type check below is the gate.
    if (!line || !line.includes('gemini')) { continue }
    let row
    try { row = JSON.parse(line) } catch { continue }
    const occurredAtUtc = isoTimestamp(row?.timestamp)
    if (row?.type !== 'gemini' || !row.tokens || !occurredAtUtc) { continue }
    const cacheReadTokens = token(row.tokens.cached)
    const promptTokens = token(row.tokens.input)
    records.push({
      tool: 'gemini-review',
      date: occurredAtUtc.slice(0, 10),
      model: row.model ?? 'unknown',
      occurredAtUtc,
      inputTokens: Math.max(0, promptTokens - cacheReadTokens),
      outputTokens: token(row.tokens.output),
      cacheReadTokens,
      cacheWriteTokens: 0,
      thoughtTokens: token(row.tokens.thoughts),
      context: promptTokens > GEMINI_LONG_CONTEXT_THRESHOLD ? 'long' : 'short',
    })
  }
  return records
}

function decodeVarint(data, start) {
  let value = 0
  let shift = 0
  let index = start
  while (index < data.length) {
    const byte = data[index++]
    value += (byte & 0x7f) * (2 ** shift)
    if (!(byte & 0x80)) { return [value, index] }
    shift += 7
  }
  return [0, data.length]
}

function protobufFields(data) {
  const fields = []
  for (let index = 0; index < data.length;) {
    const [tag, afterTag] = decodeVarint(data, index)
    const field = Math.floor(tag / 8)
    const wire = tag & 7
    index = afterTag
    if (!field) { return [] }
    if (wire === 0) {
      const [value, next] = decodeVarint(data, index)
      fields.push({ field, value })
      index = next
    } else if (wire === 1) {
      index += 8
    } else if (wire === 2) {
      const [length, start] = decodeVarint(data, index)
      const end = start + length
      if (end > data.length) { return [] }
      fields.push({ field, value: data.subarray(start, end) })
      index = end
    } else if (wire === 5) {
      index += 4
    } else {
      return []
    }
  }
  return fields
}

function antigravityTokens(payload) {
  const response = protobufFields(payload).find(field => field.field === 5 && typeof field.value !== 'number')?.value
  const usage = response
    && protobufFields(response).find(field => field.field === 9 && typeof field.value !== 'number')?.value
  if (!usage) { return null }
  const values = new Map(protobufFields(usage).filter(field => typeof field.value === 'number').map(field => [field.field, field.value]))
  const input = values.get(2)
  const output = values.get(3)
  if (input === undefined || output === undefined || input >= 100_000_000 || output >= 1_000_000) { return null }
  return { input, output, thoughts: values.get(6) ?? 0 }
}

function antigravityTranscript(content) {
  let occurredAtUtc = null
  let model = 'unknown'
  const mappings = [
    [/Gemini 3\.1 Pro \(High\)/i, 'gemini-3.1-pro-high'],
    [/Gemini 3\.1 Pro/i, 'gemini-3.1-pro'],
    [/Gemini 3\.1 Flash/i, 'gemini-3.1-flash'],
    [/Gemini 2\.5 Pro/i, 'gemini-2.5-pro'],
    [/Gemini 2\.5 Flash/i, 'gemini-2.5-flash'],
  ]
  for (const line of content.split('\n')) {
    let row
    try { row = JSON.parse(line) } catch { continue }
    const timestamp = isoTimestamp(row?.created_at)
    if (timestamp && (!occurredAtUtc || timestamp > occurredAtUtc)) { occurredAtUtc = timestamp }
    const text = typeof row?.content === 'string' ? row.content : ''
    // Only the settings-change sentence names the model actually selected. Any
    // other row merely mentioning a model ("compare this with Gemini 2.5 Flash")
    // must not re-label the whole conversation.
    if (!/Model Selection/i.test(text)) { continue }
    const match = mappings.find(([pattern]) => pattern.test(text))
    if (match) { model = match[1] }
  }
  return { occurredAtUtc, model }
}

/**
 * Parse one Antigravity SQLite conversation using its timestamp/model transcript.
 *
 * ponytail: every usage row in the conversation is attributed wholly to the
 * transcript's last day and last selected model — a conversation that crosses
 * UTC midnight or switches model mid-flight is not split per day or per model.
 * Splitting would need per-step timestamps and models, which the steps table
 * does not carry alongside step_type 23 payloads.
 */
export async function parseAntigravityDatabase(path, transcriptContent, report = log) {
  const { occurredAtUtc, model } = antigravityTranscript(transcriptContent)
  if (!occurredAtUtc) { return [] }
  const { DatabaseSync } = await import('node:sqlite')
  const db = new DatabaseSync(path, { readOnly: true })
  let rows
  try {
    rows = db.prepare('SELECT step_payload FROM steps WHERE step_type = 23 AND step_payload IS NOT NULL ORDER BY idx').all()
  } finally {
    db.close()
  }
  const totals = { input: 0, output: 0, thoughts: 0 }
  let rejected = 0
  for (const row of rows) {
    const parsed = antigravityTokens(row.step_payload)
    if (!parsed) { rejected++; continue }
    totals.input += parsed.input
    totals.output += parsed.output
    totals.thoughts += parsed.thoughts
  }
  if (rejected) {
    report(`Skipped ${rejected} unrecognized Antigravity usage row${rejected === 1 ? '' : 's'} in ${path}`)
  }
  if (totals.input + totals.output + totals.thoughts === 0) { return [] }
  return [{
    tool: 'antigravity', date: occurredAtUtc.slice(0, 10), model, occurredAtUtc,
    inputTokens: totals.input, outputTokens: totals.output,
    cacheReadTokens: 0, cacheWriteTokens: 0, thoughtTokens: totals.thoughts,
  }]
}

/** Slugify a machine name for the per-machine source-id suffix. */
export function machineLabel(value) {
  const label = String(value ?? '').toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '')
  return label || 'unknown-machine'
}

/**
 * Namespace a tool's source id per machine so sweeps on different hosts never
 * share a namespace — and therefore never tombstone each other's history.
 */
function localSourceId(tool, machine) {
  return `${LOCAL_SOURCE_IDS[tool]}@${machineLabel(machine)}`
}

function sourceMetadata(tool, model, machine) {
  switch (tool) {
    case 'codex': return { provider: 'OpenAI', sourceId: localSourceId('codex', machine), runtime: 'codex' }
    case 'copilot': {
      const normalized = model.toLowerCase()
      const provider = ['gpt-5.6-sol', 'gpt-5.4'].some(prefix => normalized.startsWith(prefix))
        ? 'OpenAI'
        : ['claude-opus-4-5', 'claude-opus-4-8', 'claude-sonnet-5', 'claude-opus-5']
            .some(prefix => normalized.startsWith(prefix))
          ? 'Anthropic'
          : 'Copilot'
      return { provider, sourceId: localSourceId('copilot', machine), runtime: 'copilot' }
    }
    case 'claude': return { provider: 'Anthropic', sourceId: localSourceId('claude', machine), runtime: 'claude' }
    case 'kimi': return { provider: 'Moonshot', sourceId: localSourceId('kimi', machine), runtime: 'kimi' }
    case 'gemini-review': return {
      provider: 'Google', sourceId: localSourceId('gemini', machine), runtime: 'gemini',
      usageScope: 'api', costBasis: 'listPriceEstimate',
    }
    case 'antigravity': return { provider: 'Google', sourceId: localSourceId('antigravity', machine), runtime: 'antigravity' }
    default: return null
  }
}

function recordTokens(record) {
  const cumulative = record.cum
  return {
    input: token(cumulative?.input ?? record.inputTokens),
    output: token(cumulative?.output ?? record.outputTokens),
    cacheRead: token(cumulative?.cacheRead ?? record.cacheReadTokens),
    cacheWrite: token(cumulative?.cacheWrite ?? record.cacheWriteTokens),
    cacheWrite1h: token(record.cacheWrite1hTokens),
    thought: token(record.thoughtTokens ?? record.reasoning ?? cumulative?.reasoning),
  }
}

function claudeRecordScore(record) {
  return ['serviceTier', 'speed', 'inferenceGeo', 'cacheWrite1hTokens', 'cacheWrite5mTokens', 'thoughtTokens']
    .filter(key => Object.hasOwn(record, key)).length
}

function deduplicateClaudeRecords(records) {
  const deduplicated = []
  const indexes = new Map()
  for (const record of records) {
    if (record.tool !== 'claude' || !record.messageId) {
      deduplicated.push(record)
      continue
    }
    const index = indexes.get(record.messageId)
    if (index === undefined) {
      indexes.set(record.messageId, deduplicated.length)
      deduplicated.push(record)
      continue
    }
    const existing = deduplicated[index]
    const score = claudeRecordScore(record)
    const existingScore = claudeRecordScore(existing)
    if (score > existingScore || (score === existingScore && record.occurredAtUtc < existing.occurredAtUtc)) {
      deduplicated[index] = record
    }
  }
  return deduplicated
}

/** Rebuild stable cumulative day/model snapshots from cached per-file records. */
export function buildDailySnapshots(records, machine) {
  const groups = new Map()
  for (const record of deduplicateClaudeRecords(records)) {
    const metadata = sourceMetadata(record.tool, record.model, machine)
    if (!metadata || !record.date || !record.model) { continue }

    const tier = record.serviceTier ?? 'unknown'
    const speed = record.speed ?? 'unknown'
    const geo = record.inferenceGeo ?? 'unknown'
    const eventKey = record.tool === 'claude'
      ? `claude:${record.date}:${record.model}:${tier}:${speed}:${geo}`
      : record.tool === 'gemini-review'
        ? `gemini-review:${record.date}:${record.model}:${record.context}`
        : `${record.tool}:${record.date}:${record.model}`
    let group = groups.get(eventKey)
    if (!group) {
      group = {
        ...metadata,
        tool: record.tool,
        date: record.date,
        model: record.model,
        eventKey,
        serviceTier: record.serviceTier,
        speed: record.speed,
        inferenceGeo: record.inferenceGeo,
        context: record.context,
        input: 0,
        output: 0,
        cacheRead: 0,
        cacheWrite: 0,
        cacheWrite1h: 0,
        cacheWrite5m: 0,
        thought: 0,
        cacheDurationsObserved: true,
        occurredAtUtc: `${record.date}T00:00:00.000Z`,
      }
      groups.set(eventKey, group)
    }

    const usage = recordTokens(record)
    group.input += usage.input
    group.output += usage.output
    group.cacheRead += usage.cacheRead
    group.cacheWrite += usage.cacheWrite
    group.cacheWrite1h += usage.cacheWrite1h
    group.cacheWrite5m += token(record.cacheWrite5mTokens)
    if (usage.cacheWrite > 0 && !(Object.hasOwn(record, 'cacheWrite1hTokens') && Object.hasOwn(record, 'cacheWrite5mTokens'))) {
      group.cacheDurationsObserved = false
    }
    group.thought += usage.thought
    const occurredAtUtc = isoTimestamp(record.occurredAtUtc)
    if (occurredAtUtc && occurredAtUtc > group.occurredAtUtc) { group.occurredAtUtc = occurredAtUtc }
  }

  return [...groups.values()]
    .filter(group => group.input + group.output + group.cacheRead + group.cacheWrite + group.thought > 0)
    .sort((a, b) => a.eventKey.localeCompare(b.eventKey))
    .map(group => {
      return {
        provider: group.provider,
        model: group.model,
        inputTokens: group.input,
        outputTokens: group.output,
        cacheReadTokens: group.cacheRead,
        cacheWriteTokens: group.cacheWrite,
        cacheWrite1hTokens: group.cacheWrite1h,
        thoughtTokens: group.thought,
        costUsd: null,
        eventKey: group.eventKey,
        occurredAtUtc: group.occurredAtUtc,
        sourceId: group.sourceId,
        sourceKind: 'localTelemetry',
        usageScope: group.usageScope ?? 'subscription',
        costBasis: group.costBasis ?? 'notional',
        runtime: group.runtime,
        rawPayload: JSON.stringify({
          source: 'observatory-sweep',
          tool: group.tool,
          machine: machineLabel(machine),
          ...(group.tool === 'codex' ? { processing: 'standard', context: 'short', region: 'global' } : {}),
          ...(group.tool === 'gemini-review' ? { service: 'Gemini Developer API', tier: 'standard', context: group.context } : {}),
          ...(group.serviceTier ? { service_tier: group.serviceTier } : {}),
          ...(group.speed ? { speed: group.speed } : {}),
          ...(group.inferenceGeo ? { inference_geo: group.inferenceGeo } : {}),
          thinking_tokens: group.thought,
          ...(group.cacheWrite > 0 && group.cacheDurationsObserved ? { cache_creation: {
            ephemeral_5m_input_tokens: group.cacheWrite5m,
            ephemeral_1h_input_tokens: group.cacheWrite1h,
          } } : {}),
        }),
      }
    })
}

function zeroSnapshot(snapshot) {
  return {
    ...snapshot,
    inputTokens: 0,
    outputTokens: 0,
    cacheReadTokens: 0,
    cacheWriteTokens: 0,
    cacheWrite1hTokens: 0,
    thoughtTokens: 0,
    costUsd: null,
    rawPayload: JSON.stringify({
      source: 'observatory-sweep',
      tool: snapshot.runtime,
      tombstone: true,
    }),
  }
}

/** Plan current snapshots plus zero corrections for server keys that vanished locally. */
export function planSnapshotSubmissions(snapshots, inventory = []) {
  const identity = snapshot => `${snapshot.sourceId}\n${snapshot.eventKey}`
  const currentKeys = new Set(snapshots.map(identity))
  const submissions = snapshots.map(snapshot => ({ snapshot, active: true }))
  for (const snapshot of Object.values(inventory)) {
    if (!currentKeys.has(identity(snapshot))) {
      // ponytail: /api/events has correction but no deletion. Zero corrections
      // leave a one-request ceiling per removed key; add deletion only if request counts need exact removal.
      submissions.push({ snapshot: zeroSnapshot(snapshot), active: false })
    }
  }
  return submissions.sort((a, b) => {
    if (a.active !== b.active) { return a.active ? -1 : 1 }
    return a.snapshot.sourceId.localeCompare(b.snapshot.sourceId)
      || a.snapshot.eventKey.localeCompare(b.snapshot.eventKey)
  })
}

/** Parse changed files only and return a cache containing exactly the active scan. */
export async function updateFileCache(files, cache, parse, read = path => readFile(path, 'utf8')) {
  const next = {}
  const records = []
  let incomplete = false
  for (const file of files) {
    const cached = cache?.[file.path]
    const unchanged = file.cacheKey === undefined
      ? cached?.mtimeMs === file.mtimeMs
      : cached?.cacheKey === file.cacheKey
    if (unchanged && Array.isArray(cached.records)) {
      next[file.path] = cached
    } else {
      let parsed
      try {
        parsed = await parse(await read(file.path), file)
      } catch (error) {
        // A file rotated or locked mid-scan (TOCTOU ENOENT, SQLITE_BUSY on a live
        // Antigravity conversation) must not abort the whole sweep — and must not
        // be silently skipped either, because a skipped file's keys drop out of
        // the scan and are then tombstoned against server inventory. Reuse the
        // cached records for this path so the key stays exactly as last observed.
        if (cached && Array.isArray(cached.records)) {
          log(`Read failed for ${file.path}; reusing cached records:`, error.message)
          next[file.path] = cached
          for (const record of cached.records) { records.push(record) }
        } else {
          // Unreadable with no cached records. "Nothing was ever posted from this
          // file" only holds within one cache generation: a version bump wipes the
          // cache while the file's keys stay live in server inventory, so the scan
          // is incomplete and the source's corrections and tombstones are withheld.
          incomplete = true
          log(`Read failed for ${file.path}; no cached records yet:`, error.message)
        }
        continue
      }
      next[file.path] = {
        mtimeMs: file.mtimeMs,
        ...(file.cacheKey === undefined ? {} : { cacheKey: file.cacheKey }),
        records: parsed ?? [],
      }
    }
    for (const record of next[file.path].records) { records.push(record) }
  }
  return { cache: next, records, incomplete }
}

export function parseLocalSources(value) {
  if (value === undefined) { return new Set(ALL_LOCAL_SOURCES) }
  const selected = value.split(',').map(x => x.trim().toLowerCase()).filter(Boolean)
  // A typo'd name must abort, not silently shrink the enabled set: an empty or
  // unintended subset changes which sources this run is responsible for.
  const unknown = selected.filter(x => !ALL_LOCAL_SOURCES.includes(x))
  if (unknown.length) {
    throw new Error(`Unknown OBSERVATORY_LOCAL_SOURCES value(s): ${unknown.join(', ')}. Known: ${ALL_LOCAL_SOURCES.join(', ')}`)
  }
  return new Set(selected)
}

// --- IO / orchestration -----------------------------------------------------

const VERBOSE = process.argv.includes('--verbose')
const DRY_RUN = process.argv.includes('--dry-run')
const log = (...args) => { if (VERBOSE) { console.error(...args) } }

async function loadState(path) {
  try { return JSON.parse(await readFile(path, 'utf8')) }
  catch { return {} }
}

async function saveState(path, state) {
  if (DRY_RUN) { return }
  await mkdir(dirname(path), { recursive: true })
  await writeFile(path, JSON.stringify(state, null, 2), 'utf8')
}

async function postEvent(url, apiKey, body) {
  if (DRY_RUN) { log('DRYRUN would post:', JSON.stringify(body)); return true }
  try {
    const response = await observatoryFetch(`${url}/api/events`, apiKey, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    })
    if (!response.ok) { log(`POST ${response.status} for ${body.eventKey}`); return false }
    return true
  } catch (error) {
    log(`POST failed for ${body.eventKey}:`, error.message)
    return false
  }
}

async function fetchSnapshotInventory(url, apiKey, enabled, machine) {
  const inventory = []
  const failedSources = new Set()
  for (const tool of ALL_LOCAL_SOURCES) {
    // Only the current scan's sources: fetching every source's inventory would
    // plan zero tombstones for snapshots this run is not responsible for.
    if (!enabled.has(tool)) { continue }
    const sourceId = localSourceId(tool, machine)
    try {
      const response = await observatoryFetch(
        `${url}/api/events/local-snapshots?sourceId=${encodeURIComponent(sourceId)}`,
        apiKey,
      )
      if (!response.ok) { throw new Error(`Inventory GET ${response.status} for ${sourceId}`) }
      const snapshots = await response.json()
      if (!Array.isArray(snapshots)
        || snapshots.some(snapshot => !snapshot || typeof snapshot !== 'object'
          || snapshot.sourceId !== sourceId || typeof snapshot.eventKey !== 'string')) {
        throw new Error(`Invalid inventory response for ${sourceId}`)
      }
      inventory.push(...snapshots)
    } catch (error) {
      // One source's inventory failure must not abort the other collectors.
      // Continue with an empty inventory for it — but suppress that source's
      // tombstones for the run, otherwise an unreadable inventory reads as
      // "nothing on the server" and the reconciliation zeroes real history.
      console.error(`Inventory unavailable for ${sourceId} (${error.message}); its tombstones are suppressed this run.`)
      failedSources.add(sourceId)
    }
  }
  return { inventory, failedSources }
}

async function listMatching(dir, matches, out = [], io = { readdir, stat }, topLevel = true) {
  let entries
  try { entries = await io.readdir(dir, { withFileTypes: true }) }
  catch (error) {
    if (topLevel && error?.code === 'ENOENT') { return out }
    throw error
  }
  for (const entry of entries) {
    const full = join(dir, entry.name)
    if (entry.isDirectory()) { await listMatching(full, matches, out, io, false) }
    else if (matches(entry.name)) {
      const details = await io.stat(full)
      out.push({ path: full, mtimeMs: details.mtimeMs })
    }
  }
  return out
}

export const listJsonl = (dir, out = [], io = { readdir, stat }, topLevel = true) =>
  listMatching(dir, name => name.endsWith('.jsonl'), out, io, topLevel)

const listDatabases = dir => listMatching(dir, name => name.endsWith('.db'))

export async function scanRecords(cfg, state, enabled, discover = listJsonl) {
  const records = []
  const incompleteSources = new Set()
  if (state.parseCacheVersion !== PARSE_CACHE_VERSION) {
    state.files = {}
    state.parseCacheVersion = PARSE_CACHE_VERSION
  }
  state.files ??= {}

  if (enabled.has('codex')) {
    const files = await discover(join(cfg.codexHome, 'sessions'))
    const result = await updateFileCache(files, state.files.codex, content => {
      const parsed = parseCodex(content)
      if (!parsed) { return [] }
      const occurredAtUtc = parsed.endedAt
      return [{
        tool: 'codex',
        date: occurredAtUtc.slice(0, 10),
        model: parsed.model,
        occurredAtUtc,
        cum: parsed.cum,
        thoughtTokens: parsed.reasoning,
      }]
    })
    state.files.codex = result.cache
    if (result.incomplete) { incompleteSources.add('codex') }
    for (const record of result.records) { records.push(record) }
  }

  if (enabled.has('copilot')) {
    const files = (await discover(join(cfg.copilotHome, 'session-state')))
      .filter(file => basename(file.path) === 'events.jsonl')
    const result = await updateFileCache(files, state.files.copilot, content => {
      const parsed = parseCopilot(content)
      if (!parsed) { return [] }
      const occurredAtUtc = parsed.endedAt
      return Object.entries(parsed.perModel).map(([model, cum]) => ({
        tool: 'copilot',
        date: occurredAtUtc.slice(0, 10),
        model,
        occurredAtUtc,
        cum,
        thoughtTokens: cum.reasoning,
      }))
    })
    state.files.copilot = result.cache
    if (result.incomplete) { incompleteSources.add('copilot') }
    for (const record of result.records) { records.push(record) }
  }

  if (enabled.has('claude')) {
    const files = await discover(join(cfg.claudeHome, 'projects'))
    const result = await updateFileCache(files, state.files.claude, content => parseClaude(content))
    state.files.claude = result.cache
    if (result.incomplete) { incompleteSources.add('claude') }
    for (const record of result.records) { records.push(record) }
  }

  if (enabled.has('kimi')) {
    const files = (await discover(join(cfg.kimiHome, 'sessions')))
      .filter(file => basename(file.path) === 'wire.jsonl')
    const result = await updateFileCache(files, state.files.kimi, content => parseKimi(content))
    state.files.kimi = result.cache
    if (result.incomplete) { incompleteSources.add('kimi') }
    for (const record of result.records) { records.push(record) }
  }

  if (enabled.has('gemini')) {
    const files = (await discover(join(cfg.geminiHome, 'tmp')))
      .filter(file => /[\\/]gem-review-[^\\/]+[\\/]chats[\\/]session-[^\\/]+\.jsonl$/i.test(file.path))
    const result = await updateFileCache(files, state.files.gemini, content => parseGeminiReview(content))
    state.files.gemini = result.cache
    if (result.incomplete) { incompleteSources.add('gemini') }
    for (const record of result.records) { records.push(record) }
  }

  if (enabled.has('antigravity')) {
    const conversationRoot = join(cfg.geminiHome, 'antigravity-cli')
    const files = []
    for (const file of await listDatabases(join(conversationRoot, 'conversations'))) {
      const sessionId = basename(file.path, '.db')
      const transcriptPath = join(conversationRoot, 'brain', sessionId, '.system_generated', 'logs', 'transcript.jsonl')
      try {
        const transcript = await stat(transcriptPath)
        files.push({ ...file, transcriptPath, cacheKey: `${file.mtimeMs}:${transcript.mtimeMs}` })
      } catch (error) {
        if (error?.code !== 'ENOENT') { throw error }
      }
    }
    const result = await updateFileCache(
      files,
      state.files.antigravity,
      async (_path, file) => parseAntigravityDatabase(file.path, await readFile(file.transcriptPath, 'utf8')),
      path => path,
    )
    state.files.antigravity = result.cache
    if (result.incomplete) { incompleteSources.add('antigravity') }
    for (const record of result.records) { records.push(record) }
  }

  return { records, incompleteSources }
}

export async function main({ discover = listJsonl, now = () => new Date() } = {}) {
  const url = observatoryUrl(process.env.OBSERVATORY_URL ?? 'http://localhost:5039')
  const apiKey = process.env.OBSERVATORY_API_KEY
  if (!apiKey) { console.error('OBSERVATORY_API_KEY not set; nothing to do.'); process.exit(0) }
  const observedAtUtc = now().toISOString()

  const cfg = {
    codexHome: process.env.CODEX_HOME ?? join(homedir(), '.codex'),
    copilotHome: process.env.COPILOT_HOME ?? join(homedir(), '.copilot'),
    claudeHome: process.env.CLAUDE_HOME ?? join(homedir(), '.claude'),
    kimiHome: process.env.KIMI_HOME ?? join(homedir(), '.kimi-code'),
    geminiHome: process.env.GEMINI_HOME ?? join(homedir(), '.gemini'),
  }
  const statePath = process.env.OBSERVATORY_STATE ?? join(homedir(), '.ai-observatory', 'sweep-state.json')
  const state = await loadState(statePath)
  delete state.emitted
  const enabled = parseLocalSources(process.env.OBSERVATORY_LOCAL_SOURCES)
  const machine = machineLabel(process.env.OBSERVATORY_MACHINE ?? hostname())
  // A dry run previews without touching the server: no inventory reads either,
  // so the preview also works offline.
  const { inventory, failedSources } = DRY_RUN
    ? { inventory: [], failedSources: new Set() }
    : await fetchSnapshotInventory(url, apiKey, enabled, machine)
  const { records, incompleteSources } = await scanRecords(cfg, state, enabled, discover)
  const snapshots = buildDailySnapshots(records, machine)
  const submissions = planSnapshotSubmissions(snapshots, inventory)
  await saveState(statePath, state)
  // A source with any unreadable-and-uncached file this run scanned incompletely:
  // its aggregates can only shrink real totals and its vanished keys may merely be
  // missing from the scan, so both its corrections and its tombstones are withheld
  // until a complete scan succeeds.
  const incompleteSourceIds = new Set(
    [...incompleteSources].map(tool => localSourceId(tool, machine)),
  )

  // Unchanged snapshots need no re-post: inventory already carries the server's
  // token counts for every enabled source, so diffing against it keeps a steady
  // run at one request per genuinely changed key instead of re-posting the whole
  // local history every interval.
  const inventoryTokens = new Map(
    inventory.map(snapshot => [`${snapshot.sourceId}\n${snapshot.eventKey}`, snapshot]),
  )
  const unchanged = (snapshot) => {
    const prior = inventoryTokens.get(`${snapshot.sourceId}\n${snapshot.eventKey}`)
    return prior !== undefined && SNAPSHOT_TOKEN_FIELDS.every(
      field => Number.isFinite(prior[field]) && prior[field] === snapshot[field],
    )
  }

  let posted = 0
  const activeSucceeded = new Map()
  const incompleteNoted = new Set()
  for (const submission of submissions.filter(item => item.active)) {
    if (incompleteSourceIds.has(submission.snapshot.sourceId)) {
      if (!incompleteNoted.has(submission.snapshot.sourceId)) {
        console.error(`Scan incomplete for ${submission.snapshot.sourceId}; its corrections and tombstones are withheld this run.`)
        incompleteNoted.add(submission.snapshot.sourceId)
      }
      continue
    }
    if (unchanged(submission.snapshot)) {
      activeSucceeded.set(
        submission.snapshot.sourceId,
        activeSucceeded.get(submission.snapshot.sourceId) ?? true,
      )
      continue
    }
    const succeeded = await postEvent(
      url,
      apiKey,
      { ...submission.snapshot, observedAtUtc },
    )
    activeSucceeded.set(
      submission.snapshot.sourceId,
      (activeSucceeded.get(submission.snapshot.sourceId) ?? true) && succeeded,
    )
    if (succeeded) {
      posted++
    }
  }
  for (const submission of submissions.filter(item => !item.active)) {
    // A source whose inventory could not be read this run must not be
    // tombstoned: its empty inventory is a fetch failure, not server truth.
    if (failedSources.has(submission.snapshot.sourceId)) { continue }
    // An incompletely scanned source must not be tombstoned either: the key may
    // only have vanished from the scan, not from disk.
    if (incompleteSourceIds.has(submission.snapshot.sourceId)) { continue }
    // Tombstones post only for a source whose active submissions all succeeded
    // this run. `undefined` (source not in the enabled subset, or no active
    // snapshots at all) and `false` (a replacement exhausted retries) both
    // suppress, so a partial or disabled run can never zero server history.
    if (activeSucceeded.get(submission.snapshot.sourceId) !== true) { continue }
    // Server inventory is the durable retry marker: a failed tombstone remains
    // visible and is compensated by the next scheduled reconciliation.
    if (await postEvent(url, apiKey, { ...submission.snapshot, observedAtUtc })) {
      posted++
    }
  }

  log(`Sweep complete: ${posted} event(s) ${DRY_RUN ? 'would be ' : ''}posted.`)
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  main().catch(error => { console.error(error); process.exit(1) })
}
