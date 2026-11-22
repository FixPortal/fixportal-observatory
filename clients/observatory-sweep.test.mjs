// Self-check for the local usage sweeper's pure logic. Zero deps:
//   node --test clients/observatory-sweep.test.mjs
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { execFile } from 'node:child_process'
import { mkdtemp, mkdir, readdir, readFile, rename, rm, utimes, writeFile } from 'node:fs/promises'
import { createServer } from 'node:http'
import { tmpdir } from 'node:os'
import { join, relative, isAbsolute, resolve } from 'node:path'
import { DatabaseSync } from 'node:sqlite'
import { fileURLToPath } from 'node:url'
import { promisify } from 'node:util'
import * as sweep from './observatory-sweep.mjs'
import {
  parseCodex, codexProvider, parseCopilot, parseClaude, parseKimi, parseGrok, parsePi,
  buildDailySnapshots, updateFileCache, parseLocalSources, listJsonl,
  planSnapshotSubmissions, scanRecords, observatoryUrl, observatoryFetch,
  machineLabel,
} from './observatory-sweep.mjs'

const TEST_MACHINE = 'test-machine'

// Recursive deletes only ever target a directory this file created: makeTemp records each
// mkdtemp result, and removeTemp refuses anything that is not one of them or beneath one.
const ownedTempRoots = new Set()

async function makeTemp(prefix) {
  const root = await mkdtemp(join(tmpdir(), prefix))
  ownedTempRoots.add(resolve(root))
  return root
}

async function removeTemp(target) {
  const resolved = resolve(target)
  const owner = [...ownedTempRoots].find(root => {
    const rel = relative(root, resolved)
    return rel === '' || (!rel.startsWith('..') && !isAbsolute(rel))
  })
  if (!owner || resolved === resolve(tmpdir())) {
    throw new Error(`refusing to delete ${resolved}: not a directory created by this test file`)
  }
  console.log(`cleanup: removing ${resolved}`)
  await rm(resolved, { recursive: true, force: true })
}

// Synthetic native rows: prompt expectations below are hand-calculated.
function claudePromptRow(usage = {}, message = {}, timestamp = '2026-10-22T12:00:00Z') {
  return JSON.stringify({ type: 'assistant', timestamp, message: {
    id: 'synthetic-message', model: 'claude-haiku-5-5',
    ...message, usage,
  } })
}

const COMPLETE_CLAUDE_PROMPT = { input_tokens: 100000, cache_read_input_tokens: 0, cache_creation_input_tokens: 0, output_tokens: 7 }

for (const [name, usage, context] of [
  ['exact boundary', COMPLETE_CLAUDE_PROMPT, 'prompt_le_100000'],
  ['above boundary', { ...COMPLETE_CLAUDE_PROMPT, input_tokens: 100001 }, 'prompt_gt_100000'],
  ['cached input crosses boundary', { ...COMPLETE_CLAUDE_PROMPT, input_tokens: 70000, cache_read_input_tokens: 30001 }, 'prompt_gt_100000'],
  ['creation and TTL counted once', { ...COMPLETE_CLAUDE_PROMPT, input_tokens: 70000, cache_creation_input_tokens: 30000, cache_creation: { ephemeral_5m_input_tokens: 10000, ephemeral_1h_input_tokens: 20000 } }, 'prompt_le_100000'],
  ['split-only creation', { input_tokens: 70000, cache_read_input_tokens: 0, output_tokens: 8, cache_creation: { ephemeral_5m_input_tokens: 10000, ephemeral_1h_input_tokens: 20001 } }, 'prompt_gt_100000'],
  ['total with one bounded subset', { ...COMPLETE_CLAUDE_PROMPT, cache_creation: { ephemeral_5m_input_tokens: 0 } }, 'prompt_le_100000'],
  ['output excluded from threshold', { ...COMPLETE_CLAUDE_PROMPT, output_tokens: 500000 }, 'prompt_le_100000'],
]) {
  test(`Claude prompt band: ${name}`, () => {
    const [snapshot] = buildDailySnapshots(parseClaude(claudePromptRow(usage)), TEST_MACHINE)
    assert.equal(snapshot.eventKey, `claude:2026-10-22:claude-haiku-5-5:unknown:unknown:unknown:${context}`)
    const payload = JSON.parse(snapshot.rawPayload)
    assert.equal(payload.context, context)
    assert.equal(payload.claude_prompt_context_version, '1')
    assert.equal(payload.claude_prompt_context_basis, 'deduplicated_message_group')
    assert.equal(snapshot.outputTokens, usage.output_tokens)
    assert.equal(Object.hasOwn(payload, 'promptTokens'), false)
    assert.doesNotMatch(JSON.stringify(snapshot), /claudePrompt|promptTokens|incompleteSourceIds/)
  })
}

for (const [name, usage] of [
  ['missing input', { cache_read_input_tokens: 0, cache_creation_input_tokens: 0, output_tokens: 1 }],
  ['missing read', { input_tokens: 100001, cache_creation_input_tokens: 0 }],
  ['missing creation', { input_tokens: 100001, cache_read_input_tokens: 0 }],
  ['only one TTL subset', { input_tokens: 100001, cache_read_input_tokens: 0, cache_creation: { ephemeral_5m_input_tokens: 0 } }],
]) {
  test(`Claude prompt unknown: ${name} preserves safe usage`, () => {
    const [snapshot] = buildDailySnapshots(parseClaude(claudePromptRow(usage)), TEST_MACHINE)
    assert.equal(snapshot.eventKey.endsWith(':prompt_unknown'), true)
    const payload = JSON.parse(snapshot.rawPayload)
    assert.equal(Object.hasOwn(payload, 'context'), false)
    assert.equal(payload.claude_prompt_context_version, '1')
    assert.equal(payload.claude_prompt_context_basis, 'deduplicated_message_group')
    assert.equal(snapshot.inputTokens, usage.input_tokens ?? 0)
    assert.equal(snapshot.outputTokens, usage.output_tokens ?? 0)
  })
}

test('Claude prompt bands follow each message rather than equal daily totals', () => {
  const snapshots = buildDailySnapshots(parseClaude([
    claudePromptRow({ ...COMPLETE_CLAUDE_PROMPT, input_tokens: 60000, output_tokens: 3 }, { id: 'small-a' }),
    claudePromptRow({ ...COMPLETE_CLAUDE_PROMPT, input_tokens: 60000, output_tokens: 5 }, { id: 'small-b' }),
    claudePromptRow({ ...COMPLETE_CLAUDE_PROMPT, input_tokens: 120000, output_tokens: 11 }, { id: 'large' }),
  ].join('\n')), TEST_MACHINE)
  assert.equal(snapshots.length, 2)
  assert.deepEqual(snapshots.map(x => [JSON.parse(x.rawPayload).context, x.inputTokens, x.outputTokens]).sort(), [
    ['prompt_gt_100000', 120000, 11], ['prompt_le_100000', 120000, 8],
  ])
})

test('Claude prompt dedup counts identical copies once and accepts agreeing partial copies', () => {
  const complete = claudePromptRow(COMPLETE_CLAUDE_PROMPT)
  const partial = claudePromptRow({ input_tokens: 100000, output_tokens: 7 })
  for (const lines of [[complete, complete, partial], [partial, complete, complete]]) {
    const [snapshot] = buildDailySnapshots(parseClaude(lines.join('\n')), TEST_MACHINE)
    assert.equal(snapshot.inputTokens, 100000)
    assert.equal(snapshot.outputTokens, 7)
    assert.equal(JSON.parse(snapshot.rawPayload).context, 'prompt_le_100000')
  }
})

for (const [name, usage, message] of [
  ['input', { input_tokens: 100001 }, {}],
  ['read', { cache_read_input_tokens: 1 }, {}],
  ['creation', { cache_creation_input_tokens: 1 }, {}],
  ['model', {}, { model: 'claude-opus-5' }],
  ['tier', { service_tier: 'batch' }, {}],
  ['speed', { speed: 'fast' }, {}],
  ['geography', { inference_geo: 'us' }, {}],
]) {
  test(`Claude prompt dedup refuses conflicting ${name} despite richer and earlier selection`, () => {
    const fullUsage = { ...COMPLETE_CLAUDE_PROMPT, service_tier: 'standard', speed: 'standard', inference_geo: 'global' }
    const full = claudePromptRow(fullUsage)
    const conflicting = claudePromptRow(usage, message, '2026-10-22T12:00:01Z')
    for (const lines of [[full, conflicting], [conflicting, full]]) {
      const [snapshot] = buildDailySnapshots(parseClaude(lines.join('\n')), TEST_MACHINE)
      assert.equal(snapshot.eventKey.endsWith(':prompt_unknown'), true)
      assert.equal(Object.hasOwn(JSON.parse(snapshot.rawPayload), 'context'), false)
      assert.equal(snapshot.inputTokens, 100000)
    }
  })
}

test('Claude prompt dedup never unions disjoint incomplete observations', () => {
  const records = parseClaude([
    claudePromptRow({ input_tokens: 100001, output_tokens: 7 }),
    claudePromptRow({ cache_read_input_tokens: 0, cache_creation_input_tokens: 0, output_tokens: 7 }),
  ].join('\n'))
  const [snapshot] = buildDailySnapshots(records, TEST_MACHINE)
  assert.equal(snapshot.eventKey.endsWith(':prompt_unknown'), true)
  assert.equal(Object.hasOwn(JSON.parse(snapshot.rawPayload), 'context'), false)
})

test('Claude prompt dedup checks creation totals across complete and partial split-only copies', () => {
  const full = claudePromptRow({ ...COMPLETE_CLAUDE_PROMPT, input_tokens: 70000, cache_creation_input_tokens: 30000 })
  for (const [subsets, context] of [
    [{ ephemeral_5m_input_tokens: 10000, ephemeral_1h_input_tokens: 20000 }, 'prompt_le_100000'],
    [{ ephemeral_5m_input_tokens: 10000, ephemeral_1h_input_tokens: 20001 }, undefined],
    [{ ephemeral_5m_input_tokens: 30001 }, undefined],
  ]) {
    const partial = claudePromptRow({ output_tokens: 7, cache_creation: subsets })
    const [snapshot] = buildDailySnapshots(parseClaude([full, partial].join('\n')), TEST_MACHINE)
    assert.equal(JSON.parse(snapshot.rawPayload).context, context)
  }
  const contradictoryPartials = [
    full,
    claudePromptRow({ cache_creation: { ephemeral_5m_input_tokens: 10000 } }),
    claudePromptRow({ cache_creation: { ephemeral_1h_input_tokens: 20001 } }),
  ]
  assert.equal(JSON.parse(buildDailySnapshots(parseClaude(contradictoryPartials.join('\n')), TEST_MACHINE)[0].rawPayload).context, undefined)
})

test('Claude prompt dedup refuses conflicting complete copies with equal prompt totals', () => {
  const records = parseClaude([
    claudePromptRow(COMPLETE_CLAUDE_PROMPT),
    claudePromptRow({ ...COMPLETE_CLAUDE_PROMPT, input_tokens: 70000, cache_read_input_tokens: 30000 }),
  ].join('\n'))
  const [snapshot] = buildDailySnapshots(records, TEST_MACHINE)
  assert.equal(snapshot.eventKey.endsWith(':prompt_unknown'), true)
  assert.equal(snapshot.inputTokens, 100000)
})

test('Claude prompt output selection remains separate from proof and unselected malformed counters suppress the source', () => {
  const full = claudePromptRow({ ...COMPLETE_CLAUDE_PROMPT, service_tier: 'standard' })
  const laterOutput = claudePromptRow({ ...COMPLETE_CLAUDE_PROMPT, output_tokens: 99 }, {}, '2026-10-22T12:00:01Z')
  const [snapshot] = buildDailySnapshots(parseClaude([full, laterOutput].join('\n')), TEST_MACHINE)
  assert.equal(JSON.parse(snapshot.rawPayload).context, 'prompt_le_100000')
  assert.equal(snapshot.outputTokens, 7)
  const invalid = claudePromptRow({ input_tokens: 0.5 })
  const snapshots = buildDailySnapshots(parseClaude([full, invalid].join('\n')), TEST_MACHINE)
  assert.deepEqual(planSnapshotSubmissions(snapshots, [{ sourceId: 'claude-local@test-machine', eventKey: 'prior', runtime: 'claude' }]), [])
})

for (const id of [undefined, '', 42]) {
  test(`Claude prompt missing string identity stays unknown (${String(id)})`, () => {
    const [snapshot] = buildDailySnapshots(parseClaude(claudePromptRow(COMPLETE_CLAUDE_PROMPT, { id })), TEST_MACHINE)
    assert.equal(snapshot.eventKey.endsWith(':prompt_unknown'), true)
    assert.equal(Object.hasOwn(JSON.parse(snapshot.rawPayload), 'context'), false)
  })
}

test('Claude prompt legacy zero-defaulted cache records are never evidence', () => {
  const [snapshot] = buildDailySnapshots([{
    tool: 'claude', messageId: 'old-cache', model: 'claude-haiku-5-5', date: '2026-10-22',
    inputTokens: 100001, cacheReadTokens: 0, cacheWriteTokens: 0, outputTokens: 7,
  }], TEST_MACHINE)
  assert.equal(snapshot.eventKey.endsWith(':prompt_unknown'), true)
  assert.equal(Object.hasOwn(JSON.parse(snapshot.rawPayload), 'context'), false)
})

for (const [model, tiered] of [
  ['claude-haiku-5-5', true], ['CLAUDE-HAIKU-5-5', true], ['claude-haiku-5-5-20261022', true],
  ['claude-haiku-5-5-20240229', true], ['claude-haiku-5-50', false], ['claude-haiku-5-5-extra', false],
  ['claude-haiku-5-5-20260230', false], ['claude-haiku-5-5-2026-10-22', false],
  ['claude-haiku-5-5-00001022', false], ['claude-opus-5', false], ['haiku-qualified', false],
]) {
  test(`Claude prompt model identity is bounded: ${model}`, () => {
    const [snapshot] = buildDailySnapshots(parseClaude(claudePromptRow(COMPLETE_CLAUDE_PROMPT, { model })), TEST_MACHINE)
    const oldKey = `claude:2026-10-22:${model}:unknown:unknown:unknown`
    assert.equal(snapshot.eventKey, tiered ? `${oldKey}:prompt_le_100000` : oldKey)
    assert.equal(Object.hasOwn(JSON.parse(snapshot.rawPayload), 'claude_prompt_context_version'), tiered)
    if (!tiered) {
      assert.equal(snapshot.rawPayload, '{"source":"observatory-sweep","tool":"claude","machine":"test-machine","thinking_tokens":0}')
    }
  })
}

for (const [name, patch] of [
  ['negative input', { input_tokens: -1 }], ['fractional read', { cache_read_input_tokens: 0.5 }],
  ['unsafe creation', { cache_creation_input_tokens: Number.MAX_SAFE_INTEGER + 1 }],
  ['string counter', { input_tokens: '100000' }], ['null counter', { cache_read_input_tokens: null }],
  ['negative TTL subset', { cache_creation: { ephemeral_5m_input_tokens: -1 } }],
  ['fractional output', { output_tokens: 0.5 }],
  ['prompt sum overflow', { input_tokens: Number.MAX_SAFE_INTEGER, cache_read_input_tokens: 1 }],
  ['split sum overflow', { cache_creation_input_tokens: undefined, cache_creation: { ephemeral_5m_input_tokens: Number.MAX_SAFE_INTEGER, ephemeral_1h_input_tokens: 1 } }],
]) {
  test(`Claude prompt invalid discovery suppresses corrections and tombstones: ${name}`, () => {
    const parsed = parseClaude(claudePromptRow({ ...COMPLETE_CLAUDE_PROMPT, ...patch }))
    assert.deepEqual(parsed[0].claudePromptObservation, name === 'fractional output'
      ? { promptTokens: 100000, valid: true } : { promptTokens: null, valid: false })
    const healthy = { tool: 'codex', date: '2026-10-22', model: 'gpt-5.4', cum: { input: 3, output: 1 } }
    const snapshots = buildDailySnapshots([...parsed, healthy], TEST_MACHINE)
    const old = { sourceId: 'claude-local@test-machine', eventKey: 'old-valid-key', runtime: 'claude' }
    const submissions = planSnapshotSubmissions(snapshots, [old])
    assert.equal(submissions.some(x => x.snapshot.sourceId === old.sourceId), false)
    assert.equal(submissions.length, 1)
    assert.equal(submissions[0].snapshot.inputTokens, 3)
    assert.equal(Object.keys(submissions[0].snapshot).some(key => key.includes('incomplete')), false)
  })
}

test('Claude prompt aggregate overflow with individually complete messages suppresses the whole source', () => {
  const records = parseClaude([
    claudePromptRow({ ...COMPLETE_CLAUDE_PROMPT, input_tokens: Number.MAX_SAFE_INTEGER }, { id: 'max' }),
    claudePromptRow({ ...COMPLETE_CLAUDE_PROMPT, input_tokens: 100001 }, { id: 'one-more' }),
    claudePromptRow(COMPLETE_CLAUDE_PROMPT, { id: 'lower' }),
  ].join('\n'))
  const snapshots = buildDailySnapshots(records, TEST_MACHINE)
  assert.deepEqual(planSnapshotSubmissions(snapshots, [{ sourceId: 'claude-local@test-machine', eventKey: 'old', runtime: 'claude' }]), [])
})

for (const counter of ['output_tokens', 'cache_read_input_tokens', 'cache_creation_input_tokens', 'thinking_tokens']) {
  test(`Claude prompt aggregate ${counter} overflow never produces rounded submissions`, () => {
    const records = parseClaude([
      claudePromptRow({ ...COMPLETE_CLAUDE_PROMPT, input_tokens: 0, [counter]: Number.MAX_SAFE_INTEGER }, { id: 'max' }),
      claudePromptRow({ ...COMPLETE_CLAUDE_PROMPT, input_tokens: 0, [counter]: 100001 }, { id: 'extra' }),
    ].join('\n'))
    const snapshots = buildDailySnapshots(records, TEST_MACHINE)
    assert.deepEqual(planSnapshotSubmissions(snapshots, [{ sourceId: 'claude-local@test-machine', eventKey: 'prior', runtime: 'claude' }]), [])
  })
}

test('Claude prompt version-6 cache is reparsed even at the same mtime and lost files stay incomplete', async () => {
  const root = await makeTemp('observatory-claude-prompt-cache-')
  const path = join(root, 'invented.jsonl')
  const files = [{ path, mtimeMs: 123 }]
  const oldState = () => ({ parseCacheVersion: 6, files: { claude: { [path]: {
    mtimeMs: 123, records: [{ tool: 'claude', date: '2026-10-22', model: 'claude-haiku-5-5', inputTokens: 3 }],
  } } } })
  try {
    await writeFile(path, claudePromptRow(COMPLETE_CLAUDE_PROMPT))
    const state = oldState()
    const scan = await scanRecords({ claudeHome: root }, state, new Set(['claude']), async () => files)
    assert.equal(state.parseCacheVersion, 11)
    assert.equal(scan.records[0].inputTokens, 100000)
    assert.equal(JSON.parse(buildDailySnapshots(scan.records, TEST_MACHINE)[0].rawPayload).context, 'prompt_le_100000')
    const missing = await scanRecords({ claudeHome: root }, oldState(), new Set(['claude']), async () => [{ path: join(root, 'unavailable.jsonl'), mtimeMs: 123 }])
    assert.equal(missing.incompleteSources.has('claude'), true)
    assert.equal(missing.emptySources.has('claude'), false)
  } finally {
    await removeTemp(root)
  }
})

test('Claude prompt invalid scan cannot post active corrections, old-key tombstones or legacy migration', async () => {
  const root = await makeTemp('observatory-claude-prompt-invalid-')
  const path = join(root, 'invented.jsonl')
  const keys = ['OBSERVATORY_URL', 'OBSERVATORY_API_KEY', 'OBSERVATORY_STATE', 'OBSERVATORY_LOCAL_SOURCES', 'OBSERVATORY_MACHINE', 'CLAUDE_HOME']
  const before = Object.fromEntries(keys.map(key => [key, process.env[key]]))
  const originalFetch = globalThis.fetch
  const posts = []
  const gets = []
  Object.assign(process.env, {
    OBSERVATORY_URL: 'http://127.0.0.1:1', OBSERVATORY_API_KEY: 'synthetic-key',
    OBSERVATORY_STATE: join(root, 'state.json'), OBSERVATORY_LOCAL_SOURCES: 'claude',
    OBSERVATORY_MACHINE: TEST_MACHINE, CLAUDE_HOME: root,
  })
  globalThis.fetch = async (url, options = {}) => {
    if (options.method === 'POST') { posts.push({ url, body: JSON.parse(options.body) }); return new Response('{}') }
    gets.push(url)
    return new Response(JSON.stringify([{ sourceId: 'claude-local@test-machine', runtime: 'claude', eventKey: 'old-flat-haiku-key' }]))
  }
  try {
    await writeFile(path, claudePromptRow({ ...COMPLETE_CLAUDE_PROMPT, input_tokens: Number.MAX_SAFE_INTEGER + 1 }))
    await sweep.main({ discover: async () => [{ path, mtimeMs: 1 }] })
    assert.deepEqual(posts.filter(x => new URL(x.url).pathname === '/api/events'), [])
    assert.equal(gets.length, 1, 'no legacy migration inventory for an invalid source')
    assert.equal(JSON.parse(await readFile(join(root, 'state.json'), 'utf8')).legacySourceMigration?.claude, undefined)
  } finally {
    globalThis.fetch = originalFetch
    for (const key of keys) {
      if (before[key] === undefined) { delete process.env[key] } else { process.env[key] = before[key] }
    }
    await removeTemp(root)
  }
})

for (const failure of ['malformed JSONL', 'invalid counters', 'overflowed counters', 'unreadable file']) {
  test(`main warns once before posting for incomplete Claude ${failure}, including cache hits`, async () => {
    const root = await makeTemp('observatory-claude-warning-')
    const path = join(root, 'claude.jsonl')
    const kimiPath = join(root, 'wire.jsonl')
    const statePath = join(root, 'state.json')
    const keys = ['OBSERVATORY_URL', 'OBSERVATORY_API_KEY', 'OBSERVATORY_STATE', 'OBSERVATORY_LOCAL_SOURCES', 'OBSERVATORY_MACHINE', 'CLAUDE_HOME', 'KIMI_HOME']
    const before = Object.fromEntries(keys.map(key => [key, process.env[key]]))
    const originalFetch = globalThis.fetch
    const originalError = console.error
    const activity = []
    Object.assign(process.env, {
      OBSERVATORY_URL: 'http://127.0.0.1:1', OBSERVATORY_API_KEY: 'synthetic-key',
      OBSERVATORY_STATE: statePath, OBSERVATORY_LOCAL_SOURCES: 'claude,kimi',
      OBSERVATORY_MACHINE: TEST_MACHINE, CLAUDE_HOME: join(root, 'claude'), KIMI_HOME: join(root, 'kimi'),
    })
    console.error = message => activity.push({ warning: String(message) })
    globalThis.fetch = async (url, options = {}) => {
      if (options.method === 'POST') {
        activity.push({ url, body: JSON.parse(options.body) })
        return new Response('{}')
      }
      const sourceId = new URL(url).searchParams.get('sourceId')
      return new Response(JSON.stringify(sourceId === 'claude-local@test-machine'
        ? [{ sourceId, runtime: 'claude', eventKey: 'prior-flat' }] : []))
    }
    try {
      const valid = claudePromptRow(COMPLETE_CLAUDE_PROMPT)
      if (failure !== 'unreadable file') {
        const content = failure === 'malformed JSONL' ? `${valid}\n{"message":`
          : failure === 'invalid counters' ? claudePromptRow({ ...COMPLETE_CLAUDE_PROMPT, input_tokens: -1 })
            : [claudePromptRow({ ...COMPLETE_CLAUDE_PROMPT, input_tokens: Number.MAX_SAFE_INTEGER }, { id: 'max' }),
              claudePromptRow({ ...COMPLETE_CLAUDE_PROMPT, input_tokens: 100001 }, { id: 'extra' })].join('\n')
        await writeFile(path, content)
      }
      await writeFile(kimiPath, JSON.stringify({ type: 'usage.record', time: 1787572800000, model: 'kimi-code/kimi-for-coding', usage: { inputOther: 10, output: 2, inputCacheRead: 20, inputCacheCreation: 3 } }))
      const discover = async dir => [{ path: dir.startsWith(process.env.CLAUDE_HOME) ? path : kimiPath, mtimeMs: 1 }]
      for (let run = 0; run < 2; run++) {
        activity.length = 0
        await sweep.main({ discover })
        const warnings = activity.filter(item => item.warning?.startsWith('Scan incomplete'))
        assert.equal(warnings.length, 1, 'exactly one warning for the withheld source each run')
        assert.match(warnings[0].warning, /claude-local@test-machine/)
        assert.match(warnings[0].warning, /possible causes: unreadable file, malformed JSONL, malformed assistant usage or timestamp, or invalid or overflowed counters/)
        assert.match(warnings[0].warning, /keys for .+ are held until the affected file.s. parse cleanly/)
        assert.equal(warnings[0].warning.includes(root), false, 'native paths are absent')
        const events = activity.filter(item => item.url && new URL(item.url).pathname === '/api/events')
        assert.ok(events.some(item => item.body.sourceId === 'kimi-local@test-machine'), 'the other source proceeds')
        assert.equal(events.some(item => item.body.sourceId === 'claude-local@test-machine'), false, 'both active corrections and old-key tombstones stay withheld')
        assert.ok(activity.indexOf(warnings[0]) < activity.findIndex(item => item.url), 'warning precedes every POST')
      }
    } finally {
      globalThis.fetch = originalFetch
      console.error = originalError
      for (const key of keys) {
        if (before[key] === undefined) { delete process.env[key] } else { process.env[key] = before[key] }
      }
      await removeTemp(root)
    }
  })
}

const CLAUDE_SAFETY_CASES = [
  // Contradictory creation totals and TTL subsets withhold the entire source.
  ['inconsistent total and splits', claudePromptRow({ ...COMPLETE_CLAUDE_PROMPT, cache_creation_input_tokens: 9, cache_creation: { ephemeral_5m_input_tokens: 5, ephemeral_1h_input_tokens: 5 } }, { id: 'unsafe' }), true],
  ['subset exceeds total', claudePromptRow({ ...COMPLETE_CLAUDE_PROMPT, cache_creation_input_tokens: 9, cache_creation: { ephemeral_1h_input_tokens: 10 } }, { id: 'unsafe' }), true],
  ['creation total below subset', claudePromptRow({ ...COMPLETE_CLAUDE_PROMPT, cache_creation_input_tokens: 10, cache_creation: { ephemeral_5m_input_tokens: 20, ephemeral_1h_input_tokens: 0 } }, { id: 'unsafe' }), true],
  ['creation total differs from split sum', claudePromptRow({ ...COMPLETE_CLAUDE_PROMPT, cache_creation_input_tokens: 30, cache_creation: { ephemeral_5m_input_tokens: 10, ephemeral_1h_input_tokens: 10 } }, { id: 'unsafe' }), true],
  ['one creation subset above total', claudePromptRow({ ...COMPLETE_CLAUDE_PROMPT, cache_creation_input_tokens: 10, cache_creation: { ephemeral_1h_input_tokens: 20 } }, { id: 'unsafe' }), true],
  ...[[], null, 'invalid', 0, false].map(usage => [`assistant usage ${JSON.stringify(usage)}`, claudePromptRow(usage, { id: 'unsafe' }), false]),
  ...[
      false,
      true,
      [],
      [2026],
      {},
      0,
      1787572800000,
      "0",
      "2026-10-22",
      "2026-10-22T12:00:00",
      "2026-02-29T12:00:00Z",
      "2026-02-30T12:00:00Z",
      "2026-04-31T12:00:00Z",
      "2026-00-01T12:00:00Z",
      "2026-13-01T12:00:00Z",
      "2026-10-22T24:00:00Z",
      "2026-10-22T12:60:00Z",
      "2026-10-22T12:00:60Z",
      "2026-10-22T12:00:00+14:01",
      "2026-10-22T12:00:00+15:00",
      "2026-10-22T12:00:00+01:60",
      "0000-01-01T12:00:00Z",
      "0001-01-01T00:00:00+01:00",
      "9999-12-31T23:59:59-01:00",
      "2026-10-22T12:00:00.12345678Z",
  ].map((timestamp) => [
      `timestamp safety ${JSON.stringify(timestamp)}`,
      claudePromptRow(COMPLETE_CLAUDE_PROMPT, { id: "unsafe" }, timestamp),
      false,
  ]),
  ['assistant invalid timestamp', claudePromptRow(COMPLETE_CLAUDE_PROMPT, { id: 'unsafe' }, 'invalid'), false],
  ['assistant missing timestamp', JSON.stringify({ type: 'assistant', message: { id: 'unsafe', model: 'claude-haiku-5-5', usage: COMPLETE_CLAUDE_PROMPT } }), false],
]

for (const [name, unsafe, invalidCounters] of CLAUDE_SAFETY_CASES) {
  test(`Claude safety parse rejects ${name} before snapshot reconciliation`, () => {
    const records = parseClaude(`${claudePromptRow(COMPLETE_CLAUDE_PROMPT)}\n${unsafe}`)
    if (invalidCounters) { assert.equal(records.at(-1).claudeInvalidCounters, true) }
    else { assert.equal(records.incomplete, true); assert.equal(records.length, 1) }
    const snapshots = buildDailySnapshots(records, TEST_MACHINE)
    assert.equal(snapshots.length, 0)
    assert.deepEqual(planSnapshotSubmissions(snapshots, [{ sourceId: 'claude-local@test-machine', runtime: 'claude', eventKey: 'prior-flat' }]), [])
  })
  for (const cacheVersion of [null, 7, 8]) {
    test(`Claude safety main withholds ${name} with ${cacheVersion ? `version${cacheVersion} reparse` : 'cold cache'} and persisted hit`, async () => {
      const root = await makeTemp('observatory-claude-safety-')
      const path = join(root, 'claude.jsonl'), kimiPath = join(root, 'wire.jsonl'), statePath = join(root, 'state.json')
      const keys = ['OBSERVATORY_URL', 'OBSERVATORY_API_KEY', 'OBSERVATORY_STATE', 'OBSERVATORY_LOCAL_SOURCES', 'OBSERVATORY_MACHINE', 'CLAUDE_HOME', 'KIMI_HOME']
      const before = Object.fromEntries(keys.map(key => [key, process.env[key]]))
      const originalFetch = globalThis.fetch, originalError = console.error
      const activity = [], gets = []
      Object.assign(process.env, { OBSERVATORY_URL: 'http://127.0.0.1:1', OBSERVATORY_API_KEY: 'synthetic-key', OBSERVATORY_STATE: statePath, OBSERVATORY_LOCAL_SOURCES: 'claude,kimi', OBSERVATORY_MACHINE: TEST_MACHINE, CLAUDE_HOME: join(root, 'claude'), KIMI_HOME: join(root, 'kimi') })
      console.error = message => activity.push({ warning: String(message) })
      globalThis.fetch = async (url, options = {}) => {
        if (options.method === 'POST') { activity.push({ url, body: JSON.parse(options.body) }); return new Response('{}') }
        gets.push(new URL(url).searchParams.get('sourceId'))
        return new Response(JSON.stringify(new URL(url).searchParams.get('sourceId') === 'claude-local@test-machine'
          ? [{ sourceId: 'claude-local@test-machine', runtime: 'claude', eventKey: 'prior-flat' }] : []))
      }
      try {
        const valid = claudePromptRow(COMPLETE_CLAUDE_PROMPT)
        await writeFile(path, `${valid}\n${unsafe}`)
        await writeFile(kimiPath, JSON.stringify({ type: 'usage.record', time: 1787572800000, model: 'kimi-code/kimi-for-coding', usage: { inputOther: 10, output: 2, inputCacheRead: 20, inputCacheCreation: 3 } }))
        if (cacheVersion !== null) {
          await writeFile(statePath, JSON.stringify({ parseCacheVersion: cacheVersion, files: { claude: { [path]: { mtimeMs: 1, records: [{ ...parseClaude(valid)[0], inputTokens: 23, date: '1970-01-01', occurredAtUtc: '1970-01-01T00:00:00.000Z', claudeInvalidCounters: false }] } } } }))
        }
        const discover = async dir => [{ path: dir.startsWith(process.env.CLAUDE_HOME) ? path : kimiPath, mtimeMs: 1 }]
        for (let run = 0; run < 2; run++) {
          activity.length = 0; gets.length = 0
          await sweep.main({ discover })
          const events = activity.filter(item => item.url && new URL(item.url).pathname === '/api/events')
          assert.equal(events.some(item => item.body.sourceId === 'claude-local@test-machine'), false, 'affected active corrections and tombstones withheld')
          assert.ok(events.some(item => item.body.sourceId === 'kimi-local@test-machine'), 'healthy source proceeds')
          assert.equal(gets.includes('claude-local'), false, 'affected legacy migration is withheld')
          const warnings = activity.filter(item => item.warning?.startsWith('Scan incomplete'))
          assert.equal(warnings.length, 1)
          assert.match(warnings[0].warning, /claude-local@test-machine/)
          assert.match(warnings[0].warning, /keys for .+ are held until the affected file.s. parse cleanly/)
          assert.equal(warnings[0].warning.includes(root), false)
          assert.ok(activity.indexOf(warnings[0]) < activity.findIndex(item => item.url))
          const state = JSON.parse(await readFile(statePath, 'utf8'))
          assert.equal(state.parseCacheVersion, 11)
          assert.equal(state.files.claude[path].records[0].inputTokens, 100000, 'old version7/8 same-mtime false-1970 usage must be reparsed')
          assert.equal(state.files.claude[path].records[0].date, '2026-10-22')
          if (invalidCounters) { assert.ok(state.files.claude[path].records.some(row => row.claudeInvalidCounters)) }
          else { assert.equal(state.files.claude[path].incomplete, true) }
        }
      } finally {
        globalThis.fetch = originalFetch; console.error = originalError
        for (const key of keys) { if (before[key] === undefined) { delete process.env[key] } else { process.env[key] = before[key] } }
        await removeTemp(root)
      }
    })
  }
}

test("Claude native instants preserve explicit zones fractions and UTC rollover", () => {
    for (const [timestamp, expected] of [
        ["2026-10-22T12:00:00Z", "2026-10-22T12:00:00.000Z"],
        ["2026-10-22T12:00:00.1Z", "2026-10-22T12:00:00.100Z"],
        ["2026-10-22T12:00:00.1234567Z", "2026-10-22T12:00:00.123Z"],
        ["2026-10-22T00:30:00+01:00", "2026-10-21T23:30:00.000Z"],
        ["2026-10-21T23:45:00-05:30", "2026-10-22T05:15:00.000Z"],
        ["2026-10-22T00:00:00.9876543+14:00", "2026-10-21T10:00:00.987Z"],
        ["2024-02-29T23:30:00-01:00", "2024-03-01T00:30:00.000Z"],
        ["0001-01-01T00:00:00Z", "0001-01-01T00:00:00.000Z"],
    ]) {
        const records = parseClaude(
            claudePromptRow(COMPLETE_CLAUDE_PROMPT, {}, timestamp),
        )
        assert.equal(records.incomplete, false)
        assert.equal(records.length, 1)
        assert.equal(records[0].occurredAtUtc, expected)
        assert.equal(records[0].date, expected.slice(0, 10))
        const [snapshot] = buildDailySnapshots(records, TEST_MACHINE)
        assert.equal(snapshot.occurredAtUtc.slice(0, 10), expected.slice(0, 10))
        assert.equal(
            JSON.parse(snapshot.rawPayload).context,
            "prompt_le_100000",
        )
    }
})

test("numeric Kimi timestamp compatibility is unchanged", () => {
    const records = parseKimi(
        JSON.stringify({
            type: "usage.record",
            time: 1787572800000,
            model: "kimi-code/kimi-for-coding",
            usage: {
                inputOther: 10,
                output: 2,
                inputCacheRead: 20,
                inputCacheCreation: 3,
            },
        }),
    )
    assert.equal(records.length, 1)
    assert.equal(records[0].occurredAtUtc, "2026-08-24T12:00:00.000Z")
    assert.equal(records[0].date, "2026-08-24")
})

test('Claude safety ignores nonusage rows and preserves genuinely missing evidence as unknown', () => {
  const nonusage = [
    { type: 'assistant', message: { content: 'synthetic' } },
    { type: 'user', message: { usage: [] }, timestamp: 'invalid' },
    { type: 'system', message: { usage: null } },
  ].map(JSON.stringify)
  const records = parseClaude(nonusage.join('\n'))
  assert.equal(records.incomplete, false)
  assert.equal(records.length, 0)
  for (const usage of [{ output_tokens: 1 }, { input_tokens: 3 }, { input_tokens: 3, cache_read_input_tokens: 0 }, { cache_creation_input_tokens: 10, cache_creation: { ephemeral_5m_input_tokens: 5 } }]) {
    const partial = parseClaude(claudePromptRow(usage))
    assert.equal(partial.incomplete, false)
    assert.equal(partial[0].claudeInvalidCounters, false)
    const [snapshot] = buildDailySnapshots(partial, TEST_MACHINE)
    assert.ok(snapshot.eventKey.endsWith(':prompt_unknown'))
  }
})

test('Claude prompt truncated JSON retains valid records but cannot plan destructive corrections', () => {
  const records = parseClaude(`${claudePromptRow(COMPLETE_CLAUDE_PROMPT)}\n{"type":"assistant",`)
  assert.equal(records.length, 1)
  const snapshots = buildDailySnapshots(records, TEST_MACHINE)
  assert.deepEqual(planSnapshotSubmissions(snapshots, [{ sourceId: 'claude-local@test-machine', eventKey: 'prior-flat', runtime: 'claude' }]), [])
})

test('Claude prompt malformed discovery remains incomplete on serialized cache hits and recovers after a complete read', async () => {
  const root = await makeTemp('observatory-claude-prompt-partial-')
  const path = join(root, 'invented.jsonl')
  const statePath = join(root, 'state.json')
  const keys = ['OBSERVATORY_URL', 'OBSERVATORY_API_KEY', 'OBSERVATORY_STATE', 'OBSERVATORY_LOCAL_SOURCES', 'OBSERVATORY_MACHINE', 'CLAUDE_HOME']
  const before = Object.fromEntries(keys.map(key => [key, process.env[key]]))
  const originalFetch = globalThis.fetch
  const posts = []
  let mtimeMs = 1
  Object.assign(process.env, {
    OBSERVATORY_URL: 'http://127.0.0.1:1', OBSERVATORY_API_KEY: 'synthetic-key',
    OBSERVATORY_STATE: statePath, OBSERVATORY_LOCAL_SOURCES: 'claude', OBSERVATORY_MACHINE: TEST_MACHINE, CLAUDE_HOME: root,
  })
  globalThis.fetch = async (url, options = {}) => {
    if (options.method === 'POST') {
      if (new URL(url).pathname === '/api/events') { posts.push(JSON.parse(options.body)) }
      return new Response('{}')
    }
    return new Response(JSON.stringify(new URL(url).searchParams.get('sourceId') === 'claude-local@test-machine'
      ? [{ sourceId: 'claude-local@test-machine', runtime: 'claude', eventKey: 'claude:2026-10-22:prior-flat' }] : []))
  }
  try {
    await writeFile(path, `${claudePromptRow(COMPLETE_CLAUDE_PROMPT)}\n{"message":`)
    const discover = async () => [{ path, mtimeMs }]
    await sweep.main({ discover })
    assert.deepEqual(posts, [], 'cache miss must not replace the old valid key')
    const firstCache = JSON.parse(await readFile(statePath, 'utf8')).files.claude[path]
    assert.equal(firstCache.records.length, 1)
    assert.equal(firstCache.incomplete, true)
    await sweep.main({ discover })
    assert.deepEqual(posts, [], 'serialized matching-mtime cache must retain incompleteness')
    await writeFile(path, `${claudePromptRow(COMPLETE_CLAUDE_PROMPT)}\n  \n${JSON.stringify({ type: 'user', message: {} })}\n`)
    mtimeMs = 2
    await sweep.main({ discover })
    assert.equal(posts.length, 2)
    assert.equal(posts[0].eventKey.endsWith(':prompt_le_100000'), true)
    assert.equal(posts[1].eventKey, 'claude:2026-10-22:prior-flat')
    assert.equal(posts[1].inputTokens, 0)
    assert.equal(JSON.parse(await readFile(statePath, 'utf8')).files.claude[path].incomplete, undefined)
  } finally {
    globalThis.fetch = originalFetch
    for (const key of keys) {
      if (before[key] === undefined) { delete process.env[key] } else { process.env[key] = before[key] }
    }
    await removeTemp(root)
  }
})

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

function codexUsageRow(timestamp, input, output, cached = 0, reasoning = 0, writes = 0) {
  return JSON.stringify({ timestamp, type: 'event_msg', payload: { type: 'token_count', info: { total_token_usage: {
    input_tokens: input, output_tokens: output, cached_input_tokens: cached,
    reasoning_output_tokens: reasoning, cache_write_input_tokens: writes,
  } } } })
}

test('Codex daily records retain midnight and model boundaries without duplicate cumulative usage', () => {
  const content = [
    JSON.stringify({ type: 'turn_context', payload: { model: 'gpt-6-luna' } }),
    codexUsageRow('2026-10-23T23:59:00Z', 100, 10, 20, 2),
    JSON.stringify({ type: 'turn_context', payload: { model: 'gpt-6-sol' } }),
    codexUsageRow('2026-10-24T00:01:00Z', 150, 15, 30, 3),
    codexUsageRow('2026-10-24T00:02:00Z', 150, 15, 30, 3),
  ].join('\n')
  const records = sweep.parseCodexRecords(content)
  assert.deepEqual(records.map(r => [r.date, r.model, r.cum, r.thoughtTokens]), [
    ['2026-10-23', 'gpt-6-luna', { input: 80, output: 10, cacheRead: 20, cacheWrite: 0 }, 2],
    ['2026-10-24', 'gpt-6-sol', { input: 40, output: 5, cacheRead: 10, cacheWrite: 0 }, 1],
  ])
})

test('Codex decreasing cumulative counters mark the scan incomplete rather than inventing usage', () => {
  const records = sweep.parseCodexRecords([
    codexUsageRow('2026-10-24T00:00:00Z', 100, 10),
    codexUsageRow('2026-10-24T00:01:00Z', 20, 2),
  ].join('\n'))
  assert.equal(records.incomplete, true)
})

test('Codex invalid counter or timestamp evidence withholds attribution instead of shifting it to a later row', () => {
  const valid = JSON.parse(codexUsageRow('2026-10-23T23:59:00Z', 100, 10))
  for (const bad of [
    { ...valid, timestamp: 'invalid' },
    ...[true, 0, '2026-02-30T12:00:00Z'].map(timestamp => ({ ...valid, timestamp })),
    ...[false, 0, '', []].map(total_token_usage => ({ ...valid, payload: {
      ...valid.payload, info: { total_token_usage },
    } })),
    ...[null, '100', -1, Number.MAX_SAFE_INTEGER + 1].map(value => ({ ...valid, payload: {
      ...valid.payload, info: { total_token_usage: { ...valid.payload.info.total_token_usage, input_tokens: value } },
    } })),
  ]) {
    assert.equal(sweep.parseCodexRecords(`${JSON.stringify(bad)}\n${codexUsageRow('2026-10-24T00:01:00Z', 150, 15)}`).incomplete, true)
  }
})

test('Codex daily records normalize offset timestamps to UTC', () => {
  const records = sweep.parseCodexRecords(codexUsageRow('2026-10-24T00:30:00+01:00', 100, 10, 20, 2))
  assert.equal(records[0].date, '2026-10-23')
  assert.deepEqual(records[0].cum, { input: 80, output: 10, cacheRead: 20, cacheWrite: 0 })
})

test('Codex nonzero cache writes are incomplete until their billing overlap has an independent oracle', () => {
  assert.equal(sweep.parseCodexRecords(codexUsageRow('2026-10-24T00:00:00Z', 100, 10, 20, 2, 5)).incomplete, true)
})

test('Codex model context without new usage cannot relabel previous usage', () => {
  const records = sweep.parseCodexRecords([
    JSON.stringify({ type: 'turn_context', payload: { model: 'gpt-6-luna' } }),
    codexUsageRow('2026-10-23T23:59:00Z', 100, 10),
    JSON.stringify({ type: 'turn_context', payload: { model: 'gpt-6-sol' } }),
  ].join('\n'))
  assert.deepEqual(records.map(r => [r.model, r.cum.input]), [['gpt-6-luna', 100]])
})

test('Codex usage without valid model context remains unplaced and makes the source incomplete', async () => {
  const root = await makeTemp('codex-unplaced-')
  const path = join(root, 'rollout.jsonl')
  try {
    for (const prefix of [[], ...[undefined, null, '', ' ', 42, {}].map(model => [
      JSON.stringify({ type: 'turn_context', payload: { model: 'gpt-6-luna' } }),
      JSON.stringify({ type: 'turn_context', payload: { model } }),
    ])]) {
      await writeFile(path, [
        ...prefix, codexUsageRow('2026-10-23T23:59:00Z', 100, 10),
        JSON.stringify({ type: 'turn_context', payload: { model: 'gpt-6-sol' } }),
        codexUsageRow('2026-10-24T00:01:00Z', 150, 15),
      ].join('\n'))
      let state = {}
      for (let iteration = 0; iteration < 2; iteration++) {
        const result = await scanRecords({ codexHome: root }, state, new Set(['codex']), async () => [{ path, mtimeMs: 1 }])
        assert.equal(result.incompleteSources.has('codex'), true)
        assert.equal(result.records[0].model, null)
        assert.deepEqual(result.records.map(r => r.cum.input), [100, 50])
        state = JSON.parse(JSON.stringify(state))
      }
    }
  } finally {
    await removeTemp(root)
  }
})

test('Codex invalid context after valid usage clears the model for subsequent increments', () => {
  const records = sweep.parseCodexRecords([
    JSON.stringify({ type: 'turn_context', payload: { model: 'gpt-6-luna' } }),
    codexUsageRow('2026-10-23T23:59:00Z', 100, 10),
    JSON.stringify({ type: 'turn_context', payload: { model: null } }),
    codexUsageRow('2026-10-24T00:01:00Z', 150, 15),
  ].join('\n'))
  assert.equal(records.incomplete, true)
  assert.deepEqual(records.map(r => [r.model, r.cum.input]), [['gpt-6-luna', 100], [null, 50]])
})

test('Codex invalid cumulative subsets persist as an incomplete source through the parse cache', async () => {
  const root = await makeTemp('codex-incomplete-')
  const path = join(root, 'rollout.jsonl')
  try {
    await writeFile(path, codexUsageRow('2026-10-24T00:00:00Z', 10, 1, 11))
    let state = {}
    for (let iteration = 0; iteration < 2; iteration++) {
      const result = await scanRecords({ codexHome: root }, state, new Set(['codex']), async () => [{ path, mtimeMs: 1 }])
      assert.equal(result.incompleteSources.has('codex'), true)
      assert.equal(result.records.length, 0)
      state = JSON.parse(JSON.stringify(state))
    }
  } finally {
    await removeTemp(root)
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

test('parseCodex leaves the model null when no turn_context names one, and the record is unplaced', () => {
  const line = JSON.stringify({ timestamp: '2026-08-24T12:00:00Z', type: 'event_msg', payload: { type: 'token_count', info: { total_token_usage: { input_tokens: 10, cached_input_tokens: 0, output_tokens: 5 } } } })
  const parsed = parseCodex(line)
  assert.equal(parsed.model, null)

  const record = { tool: 'codex', date: '2026-08-24', model: parsed.model, modelProvider: parsed.modelProvider, occurredAtUtc: parsed.endedAt, cum: parsed.cum }
  assert.deepEqual(sweep.buildDailySnapshots([record], TEST_MACHINE), [])
  assert.deepEqual(sweep.countUnplaced([record], TEST_MACHINE), [{ tool: 'codex', model: 'unknown', count: 1 }])
})

test('parseCodex reads model_provider from the session_meta row', () => {
  const lines = [
    JSON.stringify({ type: 'session_meta', payload: { model_provider: 'ZAI' } }),
    JSON.stringify({ type: 'turn_context', payload: { model: 'glm-5.3-flash' } }),
    JSON.stringify({ timestamp: '2026-08-24T12:00:00Z', type: 'event_msg', payload: { type: 'token_count', info: { total_token_usage: { input_tokens: 10, output_tokens: 5 } } } }),
  ].join('\n')

  assert.equal(parseCodex(lines).modelProvider, 'ZAI')
  assert.equal(parseCodex(lines.split('\n').slice(1).join('\n')).modelProvider, null)
})

for (const [modelProvider, model, expected] of [
  ['ZAI', 'glm-5.3-flash', 'Zai'],
  ['deepseek', 'deepseek-flash', 'DeepSeek'],
  [null, 'glm-5.3', 'Zai'],
  [null, 'deepseek-flash', 'DeepSeek'],
  ['openai', 'gpt-6-sol', 'OpenAI'],
  [null, 'gpt-5.4', 'OpenAI'],
  [null, 'mystery-1', null],
  ['ZAI', 'mystery', 'Zai'],
  ['ollama', 'gpt-oss', null], // a present but unrecognised provider is never placed by model name
  ['', 'gpt-5.4', 'OpenAI'],
]) {
  test(`codexProvider(${modelProvider}, ${model}) is ${expected}`, () => {
    assert.equal(codexProvider(modelProvider, model), expected)
  })
}

test('buildDailySnapshots attributes Codex sessions by model provider and drops unknown ones', () => {
  const record = (model, modelProvider) => ({ tool: 'codex', date: '2026-08-24', model, modelProvider, occurredAtUtc: '2026-08-24T12:00:00Z', cum: { input: 10, output: 2, cacheRead: 0, cacheWrite: 0 } })

  const snapshots = buildDailySnapshots([
    record('glm-5.3-flash', 'ZAI'), record('deepseek-flash', 'deepseek'), record('gpt-5.4', 'openai'), record('mystery-1', 'other'),
  ], TEST_MACHINE)

  assert.deepEqual(
    snapshots.map(s => [s.model, s.provider, s.costBasis]),
    [['deepseek-flash', 'DeepSeek', 'listPriceEstimate'], ['glm-5.3-flash', 'Zai', 'notional'], ['gpt-5.4', 'OpenAI', 'notional']],
  )
  // DeepSeek is pay-as-you-go API credit; GLM and Codex stay subscription.
  assert.deepEqual(
    snapshots.map(s => [s.provider, s.usageScope]),
    [['DeepSeek', 'api'], ['Zai', 'subscription'], ['OpenAI', 'subscription']],
  )
  assert.ok(snapshots.every(s => s.sourceId === 'codex-local@test-machine' && s.runtime === 'codex'))
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
    claudePromptObservation: { promptTokens: 52, valid: true },
    claudePromptFacts: { input: 2, read: 20, creation: 30, creation5m: 5, creation1h: 25 },
    claudePromptDimensions: { model: 'claude-opus-5', tier: 'standard', speed: 'standard', geo: 'not_available' },
    claudeInvalidCounters: false,
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
  const root = await makeTemp('observatory-antigravity-')
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
    await removeTemp(root)
  }
})

test('Antigravity parser ignores protobuf fields that only resemble token counters', async () => {
  const root = await makeTemp('observatory-antigravity-false-token-')
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
    await removeTemp(root)
  }
})

test('Antigravity parser reports rejected usage rows while retaining valid totals', async () => {
  const root = await makeTemp('observatory-antigravity-rejected-row-')
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
    await removeTemp(root)
  }
})

test('scanRecords discovers retained Gemini reviews and Antigravity conversations', async () => {
  const root = await makeTemp('observatory-google-scan-')
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
    await removeTemp(root)
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
  const root = await makeTemp('observatory-sweep-upgrade-')
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
    assert.equal(state.parseCacheVersion, 11)
    assert.deepEqual(Array.from(state.files.codex[path].records), [])
    assert.equal(state.files.codex[path].incomplete, true)
  } finally {
    await removeTemp(root)
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
    parseCacheVersion: 11,
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
  const root = await makeTemp('observatory-sweep-')
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
    await removeTemp(root)
  }
})

test('listJsonl tolerates only an absent top-level tool directory', async () => {
  const root = await makeTemp('observatory-sweep-missing-')

  try {
    assert.deepEqual(await listJsonl(join(root, 'missing')), [])
  } finally {
    await removeTemp(root)
  }
})

test('touching and restoring a transcript cannot move usage to another day', async () => {
  const root = await makeTemp('observatory-sweep-touch-')
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
    await removeTemp(root)
  }
})

test('server inventory clears a deleted final snapshot after local state loss while surviving files cover its day', () => {
  const prior = buildDailySnapshots([
    { tool: 'codex', date: '2026-08-24', model: 'gpt-5.4', occurredAtUtc: '2026-08-24T12:00:00Z', cum: { input: 30, output: 5, cacheRead: 3, cacheWrite: 0 } },
  ], TEST_MACHINE)[0]

  assert.deepEqual(planSnapshotSubmissions([], [prior]), [], 'no surviving file covers the day: frozen')
  const submissions = planSnapshotSubmissions([], [prior], new Map(), new Map([[prior.sourceId, '2026-08-23']]))

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

// CodeRabbit 4657871796: driven through main, as a real disabled run is.
test('a disabled source keeps its server history untouched and resumes when re-enabled', async () => {
  const root = await makeTemp('observatory-disabled-kimi-')
  await writeCodexRollout(root)
  await mkdir(join(root, 'kimi', 'sessions'), { recursive: true })
  await writeFile(join(root, 'kimi', 'sessions', 'wire.jsonl'), JSON.stringify({
    type: 'usage.record', time: 1787572800000, model: 'kimi-code/kimi-for-coding',
    usage: { inputOther: 10, output: 2, inputCacheRead: 20, inputCacheCreation: 3 },
  }))
  const recording = recordingServer([], ['kimi:2026-08-20:kimi-code/kimi-for-coding'])
  const statePath = join(root, 'state', 'sweep.json')
  const kimi = 'kimi-local@test-machine'
  try {
    await withRecordingServer(recording, async () => {
      await runMainWith({ root, statePath, server: recording.server, sources: 'codex' })
      assert.equal(recording.gets.includes(kimi), false, 'a disabled source is never inventoried')
      assert.deepEqual(machinePosts(recording.posts, kimi), [], 'nor tombstoned')
      await runMainWith({ root, statePath, server: recording.server, sources: 'kimi' })
    })
    assert.deepEqual(machinePosts(recording.posts, kimi).map(body => [body.eventKey, body.inputTokens]), [
      ['kimi:2026-08-24:kimi-code/kimi-for-coding', 10],
    ])
  } finally { await removeTemp(root) }
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

test('a misfiled OpenAI Codex snapshot is replaced in place when the provider changes', () => {
  const record = { tool: 'codex', date: '2026-10-17', model: 'glm-5.3-flash', occurredAtUtc: '2026-10-17T12:00:00Z', cum: { input: 10, output: 2, cacheRead: 0, cacheWrite: 0 } }
  const [corrected] = buildDailySnapshots([{ ...record, modelProvider: 'ZAI' }], TEST_MACHINE)
  const prior = { ...corrected, provider: 'OpenAI' }

  const submissions = planSnapshotSubmissions([corrected], [prior])

  assert.equal(corrected.eventKey, 'codex:2026-10-17:glm-5.3-flash')
  assert.deepEqual(submissions.map(item => [item.active, item.snapshot.provider, item.snapshot.eventKey]), [
    [true, 'Zai', 'codex:2026-10-17:glm-5.3-flash'],
  ])
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

  for (const [oldKey, newKey] of [['codex:2026-08-24:a-old', 'codex:2026-08-24:z-new'], ['codex:2026-08-24:z-old', 'codex:2026-08-24:a-new']]) {
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

test("Claude band reconciliation converges after every persisted posting boundary and leaves one old posting", () => {
    const records = parseClaude(
        [
            claudePromptRow(
                {
                    input_tokens: 70000,
                    cache_read_input_tokens: 10000,
                    cache_creation_input_tokens: 20000,
                    cache_creation: {
                        ephemeral_5m_input_tokens: 5000,
                        ephemeral_1h_input_tokens: 15000,
                    },
                    output_tokens: 3,
                    thinking_tokens: 2,
                },
                { id: "lower" },
            ),
            claudePromptRow(
                {
                    input_tokens: 100001,
                    cache_read_input_tokens: 0,
                    cache_creation_input_tokens: 0,
                    output_tokens: 5,
                    thinking_tokens: 4,
                },
                { id: "upper" },
            ),
            claudePromptRow(
                { input_tokens: 17, output_tokens: 7, thinking_tokens: 8 },
                { id: "unknown" },
            ),
            claudePromptRow(
                { input_tokens: 11, output_tokens: 2 },
                { id: "flat", model: "claude-opus-5" },
            ),
        ].join("\n"),
    );
    const snapshots = buildDailySnapshots(records, TEST_MACHINE);
    // Invented exact-cent amounts exercise cumulative corrections only; provider pricing
    // and unknown-band admission are covered by the real Data service tests.
    const cents = {
        prompt_le_100000: 12,
        prompt_gt_100000: 34,
        prompt_unknown: null,
        flat: 5,
    };
    for (const snapshot of snapshots) {
        const lane = snapshot.eventKey.split(":").at(-1);
        const amount = lane === "unknown" ? cents.flat : cents[lane];
        snapshot.costUsd = amount === null ? null : amount / 100;
    }
    const flatKey = "claude:2026-10-22:claude-opus-5:unknown:unknown:unknown";
    const oldKey = "claude:2026-10-22:claude-haiku-5-5:unknown:unknown:unknown";
    const old = {
        ...snapshots[0],
        eventKey: oldKey,
        inputTokens: 170018,
        outputTokens: 15,
        cacheReadTokens: 10000,
        cacheWriteTokens: 20000,
        cacheWrite1hTokens: 15000,
        thoughtTokens: 14,
        costUsd: 0.99,
        rawPayload:
            '{"source":"observatory-sweep","tool":"claude","machine":"test-machine","thinking_tokens":14}',
    };
    const flat = snapshots.find((snapshot) => snapshot.eventKey === flatKey);
    const initial = [old, flat];
    const firstPlan = planSnapshotSubmissions(snapshots, initial);
    assert.deepEqual(
        firstPlan.map((item) => item.active),
        [true, true, true, true, false],
    );
    assert.equal(firstPlan.at(-1).snapshot.eventKey, oldKey);
    const identity = (snapshot) => `${snapshot.sourceId}\n${snapshot.eventKey}`;
    const totals = (ledger) => {
        const rows = [...ledger.values()];
        return [
            ...[
                "inputTokens",
                "outputTokens",
                "cacheReadTokens",
                "cacheWriteTokens",
                "cacheWrite1hTokens",
                "thoughtTokens",
            ].map((counter) =>
                rows.reduce((sum, row) => sum + row[counter], 0),
            ),
            rows.reduce(
                (sum, row) =>
                    sum +
                    (row.costUsd === null ? 0 : Math.round(row.costUsd * 100)),
                0,
            ),
            rows.filter(
                (row) =>
                    row.costUsd === null &&
                    !JSON.parse(row.rawPayload).tombstone,
            ).length,
        ];
    };
    const apply = (ledger, { snapshot }) =>
        ledger.set(identity(snapshot), structuredClone(snapshot));
    // Prefix 0 is before the first active; 1..4 stop after each active, including
    // immediately before the zero correction; 5 stops immediately after it.
    for (let stop = 0; stop <= firstPlan.length; stop++) {
        let ledger = new Map(
            initial.map((snapshot) => [
                identity(snapshot),
                structuredClone(snapshot),
            ]),
        );
        for (const submission of firstPlan.slice(0, stop)) {
            apply(ledger, submission);
        }
        if (stop === 4) {
            assert.deepEqual(
                totals(ledger),
                [340047, 32, 20000, 40000, 30000, 28, 150, 1],
                "active-first overlap contains both the old mixed row and all replacements",
            );
        }
        // Lose all in-memory/local state. The next planner sees only persisted server
        // inventory, including zero-corrected rows; no remembered success is assumed.
        ledger = new Map(
            JSON.parse(JSON.stringify([...ledger])).map(([key, row]) => [
                key,
                row,
            ]),
        );
        for (const submission of planSnapshotSubmissions(snapshots, [
            ...ledger.values(),
        ])) {
            apply(ledger, submission);
        }
        assert.deepEqual(
            totals(ledger),
            [170029, 17, 10000, 20000, 15000, 14, 51, 1],
            `stop=${stop}`,
        );
        assert.equal(
            ledger.size,
            5,
            "four current keys plus the retained old zero posting",
        );
        assert.equal(ledger.get(identity(old)).inputTokens, 0);
        assert.equal(
            JSON.parse(ledger.get(identity(old)).rawPayload).tombstone,
            true,
        );
        assert.deepEqual(
            ledger.get(identity(flat)),
            flat,
            "flat identity and payload remain unchanged",
        );
        const settled = [...ledger];
        for (const submission of planSnapshotSubmissions(snapshots, [
            ...ledger.values(),
        ])) {
            apply(ledger, submission);
        }
        assert.deepEqual(
            [...ledger],
            settled,
            "another complete retry cannot add keys, tokens or money",
        );
    }
});

test('parseLocalSources defaults to the active lanes, and an explicit allowlist re-enables dormant ones', () => {
  assert.deepEqual([...parseLocalSources()].sort(), [...sweep.DEFAULT_LOCAL_SOURCES].sort())
  assert.deepEqual([...parseLocalSources()].sort(), ['antigravity', 'claude', 'codex', 'copilot', 'gemini'])
  assert.deepEqual([...parseLocalSources('codex,kimi')].sort(), ['codex', 'kimi'])
  assert.deepEqual([...parseLocalSources('kimi,grok,pi')].sort(), ['grok', 'kimi', 'pi'])
})

test('countUnplaced counts dropped Codex sessions by tool and model, ignoring placeable and incomplete records', () => {
  const record = (model, modelProvider) => ({ tool: 'codex', date: '2026-08-24', model, modelProvider, occurredAtUtc: '2026-08-24T12:00:00Z', cum: { input: 10, output: 2, cacheRead: 0, cacheWrite: 0 } })

  const unplaced = sweep.countUnplaced([
    record('mystery-1', 'other'), record('mystery-1', 'other'), record('gpt-5.4', 'openai'),
    { ...record('no-date', 'other'), date: undefined },
  ], TEST_MACHINE)

  assert.deepEqual(unplaced, [{ tool: 'codex', model: 'mystery-1', count: 2 }])
})

test('postSweepStatus posts counts with a server-safe model id and never throws', async () => {
  let received
  const server = createServer((request, response) => {
    let body = ''
    request.on('data', chunk => { body += chunk })
    request.on('end', () => {
      received = { url: request.url, key: request.headers['x-observatory-key'], body: JSON.parse(body) }
      response.statusCode = 204
      response.end()
    })
  })
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve))
  try {
    const base = `http://127.0.0.1:${server.address().port}`
    await sweep.postSweepStatus(base, 'k', 'Test Box', [{ tool: 'codex', model: 'has space/ok', count: 2 }])
    assert.equal(received.url, '/api/sources/sweep-status')
    assert.equal(received.key, 'k')
    assert.deepEqual(received.body, { machine: 'test-box', unplaced: [{ tool: 'codex', model: 'has_space/ok', count: 2 }], heldFiles: 0 })
  } finally {
    await new Promise(resolve => server.close(resolve))
  }
  await sweep.postSweepStatus('http://127.0.0.1:1', 'k', 'x', [])
})

test('sweepStatusEntries sums ids that collide after sanitising and folds overflow into one total', () => {
  const merged = sweep.sweepStatusEntries([
    { tool: 'codex', model: 'a b', count: 2 },
    { tool: 'codex', model: 'a_b', count: 3 },
  ])
  assert.deepEqual(merged, [{ tool: 'codex', model: 'a_b', count: 5 }])

  const many = Array.from({ length: 250 }, (_, i) => ({ tool: 'codex', model: `m${i}`, count: 1 }))
  const capped = sweep.sweepStatusEntries(many)
  assert.equal(capped.length, 200)
  assert.equal(capped.reduce((sum, e) => sum + e.count, 0), 250)
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


const GROK_USAGE = JSON.stringify({
  sessionId: 'session-id',
  session: {
    // The session block is the sum of the turns. Reading it as well as the turns would
    // double every Grok total, so the parser must ignore it.
    inputTokens: 300_000, outputTokens: 2_000, cachedReadTokens: 200_000,
    cacheCreationTokens: 0, reasoningTokens: 800, costUsdTicks: 8_248_640_000,
  },
  turns: [
    {
      turnNumber: 1,
      endedAt: '2026-09-16T09:41:26.402839700+00:00',
      modelUsage: {
        'grok-4.6': {
          inputTokens: 200_000, outputTokens: 1_500, cachedReadTokens: 150_000,
          cacheCreationTokens: 0, reasoningTokens: 500, costUsdTicks: 5_000_000_000,
        },
      },
    },
    {
      turnNumber: 2,
      endedAt: '2026-09-16T10:05:28.288156+00:00',
      modelUsage: {
        'grok-4.6': {
          inputTokens: 60_000, outputTokens: 400, cachedReadTokens: 40_000,
          cacheCreationTokens: 0, reasoningTokens: 200, costUsdTicks: 3_000_000_000,
        },
        'grok-4.6-build': {
          inputTokens: 40_000, outputTokens: 100, cachedReadTokens: 10_000,
          cacheCreationTokens: 0, reasoningTokens: 100, costUsdTicks: 248_640_000,
        },
      },
    },
    { turnNumber: 3, endedAt: '2026-09-16T10:07:58.437472500+00:00', inputTokens: 0, outputTokens: 0 },
  ],
})

const PI_TRANSCRIPT = [
  JSON.stringify({ type: 'session', id: 'session-id', timestamp: '2026-09-16T08:00:00.000Z' }),
  JSON.stringify({ type: 'model_change', timestamp: '2026-09-16T08:00:01.000Z', provider: 'openrouter', modelId: 'meta/muse-spark-1.3-contributor' }),
  JSON.stringify({
    type: 'message', timestamp: '2026-09-16T08:00:02.000Z',
    message: {
      role: 'assistant', provider: 'openrouter', model: 'meta/muse-spark-1.3-contributor',
      usage: {
        input: 12_966, output: 320, cacheRead: 113, cacheWrite: 0, reasoning: 182,
        totalTokens: 13_399, cost: { input: 0.0012966, output: 0.000064, cacheRead: 2.26e-7, cacheWrite: 0, total: 0.001360826 },
      },
    },
  }),
  // A model PI routed to Anthropic. Its usage already reaches the Observatory through the
  // claude-local lane, so filing it again under an OpenRouter slug would double-count it.
  JSON.stringify({
    type: 'message', timestamp: '2026-09-16T08:00:03.000Z',
    message: {
      role: 'assistant', provider: 'anthropic', model: 'claude-sonnet-5',
      usage: { input: 900, output: 90, cacheRead: 0, cacheWrite: 0, totalTokens: 990, cost: { total: 0.5 } },
    },
  }),
  // A 403 from OpenRouter: recorded as a real turn with all-zero usage.
  JSON.stringify({
    type: 'message', timestamp: '2026-09-16T08:00:04.000Z',
    message: {
      role: 'assistant', provider: 'openrouter', model: 'meta/muse-spark-1.3-contributor',
      stopReason: 'error', errorMessage: '403: age confirmation required',
      usage: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, totalTokens: 0, cost: { total: 0 } },
    },
  }),
  JSON.stringify({ type: 'message', timestamp: '2026-09-16T08:00:05.000Z', message: { role: 'user', content: [] } }),
].join('\n')

test('parseGrok sums per-turn model usage and ignores the session block that mirrors it', () => {
  const records = parseGrok(GROK_USAGE)

  assert.equal(records.length, 3)
  const snapshots = buildDailySnapshots(records, TEST_MACHINE)
  const flagship = snapshots.find(snapshot => snapshot.model === 'grok-4.6')
  // Turn totals only: 260k input of which 190k cached, 1 900 output.
  assert.equal(flagship.inputTokens, 70_000)
  assert.equal(flagship.cacheReadTokens, 190_000)
  assert.equal(flagship.outputTokens, 1_900)
})

test('parseGrok converts input to exclude cached reads, which the CLI folds in', () => {
  const [first] = parseGrok(GROK_USAGE)

  assert.equal(first.inputTokens, 50_000)
  assert.equal(first.cacheReadTokens, 150_000)
  assert.equal(first.thoughtTokens, 500)
})

test('parseGrok converts cost ticks to USD and posts it as a measured cost', () => {
  const snapshots = buildDailySnapshots(parseGrok(GROK_USAGE), TEST_MACHINE)

  const flagship = snapshots.find(snapshot => snapshot.model === 'grok-4.6')
  assert.equal(flagship.provider, 'Xai')
  assert.equal(flagship.costBasis, 'providerEstimated')
  assert.equal(flagship.costUsd, 0.8)
  assert.equal(flagship.sourceId, `grok-local@${TEST_MACHINE}`)
})

test('parseGrok returns nothing for a turnless usage file', () => {
  assert.deepEqual(parseGrok(JSON.stringify({ sessionId: 'x' })), [])
  assert.deepEqual(parseGrok(JSON.stringify({ turns: [{ endedAt: 'nonsense', modelUsage: { 'grok-4.6': {} } }] })), [])
})

test('parsePi keeps Meta turns and leaves other vendors to their own lanes', () => {
  const records = parsePi(PI_TRANSCRIPT)

  assert.equal(records.length, 2)
  assert.ok(records.every(record => record.model.startsWith('meta/')))
})

test('parsePi carries the cost OpenRouter charged through to the snapshot', () => {
  const [snapshot] = buildDailySnapshots(parsePi(PI_TRANSCRIPT), TEST_MACHINE)

  assert.equal(snapshot.provider, 'Meta')
  assert.equal(snapshot.model, 'meta/muse-spark-1.3-contributor')
  assert.equal(snapshot.inputTokens, 12_966)
  assert.equal(snapshot.cacheReadTokens, 113)
  assert.equal(snapshot.costBasis, 'providerEstimated')
  assert.equal(snapshot.costUsd, 0.001360826)
  assert.equal(snapshot.sourceId, `pi-local@${TEST_MACHINE}`)
})

test('scanRecords discovers Grok usage files and PI transcripts', async () => {
  const root = await makeTemp('observatory-xai-meta-scan-')
  const grokSession = join(root, 'grok', 'sessions', 'C%3A%5CUsers%5Cchris', 'session-id')
  const piSession = join(root, 'pi', 'sessions', '--D--repo--')
  await mkdir(grokSession, { recursive: true })
  await mkdir(piSession, { recursive: true })
  await writeFile(join(grokSession, 'usage.json'), GROK_USAGE)
  // A sibling the Grok matcher must not pick up: chat history is not usage.
  await writeFile(join(grokSession, 'chat_history.jsonl'), '{"role":"user"}')
  await writeFile(join(piSession, '2026-09-16T08-00-00-000Z_session.jsonl'), PI_TRANSCRIPT)
  const cfg = { grokHome: join(root, 'grok'), piHome: join(root, 'pi') }

  try {
    const { records, incompleteSources, emptySources } = await scanRecords(cfg, {}, new Set(['grok', 'pi']))

    assert.deepEqual([...incompleteSources], [])
    assert.deepEqual([...emptySources], [])
    assert.equal(records.filter(record => record.tool === 'grok').length, 3)
    assert.equal(records.filter(record => record.tool === 'pi').length, 2)
  } finally {
    await removeTemp(root)
  }
})

test('Antigravity model detection ignores prose that merely mentions a model', async () => {
  const root = await makeTemp('observatory-antigravity-prose-')
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
    await removeTemp(root)
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
  const root = await makeTemp('observatory-sweep-v2-')
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
    assert.deepEqual([...incompleteSources], ['codex'])
    assert.equal(state.parseCacheVersion, 11)
    assert.deepEqual(Array.from(state.files.codex[path].records), [])
    assert.equal(state.files.codex[path].incomplete, true)
  } finally {
    await removeTemp(root)
  }
})

test('scanRecords marks a populated recordless root as cleanly empty and a missing root as unverifiable', async () => {
  const root = await makeTemp('observatory-sweep-empty-')
  await mkdir(join(root, 'codex', 'sessions', '2026', '08', '24'), { recursive: true })
  const cfg = {
    codexHome: join(root, 'codex'),
    copilotHome: join(root, 'copilot'),
    claudeHome: join(root, 'claude'),
    kimiHome: join(root, 'kimi'),
    geminiHome: join(root, 'gemini'),
  }

  try {
    // Copilot's home does not exist at all: an unmounted or mistyped home must
    // not read as a verified empty history, so it lands in neither set.
    const { records, incompleteSources, emptySources } = await scanRecords(cfg, {}, new Set(['codex', 'copilot']))

    assert.deepEqual(records, [])
    assert.deepEqual([...incompleteSources], [])
    assert.deepEqual([...emptySources], ['codex'])
  } finally {
    await removeTemp(root)
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

test('main fetches inventory only for enabled per-machine sources and never tombstones an unverifiable empty scan', async () => {
  const root = await makeTemp('observatory-sweep-main-')
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
    GEMINI_HOME: join(root, 'gemini'),
  }

  try {
    await run(process.execPath, [fileURLToPath(new URL('./observatory-sweep.mjs', import.meta.url))], { env })

    // Only this run's enabled source is inventoried, under this machine's
    // namespace -- the other five sources and other machines are never read.
    // The one-shot legacy migration waits for a verified replacement, and the missing
    // root verifies nothing, so the un-suffixed id is not read either.
    assert.deepEqual(gets.map(request => request.sourceId), ['codex-local@test-machine'])
    assert.equal(gets.every(request => request.apiKey === 'test-key'), true)
    // Codex's discovery root does not exist here (an unmounted home or a
    // mistyped CODEX_HOME reads the same way), so the empty scan is unverified
    // and its stale server key is left alone rather than zeroed.
    assert.deepEqual(posts, [])
  } finally {
    await new Promise(resolve => server.close(resolve))
    await removeTemp(root)
  }
})

test('main freezes stale server keys for an enabled source whose transcripts were all deleted', async () => {
  const root = await makeTemp('observatory-sweep-deleted-')
  // Deleting every transcript leaves the discovery root's directory skeleton
  // behind; only the .jsonl payloads are gone, so the empty scan is verified.
  await mkdir(join(root, 'codex', 'sessions', '2026', '08', '24'), { recursive: true })
  const statePath = join(root, 'state', 'sweep.json')
  const stale = {
    provider: 'openai', occurredAtUtc: '2026-08-20T12:00:00Z', model: 'gpt-5.5',
    costUsd: 0.01, runtime: 'codex', sourceId: 'codex-local@test-machine', sourceKind: 'localTelemetry',
    usageScope: 'subscription', costBasis: 'notional', eventKey: 'codex:2026-08-20:gpt-5.5',
  }
  const posts = []
  const server = createServer(async (request, response) => {
    const url = new URL(request.url, 'http://127.0.0.1')
    if (request.method === 'GET' && url.pathname === '/api/events/local-snapshots') {
      const sourceId = url.searchParams.get('sourceId')
      const body = sourceId === 'codex-local@test-machine' ? [stale] : []
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
    OBSERVATORY_MACHINE: 'test-machine',
    CODEX_HOME: join(root, 'codex'),
    COPILOT_HOME: join(root, 'copilot'),
    CLAUDE_HOME: join(root, 'claude'),
    KIMI_HOME: join(root, 'kimi'),
    GEMINI_HOME: join(root, 'gemini'),
  }

  try {
    await run(process.execPath, [fileURLToPath(new URL('./observatory-sweep.mjs', import.meta.url))], { env })

    // The clean scan found nothing under a populated discovery root, so no surviving
    // file covers any day: a vendor CLI's own retention could have removed the
    // transcripts, and the stale server key is frozen rather than zeroed.
    assert.deepEqual(posts, [])
  } finally {
    await new Promise(resolve => server.close(resolve))
    await removeTemp(root)
  }
})

test('main retries a failed server-inventory tombstone from persisted state and then succeeds', async () => {
  const root = await makeTemp('observatory-sweep-main-')
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
    provider: 'openai', occurredAtUtc: '2026-08-25T12:00:00Z', model: 'gpt-5.5',
    inputTokens: 2, outputTokens: 10, cacheReadTokens: 0, cacheWriteTokens: 0,
    cacheWrite1hTokens: 0, thoughtTokens: 0, costUsd: 0.01,
    runtime: 'codex', sourceId: 'codex-local@test-machine', sourceKind: 'localTelemetry',
    usageScope: 'subscription', costBasis: 'notional', eventKey: 'codex:2026-08-25:gpt-5.5',
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
    GEMINI_HOME: join(root, 'gemini'),
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
    await removeTemp(root)
  }
})

test('main withholds a source tombstone after its replacement exhausts retries but continues unrelated sources', async () => {
  const root = await makeTemp('observatory-sweep-replacement-')
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
    provider: 'moonshot', occurredAtUtc: '2026-08-25T12:00:00Z', model: 'kimi-code/kimi-for-coding',
    costUsd: null, runtime: 'kimi', sourceId: 'kimi-local@test-machine', sourceKind: 'localTelemetry',
    usageScope: 'subscription', costBasis: 'notional', eventKey: 'kimi:2026-08-25:kimi-code/kimi-for-coding',
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
    GEMINI_HOME: join(root, 'gemini'),
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
    await removeTemp(root)
  }
})

test('main aborts nested discovery failures without replacing cache or posting partial truth', async () => {
  const root = await makeTemp('observatory-sweep-read-error-')
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
    'CODEX_HOME', 'COPILOT_HOME', 'CLAUDE_HOME', 'KIMI_HOME', 'GEMINI_HOME',
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
    GEMINI_HOME: join(root, 'gemini'),
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
    await removeTemp(root)
  }
})

test('main withholds corrections and tombstones for an incompletely scanned source after a cache wipe', async () => {
  const root = await makeTemp('observatory-sweep-incomplete-')
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
    provider: 'moonshot', occurredAtUtc: '2026-08-25T12:00:00Z', model: 'kimi-code/kimi-for-coding',
    costUsd: null, runtime: 'kimi', sourceId: 'kimi-local@test-machine', sourceKind: 'localTelemetry',
    usageScope: 'subscription', costBasis: 'notional', eventKey: 'kimi:2026-08-25:kimi-code/kimi-for-coding',
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
    'OBSERVATORY_MACHINE', 'CODEX_HOME', 'COPILOT_HOME', 'CLAUDE_HOME', 'KIMI_HOME', 'GEMINI_HOME',
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
    GEMINI_HOME: join(root, 'gemini'),
  })

  try {
    const { main } = await import('./observatory-sweep.mjs')
    // A file that vanished between discovery and read (TOCTOU ENOENT) hits the
    // no-cache branch once the version bump has wiped every cached record.
    const discover = async dir => {
      const files = await listJsonl(dir)
      if (dir === join(codexHome, 'sessions')) {
        // Its hold ends on its last-write day, so that day must cover the keys under test.
        files.push({ path: join(dir, 'vanished.jsonl'), mtimeMs: Date.parse('2026-08-24T23:00:00Z') })
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
    await removeTemp(root)
  }
})

test('main tombstones the replaced legacy un-suffixed keys once and records the migration in state', async () => {
  const root = await makeTemp('observatory-sweep-legacy-')
  const sessions = join(root, 'codex', 'sessions')
  const statePath = join(root, 'state', 'sweep.json')
  await mkdir(sessions, { recursive: true })
  // The local history replaces both legacy days, so both legacy keys are zeroed.
  await writeFile(join(sessions, 'rollout.jsonl'), [
    JSON.stringify({ type: 'turn_context', payload: { model: 'gpt-5.5' } }),
    codexUsageRow('2026-08-10T12:00:00Z', 10, 5),
    codexUsageRow('2026-08-11T12:00:00Z', 20, 10),
  ].join('\n'))
  const legacyRecord = eventKey => ({
    provider: 'openai', occurredAtUtc: '2026-08-10T12:00:00Z', model: 'gpt-5.5',
    inputTokens: 10, outputTokens: 5, cacheReadTokens: 0, cacheWriteTokens: 0, cacheWrite1hTokens: 0, thoughtTokens: 0,
    costUsd: null, runtime: 'codex',
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
    'OBSERVATORY_MACHINE', 'CODEX_HOME', 'COPILOT_HOME', 'CLAUDE_HOME', 'KIMI_HOME', 'GEMINI_HOME',
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
    GEMINI_HOME: join(root, 'gemini'),
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
    await removeTemp(root)
  }
})

test('main retries the legacy migration when the legacy inventory fetch fails', async () => {
  const root = await makeTemp('observatory-sweep-legacy-fail-')
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
    'OBSERVATORY_MACHINE', 'CODEX_HOME', 'COPILOT_HOME', 'CLAUDE_HOME', 'KIMI_HOME', 'GEMINI_HOME',
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
    GEMINI_HOME: join(root, 'gemini'),
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
    await removeTemp(root)
  }
})

test('main retries the legacy migration when a legacy tombstone post fails', async () => {
  const root = await makeTemp('observatory-sweep-legacy-retry-')
  const sessions = join(root, 'codex', 'sessions')
  const statePath = join(root, 'state', 'sweep.json')
  await mkdir(sessions, { recursive: true })
  await writeFile(join(sessions, 'rollout.jsonl'), [
    JSON.stringify({ type: 'turn_context', payload: { model: 'gpt-5.5' } }),
    codexUsageRow('2026-08-10T12:00:00Z', 10, 5),
    codexUsageRow('2026-08-11T12:00:00Z', 20, 10),
  ].join('\n'))
  const legacyRecord = eventKey => ({
    provider: 'openai', occurredAtUtc: '2026-08-10T12:00:00Z', model: 'gpt-5.5',
    inputTokens: 10, outputTokens: 5, cacheReadTokens: 0, cacheWriteTokens: 0, cacheWrite1hTokens: 0, thoughtTokens: 0,
    costUsd: null, runtime: 'codex',
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
      const body = sourceId === 'codex-local'
        ? [legacyRecord('codex:2026-08-10:gpt-5.5'), legacyRecord('codex:2026-08-11:gpt-5.5')]
        : []
      response.writeHead(200, { 'Content-Type': 'application/json' }).end(JSON.stringify(body))
      return
    }
    if (request.method === 'POST' && url.pathname === '/api/events') {
      let body = ''
      for await (const chunk of request) { body += chunk }
      const parsed = JSON.parse(body)
      posts.push(parsed)
      if (parsed.sourceId === 'codex-local' && parsed.eventKey === 'codex:2026-08-10:gpt-5.5') {
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
    'OBSERVATORY_MACHINE', 'CODEX_HOME', 'COPILOT_HOME', 'CLAUDE_HOME', 'KIMI_HOME', 'GEMINI_HOME',
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
    GEMINI_HOME: join(root, 'gemini'),
  })

  try {
    const { main } = await import('./observatory-sweep.mjs')
    await main()

    assert.equal(
      posts.some(body => body.sourceId === 'codex-local' && body.eventKey === 'codex:2026-08-11:gpt-5.5'),
      true,
      'a failed tombstone does not stop the remaining legacy keys from posting',
    )
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
    await removeTemp(root)
  }
})

test('main tolerates one source inventory failing and still sweeps the rest', async () => {
  const root = await makeTemp('observatory-sweep-inventory-failure-')
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
    GEMINI_HOME: join(root, 'gemini'),
  }

  try {
    // The codex inventory GET 500s through every retry; the run must still
    // complete and post the kimi snapshot rather than aborting the sweep.
    const { stderr } = await run(process.execPath, [fileURLToPath(new URL('./observatory-sweep.mjs', import.meta.url))], { env })

    assert.deepEqual(posts, ['kimi:2026-08-24:kimi-code/kimi-for-coding'])
    assert.match(stderr, /Inventory unavailable for codex-local@test-machine/)
  } finally {
    await new Promise(resolve => server.close(resolve))
    await removeTemp(root)
  }
})

// N02 A1: the legacy series is zeroed only after this machine's replacement is verified.
test('main leaves the legacy namespace and its marker alone when the source root is missing', async () => {
  const root = await makeTemp('observatory-sweep-legacy-missing-')
  const statePath = join(root, 'state', 'sweep.json')
  const posts = []
  const server = createServer(async (request, response) => {
    const url = new URL(request.url, 'http://127.0.0.1')
    if (request.method === 'GET' && url.pathname === '/api/events/local-snapshots') {
      const body = url.searchParams.get('sourceId') === 'codex-local'
        ? [{
          provider: 'openai', occurredAtUtc: '2026-08-10T12:00:00Z', model: 'gpt-5.5',
          inputTokens: 40, outputTokens: 9, costUsd: null, runtime: 'codex',
          sourceId: 'codex-local', sourceKind: 'localTelemetry',
          usageScope: 'subscription', costBasis: 'notional', eventKey: 'codex:2026-08-10:gpt-5.5',
        }]
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
  const envKeys = [
    'OBSERVATORY_URL', 'OBSERVATORY_API_KEY', 'OBSERVATORY_STATE', 'OBSERVATORY_LOCAL_SOURCES',
    'OBSERVATORY_MACHINE', 'CODEX_HOME', 'COPILOT_HOME', 'CLAUDE_HOME', 'KIMI_HOME', 'GEMINI_HOME',
  ]
  const priorEnv = Object.fromEntries(envKeys.map(key => [key, process.env[key]]))
  Object.assign(process.env, {
    OBSERVATORY_URL: `http://127.0.0.1:${server.address().port}`,
    OBSERVATORY_API_KEY: 'test-key',
    OBSERVATORY_STATE: statePath,
    OBSERVATORY_LOCAL_SOURCES: 'codex',
    OBSERVATORY_MACHINE: 'test-machine',
    // Unmounted home: the sessions directory does not exist.
    CODEX_HOME: join(root, 'codex'),
    COPILOT_HOME: join(root, 'copilot'),
    CLAUDE_HOME: join(root, 'claude'),
    KIMI_HOME: join(root, 'kimi'),
    GEMINI_HOME: join(root, 'gemini'),
  })

  try {
    await sweep.main()
    assert.deepEqual(posts.filter(body => body.sourceId === 'codex-local'), [])
    const state = JSON.parse(await readFile(statePath, 'utf8'))
    assert.equal(state.legacySourceMigration?.codex, undefined)
  } finally {
    for (const key of envKeys) {
      if (priorEnv[key] === undefined) { delete process.env[key] } else { process.env[key] = priorEnv[key] }
    }
    await new Promise(resolve => server.close(resolve))
    await removeTemp(root)
  }
})

// N02 A2: a conversation database whose transcript is gone keeps its cached usage.
async function antigravityFixture(root) {
  const geminiHome = join(root, 'gemini')
  const conversations = join(geminiHome, 'antigravity-cli', 'conversations')
  const transcriptDir = join(geminiHome, 'antigravity-cli', 'brain', 'agy-session', '.system_generated', 'logs')
  await mkdir(conversations, { recursive: true })
  await mkdir(transcriptDir, { recursive: true })
  await writeFile(join(transcriptDir, 'transcript.jsonl'), JSON.stringify({
    created_at: '2026-08-24T12:00:00Z', content: 'Model Selection` from None to Gemini 3.1 Pro (High).',
  }))
  const db = new DatabaseSync(join(conversations, 'agy-session.db'))
  db.exec('CREATE TABLE steps (idx INTEGER PRIMARY KEY, step_type INTEGER, step_payload BLOB)')
  db.prepare('INSERT INTO steps VALUES (?, ?, ?)').run(1, 23, antigravityPayload(50, 10))
  db.close()
  return {
    cfg: {
      codexHome: join(root, 'codex'), copilotHome: join(root, 'copilot'),
      claudeHome: join(root, 'claude'), kimiHome: join(root, 'kimi'), geminiHome,
    },
    brain: join(geminiHome, 'antigravity-cli', 'brain'),
  }
}

test('scanRecords reuses cached usage when a conversation database loses its transcript', async () => {
  const root = await makeTemp('observatory-agy-no-transcript-')
  try {
    const { cfg, brain } = await antigravityFixture(root)
    const state = {}
    const first = await scanRecords(cfg, state, new Set(['antigravity']))
    assert.equal(first.records.length, 1)
    await removeTemp(brain)
    const second = await scanRecords(cfg, state, new Set(['antigravity']))
    assert.equal(second.records.length, 1, 'the cached records stay')
    assert.equal(second.emptySources.has('antigravity'), false)
    // CodeRabbit 4657871781: the cached days are held, not reposted as fresh observations, and the
    // orphan outcome is cached under its new key so the next run restores the same hold.
    assert.deepEqual(second.holds.get('antigravity')?.map(hold => hold.from), ['2026-08-24'])
    assert.equal(second.heldFiles.get('antigravity'), 1)
    const [entry] = Object.values(state.files.antigravity)
    assert.equal(entry.incomplete, true)
    assert.match(entry.cacheKey, /:no-transcript$/)
    const third = await scanRecords(cfg, state, new Set(['antigravity']))
    assert.deepEqual(third.holds.get('antigravity')?.map(hold => hold.from), ['2026-08-24'])
    assert.equal(third.records.length, 1)
    const logs = join(brain, 'agy-session', '.system_generated', 'logs')
    await mkdir(logs, { recursive: true })
    await writeFile(join(logs, 'transcript.jsonl'), JSON.stringify({
      created_at: '2026-08-24T12:00:00Z', content: 'Model Selection` from None to Gemini 3.1 Pro (High).',
    }))
    const restored = await scanRecords(cfg, state, new Set(['antigravity']))
    assert.equal(restored.holds.has('antigravity'), false, 'a restored transcript re-parses cleanly')
  } finally {
    await removeTemp(root)
  }
})

test('scanRecords flags a source incomplete, not verified-empty, when no transcript exists and nothing is cached', async () => {
  const root = await makeTemp('observatory-agy-no-transcript-cold-')
  try {
    const { cfg, brain } = await antigravityFixture(root)
    await removeTemp(brain)
    const scan = await scanRecords(cfg, {}, new Set(['antigravity']))
    assert.equal(scan.incompleteSources.has('antigravity'), true)
    assert.equal(scan.emptySources.has('antigravity'), false)
  } finally {
    await removeTemp(root)
  }
})

test('dry run previews offline without touching the server', async () => {
  const root = await makeTemp('observatory-sweep-dry-run-')
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
    GEMINI_HOME: join(root, 'gemini'),
  }

  try {
    await run(process.execPath, [fileURLToPath(new URL('./observatory-sweep.mjs', import.meta.url)), '--dry-run'], { env })
  } finally {
    await removeTemp(root)
  }
})

test('removeTemp refuses a target this test file did not create', async () => {
  await assert.rejects(() => removeTemp(tmpdir()), /refusing to delete/)
  await assert.rejects(() => removeTemp(join(tmpdir(), 'not-created-by-this-test')), /refusing to delete/)
  const root = await makeTemp('observatory-removetemp-')
  await assert.rejects(() => removeTemp(join(root, '..')), /refusing to delete/)
  await removeTemp(root)
})

// --- Per-key holds (R-F3, R-F4+F9, R-F5, R-F33, R-P2-GPT-1, R-P2-Gemini-1) ---

const CODEX_SOL = JSON.stringify({ type: 'turn_context', payload: { model: 'gpt-6-sol' } })

test('Codex parse fault holds only keys from the last accepted UTC day', () => {
  const records = sweep.parseCodexRecords([
    CODEX_SOL,
    codexUsageRow('2026-10-23T12:00:00Z', 100, 10),
    codexUsageRow('2026-10-24T12:00:00Z', 150, 15),
    codexUsageRow('2026-10-25T12:00:00Z', 120, 12),
  ].join('\n'))
  assert.equal(records.incomplete, true)
  assert.equal(records.holdFrom, '2026-10-24')
  assert.deepEqual(records.map(record => record.date), ['2026-10-23', '2026-10-24'])
})

test('Codex parse fault before any accepted row holds the whole source', () => {
  const records = sweep.parseCodexRecords([CODEX_SOL, codexUsageRow('2026-10-25T12:00:00Z', -1, 0)].join('\n'))
  assert.equal(records.incomplete, true)
  assert.equal(records.holdFrom, '')
})

test('Codex malformed line is held, not skipped silently, and later rows are still read', () => {
  const records = sweep.parseCodexRecords([
    CODEX_SOL,
    codexUsageRow('2026-10-23T12:00:00Z', 100, 10),
    '{"truncated":',
    codexUsageRow('2026-10-24T12:00:00Z', 150, 15),
  ].join('\n'))
  assert.equal(records.incomplete, true)
  assert.equal(records.holdFrom, '2026-10-23')
  assert.equal(records.length, 2)
})

test('Codex increments combine per UTC date and model', () => {
  const records = sweep.parseCodexRecords([
    CODEX_SOL,
    codexUsageRow('2026-10-23T01:00:00Z', 100, 10),
    codexUsageRow('2026-10-23T02:00:00Z', 160, 16),
    codexUsageRow('2026-10-23T03:00:00Z', 200, 20),
  ].join('\n'))
  assert.equal(records.length, 1)
  assert.equal(records[0].cum.input, 200)
  assert.equal(records[0].cum.output, 20)
  assert.equal(records[0].occurredAtUtc, '2026-10-23T03:00:00Z')
})

test('buildDailySnapshots attributes an array-level incomplete flag to its own tool', () => {
  const records = sweep.parseCodexRecords([CODEX_SOL, codexUsageRow('2026-10-25T12:00:00Z', -1, 0)].join('\n'))
  const snapshots = buildDailySnapshots(records, TEST_MACHINE)
  assert.equal(snapshots.holds.has(`codex-local@${TEST_MACHINE}`), true)
  assert.equal(snapshots.holds.has(`claude-local@${TEST_MACHINE}`), false)
})

test('Claude invalid counters hold only the key of that record day', () => {
  const content = [
    claudePromptRow(COMPLETE_CLAUDE_PROMPT, { id: 'm1' }, '2026-10-22T12:00:00Z'),
    claudePromptRow({ ...COMPLETE_CLAUDE_PROMPT, input_tokens: -1 }, { id: 'm2' }, '2026-10-23T12:00:00Z'),
    claudePromptRow(COMPLETE_CLAUDE_PROMPT, { id: 'm3' }, '2026-10-24T12:00:00Z'),
  ].join('\n')
  const snapshots = buildDailySnapshots(parseClaude(content), TEST_MACHINE)
  assert.deepEqual(snapshots.map(snapshot => snapshot.eventKey.split(':')[1]), ['2026-10-22', '2026-10-24'])
  assert.deepEqual(snapshots.holds.get(`claude-local@${TEST_MACHINE}`), [{ from: '2026-10-23', through: '2026-10-23' }])
})

test('Claude truncated row holds from the preceding valid row day', () => {
  const records = parseClaude([
    claudePromptRow(COMPLETE_CLAUDE_PROMPT, { id: 'm1' }, '2026-10-22T12:00:00Z'),
    '{"type":"assistant","message":',
  ].join('\n'))
  assert.equal(records.incomplete, true)
  assert.equal(records.holdFrom, '2026-10-22')
  assert.equal(parseClaude('{"type":"assistant"').holdFrom, '')
})

test('planSnapshotSubmissions applies per-key holds to submissions and tombstones', () => {
  const snapshot = (day, sourceId = 'claude-local@m') => ({
    sourceId, eventKey: `claude:${day}:m:u:u:u`, runtime: 'claude', inputTokens: 1,
  })
  const snapshots = [snapshot('2026-10-22'), snapshot('2026-10-24')]
  const inventory = [snapshot('2026-10-20'), snapshot('2026-10-23'), snapshot('2026-10-24')]
  const holds = new Map([['claude-local@m', [{ from: '2026-10-23', through: undefined }]]])
  const plan = planSnapshotSubmissions(snapshots, inventory, holds, new Map([['claude-local@m', '2026-10-20']]))
  assert.deepEqual(
    plan.map(item => `${item.active ? 'post' : 'tombstone'} ${item.snapshot.eventKey.split(':')[1]}`),
    ['post 2026-10-22', 'tombstone 2026-10-20'],
  )
})

test('a hold range releases the days after its through-day', () => {
  const snapshot = day => ({ sourceId: 'claude-local@m', eventKey: `claude:${day}:m:u:u:u`, runtime: 'claude', inputTokens: 1 })
  const holds = new Map([['claude-local@m', [{ from: '', through: '2026-10-22' }, { from: '2026-10-24', through: '2026-10-24' }]]])
  const plan = planSnapshotSubmissions(['2026-10-22', '2026-10-23', '2026-10-24', '2026-10-25'].map(snapshot), [], holds)
  assert.deepEqual(plan.map(item => item.snapshot.eventKey.split(':')[1]), ['2026-10-23', '2026-10-25'])
})

test('updateFileCache reports held files, the earliest hold and counted cache reuse', async () => {
  const files = [
    { path: 'a', mtimeMs: 2 }, { path: 'b', mtimeMs: 2 }, { path: 'c', mtimeMs: 2 },
  ]
  const cache = {
    a: { mtimeMs: 1, records: [], incomplete: true, holdFrom: '2026-10-24' },
    c: { mtimeMs: 1, records: [{ x: 1 }] },
  }
  const lines = []
  const original = console.error
  console.error = line => lines.push(String(line))
  let result
  try {
    result = await updateFileCache(files, cache, (content) => {
      const parsed = []
      if (content === 'a') { parsed.incomplete = true; parsed.holdFrom = '2026-10-26' }
      return parsed
    }, async path => { if (path === 'c') { throw new Error('locked') } return path })
  } finally { console.error = original }
  assert.equal(result.incomplete, true)
  // mtimeMs 2 is 1970-01-01, so the range is extended to the hold day itself rather than released.
  assert.deepEqual(result.holds, [{ from: '2026-10-26', through: '2026-10-26' }])
  assert.equal(result.heldFiles, 1)
  assert.equal(result.reusedFiles, 1)
  assert.equal(lines.some(line => /reusing cached records/.test(line)), true)
})

test('scanRecords exposes per-tool hold dates and held file counts', async () => {
  const root = await makeTemp('observatory-sweep-holds-')
  const sessions = join(root, 'codex', 'sessions')
  await mkdir(sessions, { recursive: true })
  await writeFile(join(sessions, 'a.jsonl'), [
    CODEX_SOL,
    codexUsageRow('2026-10-23T12:00:00Z', 100, 10),
    codexUsageRow('2026-10-25T12:00:00Z', 50, 5),
  ].join('\n'))
  try {
    const cfg = { codexHome: join(root, 'codex'), copilotHome: join(root, 'c'), claudeHome: join(root, 'cl'), kimiHome: join(root, 'k'), geminiHome: join(root, 'g') }
    const scan = await scanRecords(cfg, {}, new Set(['codex']))
    assert.deepEqual(scan.holds.get('codex').map(hold => hold.from), ['2026-10-23'])
    assert.equal(scan.heldFiles.get('codex'), 1)
  } finally { await removeTemp(root) }
})

test('postSweepStatus carries the held file count', async () => {
  let body
  const server = createServer(async (request, response) => {
    let text = ''
    for await (const chunk of request) { text += chunk }
    body = JSON.parse(text)
    response.writeHead(200).end('{}')
  })
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve))
  try {
    await sweep.postSweepStatus(`http://127.0.0.1:${server.address().port}`, 'k', 'm', [], 3)
    assert.equal(body.heldFiles, 3)
  } finally { await new Promise(resolve => server.close(resolve)) }
})

test('one bad Codex row holds later days while earlier days keep posting (R-F3 through the scan pipeline)', async () => {
  const root = await makeTemp('observatory-sweep-perkey-')
  const sessions = join(root, 'codex', 'sessions')
  await mkdir(sessions, { recursive: true })
  await writeFile(join(sessions, 'a.jsonl'), [
    CODEX_SOL,
    codexUsageRow('2026-10-22T12:00:00Z', 100, 10),
    codexUsageRow('2026-10-23T12:00:00Z', 150, 15),
    codexUsageRow('2026-10-24T12:00:00Z', 90, 9),
  ].join('\n'))
  try {
    const cfg = { codexHome: join(root, 'codex'), copilotHome: join(root, 'c'), claudeHome: join(root, 'cl'), kimiHome: join(root, 'k'), geminiHome: join(root, 'g') }
    const scan = await scanRecords(cfg, {}, new Set(['codex']))
    const snapshots = buildDailySnapshots(scan.records, TEST_MACHINE)
    const holds = sweep.mergeHolds(
      new Map([...scan.holds].map(([tool, ranges]) => [`${tool}-local@${TEST_MACHINE}`, ranges])),
      snapshots.holds,
    )
    const plan = planSnapshotSubmissions(snapshots, [], holds)
    assert.deepEqual(plan.map(item => item.snapshot.eventKey.split(':')[1]), ['2026-10-22'])
  } finally { await removeTemp(root) }
})

// --- Remediation batch69, lane S (C3 findings) -------------------------------

function plannerRow(fields) {
  return JSON.stringify({
    step_index: 1, source: 'MODEL', type: 'PLANNER_RESPONSE', status: 'DONE',
    created_at: '2026-08-24T12:00:00Z', ...fields,
  })
}

async function agyDatabase(root, payloads) {
  const dbPath = join(root, 'session-id.db')
  const db = new DatabaseSync(dbPath)
  db.exec('CREATE TABLE steps (idx INTEGER PRIMARY KEY, step_type INTEGER, step_payload BLOB)')
  const insert = db.prepare('INSERT INTO steps VALUES (?, ?, ?)')
  payloads.forEach((payload, index) => insert.run(index + 1, 23, payload))
  db.close()
  return dbPath
}

const UNDECODABLE_STEP = Buffer.from([0x2a, 0x02, 0x08, 0x01])

// C3-C1
test('Antigravity step rows that decode to nothing hold the conversation day instead of caching a clean empty', async () => {
  const root = await makeTemp('observatory-agy-undecodable-')
  try {
    const dbPath = await agyDatabase(root, [UNDECODABLE_STEP])
    const records = await sweep.parseAntigravityDatabase(
      dbPath, JSON.stringify({ created_at: '2026-08-24T12:00:00Z', content: 'hello' }), () => {},
    )
    assert.equal(records.incomplete, true)
    assert.equal(records.holdFrom, '2026-08-24')
  } finally { await removeTemp(root) }
})

// C3-C1
test('Antigravity rejected rows hold the conversation day while valid totals are kept', async () => {
  const root = await makeTemp('observatory-agy-partial-')
  try {
    const dbPath = await agyDatabase(root, [antigravityPayload(100, 20, 5), antigravityPayload(100_000_000, 20, 5)])
    const records = await sweep.parseAntigravityDatabase(
      dbPath, JSON.stringify({ created_at: '2026-08-24T12:00:00Z', content: 'hello' }), () => {},
    )
    assert.equal(records.length, 1)
    assert.equal(records.incomplete, true)
    assert.equal(records.holdFrom, '2026-08-24')
  } finally { await removeTemp(root) }
})

// C3-C1
test('an undecodable Antigravity conversation is not a verified-empty source', async () => {
  const root = await makeTemp('observatory-agy-undecodable-scan-')
  try {
    const { cfg } = await antigravityFixture(root)
    const db = new DatabaseSync(join(cfg.geminiHome, 'antigravity-cli', 'conversations', 'agy-session.db'))
    db.exec('DELETE FROM steps')
    db.prepare('INSERT INTO steps VALUES (?, ?, ?)').run(1, 23, UNDECODABLE_STEP)
    db.close()
    const scan = await scanRecords(cfg, {}, new Set(['antigravity']))
    assert.equal(scan.incompleteSources.has('antigravity'), true)
    assert.equal(scan.emptySources.has('antigravity'), false)
    assert.deepEqual(scan.holds.get('antigravity').map(hold => hold.from), ['2026-08-24'])
  } finally { await removeTemp(root) }
})

// C3-C1 + C3-C2
test('Antigravity takes model, tokens and cache reads from the transcript PLANNER_RESPONSE rows', async () => {
  const root = await makeTemp('observatory-agy-planner-')
  try {
    const dbPath = await agyDatabase(root, [UNDECODABLE_STEP])
    const transcript = [
      JSON.stringify({ created_at: '2026-08-24T11:00:00Z', content: 'The user changed setting `Model Selection` from None to Gemini 3.8 Flash (High).' }),
      plannerRow({ created_at: '2026-08-24T12:00:00Z', model: 'gemini-3.8-flash-high', input_tokens: 100, cache_read_tokens: 4000, output_tokens: 20 }),
      plannerRow({ created_at: '2026-08-24T12:05:00Z', model: 'gemini-3.8-flash-high', input_tokens: 50, cache_read_tokens: 6000, output_tokens: 10 }),
    ].join('\n')
    const records = await sweep.parseAntigravityDatabase(dbPath, transcript, () => {})
    assert.equal(records.incomplete, undefined)
    assert.equal(records.length, 1)
    assert.equal(records[0].model, 'gemini-3.8-flash-high')
    assert.equal(records[0].inputTokens, 150)
    assert.equal(records[0].outputTokens, 30)
    assert.equal(records[0].cacheReadTokens, 10000)
    assert.equal(records[0].occurredAtUtc, '2026-08-24T12:05:00.000Z')
  } finally { await removeTemp(root) }
})

// C3-C2
test('a record whose model never resolved is counted as unplaced', () => {
  const unplaced = sweep.countUnplaced([
    { tool: 'antigravity', date: '2026-08-24', model: 'unknown', inputTokens: 1 },
  ], TEST_MACHINE)
  assert.deepEqual(unplaced, [{ tool: 'antigravity', model: 'unknown', count: 1 }])
})

// C3-P4
test('Antigravity settings sentence selects the destination model, not the source', async () => {
  const root = await makeTemp('observatory-agy-settings-')
  try {
    const dbPath = await agyDatabase(root, [antigravityPayload(10, 2)])
    const transcript = [
      JSON.stringify({ created_at: '2026-08-24T11:00:00Z', content: 'The user changed setting `Model Selection` from Gemini 3.1 Pro (High) to Gemini 2.5 Flash.' }),
      JSON.stringify({ created_at: '2026-08-24T11:30:00Z', content: 'Earlier I read: "changed setting `Model Selection` from None to Gemini 2.5 Pro" in the docs.' }),
    ].join('\n')
    const [record] = await sweep.parseAntigravityDatabase(dbPath, transcript, () => {})
    assert.equal(record.model, 'gemini-2.5-flash')
  } finally { await removeTemp(root) }
})

// C3-C8
test('Codex timestamps with up to nine fractional digits are accepted', () => {
  const content = [
    JSON.stringify({ type: 'turn_context', payload: { model: 'gpt-6-luna' } }),
    codexUsageRow('2026-10-01T00:00:00.123456789Z', 10, 5),
  ].join('\n')
  const records = sweep.parseCodexRecords(content)
  assert.equal(records.incomplete, undefined)
  assert.equal(records.length, 1)
})

// C3-C6
test('Claude deduplication keeps the copy that carries the native counters regardless of order', () => {
  const partial = claudePromptRow({ input_tokens: 10, output_tokens: 5 }, { id: 'dup', model: 'claude-opus-5' }, '2026-10-22T00:00:00Z')
  const complete = claudePromptRow(
    { input_tokens: 10, output_tokens: 5, cache_read_input_tokens: 500, cache_creation_input_tokens: 7 },
    { id: 'dup', model: 'claude-opus-5' }, '2026-10-22T00:00:05Z',
  )
  for (const content of [`${partial}\n${complete}`, `${complete}\n${partial}`]) {
    const [snapshot] = buildDailySnapshots(parseClaude(content), TEST_MACHINE)
    assert.equal(snapshot.cacheReadTokens, 500)
    assert.equal(snapshot.cacheWriteTokens, 7)
  }
})

test('Claude deduplication never replaces an observed counter with the other copy zero default', () => {
  const earlier = claudePromptRow({ input_tokens: 10, output_tokens: 5 }, { id: 'dup', model: 'claude-opus-5' }, '2026-10-22T00:00:00Z')
  const later = claudePromptRow(
    { input_tokens: 10, cache_read_input_tokens: 500, cache_creation_input_tokens: 7 },
    { id: 'dup', model: 'claude-opus-5' }, '2026-10-22T00:00:05Z',
  )
  for (const content of [`${earlier}\n${later}`, `${later}\n${earlier}`]) {
    const [snapshot] = buildDailySnapshots(parseClaude(content), TEST_MACHINE)
    assert.equal(snapshot.outputTokens, 5)
    assert.equal(snapshot.cacheReadTokens, 500)
    assert.equal(snapshot.cacheWriteTokens, 7)
  }
})

// S1
test('Claude deduplication takes the cache-write total and its split from the same copy', () => {
  const zeroed = claudePromptRow(
    { input_tokens: 10, output_tokens: 5, service_tier: 'standard', speed: 'standard', inference_geo: 'us', cache_creation: { ephemeral_5m_input_tokens: 0, ephemeral_1h_input_tokens: 0 } },
    { id: 'dup', model: 'claude-opus-5' }, '2026-10-22T00:00:00Z',
  )
  const written = claudePromptRow(
    { input_tokens: 10, output_tokens: 5, cache_creation_input_tokens: 7, cache_creation: { ephemeral_5m_input_tokens: 7, ephemeral_1h_input_tokens: 0 } },
    { id: 'dup', model: 'claude-opus-5' }, '2026-10-22T00:00:05Z',
  )
  for (const content of [`${zeroed}\n${written}`, `${written}\n${zeroed}`]) {
    const [snapshot] = buildDailySnapshots(parseClaude(content), TEST_MACHINE)
    assert.equal(snapshot.cacheWriteTokens, 7)
    assert.deepEqual(JSON.parse(snapshot.rawPayload).cache_creation, { ephemeral_5m_input_tokens: 7, ephemeral_1h_input_tokens: 0 })
  }
})

// S2
test('Antigravity holds the day when a planner meter is malformed even though every step row decodes', async () => {
  const root = await makeTemp('observatory-agy-invalid-planner-')
  try {
    const dbPath = await agyDatabase(root, [antigravityPayload(100, 20, 5)])
    const transcript = plannerRow({ model: 'gemini-3.8-flash-high', input_tokens: 'abc', cache_read_tokens: 4000, output_tokens: 20 })
    const records = await sweep.parseAntigravityDatabase(dbPath, transcript, () => {})
    assert.equal(records.incomplete, true)
    assert.equal(records.holdFrom, '2026-08-24')
  } finally { await removeTemp(root) }
})

// C3-C7
test('five parsers hold the day of a row whose counter is present but invalid', () => {
  const cases = [
    ['kimi', sweep.parseKimi, JSON.stringify({ type: 'usage.record', time: '2026-10-22T00:00:00Z', model: 'm', usage: { inputOther: -5, output: 1 } })],
    ['grok', sweep.parseGrok, JSON.stringify({ turns: [{ endedAt: '2026-10-22T00:00:00Z', modelUsage: { g: { inputTokens: 1.5, outputTokens: 1 } } }] })],
    ['pi', sweep.parsePi, JSON.stringify({ type: 'message', timestamp: '2026-10-22T00:00:00Z', message: { role: 'assistant', model: 'meta/x', usage: { input: 'abc', output: 1 } } })],
    ['gemini-review', sweep.parseGeminiReview, JSON.stringify({ type: 'gemini', timestamp: '2026-10-22T00:00:00Z', model: 'g', tokens: { input: 1, output: -2 } })],
  ]
  for (const [name, parse, content] of cases) {
    const records = parse(content)
    assert.equal(records.incomplete, true, name)
    assert.equal(records.holdFrom, '2026-10-22', name)
  }
  const copilot = sweep.parseCopilot(JSON.stringify({
    type: 'session.shutdown', timestamp: '2026-10-22T00:00:00Z',
    data: { modelMetrics: { m: { usage: { inputTokens: 1, outputTokens: 1, cacheReadTokens: -1 } } } },
  }))
  assert.equal(copilot.incomplete, true)
})

// C3-P8
test('a Grok usage.json that does not parse holds the whole source', () => {
  const records = parseGrok('not json')
  assert.deepEqual([...records], [])
  assert.equal(records.incomplete, true)
  assert.equal(records.holdFrom, '')
})

// C3-C5
test('a Gemini tmp root with no review directory is not a verified-empty source', async () => {
  const root = await makeTemp('observatory-gemini-root-')
  try {
    const geminiHome = join(root, 'gemini')
    await mkdir(join(geminiHome, 'tmp', 'unrelated-project'), { recursive: true })
    const cfg = { codexHome: join(root, 'c'), copilotHome: join(root, 'p'), claudeHome: join(root, 'cl'), kimiHome: join(root, 'k'), geminiHome }
    const scan = await scanRecords(cfg, {}, new Set(['gemini']))
    assert.equal(scan.emptySources.has('gemini'), false)
    await mkdir(join(geminiHome, 'tmp', 'gem-review-x', 'chats'), { recursive: true })
    const reviewed = await scanRecords(cfg, {}, new Set(['gemini']))
    assert.equal(reviewed.emptySources.has('gemini'), true)
  } finally { await removeTemp(root) }
})

// The sweep lock goes in the test's own root, keyed like the real one on server and machine.
const testLockDir = root => join(root, 'locks')
const testLockPath = (root, server) => sweep.sweepLockPath(`http://127.0.0.1:${server.address().port}`, 'test-machine', testLockDir(root))

async function runMainWith({ root, statePath, server, sources = 'codex', lockDir = testLockDir(root) }) {
  const envKeys = [
    'OBSERVATORY_URL', 'OBSERVATORY_API_KEY', 'OBSERVATORY_STATE', 'OBSERVATORY_LOCAL_SOURCES',
    'OBSERVATORY_MACHINE', 'CODEX_HOME', 'COPILOT_HOME', 'CLAUDE_HOME', 'KIMI_HOME', 'GEMINI_HOME',
  ]
  const prior = Object.fromEntries(envKeys.map(key => [key, process.env[key]]))
  Object.assign(process.env, {
    OBSERVATORY_URL: `http://127.0.0.1:${server.address().port}`, OBSERVATORY_API_KEY: 'test-key',
    OBSERVATORY_STATE: statePath, OBSERVATORY_LOCAL_SOURCES: sources, OBSERVATORY_MACHINE: 'test-machine',
    CODEX_HOME: join(root, 'codex'), COPILOT_HOME: join(root, 'copilot'), CLAUDE_HOME: join(root, 'claude'),
    KIMI_HOME: join(root, 'kimi'), GEMINI_HOME: join(root, 'gemini'),
  })
  const errors = []
  const realError = console.error
  console.error = (...args) => { errors.push(args.join(' ')) }
  try { await sweep.main({ lockDir }) }
  finally {
    console.error = realError
    for (const key of envKeys) {
      if (prior[key] === undefined) { delete process.env[key] } else { process.env[key] = prior[key] }
    }
  }
  return errors
}

// The token figures an inventory row carries for the legacy migration's coverage check. They match
// writeCodexRollout's one row (input 10, output 5), so its day's legacy key is covered by default.
const INVENTORY_FIGURES = {
  inputTokens: 10, outputTokens: 5, cacheReadTokens: 0, cacheWriteTokens: 0, cacheWrite1hTokens: 0, thoughtTokens: 0, costUsd: null,
}

// Keys are event-key strings or `{ eventKey, ...figure overrides }`. `failPost` turns a POST body into a 500.
function recordingServer(legacyKeys = [], machineKeys = [], { failPost = () => false } = {}) {
  const posts = []
  const statuses = []
  const gets = []
  const server = createServer(async (request, response) => {
    const url = new URL(request.url, 'http://127.0.0.1')
    if (request.method === 'GET' && url.pathname === '/api/events/local-snapshots') {
      const sourceId = url.searchParams.get('sourceId')
      gets.push(sourceId)
      // Every bare (legacy) id serves legacyKeys; every test-machine id serves machineKeys.
      const keys = !sourceId.includes('@') ? legacyKeys : sourceId.endsWith('@test-machine') ? machineKeys : []
      const body = keys.map(key => (typeof key === 'string' ? { eventKey: key } : key)).map(({ eventKey, ...figures }) => ({
        provider: 'openai', occurredAtUtc: '2026-08-10T12:00:00Z', model: eventKey.split(':')[2], ...INVENTORY_FIGURES,
        ...figures, runtime: 'codex', sourceId,
        sourceKind: 'localTelemetry', usageScope: 'subscription', costBasis: 'notional', eventKey,
      }))
      response.writeHead(200, { 'Content-Type': 'application/json' }).end(JSON.stringify(body))
      return
    }
    if (request.method === 'POST' && (url.pathname === '/api/events' || url.pathname === '/api/sources/sweep-status')) {
      let body = ''
      for await (const chunk of request) { body += chunk }
      const parsed = JSON.parse(body)
      ;(url.pathname === '/api/events' ? posts : statuses).push(parsed)
      response.writeHead(failPost(parsed) ? 500 : 200, { 'Content-Type': 'application/json', 'Retry-After': '0' }).end('{}')
      return
    }
    response.writeHead(200).end('{}')
  })
  return { server, posts, statuses, gets }
}

// C3-P1
test('main does not zero the legacy namespace for a source that still holds days', async () => {
  const root = await makeTemp('observatory-legacy-held-')
  const sessions = join(root, 'codex', 'sessions')
  await mkdir(sessions, { recursive: true })
  await writeFile(join(sessions, 'rollout.jsonl'), [
    JSON.stringify({ type: 'turn_context', payload: { model: 'gpt-5.5' } }),
    codexUsageRow('2026-08-23T12:00:00Z', 10, 5),
    codexUsageRow('2026-08-24T12:00:00Z', 20, 8),
    codexUsageRow('2026-08-25T12:00:00Z', 5, 8),
  ].join('\n'))
  const { server, posts } = recordingServer(['codex:2026-08-24:gpt-5.5'])
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve))
  const statePath = join(root, 'state', 'sweep.json')
  try {
    await runMainWith({ root, statePath, server })
    assert.equal(posts.some(body => body.sourceId === 'codex-local@test-machine' && body.eventKey === 'codex:2026-08-23:gpt-5.5'), true)
    assert.deepEqual(posts.filter(body => body.sourceId === 'codex-local'), [])
    const state = JSON.parse(await readFile(statePath, 'utf8'))
    assert.equal(state.legacySourceMigration?.codex, undefined)
  } finally {
    await new Promise(resolve => server.close(resolve))
    await removeTemp(root)
  }
})

async function writeCodexRollout(root) {
  const sessions = join(root, 'codex', 'sessions')
  await mkdir(sessions, { recursive: true })
  await writeFile(join(sessions, 'rollout.jsonl'), [
    JSON.stringify({ type: 'turn_context', payload: { model: 'gpt-5.5' } }),
    codexUsageRow('2026-08-24T12:00:00Z', 10, 5),
  ].join('\n'))
}

// C3-P3
test('a failed state save is reported but does not stop the sweep from posting', async () => {
  const root = await makeTemp('observatory-state-save-fail-')
  await writeCodexRollout(root)
  const blocker = join(root, 'blocker')
  await writeFile(blocker, 'not a directory')
  const { server, posts } = recordingServer()
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve))
  try {
    const errors = await runMainWith({ root, statePath: join(blocker, 'sweep.json'), server })
    assert.equal(posts.some(body => body.sourceId === 'codex-local@test-machine'), true)
    assert.equal(errors.some(line => /state file/i.test(line)), true)
  } finally {
    await new Promise(resolve => server.close(resolve))
    await removeTemp(root)
  }
})

// C3-P3
test('an unparseable state file is reported rather than silently treated as empty', async () => {
  const root = await makeTemp('observatory-state-corrupt-')
  await writeCodexRollout(root)
  const statePath = join(root, 'state', 'sweep.json')
  await mkdir(join(root, 'state'), { recursive: true })
  await writeFile(statePath, '{"files": ')
  const { server } = recordingServer()
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve))
  try {
    const errors = await runMainWith({ root, statePath, server })
    assert.equal(errors.some(line => /state file/i.test(line) && /unreadable|parse|corrupt/i.test(line)), true)
  } finally {
    await new Promise(resolve => server.close(resolve))
    await removeTemp(root)
  }
})

// --- Remediation batch70, lane S --------------------------------------------

const CODEX_GPT55 = JSON.stringify({ type: 'turn_context', payload: { model: 'gpt-5.5' } })

// A finished Codex file whose counter regresses on 2026-10-01 and was last written that day.
async function writeHeldFinishedCodex(sessions) {
  const finished = join(sessions, 'finished.jsonl')
  await writeFile(finished, [
    CODEX_GPT55,
    codexUsageRow('2026-10-01T12:00:00Z', 100, 10),
    codexUsageRow('2026-10-01T13:00:00Z', 50, 5),
  ].join('\n'))
  await utimes(finished, new Date('2026-10-01T20:00:00Z'), new Date('2026-10-01T20:00:00Z'))
}

async function withRecordingServer(recording, run) {
  await new Promise(resolve => recording.server.listen(0, '127.0.0.1', resolve))
  try { await run() } finally { await new Promise(resolve => recording.server.close(resolve)) }
}

const machinePosts = (posts, sourceId = 'codex-local@test-machine') => posts.filter(body => body.sourceId === sourceId)
const isTombstone = body => JSON.parse(body.rawPayload).tombstone === true

// C3-C3
test('a held finished file stops holding at its own last-write day, parsed or cached', async () => {
  const root = await makeTemp('observatory-hold-range-')
  const sessions = join(root, 'codex', 'sessions')
  await mkdir(sessions, { recursive: true })
  await writeHeldFinishedCodex(sessions)
  await writeFile(join(sessions, 'later.jsonl'), [CODEX_GPT55, codexUsageRow('2026-10-05T12:00:00Z', 10, 5)].join('\n'))
  const recording = recordingServer()
  const statePath = join(root, 'state', 'sweep.json')
  try {
    await withRecordingServer(recording, async () => {
      for (const run of ['parsed', 'cached']) {
        recording.posts.length = 0
        await runMainWith({ root, statePath, server: recording.server })
        assert.deepEqual(machinePosts(recording.posts).map(body => body.eventKey), ['codex:2026-10-05:gpt-5.5'], run)
      }
    })
  } finally { await removeTemp(root) }
})

// C3-C3
test('a file hold never ends before the latest day the file records, whatever its timestamp says', async () => {
  const result = await updateFileCache([{ path: 'a', mtimeMs: Date.parse('2026-01-01T00:00:00Z') }], {}, () => {
    const parsed = [{ tool: 'codex', date: '2026-10-03' }]
    parsed.incomplete = true
    parsed.holdFrom = '2026-10-02'
    return parsed
  }, async path => path)
  assert.deepEqual(result.holds, [{ from: '2026-10-02', through: '2026-10-03' }])
})


// An orphan Antigravity database last written on 2026-08-24 (no transcript), beside a healthy
// conversation on 2026-08-30. Returns the orphan's path.
async function writeAgyOrphanAndLater(root) {
  const { brain } = await antigravityFixture(root)
  await removeTemp(brain)
  const conversations = join(root, 'gemini', 'antigravity-cli', 'conversations')
  const orphan = join(conversations, 'agy-session.db')
  await utimes(orphan, new Date('2026-08-24T20:00:00Z'), new Date('2026-08-24T20:00:00Z'))
  const laterLogs = join(brain, 'agy-later', '.system_generated', 'logs')
  await mkdir(laterLogs, { recursive: true })
  await writeFile(join(laterLogs, 'transcript.jsonl'), JSON.stringify({
    created_at: '2026-08-30T12:00:00Z', content: 'Model Selection` from None to Gemini 3.1 Pro (High).',
  }))
  const db = new DatabaseSync(join(conversations, 'agy-later.db'))
  db.exec('CREATE TABLE steps (idx INTEGER PRIMARY KEY, step_type INTEGER, step_payload BLOB)')
  db.prepare('INSERT INTO steps VALUES (?, ?, ?)').run(1, 23, antigravityPayload(50, 10))
  db.close()
  return orphan
}

// C3-P5
test('an orphan Antigravity database caches its hold, which ends at the database last-write day', async () => {
  const root = await makeTemp('observatory-agy-orphan-')
  try {
    const orphan = await writeAgyOrphanAndLater(root)
    const recording = recordingServer()
    const statePath = join(root, 'state', 'sweep.json')
    await withRecordingServer(recording, async () => {
      for (const run of ['first', 'second']) {
        recording.posts.length = 0
        await runMainWith({ root, statePath, server: recording.server, sources: 'antigravity' })
        assert.deepEqual(
          machinePosts(recording.posts, 'antigravity-local@test-machine').map(body => body.eventKey.split(':')[1]),
          ['2026-08-30'], run,
        )
        const cached = JSON.parse(await readFile(statePath, 'utf8')).files.antigravity[orphan]
        assert.equal(cached?.incomplete, true, `${run}: the orphan outcome is cached`)
      }
    })
  } finally { await removeTemp(root) }
})

// C3-P7
test('a held scan still posts its sweep status, carrying the held file count', async () => {
  const root = await makeTemp('observatory-status-held-')
  const sessions = join(root, 'codex', 'sessions')
  await mkdir(sessions, { recursive: true })
  await writeHeldFinishedCodex(sessions)
  const recording = recordingServer()
  try {
    await withRecordingServer(recording, async () => {
      await runMainWith({ root, statePath: join(root, 'state', 'sweep.json'), server: recording.server })
    })
    assert.deepEqual(recording.statuses.map(body => body.heldFiles), [1])
  } finally { await removeTemp(root) }
})

// C3-P7
test('a file whose Claude records carry invalid counters counts as held', async () => {
  const result = await updateFileCache(
    [{ path: 'a', mtimeMs: 1 }], {},
    () => [{ tool: 'claude', date: '2026-10-22', claudeInvalidCounters: true }],
    async path => path,
  )
  assert.equal(result.heldFiles, 1)
})

// C3-P2
test('main never zeroes a key older than the oldest day the local files still cover', async () => {
  const root = await makeTemp('observatory-freeze-old-')
  const sessions = join(root, 'codex', 'sessions')
  await mkdir(sessions, { recursive: true })
  await writeFile(join(sessions, 'rollout.jsonl'), [CODEX_GPT55, codexUsageRow('2026-10-10T12:00:00Z', 10, 5)].join('\n'))
  const recording = recordingServer([], ['codex:2026-10-01:gpt-5.5', 'codex:2026-10-12:gpt-5.5'])
  try {
    await withRecordingServer(recording, async () => {
      await runMainWith({ root, statePath: join(root, 'state', 'sweep.json'), server: recording.server })
    })
    assert.deepEqual(machinePosts(recording.posts).filter(isTombstone).map(body => body.eventKey), ['codex:2026-10-12:gpt-5.5'])
  } finally { await removeTemp(root) }
})

// C3-C4
test('main zeroes only the legacy keys this run replaced for the same day and model', async () => {
  const root = await makeTemp('observatory-legacy-replaced-')
  await writeCodexRollout(root)
  const recording = recordingServer(['codex:2026-08-24:gpt-5.5', 'codex:2026-08-10:gpt-5.5', 'codex:2026-08-24:gpt-5.4'])
  const statePath = join(root, 'state', 'sweep.json')
  try {
    await withRecordingServer(recording, async () => {
      await runMainWith({ root, statePath, server: recording.server })
    })
    assert.deepEqual(machinePosts(recording.posts, 'codex-local').map(body => body.eventKey), ['codex:2026-08-24:gpt-5.5'])
    const state = JSON.parse(await readFile(statePath, 'utf8'))
    assert.equal(typeof state.legacySourceMigration.codex, 'string')
  } finally { await removeTemp(root) }
})

// --- Remediation batch70, lane S: composition review ------------------------

const legacyZeroes = posts => machinePosts(posts, 'codex-local').map(body => body.eventKey)

// Q1
test('a legacy key replaced in an earlier run is still zeroed after its local file ages out', async () => {
  const root = await makeTemp('observatory-legacy-replay-')
  await writeCodexRollout(root)
  const machineKeys = []
  let failLegacy = true
  const recording = recordingServer(['codex:2026-08-24:gpt-5.5'], machineKeys, {
    failPost: body => failLegacy && body.sourceId === 'codex-local',
  })
  const statePath = join(root, 'state', 'sweep.json')
  try {
    await withRecordingServer(recording, async () => {
      await runMainWith({ root, statePath, server: recording.server })
      assert.equal(JSON.parse(await readFile(statePath, 'utf8')).legacySourceMigration?.codex, undefined, 'run 1: the legacy zero failed')
      // Between runs the per-machine key is on the server and retention deletes its file.
      machineKeys.push('codex:2026-08-24:gpt-5.5')
      const sessions = join(root, 'codex', 'sessions')
      await rm(join(sessions, 'rollout.jsonl'))
      await writeFile(join(sessions, 'later.jsonl'), [CODEX_GPT55, codexUsageRow('2026-08-30T12:00:00Z', 10, 5)].join('\n'))
      failLegacy = false
      recording.posts.length = 0
      await runMainWith({ root, statePath, server: recording.server })
    })
    assert.deepEqual(legacyZeroes(recording.posts), ['codex:2026-08-24:gpt-5.5'])
    assert.equal(typeof JSON.parse(await readFile(statePath, 'utf8')).legacySourceMigration.codex, 'string')
  } finally { await removeTemp(root) }
})

// Q3b
test('a legacy key is zeroed only when this machine covers every figure, a null legacy counter reading as 0', async () => {
  const root = await makeTemp('observatory-legacy-covered-')
  const sessions = join(root, 'codex', 'sessions')
  await mkdir(sessions, { recursive: true })
  await writeFile(join(sessions, 'rollout.jsonl'), [
    CODEX_GPT55,
    codexUsageRow('2026-08-24T12:00:00Z', 10, 5),
    codexUsageRow('2026-08-25T12:00:00Z', 20, 10),
  ].join('\n'))
  const statePath = join(root, 'state', 'sweep.json')
  try {
    // 08-24: another machine's usage shares the legacy key (input 50 > 10), so it is kept.
    const covered = recordingServer(['codex:2026-08-25:gpt-5.5', { eventKey: 'codex:2026-08-24:gpt-5.5', inputTokens: 50 }])
    await withRecordingServer(covered, async () => { await runMainWith({ root, statePath, server: covered.server }) })
    assert.deepEqual(legacyZeroes(covered.posts), ['codex:2026-08-25:gpt-5.5'])
    assert.equal(typeof JSON.parse(await readFile(statePath, 'utf8')).legacySourceMigration.codex, 'string')

    // Older clients posted null for a counter the vendor did not report: a present-null token
    // figure reads as 0, so a row covered on every other figure is zeroed.
    await writeFile(statePath, '{}')
    const nullCounter = recordingServer([{ eventKey: 'codex:2026-08-25:gpt-5.5', cacheReadTokens: null }])
    await withRecordingServer(nullCounter, async () => { await runMainWith({ root, statePath, server: nullCounter.server }) })
    assert.deepEqual(legacyZeroes(nullCounter.posts), ['codex:2026-08-25:gpt-5.5'])
    assert.equal(typeof JSON.parse(await readFile(statePath, 'utf8')).legacySourceMigration.codex, 'string')
  } finally { await removeTemp(root) }
})

// An older server lists no figure properties at all: nothing can be compared yet.
test('legacy rows from a server that lists no figures leave the migration pending', async () => {
  const root = await makeTemp('observatory-legacy-old-server-')
  await writeCodexRollout(root)
  const noFigures = Object.fromEntries(Object.keys(INVENTORY_FIGURES).map(field => [field, undefined]))
  const recording = recordingServer([{ eventKey: 'codex:2026-08-24:gpt-5.5', ...noFigures, hasCost: false }])
  const statePath = join(root, 'state', 'sweep.json')
  try {
    await withRecordingServer(recording, async () => { await runMainWith({ root, statePath, server: recording.server }) })
    assert.deepEqual(legacyZeroes(recording.posts), [])
    assert.equal(JSON.parse(await readFile(statePath, 'utf8')).legacySourceMigration?.codex, undefined)
  } finally { await removeTemp(root) }
})

test('a legacy row with a cost but no costUsd property waits, and one with a null costUsd is kept for good', async () => {
  const root = await makeTemp('observatory-legacy-cost-shape-')
  await writeCodexRollout(root)
  const statePath = join(root, 'state', 'sweep.json')
  try {
    const absent = recordingServer([{ eventKey: 'codex:2026-08-24:gpt-5.5', costUsd: undefined, hasCost: true }])
    await withRecordingServer(absent, async () => { await runMainWith({ root, statePath, server: absent.server }) })
    assert.equal(JSON.parse(await readFile(statePath, 'utf8')).legacySourceMigration?.codex, undefined)
    const nulled = recordingServer([{ eventKey: 'codex:2026-08-24:gpt-5.5', costUsd: null, hasCost: true }])
    await withRecordingServer(nulled, async () => { await runMainWith({ root, statePath, server: nulled.server }) })
    assert.deepEqual(legacyZeroes(nulled.posts), [])
    assert.equal(typeof JSON.parse(await readFile(statePath, 'utf8')).legacySourceMigration.codex, 'string')
  } finally { await removeTemp(root) }
})

// Composition re-review, non-convergence: only this machine's side can still change.
test('a legacy key whose per-machine counterpart lacks a figure is kept and the migration stays pending', async () => {
  const root = await makeTemp('observatory-legacy-mine-unknown-')
  const sessions = join(root, 'codex', 'sessions')
  await mkdir(sessions, { recursive: true })
  // 08-24 survives only on the server, as a row without its cache-read figure; local disk covers 08-30.
  await writeFile(join(sessions, 'later.jsonl'), [CODEX_GPT55, codexUsageRow('2026-08-30T12:00:00Z', 10, 5)].join('\n'))
  const recording = recordingServer(['codex:2026-08-24:gpt-5.5'], [{ eventKey: 'codex:2026-08-24:gpt-5.5', cacheReadTokens: undefined }])
  const statePath = join(root, 'state', 'sweep.json')
  try {
    await withRecordingServer(recording, async () => { await runMainWith({ root, statePath, server: recording.server }) })
    assert.deepEqual(legacyZeroes(recording.posts), [])
    assert.equal(JSON.parse(await readFile(statePath, 'utf8')).legacySourceMigration?.codex, undefined)
  } finally { await removeTemp(root) }
})

// Two Claude messages on 2026-08-24 that differ only in service tier.
async function writeClaudeSiblings(root, tiers) {
  const projects = join(root, 'claude', 'projects')
  await mkdir(projects, { recursive: true })
  await writeFile(join(projects, 'session.jsonl'), tiers.map(([tier, input, output], index) => JSON.stringify({
    type: 'assistant', timestamp: '2026-08-24T12:00:00Z',
    message: { id: `msg-${index}`, model: 'claude-opus-5', usage: { input_tokens: input, output_tokens: output, service_tier: tier, speed: 'standard', inference_geo: 'us' } },
  })).join('\n'))
}

// Composition re-review, slot collision
test('a legacy key is never zeroed by a larger sibling key this machine posted for the same day and model', async () => {
  const root = await makeTemp('observatory-legacy-sibling-larger-')
  await writeClaudeSiblings(root, [['standard', 100, 50]])
  const recording = recordingServer(['claude:2026-08-24:claude-opus-5:priority:standard:us'])
  try {
    await withRecordingServer(recording, async () => {
      await runMainWith({ root, statePath: join(root, 'state', 'sweep.json'), server: recording.server, sources: 'claude' })
    })
    assert.equal(machinePosts(recording.posts, 'claude-local@test-machine').length, 1)
    assert.deepEqual(machinePosts(recording.posts, 'claude-local').map(body => body.eventKey), [])
  } finally { await removeTemp(root) }
})

// Composition re-review, slot collision
test('a legacy key is zeroed by its own per-machine key even when a smaller sibling posted after it', async () => {
  const root = await makeTemp('observatory-legacy-sibling-smaller-')
  await writeClaudeSiblings(root, [['priority', 100, 50], ['standard', 3, 1]])
  const legacyKey = 'claude:2026-08-24:claude-opus-5:priority:standard:us'
  const recording = recordingServer([legacyKey])
  try {
    await withRecordingServer(recording, async () => {
      await runMainWith({ root, statePath: join(root, 'state', 'sweep.json'), server: recording.server, sources: 'claude' })
    })
    assert.equal(machinePosts(recording.posts, 'claude-local@test-machine').length, 2)
    assert.deepEqual(machinePosts(recording.posts, 'claude-local').map(body => body.eventKey), [legacyKey])
  } finally { await removeTemp(root) }
})

// Q2/Q3a
test('the sweep status carries one attemptedAt per run, identical across retries', async () => {
  const bodies = []
  const server = createServer(async (request, response) => {
    let text = ''
    for await (const chunk of request) { text += chunk }
    bodies.push(JSON.parse(text))
    response.writeHead(bodies.length === 1 ? 500 : 200, { 'Retry-After': '0' }).end('{}')
  })
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve))
  try {
    await sweep.postSweepStatus(`http://127.0.0.1:${server.address().port}`, 'k', 'm', [], 0, '2026-11-22T10:00:00.000Z')
  } finally { await new Promise(resolve => server.close(resolve)) }
  assert.deepEqual(bodies.map(body => body.attemptedAt), ['2026-11-22T10:00:00.000Z', '2026-11-22T10:00:00.000Z'])
})

// Q2/Q3a
test('main sends the run start time as the status attemptedAt', async () => {
  const root = await makeTemp('observatory-status-attempted-')
  await writeCodexRollout(root)
  const recording = recordingServer()
  try {
    await withRecordingServer(recording, async () => {
      await runMainWith({ root, statePath: join(root, 'state', 'sweep.json'), server: recording.server })
    })
    assert.equal(recording.statuses.length, 1)
    assert.equal(recording.statuses[0].attemptedAt, recording.posts[0].observedAtUtc)
  } finally { await removeTemp(root) }
})

// Atomic state save
test('a state save that fails mid-write leaves the previous state file whole', async () => {
  const root = await makeTemp('observatory-state-atomic-')
  const statePath = join(root, 'sweep.json')
  await writeFile(statePath, '{"files":{"old":1}}')
  const tornWrite = async (path, text) => { await writeFile(path, text.slice(0, 5)); throw new Error('disk full') }
  const original = console.error
  console.error = () => {}
  try {
    await sweep.saveState(statePath, { files: { new: 2 } }, { mkdir, writeFile: tornWrite, rename, rm })
  } finally { console.error = original }
  try {
    assert.equal(await readFile(statePath, 'utf8'), '{"files":{"old":1}}')
    assert.deepEqual(await readdir(root), ['sweep.json'], 'no temp file is left behind')
    await sweep.saveState(statePath, { files: { new: 2 } })
    assert.deepEqual(JSON.parse(await readFile(statePath, 'utf8')), { files: { new: 2 } })
    assert.deepEqual(await readdir(root), ['sweep.json'])
  } finally { await removeTemp(root) }
})

// --- Remediation batch70, lane S: third composition pass ---------------------

// Q3
test('a refused sweep status prints its status and server message without --verbose', async () => {
  const server = createServer(async (request, response) => {
    for await (const chunk of request) { void chunk }
    response.writeHead(400, { 'Content-Type': 'text/plain' }).end('attemptedAt is more than five minutes in the future')
  })
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve))
  const lines = []
  const original = console.error
  console.error = (...args) => { lines.push(args.join(' ')) }
  try {
    await sweep.postSweepStatus(`http://127.0.0.1:${server.address().port}`, 'secret-key', 'm', [], 2, '2026-11-22T10:00:00.000Z')
  } finally {
    console.error = original
    await new Promise(resolve => server.close(resolve))
  }
  assert.equal(process.argv.includes('--verbose'), false)
  assert.equal(lines.some(line => /sweep-status POST 400/.test(line) && /more than five minutes in the future/.test(line)), true)
  assert.equal(lines.some(line => line.includes('secret-key')), false)
})
// Q5a
test('a cached orphan hold defers only its own days of the legacy migration', async () => {
  const root = await makeTemp('observatory-legacy-orphan-hold-')
  try {
    const orphan = await writeAgyOrphanAndLater(root)
    const coveredKey = 'antigravity:2026-08-30:gemini-3.1-pro-high'
    const heldKey = 'antigravity:2026-08-20:gemini-3.1-pro-high'
    const legacyKeys = [coveredKey, heldKey]
    const recording = recordingServer(legacyKeys)
    const statePath = join(root, 'state', 'sweep.json')
    const migrated = async () => JSON.parse(await readFile(statePath, 'utf8')).legacySourceMigration?.antigravity
    await withRecordingServer(recording, async () => {
      await runMainWith({ root, statePath, server: recording.server, sources: 'antigravity' })
      assert.deepEqual(machinePosts(recording.posts, 'antigravity-local').map(body => body.eventKey), [coveredKey])
      assert.equal(await migrated(), undefined, 'the held day is still pending')
      // The zeroed key leaves the server list; the orphan goes, so its day is decidable (and uncovered).
      legacyKeys.splice(0, 1)
      await removeTemp(orphan)
      recording.posts.length = 0
      await runMainWith({ root, statePath, server: recording.server, sources: 'antigravity' })
    })
    assert.deepEqual(machinePosts(recording.posts, 'antigravity-local'), [])
    assert.equal(typeof await migrated(), 'string')
  } finally { await removeTemp(root) }
})
// Final composition pass: the server already holds this machine's figures for a held day.
test('a legacy key on a held day is zeroed when this machine already covers it, else stays pending', async () => {
  const root = await makeTemp('observatory-legacy-held-covered-')
  try {
    await writeAgyOrphanAndLater(root)
    const coveredKey = 'antigravity:2026-08-20:gemini-3.1-pro-high'
    const uncoveredKey = 'antigravity:2026-08-21:gemini-3.1-pro-high'
    const recording = recordingServer([coveredKey, uncoveredKey], [coveredKey])
    const statePath = join(root, 'state', 'sweep.json')
    await withRecordingServer(recording, async () => {
      await runMainWith({ root, statePath, server: recording.server, sources: 'antigravity' })
    })
    assert.deepEqual(machinePosts(recording.posts, 'antigravity-local').map(body => body.eventKey), [coveredKey])
    assert.equal(JSON.parse(await readFile(statePath, 'utf8')).legacySourceMigration?.antigravity, undefined)
  } finally { await removeTemp(root) }
})
// --- PR #468 CodeRabbit follow-ups ---------------------------------------------

// CodeRabbit 4657871774
test('a malformed Claude copy holds every day its message lands on after deduplication', () => {
  const malformed = claudePromptRow({ input_tokens: 10, output_tokens: -1 }, { id: 'dup', model: 'claude-opus-5' }, '2026-10-22T23:59:59Z')
  const richer = claudePromptRow(
    { input_tokens: 0, output_tokens: 5, cache_read_input_tokens: 500, cache_creation_input_tokens: 7 },
    { id: 'dup', model: 'claude-opus-5' }, '2026-10-23T00:00:05Z',
  )
  const unrelated = claudePromptRow({ input_tokens: 3, output_tokens: 1 }, { id: 'other', model: 'claude-opus-5' }, '2026-10-24T12:00:00Z')
  const snapshots = buildDailySnapshots(parseClaude([malformed, richer, unrelated].join('\n')), TEST_MACHINE)
  assert.deepEqual(snapshots.map(snapshot => snapshot.eventKey.split(':')[1]), ['2026-10-24'])
})
// CodeRabbit 4657871792
test('a per-machine key selected for removal never covers a legacy key, even when its tombstone fails', async () => {
  const root = await makeTemp('observatory-legacy-removed-counterpart-')
  await writeCodexRollout(root)
  const removed = 'codex:2026-08-25:gpt-5.5'
  const recording = recordingServer([removed], [removed], {
    failPost: body => body.sourceId === 'codex-local@test-machine' && isTombstone(body),
  })
  try {
    await withRecordingServer(recording, async () => {
      await runMainWith({ root, statePath: join(root, 'state', 'sweep.json'), server: recording.server })
    })
    assert.equal(machinePosts(recording.posts).some(isTombstone), true, 'the per-machine tombstone was attempted')
    assert.deepEqual(legacyZeroes(recording.posts), [])
  } finally { await removeTemp(root) }
})

// CodeRabbit 4657871787
test('legacy cost coverage uses the cost the server stored for this machine, not the posted null', async () => {
  const root = await makeTemp('observatory-legacy-stored-cost-')
  await writeCodexRollout(root)
  const key = 'codex:2026-08-24:gpt-5.5'
  const statePath = join(root, 'state', 'sweep.json')
  const priced = { eventKey: key, costUsd: 0.01, hasCost: true }
  try {
    const stored = recordingServer([priced], [priced])
    await withRecordingServer(stored, async () => { await runMainWith({ root, statePath, server: stored.server }) })
    assert.deepEqual(legacyZeroes(stored.posts), [key])

    // A stored row whose tokens differ from this run's post carries a stale cost: pending.
    await writeFile(statePath, '{}')
    const stale = recordingServer([priced], [{ ...priced, inputTokens: 9 }])
    await withRecordingServer(stale, async () => { await runMainWith({ root, statePath, server: stale.server }) })
    assert.deepEqual(legacyZeroes(stale.posts), [])
    assert.equal(JSON.parse(await readFile(statePath, 'utf8')).legacySourceMigration?.codex, undefined)
  } finally { await removeTemp(root) }
})

// CodeRabbit 4657871784
// CodeRabbit 4658317586: the lock follows the server and machine, not OBSERVATORY_STATE.
test('a second sweep for the same server and machine refuses to start, whatever state file it uses', async () => {
  const root = await makeTemp('observatory-sweep-lock-')
  await writeCodexRollout(root)
  const recording = recordingServer()
  try {
    await withRecordingServer(recording, async () => {
      // Resolved once the server listens, since the lock is keyed on its URL.
  const lock = () => testLockPath(root, recording.server)
      await mkdir(testLockDir(root), { recursive: true })
      await writeFile(lock(), JSON.stringify({ pid: process.pid, startedAt: new Date().toISOString() }))
      for (const state of ['state-a', 'state-b']) {
        await assert.rejects(() => runMainWith({ root, statePath: join(root, state, 'sweep.json'), server: recording.server }), /Another sweep is running/, state)
      }
      assert.deepEqual(recording.posts, [])
      // A lock left by a run whose process is gone is stale: the next run takes it and releases it.
      await writeFile(lock(), JSON.stringify({ pid: await deadPid(), startedAt: new Date().toISOString() }))
      await runMainWith({ root, statePath: join(root, 'state-a', 'sweep.json'), server: recording.server })
    })
    assert.equal(machinePosts(recording.posts).length, 1)
    assert.deepEqual(await readdir(testLockDir(root)), [])
  } finally { await removeTemp(root) }
})
// --- Sweep lock, composition review ---------------------------------------------

async function deadPid() {
  const child = execFile(process.execPath, ['-e', ''])
  await new Promise(resolve => child.on('exit', resolve))
  return child.pid
}

async function lockTemp(prefix) {
  const root = await makeTemp(prefix)
  return { root, lock: join(root, 'sweep.json.lock') }
}

test('an empty sweep lock is busy until it has sat unchanged for a while', async () => {
  const { root, lock } = await lockTemp('observatory-lock-empty-')
  try {
    await writeFile(lock, '')
    await assert.rejects(() => sweep.acquireSweepLock(lock), /Another sweep is running/)
    await utimes(lock, new Date('2020-01-01T00:00:00Z'), new Date('2020-01-01T00:00:00Z'))
    const taken = await sweep.acquireSweepLock(lock)
    assert.equal(taken.locked, true)
    await taken.release()
    assert.deepEqual(await readdir(root), [])
  } finally { await removeTemp(root) }
})

test('a live sweep lock is busy however old it is; a dead one is taken over', async () => {
  const { root, lock } = await lockTemp('observatory-lock-live-')
  try {
    await writeFile(lock, JSON.stringify({ pid: process.pid, startedAt: '2020-01-01T00:00:00.000Z' }))
    await assert.rejects(() => sweep.acquireSweepLock(lock), error => error.message.includes(`delete ${lock}`))
    await writeFile(lock, JSON.stringify({ pid: await deadPid(), startedAt: new Date().toISOString() }))
    const taken = await sweep.acquireSweepLock(lock)
    assert.equal(taken.locked, true)
    await taken.release()
  } finally { await removeTemp(root) }
})

// Every fs call a start makes goes through `hook(name, op, args)` first, and is logged in `calls`.
async function lockSeam(name, calls, hook) {
  const fs = await import('node:fs/promises')
  return Object.fromEntries(['mkdir', 'writeFile', 'readFile', 'stat', 'unlink', 'rename', 'link', 'rm'].map(op => [op, async (...args) => {
    calls.push([name, op, String(args[0])])
    await hook(name, op, args)
    return fs[op](...args)
  }]))
}

test('three starts racing over one stale lock leave exactly one holder and never touch its lock', async () => {
  const { root, lock } = await lockTemp('observatory-lock-takeover-')
  try {
    await writeFile(lock, JSON.stringify({ pid: await deadPid(), startedAt: '2020-01-01T00:00:00.000Z' }))
    // A and B both judge the same stale text; B's takeover then waits until A holds the lock, and C
    // only arrives after that.
    const firstRead = new Set()
    let bothRead
    const read = new Promise(resolve => { bothRead = resolve })
    let aHolds
    const aDone = new Promise(resolve => { aHolds = resolve })
    const calls = []
    const hook = async (name, op, [path]) => {
      if (op === 'readFile' && path === lock && name !== 'C' && !firstRead.has(name)) {
        firstRead.add(name)
        if (firstRead.size === 2) { bothRead() }
        await read
      }
      const takeoverStep = (op === 'writeFile' && path !== lock) || ((op === 'rename' || op === 'rm' || op === 'unlink') && path === lock)
      if (name === 'B' && takeoverStep && !String(path).endsWith('.tmp')) { await aDone }
    }
    const a = sweep.acquireSweepLock(lock, { nowMs: Date.parse('2026-11-22T10:00:00Z'), io: await lockSeam('A', calls, hook) })
    const b = sweep.acquireSweepLock(lock, { nowMs: Date.parse('2026-11-22T10:00:01Z'), io: await lockSeam('B', calls, hook) })
    const held = await a
    aHolds()
    const c = sweep.acquireSweepLock(lock, { nowMs: Date.parse('2026-11-22T10:00:02Z'), io: await lockSeam('C', calls, hook) })
    const results = await Promise.allSettled([b, c])
    assert.equal(held.locked, true)
    assert.deepEqual(results.map(result => result.status), ['rejected', 'rejected'])
    assert.equal(JSON.parse(await readFile(lock, 'utf8')).startedAt, '2026-11-22T10:00:00.000Z')
    assert.deepEqual(calls.filter(([name, op, path]) => name !== 'A' && path === lock && ['unlink', 'rename', 'rm'].includes(op)), [], "A's lock is never moved or deleted")
    await held.release()
    assert.deepEqual(await readdir(root), [])
  } finally { await removeTemp(root) }
})

test('a filesystem without hard links still takes the lock', async () => {
  const { root, lock } = await lockTemp('observatory-lock-nolink-')
  try {
    const fs = await import('node:fs/promises')
    const io = { ...fs, link: async () => { throw Object.assign(new Error('operation not permitted'), { code: 'EPERM' }) } }
    const taken = await sweep.acquireSweepLock(lock, { io })
    assert.equal(taken.locked, true)
    await taken.release()
    assert.deepEqual(await readdir(root), [])
  } finally { await removeTemp(root) }
})
test('an unlocked run posts its snapshots but no tombstones and no legacy zero', async () => {
  const root = await makeTemp('observatory-lock-unlocked-')
  await writeCodexRollout(root)
  const blocker = join(root, 'blocker')
  await writeFile(blocker, 'not a directory')
  const vanished = 'codex:2026-08-25:gpt-5.5'
  const recording = recordingServer(['codex:2026-08-24:gpt-5.5'], [vanished])
  try {
    let errors
    await withRecordingServer(recording, async () => {
      errors = await runMainWith({ root, statePath: join(blocker, 'sweep.json'), server: recording.server, lockDir: join(blocker, 'locks') })
    })
    assert.deepEqual(machinePosts(recording.posts).map(body => [body.eventKey, isTombstone(body)]), [['codex:2026-08-24:gpt-5.5', false]])
    assert.deepEqual(legacyZeroes(recording.posts), [])
    assert.equal(errors.some(line => /tombstones and the legacy migration are skipped/.test(line)), true)
  } finally { await removeTemp(root) }
})
test('a takeover lock left by a start that died mid-takeover is cleared after the grace period', async () => {
  const { root, lock } = await lockTemp('observatory-lock-stuck-takeover-')
  try {
    await writeFile(lock, JSON.stringify({ pid: await deadPid(), startedAt: '2020-01-01T00:00:00.000Z' }))
    await writeFile(`${lock}.takeover`, JSON.stringify({ pid: await deadPid(), startedAt: '2020-01-01T00:00:00.000Z' }))
    await assert.rejects(() => sweep.acquireSweepLock(lock), /Another sweep is running/, 'a fresh takeover lock is respected')
    await utimes(`${lock}.takeover`, new Date('2020-01-01T00:00:00Z'), new Date('2020-01-01T00:00:00Z'))
    const taken = await sweep.acquireSweepLock(lock)
    assert.equal(taken.locked, true)
    await taken.release()
    assert.deepEqual(await readdir(root), [])
  } finally { await removeTemp(root) }
})
test('a run that loses its sweep lock mid-run issues no further tombstone or legacy zero', async () => {
  const root = await makeTemp('observatory-lock-fence-')
  await writeCodexRollout(root)
  const statePath = join(root, 'state', 'sweep.json')
  // Resolved once the server listens, since the lock is keyed on its URL.
  const lock = () => testLockPath(root, recording.server)
  const intruder = JSON.stringify({ pid: process.pid, startedAt: '2026-11-22T09:00:00.000Z' })
  const { writeFileSync } = await import('node:fs')
  let swapped = false
  // The first tombstone the server sees hands the lock to another run.
  const recording = recordingServer(['codex:2026-08-24:gpt-5.5'], ['codex:2026-08-25:gpt-5.5', 'codex:2026-08-26:gpt-5.5'], {
    failPost: body => {
      if (!swapped && body.sourceId === 'codex-local@test-machine' && isTombstone(body)) { swapped = lock(); writeFileSync(swapped, intruder) }
      return false
    },
  })
  try {
    let errors
    await withRecordingServer(recording, async () => {
      errors = await runMainWith({ root, statePath, server: recording.server })
    })
    assert.deepEqual(machinePosts(recording.posts).filter(isTombstone).map(body => body.eventKey), ['codex:2026-08-25:gpt-5.5'])
    assert.deepEqual(legacyZeroes(recording.posts), [])
    assert.equal(errors.some(line => /lost the sweep lock to another run; skipped removals/.test(line)), true)
    assert.equal(await readFile(swapped, 'utf8'), intruder, "the other run's lock is left alone")
  } finally { await removeTemp(root) }
})
// CodeRabbit 4658317581
test('a stored cost is reused only when the stored provider matches the post', async () => {
  const root = await makeTemp('observatory-legacy-stored-provider-')
  await writeCodexRollout(root)
  const key = 'codex:2026-08-24:gpt-5.5'
  const statePath = join(root, 'state', 'sweep.json')
  const priced = { eventKey: key, costUsd: 0.01, hasCost: true }
  const recording = recordingServer([priced], [{ ...priced, provider: 'anthropic' }])
  try {
    await withRecordingServer(recording, async () => { await runMainWith({ root, statePath, server: recording.server }) })
    assert.deepEqual(legacyZeroes(recording.posts), [])
    assert.equal(JSON.parse(await readFile(statePath, 'utf8')).legacySourceMigration?.codex, undefined)
  } finally { await removeTemp(root) }
})
// CodeRabbit 4658317590
test('a removal is not retried after the run loses its sweep lock', async () => {
  const root = await makeTemp('observatory-lock-retry-fence-')
  await writeCodexRollout(root)
  const statePath = join(root, 'state', 'sweep.json')
  // Resolved once the server listens, since the lock is keyed on its URL.
  const lock = () => testLockPath(root, recording.server)
  const { writeFileSync } = await import('node:fs')
  const vanished = 'codex:2026-08-25:gpt-5.5'
  // The first tombstone attempt fails with a 500 and, meanwhile, another run takes the lock.
  const recording = recordingServer([], [vanished], {
    failPost: body => {
      if (body.eventKey !== vanished || !isTombstone(body)) { return false }
      writeFileSync(lock(), JSON.stringify({ pid: process.pid, startedAt: '2026-11-22T09:00:00.000Z' }))
      return true
    },
  })
  try {
    await withRecordingServer(recording, async () => { await runMainWith({ root, statePath, server: recording.server }) })
    assert.equal(machinePosts(recording.posts).filter(body => body.eventKey === vanished).length, 1)
  } finally { await removeTemp(root) }
})