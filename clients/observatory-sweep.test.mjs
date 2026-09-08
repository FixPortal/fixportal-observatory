// Self-check for the local usage sweeper's pure logic. Zero deps:
//   node --test clients/observatory-sweep.test.mjs
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { execFile } from 'node:child_process'
import { mkdtemp, mkdir, readFile, rm, utimes, writeFile } from 'node:fs/promises'
import { createServer } from 'node:http'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { DatabaseSync } from 'node:sqlite'
import { fileURLToPath } from 'node:url'
import { promisify } from 'node:util'
import * as sweep from './observatory-sweep.mjs'
import {
  parseCodex, parseCopilot, parseClaude, parseKimi,
  buildDailySnapshots, updateFileCache, parseLocalSources, listJsonl,
  planSnapshotSubmissions, scanRecords, observatoryUrl, observatoryFetch,
  machineLabel,
} from './observatory-sweep.mjs'

const TEST_MACHINE = 'test-machine'

function varint(value) {
  const bytes = []
  do {
    bytes.push((value & 0x7f) | (value > 0x7f ? 0x80 : 0))
    value = Math.floor(value / 128)
  } while (value)
  return bytes
}

function antigravityPayload(input, output, thoughts = 0, prefix = []) {
  const usage = [0x08, 1, 0x10, ...varint(input), 0x18, ...varint(output), 0x30, ...varint(thoughts)]
  const response = [...prefix, 0x4a, ...varint(usage.length), ...usage]
  return Buffer.from([0x2a, ...varint(response.length), ...response])
}

test('observatoryUrl protects the API key in transit', () => {
  assert.equal(observatoryUrl('https://observatory.example/api/'), 'https://observatory.example/api')
  assert.equal(observatoryUrl('http://127.0.0.1:5039/'), 'http://127.0.0.1:5039')
  assert.equal(observatoryUrl('http://[::1]:5039'), 'http://[::1]:5039')

  for (const value of [
    'http://observatory.example',
    'ftp://observatory.example',
    'https://key@observatory.example',
    'https://observatory.example?redirect=elsewhere',
    'not-a-url',
  ]) {
    assert.throws(() => observatoryUrl(value), /OBSERVATORY_URL/)
  }
})

test('observatoryFetch does not forward the API key across redirects', async () => {
  let redirectedKey = null
  const target = createServer((request, response) => {
    redirectedKey = request.headers['x-observatory-key']
    response.writeHead(200).end()
  })
  await new Promise(resolve => target.listen(0, '127.0.0.1', resolve))
  const targetAddress = target.address()
  const redirector = createServer((_request, response) => {
    response.writeHead(302, { Location: `http://127.0.0.1:${targetAddress.port}` }).end()
  })
  await new Promise(resolve => redirector.listen(0, '127.0.0.1', resolve))
  const redirectorAddress = redirector.address()

  try {
    await assert.rejects(observatoryFetch(`http://127.0.0.1:${redirectorAddress.port}`, 'secret'))
    assert.equal(redirectedKey, null)
  } finally {
    await new Promise(resolve => redirector.close(resolve))
    await new Promise(resolve => target.close(resolve))
  }
})

test('parseCodex takes the last token_count and splits cached input out', () => {
  const lines = [
    JSON.stringify({ type: 'turn_context', payload: { model: 'gpt-5.5' } }),
    JSON.stringify({ timestamp: '2026-06-01T10:00:00Z', type: 'event_msg', payload: { type: 'token_count', info: { total_token_usage: { input_tokens: 100, cached_input_tokens: 40, output_tokens: 25, reasoning_output_tokens: 7, total_tokens: 125 } } } }),
    // a later, larger cumulative reading wins
    JSON.stringify({ timestamp: '2026-06-01T10:05:00Z', type: 'event_msg', payload: { type: 'token_count', info: { total_token_usage: { input_tokens: 300, cached_input_tokens: 100, output_tokens: 90, reasoning_output_tokens: 12, total_tokens: 390 } } } }),
  ].join('\n')
  const r = parseCodex(lines)
  assert.equal(r.model, 'gpt-5.5')
  assert.equal(r.endedAt, '2026-06-01T10:05:00Z')
  assert.equal(r.reasoning, 12)
  // input billable = 300 - 100 cached; cacheRead = 100; output whole (incl reasoning)
  assert.deepEqual(r.cum, { input: 200, output: 90, cacheRead: 100, cacheWrite: 0 })
})

test('parseCodex returns null when no token_count is present', () => {
  const lines = JSON.stringify({ type: 'turn_context', payload: { model: 'gpt-5' } })
  assert.equal(parseCodex(lines), null)
})

test('parseCodex defaults the model when no turn_context carries one', () => {
  const line = JSON.stringify({ timestamp: '2026-08-24T12:00:00Z', type: 'event_msg', payload: { type: 'token_count', info: { total_token_usage: { input_tokens: 10, cached_input_tokens: 0, output_tokens: 5 } } } })
  assert.equal(parseCodex(line).model, 'gpt-5')
})

test('parseCodex ignores valid JSON values that are not telemetry objects', () => {
  const content = ['null', '0', 'true', '"text"', '[]'].join('\n')

  assert.equal(parseCodex(content), null)
})

test('parseCodex rejects token totals without a telemetry timestamp', () => {
  const line = JSON.stringify({ type: 'event_msg', payload: { type: 'token_count', info: { total_token_usage: { input_tokens: 10, output_tokens: 5 } } } })

  assert.equal(parseCodex(line), null)
})

test('parseCopilot reads modelMetrics from the shutdown event and splits cache reads', () => {
  const shutdown = {
    type: 'session.shutdown',
    timestamp: '2026-06-13T18:25:23.144Z',
    data: {
      modelMetrics: {
        'gpt-5.4': { usage: { inputTokens: 185510, outputTokens: 7710, cacheReadTokens: 141312, cacheWriteTokens: 0, reasoningTokens: 6068 } },
      },
    },
  }
  const r = parseCopilot(['{"type":"session.start"}', JSON.stringify(shutdown)].join('\n'))
  assert.equal(r.endedAt, '2026-06-13T18:25:23.144Z')
  // input billable = 185510 - 141312 = 44198
  assert.deepEqual(r.perModel['gpt-5.4'], { input: 44198, output: 7710, cacheRead: 141312, cacheWrite: 0, reasoning: 6068 })
})

test('parseCopilot returns null when the session has not shut down', () => {
  assert.equal(parseCopilot('{"type":"session.start"}\n{"type":"turn"}'), null)
})

test('parseCopilot rejects shutdown totals without a telemetry timestamp', () => {
  const shutdown = JSON.stringify({ type: 'session.shutdown', data: { modelMetrics: { 'gpt-5.4': { usage: { inputTokens: 10, outputTokens: 5 } } } } })

  assert.equal(parseCopilot(shutdown), null)
})

test('parseClaude preserves pricing dimensions before global message deduplication', () => {
  const content = [
    JSON.stringify({ type: 'assistant', timestamp: '2026-08-24T12:00:00Z', message: { id: 'msg-1', model: 'claude-opus-5', usage: { input_tokens: 2, output_tokens: 10, cache_read_input_tokens: 20, cache_creation_input_tokens: 30, thinking_tokens: 4, cache_creation: { ephemeral_5m_input_tokens: 5, ephemeral_1h_input_tokens: 25 }, service_tier: 'standard', speed: 'standard', inference_geo: 'not_available' } } }),
    JSON.stringify({ type: 'assistant', timestamp: '2026-08-24T12:00:01Z', message: { id: 'msg-1', model: 'claude-opus-5', usage: { input_tokens: 2, output_tokens: 10, cache_read_input_tokens: 20, cache_creation_input_tokens: 30 } } }),
  ].join('\n')

  const records = parseClaude(content)

  assert.equal(records.length, 2)
  assert.deepEqual(records[0], {
    tool: 'claude', messageId: 'msg-1', date: '2026-08-24', model: 'claude-opus-5',
    occurredAtUtc: '2026-08-24T12:00:00.000Z', inputTokens: 2, outputTokens: 10,
    cacheReadTokens: 20, cacheWriteTokens: 30, cacheWrite1hTokens: 25,
    cacheWrite5mTokens: 5, thoughtTokens: 4, serviceTier: 'standard', speed: 'standard',
    inferenceGeo: 'not_available',
  })
})

test('parseClaude keeps sparse and rich copies for global message-id selection', () => {
  const content = [
    JSON.stringify({ type: 'assistant', timestamp: '2026-08-24T12:00:00Z', message: { id: 'msg-sparse-first', model: 'claude-opus-5', usage: { input_tokens: 2, output_tokens: 10, cache_creation_input_tokens: 30 } } }),
    JSON.stringify({ type: 'assistant', timestamp: '2026-08-24T12:00:01Z', message: { id: 'msg-sparse-first', model: 'claude-opus-5', usage: { input_tokens: 2, output_tokens: 10, cache_creation_input_tokens: 30, cache_creation: { ephemeral_5m_input_tokens: 5, ephemeral_1h_input_tokens: 25 }, service_tier: 'standard', speed: 'fast', inference_geo: 'us' } } }),
  ].join('\n')

  const records = parseClaude(content)
  const snapshots = buildDailySnapshots(records, TEST_MACHINE)

  assert.equal(records.length, 2)
  assert.equal(snapshots.length, 1)
  assert.equal(snapshots[0].eventKey, 'claude:2026-08-24:claude-opus-5:standard:fast:us')
  assert.equal(snapshots[0].cacheWrite1hTokens, 25)
})

test('parseClaude ignores valid JSON values that are not telemetry objects', () => {
  const content = ['null', '0', 'true', '"text"', '[]'].join('\n')

  assert.deepEqual(parseClaude(content), [])
})

test('buildDailySnapshots deduplicates Claude message ids across transcript files', () => {
  const line = JSON.stringify({ type: 'assistant', timestamp: '2026-08-24T12:00:00Z', message: { id: 'msg-copy', model: 'claude-opus-5', usage: { input_tokens: 2, output_tokens: 10 } } })

  const snapshots = buildDailySnapshots([...parseClaude(line), ...parseClaude(line)], TEST_MACHINE)

  assert.equal(snapshots.length, 1)
  assert.equal(snapshots[0].inputTokens, 2)
})

test('buildDailySnapshots keeps the richest Claude copy when duplicate transcripts differ', () => {
  const sparse = JSON.stringify({ type: 'assistant', timestamp: '2026-08-24T12:00:01Z', message: { id: 'msg-richest', model: 'claude-opus-5', usage: { input_tokens: 2, output_tokens: 10, cache_creation_input_tokens: 30 } } })
  const full = JSON.stringify({ type: 'assistant', timestamp: '2026-08-24T12:00:00Z', message: { id: 'msg-richest', model: 'claude-opus-5', usage: { input_tokens: 2, output_tokens: 10, cache_creation_input_tokens: 30, cache_creation: { ephemeral_5m_input_tokens: 5, ephemeral_1h_input_tokens: 25 }, service_tier: 'standard', speed: 'fast', inference_geo: 'us' } } })

  const snapshots = buildDailySnapshots([...parseClaude(sparse), ...parseClaude(full)], TEST_MACHINE)

  assert.equal(snapshots.length, 1)
  assert.equal(snapshots[0].eventKey, 'claude:2026-08-24:claude-opus-5:standard:fast:us')
  assert.equal(snapshots[0].cacheWrite1hTokens, 25)
})

test('buildDailySnapshots prefers a split-only richer Claude copy across files', () => {
  const sparse = JSON.stringify({ type: 'assistant', timestamp: '2026-08-24T12:00:00Z', message: { id: 'msg-split-only', model: 'claude-opus-5', usage: { input_tokens: 2, output_tokens: 10, cache_creation_input_tokens: 30 } } })
  const split = JSON.stringify({ type: 'assistant', timestamp: '2026-08-24T12:00:01Z', message: { id: 'msg-split-only', model: 'claude-opus-5', usage: { input_tokens: 2, output_tokens: 10, cache_creation_input_tokens: 30, cache_creation: { ephemeral_5m_input_tokens: 5, ephemeral_1h_input_tokens: 25 } } } })

  const snapshots = buildDailySnapshots([...parseClaude(sparse), ...parseClaude(split)], TEST_MACHINE)

  assert.equal(snapshots.length, 1)
  assert.equal(snapshots[0].cacheWrite1hTokens, 25)
})

test('parseKimi reads usage.record and ignores its mirrored step.end usage', () => {
  const usage = { inputOther: 10, output: 2, inputCacheRead: 20, inputCacheCreation: 3 }
  const content = [
    JSON.stringify({ type: 'usage.record', time: 1787572800000, model: 'kimi-code/kimi-for-coding', usage }),
    JSON.stringify({ type: 'context.append_loop_event', time: 1787572800001, event: { type: 'step.end', usage } }),
  ].join('\n')

  const records = parseKimi(content)

  assert.equal(records.length, 1)
  assert.equal(records[0].inputTokens, 10)
  assert.equal(records[0].outputTokens, 2)
  assert.equal(records[0].cacheReadTokens, 20)
  assert.equal(records[0].cacheWriteTokens, 3)
  assert.equal(records[0].occurredAtUtc, '2026-08-24T12:00:00.000Z')
})

test('parseKimi ignores valid JSON values that are not telemetry objects', () => {
  const content = ['null', '0', 'true', '"text"', '[]'].join('\n')

  assert.deepEqual(parseKimi(content), [])
})

test('Gemini review transcripts produce API list-price snapshots', () => {
  const records = sweep.parseGeminiReview([
    JSON.stringify({ sessionId: 'review-session', startTime: '2026-08-24T11:59:00Z' }),
    JSON.stringify({
      type: 'gemini', timestamp: '2026-08-24T12:00:00Z', model: 'gemini-3.1-pro-preview',
      tokens: { input: 100, output: 20, cached: 10, thoughts: 5 },
    }),
  ].join('\n'))

  const [snapshot] = buildDailySnapshots(records, TEST_MACHINE)

  assert.equal(snapshot.provider, 'Google')
  assert.equal(snapshot.sourceId, 'gemini-review-local@test-machine')
  assert.equal(snapshot.usageScope, 'api')
  assert.equal(snapshot.costBasis, 'listPriceEstimate')
  assert.equal(snapshot.inputTokens, 90)
  assert.equal(snapshot.outputTokens, 20)
  assert.equal(snapshot.cacheReadTokens, 10)
  assert.equal(snapshot.thoughtTokens, 5)
  assert.deepEqual(JSON.parse(snapshot.rawPayload), {
    source: 'observatory-sweep', tool: 'gemini-review', machine: 'test-machine',
    service: 'Gemini Developer API', tier: 'standard', context: 'short', thinking_tokens: 5,
  })
})

test('Antigravity databases produce subscription notional snapshots', async () => {
  const root = await mkdtemp(join(tmpdir(), 'observatory-antigravity-'))
  const dbPath = join(root, 'session-id.db')
  const db = new DatabaseSync(dbPath)
  db.exec('CREATE TABLE steps (idx INTEGER PRIMARY KEY, step_type INTEGER, step_payload BLOB)')
  const insert = db.prepare('INSERT INTO steps (idx, step_type, step_payload) VALUES (?, ?, ?)')
  insert.run(1, 23, antigravityPayload(100, 20, 5))
  insert.run(2, 23, antigravityPayload(50, 10, 2))
  db.close()
  const transcript = JSON.stringify({
    created_at: '2026-08-24T12:00:00Z',
    content: 'The user changed setting `Model Selection` from None to Gemini 3.1 Pro (High).',
  })

  try {
    const [record] = await sweep.parseAntigravityDatabase(dbPath, transcript)
    const [snapshot] = buildDailySnapshots([record], TEST_MACHINE)

    assert.equal(snapshot.provider, 'Google')
    assert.equal(snapshot.sourceId, 'antigravity-local@test-machine')
    assert.equal(snapshot.usageScope, 'subscription')
    assert.equal(snapshot.costBasis, 'notional')
    assert.equal(snapshot.model, 'gemini-3.1-pro-high')
    assert.equal(snapshot.inputTokens, 150)
    assert.equal(snapshot.outputTokens, 30)
    assert.equal(snapshot.thoughtTokens, 7)
  } finally {
    await rm(root, { recursive: true, force: true })
  }
})

test('Antigravity parser ignores protobuf fields that only resemble token counters', async () => {
  const root = await mkdtemp(join(tmpdir(), 'observatory-antigravity-false-token-'))
  const dbPath = join(root, 'session-id.db')
  const db = new DatabaseSync(dbPath)
  db.exec('CREATE TABLE steps (idx INTEGER PRIMARY KEY, step_type INTEGER, step_payload BLOB)')
  const unrelated = [0x08, ...varint(1_765_000_000), 0x10, ...varint(32_437_700), 0x18, 5]
  db.prepare('INSERT INTO steps VALUES (?, ?, ?)')
    .run(1, 23, antigravityPayload(100, 20, 5, [0x0a, ...varint(unrelated.length), ...unrelated]))
  db.close()

  try {
    const [record] = await sweep.parseAntigravityDatabase(
      dbPath,
      JSON.stringify({ created_at: '2026-08-24T12:00:00Z', content: 'Gemini 3.1 Pro (High)' }),
    )
    assert.equal(record.inputTokens, 100)
    assert.equal(record.outputTokens, 20)
    assert.equal(record.thoughtTokens, 5)
  } finally {
    await rm(root, { recursive: true, force: true })
  }
})

test('Antigravity parser reports rejected usage rows while retaining valid totals', async () => {
  const root = await mkdtemp(join(tmpdir(), 'observatory-antigravity-rejected-row-'))
  const dbPath = join(root, 'session-id.db')
  const db = new DatabaseSync(dbPath)
  db.exec('CREATE TABLE steps (idx INTEGER PRIMARY KEY, step_type INTEGER, step_payload BLOB)')
  const insert = db.prepare('INSERT INTO steps VALUES (?, ?, ?)')
  insert.run(1, 23, antigravityPayload(100, 20, 5))
  insert.run(2, 23, antigravityPayload(100_000_000, 20, 5))
  db.close()
  const messages = []

  try {
    const [record] = await sweep.parseAntigravityDatabase(
      dbPath,
      JSON.stringify({ created_at: '2026-08-24T12:00:00Z', content: 'Gemini 3.1 Pro' }),
      message => messages.push(message),
    )

    assert.equal(record.inputTokens, 100)
    assert.deepEqual(messages, [`Skipped 1 unrecognized Antigravity usage row in ${dbPath}`])
  } finally {
    await rm(root, { recursive: true, force: true })
  }
})

test('scanRecords discovers retained Gemini reviews and Antigravity conversations', async () => {
  const root = await mkdtemp(join(tmpdir(), 'observatory-google-scan-'))
  const geminiHome = join(root, 'gemini')
  const reviewChats = join(geminiHome, 'tmp', 'gem-review-fixture', 'chats')
  const conversations = join(geminiHome, 'antigravity-cli', 'conversations')
  const transcriptDir = join(geminiHome, 'antigravity-cli', 'brain', 'agy-session', '.system_generated', 'logs')
  await mkdir(reviewChats, { recursive: true })
  await mkdir(conversations, { recursive: true })
  await mkdir(transcriptDir, { recursive: true })
  await writeFile(join(reviewChats, 'session-review.jsonl'), JSON.stringify({
    type: 'gemini', timestamp: '2026-08-24T12:00:00Z', model: 'gemini-3.1-pro-preview',
    tokens: { input: 100, output: 20, cached: 10, thoughts: 5 },
  }))
  await writeFile(join(transcriptDir, 'transcript.jsonl'), JSON.stringify({
    created_at: '2026-08-24T12:00:00Z', content: 'Model Selection` from None to Gemini 3.1 Pro (High).',
  }))
  const db = new DatabaseSync(join(conversations, 'agy-session.db'))
  db.exec('CREATE TABLE steps (idx INTEGER PRIMARY KEY, step_type INTEGER, step_payload BLOB)')
  db.prepare('INSERT INTO steps VALUES (?, ?, ?)').run(1, 23, antigravityPayload(50, 10))
  db.close()
  const cfg = {
    codexHome: join(root, 'codex'), copilotHome: join(root, 'copilot'),
    claudeHome: join(root, 'claude'), kimiHome: join(root, 'kimi'), geminiHome,
  }

  try {
    const { records } = await scanRecords(cfg, {}, new Set(['gemini', 'antigravity']))
    assert.deepEqual(records.map(record => record.tool).sort(), ['antigravity', 'gemini-review'])
  } finally {
    await rm(root, { recursive: true, force: true })
  }
})

test('local parsers ignore records without a valid observation timestamp', () => {
  const claude = parseClaude(JSON.stringify({ type: 'assistant', timestamp: null, message: { id: 'msg-no-time', model: 'claude-opus-5', usage: { input_tokens: 1, output_tokens: 1 } } }))
  const kimi = parseKimi(JSON.stringify({ type: 'usage.record', time: null, model: 'kimi-code/kimi-for-coding', usage: { inputOther: 1, output: 1 } }))

  assert.deepEqual(claude, [])
  assert.deepEqual(kimi, [])
})

test('buildDailySnapshots sums sessions into one stable cumulative day/model key', () => {
  const records = [
    { tool: 'codex', date: '2026-08-24', model: 'gpt-5.4', occurredAtUtc: '2026-08-24T10:00:00Z', cum: { input: 10, output: 2, cacheRead: 1, cacheWrite: 0 } },
    { tool: 'codex', date: '2026-08-24', model: 'gpt-5.4', occurredAtUtc: '2026-08-24T12:00:00Z', cum: { input: 20, output: 3, cacheRead: 2, cacheWrite: 0 } },
  ]

  const snapshots = buildDailySnapshots(records, TEST_MACHINE)

  assert.equal(snapshots.length, 1)
  assert.equal(snapshots[0].eventKey, 'codex:2026-08-24:gpt-5.4')
  assert.equal(snapshots[0].inputTokens, 30)
  assert.equal(snapshots[0].outputTokens, 5)
  assert.equal(snapshots[0].cacheReadTokens, 3)
  assert.equal(snapshots[0].occurredAtUtc, '2026-08-24T12:00:00.000Z')
  assert.equal(snapshots[0].sourceId, 'codex-local@test-machine')
  assert.equal(snapshots[0].usageScope, 'subscription')
  assert.equal(snapshots[0].costBasis, 'notional')
  assert.equal(snapshots[0].costUsd, null)
  assert.deepEqual(JSON.parse(snapshots[0].rawPayload), {
    source: 'observatory-sweep', tool: 'codex', machine: 'test-machine',
    processing: 'standard', context: 'short', region: 'global', thinking_tokens: 0,
  })
})

test('buildDailySnapshots maps only exact known Copilot model prefixes without inventing dimensions', () => {
  const snapshot = model => buildDailySnapshots([{
    tool: 'copilot', date: '2026-08-24', model, occurredAtUtc: '2026-08-24T12:00:00Z',
    inputTokens: 1, outputTokens: 1, cacheReadTokens: 0, cacheWriteTokens: 0,
  }], TEST_MACHINE)[0]

  assert.equal(snapshot('gpt-5.4-2026-08-24').provider, 'OpenAI')
  assert.equal(snapshot('claude-opus-5-20260824').provider, 'Anthropic')
  assert.equal(snapshot('gpt-4o').provider, 'Copilot')
  assert.equal(snapshot('gpt-4o').costUsd, null)
  assert.equal(JSON.parse(snapshot('gpt-4o').rawPayload).region, undefined)
})

test('buildDailySnapshots replaces a changed transcript under the same key', () => {
  const record = input => ({ tool: 'codex', date: '2026-08-24', model: 'gpt-5.4', cum: { input, output: 2, cacheRead: 1, cacheWrite: 0 } })

  const before = buildDailySnapshots([record(10)], TEST_MACHINE)[0]
  const after = buildDailySnapshots([record(25)], TEST_MACHINE)[0]

  assert.equal(after.eventKey, before.eventKey)
  assert.equal(after.inputTokens, 25)
})

test('buildDailySnapshots keeps thought-only usage active', () => {
  const [snapshot] = buildDailySnapshots([{
    tool: 'claude',
    date: '2026-08-24',
    model: 'claude-opus-5',
    occurredAtUtc: '2026-08-24T12:00:00Z',
    inputTokens: 0,
    outputTokens: 0,
    cacheReadTokens: 0,
    cacheWriteTokens: 0,
    thoughtTokens: 7,
  }], TEST_MACHINE)

  assert.equal(snapshot.eventKey, 'claude:2026-08-24:claude-opus-5:unknown:unknown:unknown')
  assert.equal(snapshot.thoughtTokens, 7)
})

test('buildDailySnapshots keeps Claude grouping dimensions and Kimi cost unknown', () => {
  const claude = parseClaude(JSON.stringify({ type: 'assistant', timestamp: '2026-08-24T12:00:00Z', message: { id: 'msg-dimensions', model: 'claude-opus-5', usage: { input_tokens: 2, output_tokens: 10, cache_creation_input_tokens: 30, thinking_tokens: 4, cache_creation: { ephemeral_5m_input_tokens: 5, ephemeral_1h_input_tokens: 25 }, service_tier: 'standard', speed: 'fast', inference_geo: 'us' } } }))
  const kimi = parseKimi(JSON.stringify({ type: 'usage.record', time: 1787572800000, model: 'kimi-code/kimi-for-coding', usage: { inputOther: 10, output: 2, inputCacheRead: 20, inputCacheCreation: 3 } }))

  const snapshots = buildDailySnapshots([...claude, ...kimi], TEST_MACHINE)
  const claudeSnapshot = snapshots.find(x => x.sourceId === 'claude-local@test-machine')
  const kimiSnapshot = snapshots.find(x => x.sourceId === 'kimi-local@test-machine')

  assert.equal(claudeSnapshot.eventKey, 'claude:2026-08-24:claude-opus-5:standard:fast:us')
  assert.equal(claudeSnapshot.cacheWrite1hTokens, 25)
  assert.equal(claudeSnapshot.thoughtTokens, 4)
  assert.equal(kimiSnapshot.eventKey, 'kimi:2026-08-24:kimi-code/kimi-for-coding')
  assert.equal(kimiSnapshot.costUsd, null)
  assert.equal(JSON.parse(kimiSnapshot.rawPayload).note, undefined)
})

test('updateFileCache parses only changed paths and drops files deleted from the full scan', async () => {
  const cache = {
    same: { mtimeMs: 1, records: [{ id: 'same' }] },
    removed: { mtimeMs: 1, records: [{ id: 'removed' }] },
  }
  const reads = []
  const files = [{ path: 'same', mtimeMs: 1 }, { path: 'changed', mtimeMs: 2 }]

  const result = await updateFileCache(
    files,
    cache,
    content => [{ id: content }],
    async path => { reads.push(path); return path },
  )

  assert.deepEqual(reads, ['changed'])
  assert.deepEqual(result.records.map(x => x.id).sort(), ['changed', 'same'])
  assert.deepEqual(Object.keys(result.cache).sort(), ['changed', 'same'])
})

test('updateFileCache does not collapse distinct composite cache keys with the same mtime sum', async () => {
  const cache = {
    conversation: { mtimeMs: 300, cacheKey: '100:200', records: [{ id: 'old' }] },
  }
  const reads = []

  const result = await updateFileCache(
    [{ path: 'conversation', mtimeMs: 300, cacheKey: '200:100' }],
    cache,
    content => [{ id: content }],
    async path => { reads.push(path); return 'new' },
  )

  assert.deepEqual(reads, ['conversation'])
  assert.deepEqual(result.records, [{ id: 'new' }])
  assert.equal(result.cache.conversation.cacheKey, '200:100')
})

test('scanRecords rebuilds an unversioned matching-mtime cache instead of reusing stale records', async () => {
  const root = await mkdtemp(join(tmpdir(), 'observatory-sweep-upgrade-'))
  const sessions = join(root, 'codex', 'sessions')
  await mkdir(sessions, { recursive: true })
  const path = join(sessions, 'rollout.jsonl')
  await writeFile(path, JSON.stringify({
    type: 'event_msg',
    payload: { type: 'token_count', info: { total_token_usage: { input_tokens: 10, output_tokens: 5 } } },
  }))
  const [file] = await listJsonl(sessions)
  const stale = {
    tool: 'codex',
    date: '2030-01-01',
    model: 'gpt-5',
    occurredAtUtc: '2030-01-01T00:00:00.000Z',
    cum: { input: 10, output: 5, cacheRead: 0, cacheWrite: 0 },
  }
  const state = { files: { codex: { [path]: { mtimeMs: file.mtimeMs, records: [stale] } } } }
  const cfg = {
    codexHome: join(root, 'codex'),
    copilotHome: join(root, 'copilot'),
    claudeHome: join(root, 'claude'),
    kimiHome: join(root, 'kimi'),
  }

  try {
    const { records } = await scanRecords(cfg, state, new Set(['codex']))

    assert.deepEqual(records, [])
    assert.equal(state.parseCacheVersion, 3)
    assert.deepEqual(state.files.codex[path].records, [])
  } finally {
    await rm(root, { recursive: true, force: true })
  }
})

test('observatoryFetch retries transient responses', async () => {
  let attempts = 0
  const server = createServer((_request, response) => {
    attempts++
    if (attempts === 1) {
      response.writeHead(429, { 'Retry-After': '0' }).end()
    } else if (attempts === 2) {
      response.writeHead(500, { 'Retry-After': '0' }).end()
    } else {
      response.writeHead(200).end()
    }
  })
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve))
  const address = server.address()

  try {
    const response = await observatoryFetch(`http://127.0.0.1:${address.port}/events`, 'test-key')
    assert.equal(response.status, 200)
    assert.equal(attempts, 3)
  } finally {
    await new Promise(resolve => server.close(resolve))
  }
})

test('observatoryFetch retries transient transport failures', async () => {
  let attempts = 0
  const server = createServer((request, response) => {
    attempts++
    if (attempts < 3) {
      request.socket.destroy()
    } else {
      response.writeHead(200).end()
    }
  })
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve))
  const address = server.address()

  try {
    const response = await observatoryFetch(`http://127.0.0.1:${address.port}/events`, 'test-key')
    assert.equal(response.status, 200)
    assert.equal(attempts, 3)
  } finally {
    await new Promise(resolve => server.close(resolve))
  }
})

test('scanRecords handles transcript histories larger than the engine argument limit', async () => {
  const record = {
    tool: 'claude', date: '2026-08-26', model: 'claude-opus-5',
    occurredAtUtc: '2026-08-26T12:00:00Z',
    cum: { input: 1, output: 1, cacheRead: 0, cacheWrite: 0, cacheWrite1h: 0 },
  }
  const files = Array.from({ length: 200 }, (_, index) => ({ path: `claude-${index}.jsonl`, mtimeMs: 1 }))
  const state = {
    parseCacheVersion: 3,
    files: {
      claude: Object.fromEntries(files.map(file => [file.path, {
        mtimeMs: file.mtimeMs,
        records: Array(1_000).fill(record),
      }])),
    },
  }

  const { records } = await scanRecords(
    { claudeHome: 'claude-home' },
    state,
    new Set(['claude']),
    async () => files,
  )

  assert.equal(records.length, 200_000)
})

test('listJsonl discovers old and current transcripts so age never changes cumulative truth', async () => {
  const root = await mkdtemp(join(tmpdir(), 'observatory-sweep-'))
  const nested = join(root, 'nested')
  await mkdir(nested)
  const oldPath = join(root, 'old.jsonl')
  const currentPath = join(nested, 'current.jsonl')
  await writeFile(oldPath, '{}\n')
  await writeFile(currentPath, '{}\n')
  await utimes(oldPath, new Date('2020-01-01T00:00:00Z'), new Date('2020-01-01T00:00:00Z'))

  try {
    const files = await listJsonl(root)

    assert.deepEqual(files.map(file => file.path).sort(), [currentPath, oldPath].sort())
  } finally {
    await rm(root, { recursive: true, force: true })
  }
})

test('listJsonl tolerates only an absent top-level tool directory', async () => {
  const root = await mkdtemp(join(tmpdir(), 'observatory-sweep-missing-'))

  try {
    assert.deepEqual(await listJsonl(join(root, 'missing')), [])
  } finally {
    await rm(root, { recursive: true, force: true })
  }
})

test('touching and restoring a transcript cannot move usage to another day', async () => {
  const root = await mkdtemp(join(tmpdir(), 'observatory-sweep-touch-'))
  const sessions = join(root, 'codex', 'sessions')
  await mkdir(sessions, { recursive: true })
  const path = join(sessions, 'rollout.jsonl')
  await writeFile(path, JSON.stringify({
    timestamp: '2026-08-24T12:00:00Z',
    type: 'event_msg',
    payload: { type: 'token_count', info: { total_token_usage: { input_tokens: 10, output_tokens: 5 } } },
  }))
  const cfg = {
    codexHome: join(root, 'codex'),
    copilotHome: join(root, 'copilot'),
    claudeHome: join(root, 'claude'),
    kimiHome: join(root, 'kimi'),
  }
  const state = {}

  try {
    const { records: before } = await scanRecords(cfg, state, new Set(['codex']))
    await utimes(path, new Date('2030-01-01T00:00:00Z'), new Date('2030-01-01T00:00:00Z'))
    const { records: touched } = await scanRecords(cfg, state, new Set(['codex']))
    await utimes(path, new Date('2020-01-01T00:00:00Z'), new Date('2020-01-01T00:00:00Z'))
    const { records: restored } = await scanRecords(cfg, state, new Set(['codex']))

    assert.deepEqual([before[0].date, touched[0].date, restored[0].date], [
      '2026-08-24', '2026-08-24', '2026-08-24',
    ])
    assert.deepEqual([before[0].occurredAtUtc, touched[0].occurredAtUtc, restored[0].occurredAtUtc], [
      '2026-08-24T12:00:00Z', '2026-08-24T12:00:00Z', '2026-08-24T12:00:00Z',
    ])
  } finally {
    await rm(root, { recursive: true, force: true })
  }
})

test('server inventory clears a deleted final snapshot after local state loss', () => {
  const prior = buildDailySnapshots([
    { tool: 'codex', date: '2026-08-24', model: 'gpt-5.4', occurredAtUtc: '2026-08-24T12:00:00Z', cum: { input: 30, output: 5, cacheRead: 3, cacheWrite: 0 } },
  ], TEST_MACHINE)[0]

  const submissions = planSnapshotSubmissions([], [prior])

  assert.equal(submissions.length, 1)
  assert.equal(submissions[0].active, false)
  assert.deepEqual({
    eventKey: submissions[0].snapshot.eventKey,
    inputTokens: submissions[0].snapshot.inputTokens,
    outputTokens: submissions[0].snapshot.outputTokens,
    cacheReadTokens: submissions[0].snapshot.cacheReadTokens,
    cacheWriteTokens: submissions[0].snapshot.cacheWriteTokens,
    thoughtTokens: submissions[0].snapshot.thoughtTokens,
    costUsd: submissions[0].snapshot.costUsd,
  }, {
    eventKey: 'codex:2026-08-24:gpt-5.4',
    inputTokens: 0,
    outputTokens: 0,
    cacheReadTokens: 0,
    cacheWriteTokens: 0,
    thoughtTokens: 0,
    costUsd: null,
  })
  assert.equal(JSON.parse(submissions[0].snapshot.rawPayload).tombstone, true)
})

test('server inventory clears a disabled source after state loss and allows re-enable', () => {
  const snapshot = buildDailySnapshots([
    { tool: 'kimi', date: '2026-08-24', model: 'kimi-code/kimi-for-coding', occurredAtUtc: '2026-08-24T12:00:00Z', inputTokens: 10, outputTokens: 2, cacheReadTokens: 20, cacheWriteTokens: 3 },
  ], TEST_MACHINE)[0]
  const disabledSnapshots = parseLocalSources('codex').has('kimi') ? [snapshot] : []
  const [disabled] = planSnapshotSubmissions(disabledSnapshots, [snapshot])

  const reenabledSnapshots = parseLocalSources('kimi').has('kimi') ? [snapshot] : []
  const [reenabled] = planSnapshotSubmissions(reenabledSnapshots, [])

  assert.equal(disabled.active, false)
  assert.equal(disabled.snapshot.inputTokens, 0)
  assert.equal(reenabled.active, true)
  assert.equal(reenabled.snapshot.eventKey, 'kimi:2026-08-24:kimi-code/kimi-for-coding')
  assert.equal(reenabled.snapshot.inputTokens, 10)
})

test('reconciliation corrects a daily snapshot after one transcript is deleted', () => {
  const record = input => ({
    tool: 'codex', date: '2026-08-24', model: 'gpt-5.4', occurredAtUtc: '2026-08-24T12:00:00Z',
    cum: { input, output: 2, cacheRead: 1, cacheWrite: 0 },
  })
  const prior = buildDailySnapshots([record(10), record(20)], TEST_MACHINE)[0]
  const current = buildDailySnapshots([record(10)], TEST_MACHINE)[0]

  const submissions = planSnapshotSubmissions([current], [prior])

  assert.equal(submissions.length, 1)
  assert.equal(submissions[0].active, true)
  assert.equal(submissions[0].snapshot.eventKey, 'codex:2026-08-24:gpt-5.4')
  assert.equal(submissions[0].snapshot.inputTokens, 10)
})

test('server inventory clears the old Claude dimension key after local state loss', () => {
  const claude = speed => buildDailySnapshots(parseClaude(JSON.stringify({
    type: 'assistant',
    timestamp: '2026-08-24T12:00:00Z',
    message: {
      id: 'msg-dimension-change',
      model: 'claude-opus-5',
      usage: { input_tokens: 2, output_tokens: 10, service_tier: 'standard', speed, inference_geo: 'us' },
    },
  })), TEST_MACHINE)[0]
  const prior = claude('standard')
  const current = claude('fast')

  const submissions = planSnapshotSubmissions([current], [prior])
  const oldKey = submissions.find(item => !item.active)
  const newKey = submissions.find(item => item.active)

  assert.equal(submissions.length, 2)
  assert.equal(oldKey.snapshot.eventKey, 'claude:2026-08-24:claude-opus-5:standard:standard:us')
  assert.equal(oldKey.snapshot.inputTokens, 0)
  assert.equal(newKey.snapshot.eventKey, 'claude:2026-08-24:claude-opus-5:standard:fast:us')
  assert.equal(newKey.snapshot.inputTokens, 2)
})

test('replacement plans active snapshots before tombstones in both lexical key directions', () => {
  const snapshot = (sourceId, eventKey) => ({
    provider: 'openai', model: 'gpt-5.4', inputTokens: 1, outputTokens: 0,
    cacheReadTokens: 0, cacheWriteTokens: 0, thoughtTokens: 0, costUsd: 0,
    occurredAtUtc: '2026-08-24T12:00:00Z', runtime: 'codex', sourceId,
    sourceKind: 'localTelemetry', usageScope: 'subscription', costBasis: 'notional', eventKey,
  })

  for (const [oldKey, newKey] of [['a-old', 'z-new'], ['z-old', 'a-new']]) {
    const submissions = planSnapshotSubmissions(
      [snapshot('codex-local', newKey)],
      [snapshot('codex-local', oldKey)],
    )

    assert.deepEqual(submissions.map(item => [item.active, item.snapshot.eventKey]), [
      [true, newKey],
      [false, oldKey],
    ])
  }
})

test('parseLocalSources defaults to every collector and honors an explicit allowlist', () => {
  assert.deepEqual([...parseLocalSources()].sort(), ['antigravity', 'claude', 'codex', 'copilot', 'gemini', 'kimi'])
  assert.deepEqual([...parseLocalSources('codex,kimi')].sort(), ['codex', 'kimi'])
})

test('parseLocalSources aborts on unknown names instead of silently shrinking the set', () => {
  assert.throws(() => parseLocalSources('codxe'), /Unknown OBSERVATORY_LOCAL_SOURCES.*codxe/)
  assert.throws(() => parseLocalSources('codex,nope'), /nope/)
})

test('parseGeminiReview tolerates whitespace-formatted JSON rows', () => {
  const spaced = '{ "type": "gemini", "timestamp": "2026-08-24T12:00:00Z", "model": "gemini-3.1-pro-preview", "tokens": { "input": 100, "output": 20, "cached": 10, "thoughts": 5 } }'

  const [record] = sweep.parseGeminiReview(spaced)

  assert.equal(record.tool, 'gemini-review')
  assert.equal(record.inputTokens, 90)
})

test('Antigravity model detection ignores prose that merely mentions a model', async () => {
  const root = await mkdtemp(join(tmpdir(), 'observatory-antigravity-prose-'))
  const dbPath = join(root, 'session-id.db')
  const db = new DatabaseSync(dbPath)
  db.exec('CREATE TABLE steps (idx INTEGER PRIMARY KEY, step_type INTEGER, step_payload BLOB)')
  db.prepare('INSERT INTO steps VALUES (?, ?, ?)').run(1, 23, antigravityPayload(100, 20, 5))
  db.close()
  const transcript = [
    JSON.stringify({ created_at: '2026-08-24T12:00:00Z', content: 'The user changed setting `Model Selection` from None to Gemini 3.1 Pro (High).' }),
    JSON.stringify({ created_at: '2026-08-24T12:05:00Z', content: 'Now compare this answer with Gemini 2.5 Flash.' }),
  ].join('\n')

  try {
    const [record] = await sweep.parseAntigravityDatabase(dbPath, transcript)

    assert.equal(record.model, 'gemini-3.1-pro-high')
  } finally {
    await rm(root, { recursive: true, force: true })
  }
})

test('updateFileCache reuses cached records when a read fails mid-scan', async () => {
  const cache = {
    flaky: { mtimeMs: 1, records: [{ id: 'cached' }] },
  }

  const result = await updateFileCache(
    [{ path: 'flaky', mtimeMs: 2 }],
    cache,
    content => [{ id: content }],
    async () => { throw new Error('rotated away') },
  )

  assert.deepEqual(result.records, [{ id: 'cached' }])
  assert.deepEqual(result.cache.flaky.records, [{ id: 'cached' }])
})

test('updateFileCache flags only an unreadable-and-uncached file as an incomplete scan', async () => {
  const uncached = await updateFileCache(
    [{ path: 'locked', mtimeMs: 1 }],
    {},
    content => [{ id: content }],
    async () => { throw new Error('locked') },
  )

  assert.equal(uncached.incomplete, true)
  assert.deepEqual(uncached.records, [])

  const cached = await updateFileCache(
    [{ path: 'flaky', mtimeMs: 2 }],
    { flaky: { mtimeMs: 1, records: [{ id: 'cached' }] } },
    content => [{ id: content }],
    async () => { throw new Error('rotated away') },
  )

  assert.equal(cached.incomplete, false)
})

test('scanRecords reparses a version-2 cache instead of reusing its stale records', async () => {
  const root = await mkdtemp(join(tmpdir(), 'observatory-sweep-v2-'))
  const sessions = join(root, 'codex', 'sessions')
  await mkdir(sessions, { recursive: true })
  const path = join(sessions, 'rollout.jsonl')
  await writeFile(path, JSON.stringify({
    type: 'event_msg',
    payload: { type: 'token_count', info: { total_token_usage: { input_tokens: 10, output_tokens: 5 } } },
  }))
  const [file] = await listJsonl(sessions)
  const stale = {
    tool: 'codex',
    date: '2030-01-01',
    model: 'gpt-5',
    occurredAtUtc: '2030-01-01T00:00:00.000Z',
    cum: { input: 10, output: 5, cacheRead: 0, cacheWrite: 0 },
  }
  const state = {
    parseCacheVersion: 2,
    files: { codex: { [path]: { mtimeMs: file.mtimeMs, records: [stale] } } },
  }
  const cfg = {
    codexHome: join(root, 'codex'),
    copilotHome: join(root, 'copilot'),
    claudeHome: join(root, 'claude'),
    kimiHome: join(root, 'kimi'),
  }

  try {
    const { records, incompleteSources } = await scanRecords(cfg, state, new Set(['codex']))

    assert.deepEqual(records, [])
    assert.deepEqual([...incompleteSources], [])
    assert.equal(state.parseCacheVersion, 3)
    assert.deepEqual(state.files.codex[path].records, [])
  } finally {
    await rm(root, { recursive: true, force: true })
  }
})

test('machineLabel slugifies host names for source-id namespacing', () => {
  assert.equal(machineLabel('DESKTOP-ABC123'), 'desktop-abc123')
  assert.equal(machineLabel('Chris’s MacBook Pro'), 'chris-s-macbook-pro')
  assert.equal(machineLabel('--weird__host--'), 'weird-host')
  assert.equal(machineLabel(''), 'unknown-machine')
  assert.equal(machineLabel(undefined), 'unknown-machine')
  assert.equal(machineLabel('!!!'), 'unknown-machine')
})

test('buildDailySnapshots namespaces source ids per machine so hosts never share a namespace', () => {
  const record = {
    tool: 'codex', date: '2026-08-24', model: 'gpt-5.4', occurredAtUtc: '2026-08-24T12:00:00Z',
    cum: { input: 10, output: 2, cacheRead: 1, cacheWrite: 0 },
  }

  const hostA = buildDailySnapshots([record], 'host-a')[0]
  const hostB = buildDailySnapshots([record], 'host-b')[0]

  assert.equal(hostA.sourceId, 'codex-local@host-a')
  assert.equal(hostB.sourceId, 'codex-local@host-b')
  // Same day/model key: machines are separated by source id alone, which is
  // also the server's dedupe and inventory scope.
  assert.equal(hostA.eventKey, hostB.eventKey)
  assert.equal(JSON.parse(hostA.rawPayload).machine, 'host-a')
})

test('main fetches inventory only for enabled per-machine sources and never tombstones without an active submission', async () => {
  const root = await mkdtemp(join(tmpdir(), 'observatory-sweep-main-'))
  const statePath = join(root, 'state', 'sweep.json')
  const posts = []
  const gets = []
  const staleCodex = {
    provider: 'openai', occurredAtUtc: '2026-08-23T12:00:00Z', model: 'gpt-5.5',
    costUsd: 0.01, runtime: 'codex', sourceId: 'codex-local@test-machine', sourceKind: 'localTelemetry',
    usageScope: 'subscription', costBasis: 'notional', eventKey: 'codex:2026-08-23:gpt-5.5',
  }
  const otherMachine = { ...staleCodex, sourceId: 'codex-local@other-machine' }
  const server = createServer(async (request, response) => {
    const url = new URL(request.url, 'http://127.0.0.1')
    if (request.method === 'GET' && url.pathname === '/api/events/local-snapshots') {
      const sourceId = url.searchParams.get('sourceId')
      gets.push({ sourceId, apiKey: request.headers['x-observatory-key'] })
      const body = sourceId === 'codex-local@test-machine' ? [staleCodex]
        : sourceId === 'codex-local@other-machine' ? [otherMachine]
          : []
      response.writeHead(200, { 'Content-Type': 'application/json' }).end(JSON.stringify(body))
      return
    }
    if (request.method === 'POST' && url.pathname === '/api/events') {
      let body = ''
      for await (const chunk of request) { body += chunk }
      posts.push(JSON.parse(body))
      response.writeHead(200, { 'Content-Type': 'application/json' }).end('{}')
      return
    }
    response.writeHead(404).end()
  })
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve))
  const address = server.address()
  const run = promisify(execFile)
  const env = {
    ...process.env,
    OBSERVATORY_URL: `http://127.0.0.1:${address.port}`,
    OBSERVATORY_API_KEY: 'test-key',
    OBSERVATORY_STATE: statePath,
    OBSERVATORY_LOCAL_SOURCES: 'codex',
    OBSERVATORY_MACHINE: 'Test Machine',
    CODEX_HOME: join(root, 'codex'),
    COPILOT_HOME: join(root, 'copilot'),
    CLAUDE_HOME: join(root, 'claude'),
    KIMI_HOME: join(root, 'kimi'),
  }

  try {
    await run(process.execPath, [fileURLToPath(new URL('./observatory-sweep.mjs', import.meta.url))], { env })

    // Only this run's enabled source is inventoried, under this machine's
    // namespace -- the other five sources and other machines are never read.
    // The one-shot legacy migration reads the un-suffixed id for the same tool.
    assert.deepEqual(gets.map(request => request.sourceId), ['codex-local@test-machine', 'codex-local'])
    assert.equal(gets.every(request => request.apiKey === 'test-key'), true)
    // Codex posted no active snapshot, so its stale server key is left alone
    // rather than zeroed by a scan that observed nothing.
    assert.deepEqual(posts, [])
  } finally {
    await new Promise(resolve => server.close(resolve))
    await rm(root, { recursive: true, force: true })
  }
})

test('main retries a failed server-inventory tombstone from persisted state and then succeeds', async () => {
  const root = await mkdtemp(join(tmpdir(), 'observatory-sweep-main-'))
  const sessions = join(root, 'codex', 'sessions')
  const statePath = join(root, 'state', 'sweep.json')
  await mkdir(sessions, { recursive: true })
  await writeFile(join(sessions, 'rollout.jsonl'), [
    JSON.stringify({ type: 'turn_context', payload: { model: 'gpt-5.5' } }),
    JSON.stringify({ timestamp: '2026-08-24T12:00:00Z', type: 'event_msg', payload: { type: 'token_count', info: { total_token_usage: { input_tokens: 10, cached_input_tokens: 0, output_tokens: 5 } } } }),
  ].join('\n'))
  const posts = []
  const gets = []
  let inventoryActive = true
  let tombstoneFailures = 3
  const activeKey = 'codex:2026-08-24:gpt-5.5'
  const prior = {
    provider: 'openai', occurredAtUtc: '2026-08-23T12:00:00Z', model: 'gpt-5.5',
    inputTokens: 2, outputTokens: 10, cacheReadTokens: 0, cacheWriteTokens: 0,
    cacheWrite1hTokens: 0, thoughtTokens: 0, costUsd: 0.01,
    runtime: 'codex', sourceId: 'codex-local@test-machine', sourceKind: 'localTelemetry',
    usageScope: 'subscription', costBasis: 'notional', eventKey: 'codex:2026-08-23:gpt-5.5',
  }
  const server = createServer(async (request, response) => {
    const url = new URL(request.url, 'http://127.0.0.1')
    if (request.method === 'GET' && url.pathname === '/api/events/local-snapshots') {
      const sourceId = url.searchParams.get('sourceId')
      gets.push({ sourceId, apiKey: request.headers['x-observatory-key'] })
      const body = inventoryActive && sourceId === 'codex-local@test-machine' ? [prior] : []
      response.writeHead(200, { 'Content-Type': 'application/json' }).end(JSON.stringify(body))
      return
    }
    if (request.method === 'POST' && url.pathname === '/api/events') {
      let body = ''
      for await (const chunk of request) { body += chunk }
      const parsed = JSON.parse(body)
      posts.push(parsed)
      if (JSON.parse(parsed.rawPayload).tombstone && tombstoneFailures > 0) {
        tombstoneFailures--
        response.writeHead(500, { 'Retry-After': '0' }).end()
      } else {
        if (JSON.parse(parsed.rawPayload).tombstone) { inventoryActive = false }
        response.writeHead(200, { 'Content-Type': 'application/json' }).end('{}')
      }
      return
    }
    response.writeHead(404).end()
  })
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve))
  const address = server.address()
  const run = promisify(execFile)
  const env = {
    ...process.env,
    OBSERVATORY_URL: `http://127.0.0.1:${address.port}`,
    OBSERVATORY_API_KEY: 'test-key',
    OBSERVATORY_STATE: statePath,
    OBSERVATORY_LOCAL_SOURCES: 'codex',
    OBSERVATORY_MACHINE: 'test-machine',
    CODEX_HOME: join(root, 'codex'),
    COPILOT_HOME: join(root, 'copilot'),
    CLAUDE_HOME: join(root, 'claude'),
    KIMI_HOME: join(root, 'kimi'),
  }

  try {
    await run(process.execPath, [fileURLToPath(new URL('./observatory-sweep.mjs', import.meta.url))], { env })
    const persistedAfterFailure = JSON.parse(await readFile(statePath, 'utf8'))
    await run(process.execPath, [fileURLToPath(new URL('./observatory-sweep.mjs', import.meta.url))], { env })

    assert.equal(Object.keys(persistedAfterFailure.files.codex).length, 1)
    // Run 1 also fetches the un-suffixed legacy namespace once for the one-shot migration;
    // the recorded marker keeps run 2 from re-fetching it.
    assert.deepEqual(gets.map(request => request.sourceId), [
      'codex-local@test-machine', 'codex-local', 'codex-local@test-machine',
    ])
    assert.equal(gets.every(request => request.apiKey === 'test-key'), true)
    // Run 1: the active snapshot posts, then the tombstone exhausts its three
    // retries. Run 2 replays both from server inventory and lands the zero.
    assert.deepEqual(posts.map(body => ({
      eventKey: body.eventKey,
      sourceId: body.sourceId,
      inputTokens: body.inputTokens,
      outputTokens: body.outputTokens,
    })), [
      { eventKey: activeKey, sourceId: 'codex-local@test-machine', inputTokens: 10, outputTokens: 5 },
      { eventKey: prior.eventKey, sourceId: 'codex-local@test-machine', inputTokens: 0, outputTokens: 0 },
      { eventKey: prior.eventKey, sourceId: 'codex-local@test-machine', inputTokens: 0, outputTokens: 0 },
      { eventKey: prior.eventKey, sourceId: 'codex-local@test-machine', inputTokens: 0, outputTokens: 0 },
      { eventKey: activeKey, sourceId: 'codex-local@test-machine', inputTokens: 10, outputTokens: 5 },
      { eventKey: prior.eventKey, sourceId: 'codex-local@test-machine', inputTokens: 0, outputTokens: 0 },
    ])
  } finally {
    await new Promise(resolve => server.close(resolve))
    await rm(root, { recursive: true, force: true })
  }
})

test('main withholds a source tombstone after its replacement exhausts retries but continues unrelated sources', async () => {
  const root = await mkdtemp(join(tmpdir(), 'observatory-sweep-replacement-'))
  const projects = join(root, 'claude', 'projects')
  const kimiSessions = join(root, 'kimi', 'sessions')
  const statePath = join(root, 'state', 'sweep.json')
  await mkdir(projects, { recursive: true })
  await mkdir(kimiSessions, { recursive: true })
  await writeFile(join(projects, 'session.jsonl'), JSON.stringify({
    type: 'assistant',
    timestamp: '2026-08-24T12:00:00Z',
    message: {
      id: 'msg-replacement',
      model: 'claude-opus-5',
      usage: { input_tokens: 2, output_tokens: 10, service_tier: 'standard', speed: 'zzz', inference_geo: 'us' },
    },
  }))
  await writeFile(join(kimiSessions, 'wire.jsonl'), JSON.stringify({
    type: 'usage.record', time: 1787572800000, model: 'kimi-code/kimi-for-coding',
    usage: { inputOther: 10, output: 2, inputCacheRead: 20, inputCacheCreation: 3 },
  }))
  const oldClaude = {
    provider: 'anthropic', occurredAtUtc: '2026-08-24T12:00:00Z', model: 'claude-opus-5',
    costUsd: 0.01, runtime: 'claude', sourceId: 'claude-local@test-machine', sourceKind: 'localTelemetry',
    usageScope: 'subscription', costBasis: 'notional',
    eventKey: 'claude:2026-08-24:claude-opus-5:standard:aaa:us',
  }
  const oldKimi = {
    provider: 'moonshot', occurredAtUtc: '2026-08-23T12:00:00Z', model: 'kimi-code/kimi-for-coding',
    costUsd: null, runtime: 'kimi', sourceId: 'kimi-local@test-machine', sourceKind: 'localTelemetry',
    usageScope: 'subscription', costBasis: 'notional', eventKey: 'kimi:2026-08-23:kimi-code/kimi-for-coding',
  }
  const currentKey = 'claude:2026-08-24:claude-opus-5:standard:zzz:us'
  const currentKimiKey = 'kimi:2026-08-24:kimi-code/kimi-for-coding'
  const posts = []
  const server = createServer(async (request, response) => {
    const url = new URL(request.url, 'http://127.0.0.1')
    if (request.method === 'GET' && url.pathname === '/api/events/local-snapshots') {
      const sourceId = url.searchParams.get('sourceId')
      const body = sourceId === 'claude-local@test-machine' ? [oldClaude]
        : sourceId === 'kimi-local@test-machine' ? [oldKimi]
          : []
      response.writeHead(200, { 'Content-Type': 'application/json' }).end(JSON.stringify(body))
      return
    }
    if (request.method === 'POST' && url.pathname === '/api/events') {
      let body = ''
      for await (const chunk of request) { body += chunk }
      const parsed = JSON.parse(body)
      posts.push(parsed)
      response.writeHead(parsed.eventKey === currentKey ? 500 : 200, { 'Content-Type': 'application/json', 'Retry-After': '0' }).end('{}')
      return
    }
    response.writeHead(404).end()
  })
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve))
  const address = server.address()
  const run = promisify(execFile)
  const env = {
    ...process.env,
    OBSERVATORY_URL: `http://127.0.0.1:${address.port}`,
    OBSERVATORY_API_KEY: 'test-key',
    OBSERVATORY_STATE: statePath,
    OBSERVATORY_LOCAL_SOURCES: 'claude,kimi',
    OBSERVATORY_MACHINE: 'test-machine',
    CODEX_HOME: join(root, 'codex'),
    COPILOT_HOME: join(root, 'copilot'),
    CLAUDE_HOME: join(root, 'claude'),
    KIMI_HOME: join(root, 'kimi'),
  }

  try {
    await run(process.execPath, [fileURLToPath(new URL('./observatory-sweep.mjs', import.meta.url))], { env })

    // Claude's replacement exhausts retries, so its old key is NOT tombstoned;
    // Kimi's active snapshot succeeded, so its stale key is corrected.
    assert.deepEqual(posts.map(body => body.eventKey), [
      currentKey, currentKey, currentKey, currentKimiKey, oldKimi.eventKey,
    ])
    assert.equal(posts.some(body => body.eventKey === oldClaude.eventKey), false)
  } finally {
    await new Promise(resolve => server.close(resolve))
    await rm(root, { recursive: true, force: true })
  }
})

test('main aborts nested discovery failures without replacing cache or posting partial truth', async () => {
  const root = await mkdtemp(join(tmpdir(), 'observatory-sweep-read-error-'))
  const statePath = join(root, 'state', 'sweep.json')
  const originalState = JSON.stringify({
    parseCacheVersion: 1,
    files: { codex: { sentinel: { mtimeMs: 1, records: [{ id: 'keep' }] } } },
  })
  await mkdir(join(root, 'state'), { recursive: true })
  const posts = []
  const server = createServer(async (request, response) => {
    const url = new URL(request.url, 'http://127.0.0.1')
    if (request.method === 'GET' && url.pathname === '/api/events/local-snapshots') {
      response.writeHead(200, { 'Content-Type': 'application/json' }).end('[]')
      return
    }
    if (request.method === 'POST' && url.pathname === '/api/events') {
      posts.push(url.pathname)
      response.writeHead(200, { 'Content-Type': 'application/json' }).end('{}')
      return
    }
    response.writeHead(404).end()
  })
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve))
  const address = server.address()
  const envKeys = [
    'OBSERVATORY_URL', 'OBSERVATORY_API_KEY', 'OBSERVATORY_STATE', 'OBSERVATORY_LOCAL_SOURCES',
    'CODEX_HOME', 'COPILOT_HOME', 'CLAUDE_HOME', 'KIMI_HOME',
  ]
  const priorEnv = Object.fromEntries(envKeys.map(key => [key, process.env[key]]))
  Object.assign(process.env, {
    OBSERVATORY_URL: `http://127.0.0.1:${address.port}`,
    OBSERVATORY_API_KEY: 'test-key',
    OBSERVATORY_STATE: statePath,
    OBSERVATORY_LOCAL_SOURCES: 'codex',
    CODEX_HOME: join(root, 'codex'),
    COPILOT_HOME: join(root, 'copilot'),
    CLAUDE_HOME: join(root, 'claude'),
    KIMI_HOME: join(root, 'kimi'),
  })

  try {
    const { main } = await import('./observatory-sweep.mjs')
    for (const code of ['EACCES', 'EIO', 'ENOENT']) {
      await writeFile(statePath, originalState)
      const discover = dir => listJsonl(dir, [], {
        readdir: async path => {
          if (path === dir) { return [{ name: 'nested', isDirectory: () => true }] }
          const error = new Error(`synthetic nested ${code}`)
          error.code = code
          throw error
        },
        stat: async () => ({ mtimeMs: 1 }),
      })

      await assert.rejects(main({ discover }), error => error.code === code)
      assert.equal(await readFile(statePath, 'utf8'), originalState)
    }

    assert.deepEqual(posts, [])
  } finally {
    for (const key of envKeys) {
      if (priorEnv[key] === undefined) { delete process.env[key] } else { process.env[key] = priorEnv[key] }
    }
    await new Promise(resolve => server.close(resolve))
    await rm(root, { recursive: true, force: true })
  }
})

test('main withholds corrections and tombstones for an incompletely scanned source after a cache wipe', async () => {
  const root = await mkdtemp(join(tmpdir(), 'observatory-sweep-incomplete-'))
  const codexSessions = join(root, 'codex', 'sessions')
  const kimiSessions = join(root, 'kimi', 'sessions')
  const statePath = join(root, 'state', 'sweep.json')
  await mkdir(codexSessions, { recursive: true })
  await mkdir(kimiSessions, { recursive: true })
  await writeFile(join(codexSessions, 'rollout.jsonl'), [
    JSON.stringify({ type: 'turn_context', payload: { model: 'gpt-5.5' } }),
    JSON.stringify({ timestamp: '2026-08-24T12:00:00Z', type: 'event_msg', payload: { type: 'token_count', info: { total_token_usage: { input_tokens: 10, cached_input_tokens: 0, output_tokens: 5 } } } }),
  ].join('\n'))
  await writeFile(join(kimiSessions, 'wire.jsonl'), JSON.stringify({
    type: 'usage.record', time: 1787572800000, model: 'kimi-code/kimi-for-coding',
    usage: { inputOther: 10, output: 2, inputCacheRead: 20, inputCacheCreation: 3 },
  }))
  // Inventory left by the previous cache generation: the wipe drops all cached
  // records, so an unreadable file's keys are still live on the server.
  const oldCodex = {
    provider: 'openai', occurredAtUtc: '2026-08-20T12:00:00Z', model: 'gpt-5.5',
    costUsd: null, runtime: 'codex', sourceId: 'codex-local@test-machine', sourceKind: 'localTelemetry',
    usageScope: 'subscription', costBasis: 'notional', eventKey: 'codex:2026-08-20:gpt-5.5',
  }
  const oldKimi = {
    provider: 'moonshot', occurredAtUtc: '2026-08-23T12:00:00Z', model: 'kimi-code/kimi-for-coding',
    costUsd: null, runtime: 'kimi', sourceId: 'kimi-local@test-machine', sourceKind: 'localTelemetry',
    usageScope: 'subscription', costBasis: 'notional', eventKey: 'kimi:2026-08-23:kimi-code/kimi-for-coding',
  }
  const currentKimiKey = 'kimi:2026-08-24:kimi-code/kimi-for-coding'
  const posts = []
  const server = createServer(async (request, response) => {
    const url = new URL(request.url, 'http://127.0.0.1')
    if (request.method === 'GET' && url.pathname === '/api/events/local-snapshots') {
      const sourceId = url.searchParams.get('sourceId')
      const body = sourceId === 'codex-local@test-machine' ? [oldCodex]
        : sourceId === 'kimi-local@test-machine' ? [oldKimi]
          : []
      response.writeHead(200, { 'Content-Type': 'application/json' }).end(JSON.stringify(body))
      return
    }
    if (request.method === 'POST' && url.pathname === '/api/events') {
      let body = ''
      for await (const chunk of request) { body += chunk }
      posts.push(JSON.parse(body))
      response.writeHead(200, { 'Content-Type': 'application/json' }).end('{}')
      return
    }
    response.writeHead(404).end()
  })
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve))
  const address = server.address()
  const codexHome = join(root, 'codex')
  const envKeys = [
    'OBSERVATORY_URL', 'OBSERVATORY_API_KEY', 'OBSERVATORY_STATE', 'OBSERVATORY_LOCAL_SOURCES',
    'OBSERVATORY_MACHINE', 'CODEX_HOME', 'COPILOT_HOME', 'CLAUDE_HOME', 'KIMI_HOME',
  ]
  const priorEnv = Object.fromEntries(envKeys.map(key => [key, process.env[key]]))
  Object.assign(process.env, {
    OBSERVATORY_URL: `http://127.0.0.1:${address.port}`,
    OBSERVATORY_API_KEY: 'test-key',
    OBSERVATORY_STATE: statePath,
    OBSERVATORY_LOCAL_SOURCES: 'codex,kimi',
    OBSERVATORY_MACHINE: 'test-machine',
    CODEX_HOME: codexHome,
    COPILOT_HOME: join(root, 'copilot'),
    CLAUDE_HOME: join(root, 'claude'),
    KIMI_HOME: join(root, 'kimi'),
  })

  try {
    const { main } = await import('./observatory-sweep.mjs')
    // A file that vanished between discovery and read (TOCTOU ENOENT) hits the
    // no-cache branch once the version bump has wiped every cached record.
    const discover = async dir => {
      const files = await listJsonl(dir)
      if (dir === join(codexHome, 'sessions')) {
        files.push({ path: join(dir, 'vanished.jsonl'), mtimeMs: 1 })
      }
      return files
    }

    await main({ discover })

    // Codex scanned incompletely: neither the readable sibling's reduced
    // correction nor the previous generation's tombstone may post.
    assert.equal(posts.some(body => body.sourceId === 'codex-local@test-machine'), false)
    // Kimi scanned completely and is unaffected.
    assert.deepEqual(posts.map(body => body.eventKey), [currentKimiKey, oldKimi.eventKey])
  } finally {
    for (const key of envKeys) {
      if (priorEnv[key] === undefined) { delete process.env[key] } else { process.env[key] = priorEnv[key] }
    }
    await new Promise(resolve => server.close(resolve))
    await rm(root, { recursive: true, force: true })
  }
})

test('main tombstones the legacy un-suffixed namespace once and records the migration in state', async () => {
  const root = await mkdtemp(join(tmpdir(), 'observatory-sweep-legacy-'))
  const sessions = join(root, 'codex', 'sessions')
  const statePath = join(root, 'state', 'sweep.json')
  await mkdir(sessions, { recursive: true })
  await writeFile(join(sessions, 'rollout.jsonl'), [
    JSON.stringify({ type: 'turn_context', payload: { model: 'gpt-5.5' } }),
    JSON.stringify({ timestamp: '2026-08-24T12:00:00Z', type: 'event_msg', payload: { type: 'token_count', info: { total_token_usage: { input_tokens: 10, cached_input_tokens: 0, output_tokens: 5 } } } }),
  ].join('\n'))
  const legacyRecord = eventKey => ({
    provider: 'openai', occurredAtUtc: '2026-08-10T12:00:00Z', model: 'gpt-5.5',
    inputTokens: 40, outputTokens: 9, costUsd: null, runtime: 'codex',
    sourceId: 'codex-local', sourceKind: 'localTelemetry',
    usageScope: 'subscription', costBasis: 'notional', eventKey,
  })
  const posts = []
  let legacyGets = 0
  const server = createServer(async (request, response) => {
    const url = new URL(request.url, 'http://127.0.0.1')
    if (request.method === 'GET' && url.pathname === '/api/events/local-snapshots') {
      const sourceId = url.searchParams.get('sourceId')
      if (sourceId === 'codex-local') { legacyGets++ }
      const body = sourceId === 'codex-local'
        ? [legacyRecord('codex:2026-08-10:gpt-5.5'), legacyRecord('codex:2026-08-11:gpt-5.5')]
        : []
      response.writeHead(200, { 'Content-Type': 'application/json' }).end(JSON.stringify(body))
      return
    }
    if (request.method === 'POST' && url.pathname === '/api/events') {
      let body = ''
      for await (const chunk of request) { body += chunk }
      posts.push(JSON.parse(body))
      response.writeHead(200, { 'Content-Type': 'application/json' }).end('{}')
      return
    }
    response.writeHead(404).end()
  })
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve))
  const address = server.address()
  const envKeys = [
    'OBSERVATORY_URL', 'OBSERVATORY_API_KEY', 'OBSERVATORY_STATE', 'OBSERVATORY_LOCAL_SOURCES',
    'OBSERVATORY_MACHINE', 'CODEX_HOME', 'COPILOT_HOME', 'CLAUDE_HOME', 'KIMI_HOME',
  ]
  const priorEnv = Object.fromEntries(envKeys.map(key => [key, process.env[key]]))
  Object.assign(process.env, {
    OBSERVATORY_URL: `http://127.0.0.1:${address.port}`,
    OBSERVATORY_API_KEY: 'test-key',
    OBSERVATORY_STATE: statePath,
    OBSERVATORY_LOCAL_SOURCES: 'codex',
    OBSERVATORY_MACHINE: 'test-machine',
    CODEX_HOME: join(root, 'codex'),
    COPILOT_HOME: join(root, 'copilot'),
    CLAUDE_HOME: join(root, 'claude'),
    KIMI_HOME: join(root, 'kimi'),
  })

  try {
    const { main } = await import('./observatory-sweep.mjs')
    await main()

    const tombstones = posts.filter(body => body.sourceId === 'codex-local')
    assert.deepEqual(tombstones.map(body => [body.eventKey, body.inputTokens, body.outputTokens]), [
      ['codex:2026-08-10:gpt-5.5', 0, 0],
      ['codex:2026-08-11:gpt-5.5', 0, 0],
    ])
    assert.equal(posts.some(body => body.sourceId === 'codex-local@test-machine'), true)
    const state = JSON.parse(await readFile(statePath, 'utf8'))
    assert.equal(typeof state.legacySourceMigration.codex, 'string')

    posts.length = 0
    await main()

    assert.equal(legacyGets, 1, 'a recorded migration never re-fetches the legacy namespace')
    assert.equal(posts.some(body => body.sourceId === 'codex-local'), false)
  } finally {
    for (const key of envKeys) {
      if (priorEnv[key] === undefined) { delete process.env[key] } else { process.env[key] = priorEnv[key] }
    }
    await new Promise(resolve => server.close(resolve))
    await rm(root, { recursive: true, force: true })
  }
})

test('main retries the legacy migration when the legacy inventory fetch fails', async () => {
  const root = await mkdtemp(join(tmpdir(), 'observatory-sweep-legacy-fail-'))
  const sessions = join(root, 'codex', 'sessions')
  const statePath = join(root, 'state', 'sweep.json')
  await mkdir(sessions, { recursive: true })
  await writeFile(join(sessions, 'rollout.jsonl'), [
    JSON.stringify({ type: 'turn_context', payload: { model: 'gpt-5.5' } }),
    JSON.stringify({ timestamp: '2026-08-24T12:00:00Z', type: 'event_msg', payload: { type: 'token_count', info: { total_token_usage: { input_tokens: 10, cached_input_tokens: 0, output_tokens: 5 } } } }),
  ].join('\n'))
  const posts = []
  const server = createServer(async (request, response) => {
    const url = new URL(request.url, 'http://127.0.0.1')
    if (request.method === 'GET' && url.pathname === '/api/events/local-snapshots') {
      if (url.searchParams.get('sourceId') === 'codex-local') {
        response.writeHead(500).end('{}')
        return
      }
      response.writeHead(200, { 'Content-Type': 'application/json' }).end('[]')
      return
    }
    if (request.method === 'POST' && url.pathname === '/api/events') {
      let body = ''
      for await (const chunk of request) { body += chunk }
      posts.push(JSON.parse(body))
      response.writeHead(200, { 'Content-Type': 'application/json' }).end('{}')
      return
    }
    response.writeHead(404).end()
  })
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve))
  const address = server.address()
  const envKeys = [
    'OBSERVATORY_URL', 'OBSERVATORY_API_KEY', 'OBSERVATORY_STATE', 'OBSERVATORY_LOCAL_SOURCES',
    'OBSERVATORY_MACHINE', 'CODEX_HOME', 'COPILOT_HOME', 'CLAUDE_HOME', 'KIMI_HOME',
  ]
  const priorEnv = Object.fromEntries(envKeys.map(key => [key, process.env[key]]))
  Object.assign(process.env, {
    OBSERVATORY_URL: `http://127.0.0.1:${address.port}`,
    OBSERVATORY_API_KEY: 'test-key',
    OBSERVATORY_STATE: statePath,
    OBSERVATORY_LOCAL_SOURCES: 'codex',
    OBSERVATORY_MACHINE: 'test-machine',
    CODEX_HOME: join(root, 'codex'),
    COPILOT_HOME: join(root, 'copilot'),
    CLAUDE_HOME: join(root, 'claude'),
    KIMI_HOME: join(root, 'kimi'),
  })

  try {
    const { main } = await import('./observatory-sweep.mjs')
    await main()

    assert.equal(posts.some(body => body.sourceId === 'codex-local'), false)
    assert.equal(posts.some(body => body.sourceId === 'codex-local@test-machine'), true)
    const state = JSON.parse(await readFile(statePath, 'utf8'))
    assert.equal(state.legacySourceMigration?.codex, undefined)
  } finally {
    for (const key of envKeys) {
      if (priorEnv[key] === undefined) { delete process.env[key] } else { process.env[key] = priorEnv[key] }
    }
    await new Promise(resolve => server.close(resolve))
    await rm(root, { recursive: true, force: true })
  }
})

test('main retries the legacy migration when a legacy tombstone post fails', async () => {
  const root = await mkdtemp(join(tmpdir(), 'observatory-sweep-legacy-retry-'))
  const sessions = join(root, 'codex', 'sessions')
  const statePath = join(root, 'state', 'sweep.json')
  await mkdir(sessions, { recursive: true })
  await writeFile(join(sessions, 'rollout.jsonl'), [
    JSON.stringify({ type: 'turn_context', payload: { model: 'gpt-5.5' } }),
    JSON.stringify({ timestamp: '2026-08-24T12:00:00Z', type: 'event_msg', payload: { type: 'token_count', info: { total_token_usage: { input_tokens: 10, cached_input_tokens: 0, output_tokens: 5 } } } }),
  ].join('\n'))
  const legacyRecord = eventKey => ({
    provider: 'openai', occurredAtUtc: '2026-08-10T12:00:00Z', model: 'gpt-5.5',
    inputTokens: 40, outputTokens: 9, costUsd: null, runtime: 'codex',
    sourceId: 'codex-local', sourceKind: 'localTelemetry',
    usageScope: 'subscription', costBasis: 'notional', eventKey,
  })
  const posts = []
  let legacyGets = 0
  let failLegacyPosts = true
  const server = createServer(async (request, response) => {
    const url = new URL(request.url, 'http://127.0.0.1')
    if (request.method === 'GET' && url.pathname === '/api/events/local-snapshots') {
      const sourceId = url.searchParams.get('sourceId')
      if (sourceId === 'codex-local') { legacyGets++ }
      const body = sourceId === 'codex-local' ? [legacyRecord('codex:2026-08-10:gpt-5.5')] : []
      response.writeHead(200, { 'Content-Type': 'application/json' }).end(JSON.stringify(body))
      return
    }
    if (request.method === 'POST' && url.pathname === '/api/events') {
      let body = ''
      for await (const chunk of request) { body += chunk }
      const parsed = JSON.parse(body)
      posts.push(parsed)
      if (parsed.sourceId === 'codex-local') {
        response.writeHead(failLegacyPosts ? 500 : 200, { 'Content-Type': 'application/json' }).end('{}')
        return
      }
      response.writeHead(200, { 'Content-Type': 'application/json' }).end('{}')
      return
    }
    response.writeHead(404).end()
  })
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve))
  const address = server.address()
  const envKeys = [
    'OBSERVATORY_URL', 'OBSERVATORY_API_KEY', 'OBSERVATORY_STATE', 'OBSERVATORY_LOCAL_SOURCES',
    'OBSERVATORY_MACHINE', 'CODEX_HOME', 'COPILOT_HOME', 'CLAUDE_HOME', 'KIMI_HOME',
  ]
  const priorEnv = Object.fromEntries(envKeys.map(key => [key, process.env[key]]))
  Object.assign(process.env, {
    OBSERVATORY_URL: `http://127.0.0.1:${address.port}`,
    OBSERVATORY_API_KEY: 'test-key',
    OBSERVATORY_STATE: statePath,
    OBSERVATORY_LOCAL_SOURCES: 'codex',
    OBSERVATORY_MACHINE: 'test-machine',
    CODEX_HOME: join(root, 'codex'),
    COPILOT_HOME: join(root, 'copilot'),
    CLAUDE_HOME: join(root, 'claude'),
    KIMI_HOME: join(root, 'kimi'),
  })

  try {
    const { main } = await import('./observatory-sweep.mjs')
    await main()

    let state = JSON.parse(await readFile(statePath, 'utf8'))
    assert.equal(state.legacySourceMigration?.codex, undefined, 'a failed tombstone leaves the migration pending')

    failLegacyPosts = false
    await main()

    assert.equal(legacyGets, 2, 'a pending migration re-fetches and retries next run')
    state = JSON.parse(await readFile(statePath, 'utf8'))
    assert.equal(typeof state.legacySourceMigration.codex, 'string')
    assert.equal(
      posts.filter(body => body.sourceId === 'codex-local' && body.eventKey === 'codex:2026-08-10:gpt-5.5').length >= 2,
      true,
      'the retried tombstone is idempotent',
    )
  } finally {
    for (const key of envKeys) {
      if (priorEnv[key] === undefined) { delete process.env[key] } else { process.env[key] = priorEnv[key] }
    }
    await new Promise(resolve => server.close(resolve))
    await rm(root, { recursive: true, force: true })
  }
})

test('main skips re-posting snapshots whose token counts already match server inventory', async () => {
  const root = await mkdtemp(join(tmpdir(), 'observatory-sweep-unchanged-'))
  const sessions = join(root, 'codex', 'sessions')
  const statePath = join(root, 'state', 'sweep.json')
  await mkdir(sessions, { recursive: true })
  await writeFile(join(sessions, 'rollout.jsonl'), [
    JSON.stringify({ type: 'turn_context', payload: { model: 'gpt-5.5' } }),
    JSON.stringify({ timestamp: '2026-08-24T12:00:00Z', type: 'event_msg', payload: { type: 'token_count', info: { total_token_usage: { input_tokens: 10, cached_input_tokens: 0, output_tokens: 5 } } } }),
  ].join('\n'))
  const current = {
    provider: 'openai', occurredAtUtc: '2026-08-24T12:00:00Z', model: 'gpt-5.5',
    inputTokens: 10, outputTokens: 5, cacheReadTokens: 0, cacheWriteTokens: 0,
    cacheWrite1hTokens: 0, thoughtTokens: 0, costUsd: null,
    runtime: 'codex', sourceId: 'codex-local@test-machine', sourceKind: 'localTelemetry',
    usageScope: 'subscription', costBasis: 'notional', eventKey: 'codex:2026-08-24:gpt-5.5',
  }
  const posts = []
  const server = createServer(async (request, response) => {
    const url = new URL(request.url, 'http://127.0.0.1')
    if (request.method === 'GET' && url.pathname === '/api/events/local-snapshots') {
      response.writeHead(200, { 'Content-Type': 'application/json' }).end(JSON.stringify([current]))
      return
    }
    if (request.method === 'POST' && url.pathname === '/api/events') {
      posts.push(url.pathname)
      response.writeHead(200, { 'Content-Type': 'application/json' }).end('{}')
      return
    }
    response.writeHead(404).end()
  })
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve))
  const address = server.address()
  const run = promisify(execFile)
  const env = {
    ...process.env,
    OBSERVATORY_URL: `http://127.0.0.1:${address.port}`,
    OBSERVATORY_API_KEY: 'test-key',
    OBSERVATORY_STATE: statePath,
    OBSERVATORY_LOCAL_SOURCES: 'codex',
    OBSERVATORY_MACHINE: 'test-machine',
    CODEX_HOME: join(root, 'codex'),
    COPILOT_HOME: join(root, 'copilot'),
    CLAUDE_HOME: join(root, 'claude'),
    KIMI_HOME: join(root, 'kimi'),
  }

  try {
    await run(process.execPath, [fileURLToPath(new URL('./observatory-sweep.mjs', import.meta.url))], { env })

    assert.deepEqual(posts, [])
  } finally {
    await new Promise(resolve => server.close(resolve))
    await rm(root, { recursive: true, force: true })
  }
})

test('main tolerates one source inventory failing and still sweeps the rest', async () => {
  const root = await mkdtemp(join(tmpdir(), 'observatory-sweep-inventory-failure-'))
  const kimiSessions = join(root, 'kimi', 'sessions')
  const statePath = join(root, 'state', 'sweep.json')
  await mkdir(kimiSessions, { recursive: true })
  await writeFile(join(kimiSessions, 'wire.jsonl'), JSON.stringify({
    type: 'usage.record', time: 1787572800000, model: 'kimi-code/kimi-for-coding',
    usage: { inputOther: 10, output: 2, inputCacheRead: 20, inputCacheCreation: 3 },
  }))
  const posts = []
  const server = createServer(async (request, response) => {
    const url = new URL(request.url, 'http://127.0.0.1')
    if (request.method === 'GET' && url.pathname === '/api/events/local-snapshots') {
      const sourceId = url.searchParams.get('sourceId')
      if (sourceId === 'codex-local@test-machine') {
        response.writeHead(500, { 'Retry-After': '0' }).end()
        return
      }
      response.writeHead(200, { 'Content-Type': 'application/json' }).end('[]')
      return
    }
    if (request.method === 'POST' && url.pathname === '/api/events') {
      let body = ''
      for await (const chunk of request) { body += chunk }
      posts.push(JSON.parse(body).eventKey)
      response.writeHead(200, { 'Content-Type': 'application/json' }).end('{}')
      return
    }
    response.writeHead(404).end()
  })
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve))
  const address = server.address()
  const run = promisify(execFile)
  const env = {
    ...process.env,
    OBSERVATORY_URL: `http://127.0.0.1:${address.port}`,
    OBSERVATORY_API_KEY: 'test-key',
    OBSERVATORY_STATE: statePath,
    OBSERVATORY_LOCAL_SOURCES: 'codex,kimi',
    OBSERVATORY_MACHINE: 'test-machine',
    CODEX_HOME: join(root, 'codex'),
    COPILOT_HOME: join(root, 'copilot'),
    CLAUDE_HOME: join(root, 'claude'),
    KIMI_HOME: join(root, 'kimi'),
  }

  try {
    // The codex inventory GET 500s through every retry; the run must still
    // complete and post the kimi snapshot rather than aborting the sweep.
    const { stderr } = await run(process.execPath, [fileURLToPath(new URL('./observatory-sweep.mjs', import.meta.url))], { env })

    assert.deepEqual(posts, ['kimi:2026-08-24:kimi-code/kimi-for-coding'])
    assert.match(stderr, /Inventory unavailable for codex-local@test-machine/)
  } finally {
    await new Promise(resolve => server.close(resolve))
    await rm(root, { recursive: true, force: true })
  }
})

test('dry run previews offline without touching the server', async () => {
  const root = await mkdtemp(join(tmpdir(), 'observatory-sweep-dry-run-'))
  const statePath = join(root, 'state', 'sweep.json')
  const run = promisify(execFile)
  const env = {
    ...process.env,
    // Nothing listens here: any network attempt fails the run.
    OBSERVATORY_URL: 'http://127.0.0.1:1',
    OBSERVATORY_API_KEY: 'test-key',
    OBSERVATORY_STATE: statePath,
    OBSERVATORY_LOCAL_SOURCES: 'codex',
    OBSERVATORY_MACHINE: 'test-machine',
    CODEX_HOME: join(root, 'codex'),
    COPILOT_HOME: join(root, 'copilot'),
    CLAUDE_HOME: join(root, 'claude'),
    KIMI_HOME: join(root, 'kimi'),
  }

  try {
    await run(process.execPath, [fileURLToPath(new URL('./observatory-sweep.mjs', import.meta.url)), '--dry-run'], { env })
  } finally {
    await rm(root, { recursive: true, force: true })
  }
})
