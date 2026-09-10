import { expect, test } from 'vitest'
import {
  PROVIDER_KEYS,
  PROVIDER_ORDER,
  PROVIDERS,
  getProvider,
  getSource,
  providerDisplayName,
  sourceDisplayName,
} from './providers'
import { providerColor } from '../theme/providerColors'

// The backend's AiObservatory.Data.Entities.Provider enum. Any provider the API can
// persist must be renderable here — Moonshot shipped in the enum and in the adversarial
// review grouping but was missing from PROVIDERS, so its usage rows would have rendered
// as unnamed grey "other". Keep this list in step with the enum.
const BACKEND_PROVIDERS = ['anthropic', 'copilot', 'google', 'openai', 'moonshot']

test('every backend provider has a frontend config entry', () => {
  expect([...PROVIDER_ORDER].sort()).toEqual([...BACKEND_PROVIDERS].sort())
})

test.each(BACKEND_PROVIDERS)('%s resolves to its own colour, not the "other" fallback', key => {
  expect(providerColor(key)).toBe(`var(--provider-${key})`)
  expect(providerColor(key)).not.toBe('var(--provider-other)')
})

test.each(BACKEND_PROVIDERS)('%s declares a display name and badge style', key => {
  const provider = getProvider(key)
  expect(provider?.displayName).toBeTruthy()
  expect(provider?.badgeStyle.color).toBe(`var(--provider-${key})`)
})

test.each(BACKEND_PROVIDERS)('%s has no frontend cache-pricing rate', key => {
  expect(getProvider(key)).not.toHaveProperty('cacheSavingsPerToken')
})

test('an unknown provider still falls back to the neutral colour', () => {
  expect(providerColor('not-a-provider')).toBe('var(--provider-other)')
})

test('declares every current acquisition and pricing source with public setup guidance', () => {
  const setupHref = 'https://github.com/FixPortal/fixportal-observatory/blob/main/docs/provider-setup.md'
  expect(PROVIDERS.flatMap(provider => provider.sources.map(source => [provider.key, source.id, source.displayName, source.setupHref]))).toEqual([
    ['anthropic', 'anthropic-usage-api', 'Messages usage', setupHref],
    ['anthropic', 'anthropic-cost-report', 'Cost report', setupHref],
    ['anthropic', 'claude-code-usage-api', 'Claude Code usage', setupHref],
    ['anthropic', 'claude-local', 'Claude local', setupHref],
    ['anthropic', 'claude-pricing', 'Claude pricing', setupHref],
    ['copilot', 'copilot-org-report', 'Organization report', setupHref],
    ['copilot', 'copilot-local', 'Copilot local', setupHref],
    ['google', 'google-cloud-billing-export', 'Cloud Billing export', setupHref],
    ['google', 'google-cloud-catalog', 'Cloud catalog', setupHref],
    ['openai', 'openai-usage-api', 'Usage API', setupHref],
    ['openai', 'openai-costs-api', 'Costs API', setupHref],
    ['openai', 'codex-local', 'Codex local', setupHref],
    ['openai', 'openai-pricing', 'OpenAI pricing', setupHref],
    ['moonshot', 'kimi-local', 'Kimi local', setupHref],
    ['moonshot', 'kimi-pricing', 'Kimi pricing', setupHref],
  ])
  expect(getSource('openai-usage-api')?.displayName).toBe('Usage API')
  expect(getSource('missing-source')).toBeUndefined()
})

test('keeps known ordering closed while arbitrary provider and source slugs stay readable', () => {
  expect(PROVIDER_KEYS).toEqual(['anthropic', 'copilot', 'google', 'openai', 'moonshot'])
  expect(getProvider('new-oss-provider')).toBeUndefined()
  expect(providerDisplayName('new-oss-provider')).toBe('New oss provider')
  expect(sourceDisplayName('new-source-feed')).toBe('New source feed')
})

test('provider and source metadata contain no financial constants', () => {
  for (const provider of PROVIDERS) {
    expect(provider).not.toHaveProperty('rate')
    expect(provider).not.toHaveProperty('price')
    for (const source of provider.sources) {
      expect(source).not.toHaveProperty('rate')
      expect(source).not.toHaveProperty('price')
    }
  }
})

// Not covered here: that each --provider-<key> variable actually exists in index.css.
// Vitest stubs CSS imports, so `?raw` reads back empty, and node:fs would mean adding
// @types/node for a single assertion. A missing variable renders the series transparent
// rather than throwing — see the "three edits in step" note in the README.
