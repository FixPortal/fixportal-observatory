import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import path from 'node:path'
import test from 'node:test'
import { fileURLToPath } from 'node:url'

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const read = relativePath => readFile(path.join(root, relativePath), 'utf8')
const guides = ['docs/provider-setup.md', 'docs/truth-and-pricing.md', 'docs/adding-a-provider.md']
const configKeys = [
  'ANTHROPIC_BILLING_KEY',
  'CLAUDE_CODE_USAGE_ENABLED',
  'OPENAI_ADMIN_KEY',
  'GITHUB_TOKEN',
  'COPILOT_ORG',
  'GOOGLE_CLOUD_PROJECT_ID',
  'GOOGLE_BILLING_EXPORT_TABLE',
  'GOOGLE_CLOUD_CATALOG_API_KEY',
  'GOOGLE_CLOUD_CATALOG_SERVICE_ID',
]

function jsonWithComments(text) {
  return JSON.parse(text.replace(/^\s*\/\/.*$/gm, '').replace(/,\s*([}\]])/g, '$1'))
}

function sourceValues(text) {
  return [...text.matchAll(/public const string \w+ = "([^"]+)";/g)].map(match => match[1])
}

function enumValues(text, name) {
  const body = text.match(new RegExp(`public enum ${name}\\s*\\{([\\s\\S]*?)\\n\\}`))?.[1] ?? ''
  return [...body.matchAll(/^\s*(\w+),/gm)].map(match => match[1])
}

function requests(items) {
  return items.flatMap(item => item.item ? requests(item.item) : item.request ? [item] : [])
}

function powerShellBlocks(text) {
  return [...text.matchAll(/```powershell\r?\n([\s\S]*?)```/g)].map(match => match[1])
}

test('documentation describes the source-aware setup contract', async () => {
  const [readme, provenance, pricing, truth, settings, postman, providerSetup, addingProvider, clientsReadme] = await Promise.all([
    read('README.md'),
    read('src/AiObservatory.Data/Entities/ObservationProvenance.cs'),
    read('src/AiObservatory.Data/Pricing/PricingSnapshotCandidate.cs'),
    read('src/AiObservatory.Data/Entities/ObservationProvenance.cs'),
    read('src/AiObservatory.Ingest/appsettings.json'),
    read('docs/observatory.postman_collection.json'),
    read('docs/provider-setup.md'),
    read('docs/adding-a-provider.md'),
    read('clients/README.md'),
  ])
  const guideContents = await Promise.all(guides.map(read))

  for (const [index, guide] of guideContents.entries()) {
    assert.match(guide, /^# .+\r?\n\r?\n> .+as of 2026-08-25/im, `${guides[index]} needs its H1 and orientation`)
    assert.doesNotMatch(guide, /^---\r?\n/m, `${guides[index]} must not have frontmatter`)
    assert.doesNotMatch(guide, /author:/i, `${guides[index]} must not have author metadata`)
    assert.doesNotMatch(guide, /[ \t]+\r?$/m, `${guides[index]} must not have trailing whitespace`)
    assert.ok(guide.endsWith('\n'), `${guides[index]} must end with a newline`)
  }
  assert.doesNotMatch(readme, /^---\r?\n/m, 'README must not have frontmatter')
  assert.doesNotMatch(readme, /author:/i, 'README must not have author metadata')

  const focusedGuides = guideContents.join('\n')
  for (const sourceId of [...sourceValues(provenance), ...sourceValues(pricing)]) {
    assert.match(focusedGuides, new RegExp(`\\b${sourceId}\\b`), `missing source ID ${sourceId}`)
  }
  const setupOrderIndex = providerSetup.indexOf('## Setup order')
  assert.ok(setupOrderIndex >= 0 && setupOrderIndex < providerSetup.indexOf('## Dashboard sources'), 'Provider setup needs leading setup order')
  assert.match(providerSetup, /> \[!IMPORTANT\][\s\S]*anthropic-usage-api[\s\S]*anthropic-cost-report[\s\S]*claude-code-usage-api[\s\S]*Admin[\s\S]*Enterprise[\s\S]*not supported/i, 'Anthropic callout must distinguish the shipped Admin adapters from Enterprise Analytics')
  for (const enumName of ['SourceKind', 'UsageScope', 'CostBasis']) {
    for (const value of enumValues(truth, enumName)) {
      assert.match(guideContents[1], new RegExp(`\\b${value}\\b`), `truth guide missing ${enumName}.${value}`)
    }
  }

  for (const guide of guides) assert.match(readme, new RegExp(`\\(${guide.replace('.', '\\.') }\\)`), `README missing ${guide}`)
  assert.doesNotMatch(readme, /14-day|merged spend|exhaustive Postman/i, 'README contains retired dashboard or collection claim')
  assert.match(readme, /Restore uses public feeds; no GitHub Packages token is required\./i, 'README must state the public restore prerequisite')
  const privateRestoreToken = ['GITHUB', 'PACKAGES', 'TOKEN'].join('_')
  assert.doesNotMatch(readme, new RegExp(`${privateRestoreToken}|private FixPortal GitHub Packages|public users.*cannot complete.*quick start`, 'i'), 'README must not retain the private restore blocker')
  assert.doesNotMatch(readme, /Task 5/i, 'README must not expose internal task jargon')
  assert.match(readme, /^## Local development$/m, 'README needs the Local development anchor')
  assert.match(readme, /npm --prefix src\/AiObservatory\.Web ci/, 'README manual quick start needs deterministic dependency installation')
  assert.doesNotMatch(readme, /npm --prefix src\/AiObservatory\.Web install/, 'README must not use non-deterministic npm install')
  assert.match(readme, /\[Apache-2\.0\]\(LICENSE\)/, 'README needs a navigable license link')
  for (const file of ['CONTRIBUTING.md', 'CODE_OF_CONDUCT.md', 'SECURITY.md']) {
    assert.match(readme, new RegExp(`\\(${file.replace('.', '\\.') }\\)`), `README missing ${file} navigation`)
  }

  for (const text of [readme, ...guideContents, clientsReadme]) {
    for (const block of powerShellBlocks(text)) {
      assert.equal(block.split(/\r?\n/).filter(line => line.trim()).length, 1, 'PowerShell blocks must contain one command')
    }
  }

  const parsedSettings = jsonWithComments(settings)
  for (const key of configKeys) assert.equal(parsedSettings[key], '', `appsettings missing empty ${key}`)
  assert.ok(!('GITHUB_BILLING_ORG' in parsedSettings), 'Ingest appsettings must not own the API GitHub billing setting')

  const collection = JSON.parse(postman)
  const collectionRequests = requests(collection.item)
  assert.match(collection.info.description, /representative/i, 'Postman metadata must say representative')
  assert.equal(collection.variable.find(variable => variable.key === 'base_url')?.value, 'http://localhost:5039/api', 'Postman local base must be the API development address')
  assert.match(collection.info.description, /GET.*both `OBSERVATORY_API_KEY` and `OBSERVATORY_READONLY_API_KEY` are unset/i, 'Postman GET key wording must be request-specific')
  assert.match(collection.info.description, /Non-GET.*`OBSERVATORY_API_KEY` is unset/i, 'Postman write key wording must be request-specific')
  assert.match(collection.info.description, /Non-Development.*refuses to start.*OBSERVATORY_API_KEY.*OBSERVATORY_READONLY_API_KEY.*OBSERVATORY_IDE_API_KEY.*mutually distinct/i, 'Postman must describe non-Development startup validation')
  assert.doesNotMatch(collection.info.description, /missing keys fail closed \(503\)/i, 'Postman must not claim request-time missing-key 503s')
  assert.match(collection.variable.find(variable => variable.key === 'base_url')?.description ?? '', /API base URL.*\/api/i, 'Postman deployed base must be an API URL ending in /api')
  assert.match(collection.variable.find(variable => variable.key === 'api_key')?.description ?? '', /GET.*both `OBSERVATORY_API_KEY` and `OBSERVATORY_READONLY_API_KEY` are unset/i, 'Postman API key variable must describe GET behavior')
  assert.match(collection.variable.find(variable => variable.key === 'api_key')?.description ?? '', /Non-GET.*`OBSERVATORY_API_KEY` is unset/i, 'Postman API key variable must describe write behavior')
  assert.ok(collectionRequests.some(item => item.request?.method === 'GET' && item.request.url.raw === '{{base_url}}/sources/status'), 'Postman needs source status')
  assert.ok(!collectionRequests.some(item => item.request?.url.raw === '{{base_url}}/budget-rules/email-status'), 'Postman must not include retired email status')
  assert.ok(!collectionRequests.some(item => item.request?.url.raw === '{{base_url}}/budget-rules/webhook-status'), 'Postman must not include retired webhook status')
  assert.ok(collectionRequests.some(item => item.request?.method === 'GET' && item.request.url.raw === '{{base_url}}/notification-settings'), 'Postman needs get notification settings')
  assert.ok(collectionRequests.some(item => item.request?.method === 'PUT' && item.request.url.raw === '{{base_url}}/notification-settings'), 'Postman needs update notification settings')
  const eventPosts = collectionRequests.filter(item => item.request?.method === 'POST' && item.request.url.raw === '{{base_url}}/events' && item.request.body?.mode === 'raw' && item.request.body.options?.raw?.language === 'json')
  for (const item of eventPosts) {
    const body = JSON.parse(item.request.body.raw)
    assert.ok('sourceId' in body && 'sourceKind' in body && 'usageScope' in body && 'costBasis' in body, `${item.name} needs explicit provenance`)
    assert.equal(body.costUsd, null, `${item.name} must not hand-supply numeric cost`)
    assert.notEqual(body.costBasis, 'billed', `${item.name} must not send billed evidence to /events`)
    assert.notEqual(body.sourceId, 'google-cloud-billing-export', `${item.name} must not spoof the Google billing export source`)
  }
  const anthropicEvent = eventPosts.find(item => item.name === 'Ingest event')
  const anthropicBody = JSON.parse(anthropicEvent?.request.body?.raw ?? '{}')
  assert.deepEqual(JSON.parse(anthropicBody.rawPayload), {
    service_tier: 'standard',
    speed: 'standard',
    inference_geo: 'global',
    cache_creation: { ephemeral_5m_input_tokens: 0, ephemeral_1h_input_tokens: 0 },
  }, 'Anthropic event rawPayload must have resolvable pricing dimensions')
  assert.match(eventPosts[0].request.description, /moonshot/, 'Postman valid-provider description must include moonshot')
  assert.ok(!collectionRequests.some(item => item.request?.method === 'PATCH' && item.request.url.raw.includes('/events/')), 'Postman must not include PATCH event-cost examples')
  assert.match(eventPosts[0].request.description, /sourceId.*eventKey/i, 'Postman event idempotency wording must be source-scoped')
  assert.match(collection.variable.find(variable => variable.key === 'event_key')?.description ?? '', /source-scoped/i, 'Postman event key description must be source-scoped')
  const reviewRun = collectionRequests.find(item => item.name === 'Record review run')
  assert.equal(JSON.parse(reviewRun?.request.body?.raw ?? '{}').role, 'reviewer', 'Adversarial review example needs reviewer role')
  const createBudget = collectionRequests.find(item => item.name === 'Create budget rule')
  assert.match(createBudget?.request.description ?? '', /conditional email notification.*email alerts.*configured/i, 'Budget example must describe conditional email notification')
  assert.doesNotMatch(createBudget?.request.description ?? '', /webhook/i, 'Budget example must not claim webhook delivery')
  const seed = collectionRequests.find(item => item.name === 'Seed sample data')
  assert.match(seed?.request.description ?? '', /tables.*empty[\s\S]*skip/i, 'Seed example must say it only seeds empty tables')
  assert.doesNotMatch(seed?.request.description ?? '', /wipe/i, 'Seed example must not claim a wipe')
  const listEvents = collectionRequests.find(item => item.name === 'List events by provider')
  assert.match(listEvents?.request.url.query?.find(query => query.key === 'provider')?.description ?? '', /moonshot/i, 'Provider query description must include moonshot')
  const aggregates = collectionRequests.find(item => item.name === 'Get daily aggregates')
  for (const query of aggregates?.request.url.query ?? []) {
    if (query.key === 'from' || query.key === 'to') assert.match(query.description ?? '', /30-day inclusive window \(today-29 through today\)/i, 'Aggregate defaults must describe the inclusive 30-day window')
  }

  assert.match(providerSetup, /`organization-28-day\/latest`/, 'Copilot setup needs the current report descriptor')
  assert.match(providerSetup, /`GITHUB_TOKEN` plus `Ingest__GitHubRepoAllowlist`[\s\S]*contents:read.*pull-requests:read.*actions:read/i, 'GitHub activity setup needs its token and repository permissions')
  assert.match(providerSetup, /`GITHUB_BILLING_ORG`[\s\S]*Plan.*admin:org/i, 'GitHub billing setup needs its organization and token permissions')
  assert.match(providerSetup, /`google-cloud-billing-export`[\s\S]*?API \/ billed/, 'Google billing export must be API/billed')
  const googleCatalogRow = providerSetup.split(/\r?\n/).find(line => line.includes('`google-cloud-catalog`')) ?? ''
  assert.match(googleCatalogRow, /no fetch.*not configured.*verified exact SKU mappings/i, 'Google catalog cadence must state its unfetched, mapping-gated state')
  assert.match(guideContents[1], /Fetched runtime inputs:[\s\S]*OpenAI[\s\S]*Claude[\s\S]*Kimi/i, 'Truth guide must identify the three fetched pricing inputs')
  assert.match(guideContents[1], /Google Cloud Billing Catalog API.*planned official authority.*unfetched.*unavailable/i, 'Truth guide must distinguish the unfetched Google authority')
  assert.match(addingProvider, /src\/AiObservatory\.Data\/Entities\/Provider\.cs/, 'Enum-keyed usage or pricing needs Provider.cs')
  assert.match(addingProvider, /billing-only.*BillingObservation\.ProviderKey.*without.*fake enum/i, 'Billing-only adapters must use ProviderKey without a fake enum')
  assert.match(addingProvider, /src\/AiObservatory\.Ingest\/Services\/<Provider>/, 'Usage adapter path must be provider services')
  assert.match(addingProvider, /src\/AiObservatory\.Ingest\/Pricing/, 'Pricing adapter path must be ingest pricing')
  assert.match(addingProvider, /Data\/Pricing[\s\S]*ServiceCollectionExtensions[\s\S]*PricingSnapshotStore[\s\S]*Program\.cs/, 'Provider pricing seam must name catalog, calculator, registration, validation, and composition')
  assert.match(addingProvider, /BundledPricingCatalogLoader[\s\S]*BundledPricingCatalogLoader\.cs/, 'Bundled pricing guidance must name its loader mapping and file')
  const payload = clientsReadme.match(/```json\r?\n([\s\S]*?)```/)?.[1] ?? ''
  for (const field of ['"provider": "OpenAI"', '"eventKey"', '"occurredAtUtc"', '"costUsd": null', '"sourceKind": "localTelemetry"', '"usageScope": "subscription"', '"costBasis": "notional"', '"observedAtUtc"']) {
    assert.match(payload, new RegExp(field), `Client payload missing ${field}`)
  }
  const parsedPayload = JSON.parse(payload)
  assert.ok(parsedPayload.inputTokens + parsedPayload.outputTokens + parsedPayload.cacheReadTokens + parsedPayload.cacheWriteTokens + parsedPayload.thoughtTokens > 0, 'Client payload must be a normal non-zero snapshot')
  assert.ok(JSON.parse(parsedPayload.rawPayload).thinking_tokens > 0, 'Client payload raw evidence must include thinking_tokens')

  const sourceRows = providerSetup.split(/\r?\n/).filter(line => line.startsWith('|'))
  for (const sourceId of [...sourceValues(provenance), ...sourceValues(pricing)]) {
    const row = sourceRows.find(line => line.includes(`\`${sourceId}\``))
    assert.ok(row, `Provider matrix missing source row ${sourceId}`)
    assert.ok(row.split('|').some(cell => /startup|daily|15m|submission|no polling/i.test(cell)), `Provider matrix source ${sourceId} needs an explicit cadence`)
  }
  assert.match(providerSetup, /`Ingest__PollingIntervalMinutes`/, 'Provider matrix needs the configurable 60-minute polling setting')
  assert.match(providerSetup, /daily/i, 'Provider matrix needs daily pricing or billing cadence')
  assert.match(providerSetup, /15m/, 'Provider matrix needs the local 15-minute recommendation')

  const liveText = [readme, ...guideContents, await read('clients/README.md'), settings, postman].join('\n')
  assert.doesNotMatch(liveText, /sk-ant-|sk-[A-Za-z0-9]{8,}|google.*\/reports|\/reports.*google/i, 'live docs/config/collection contain a secret-shaped value or retired Google route')
  assert.doesNotMatch(liveText, /\$\d+(?:\.\d+)?\s*(?:\/|per)\s*(?:1m|million)|cacheSavingsPerToken|OPENAI_PRICING|COPILOT_PRICING/i, 'live docs/config/collection contain hand-maintained pricing')
})
