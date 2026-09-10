// Single source of truth for all provider metadata.
// Add new providers here — consuming modules derive from this list automatically.

export const PROVIDER_KEYS = ['anthropic', 'copilot', 'google', 'openai', 'moonshot'] as const
export type ProviderKey = typeof PROVIDER_KEYS[number]

export interface ProviderSource {
  id: string
  displayName: string
  setupHref: string
}

export interface ProviderConfig {
  key: ProviderKey
  displayName: string
  colorVar: string
  badgeStyle: { color: string; background: string }
  sources: ProviderSource[]
}

const SETUP_HREF = 'https://github.com/FixPortal/fixportal-observatory/blob/main/docs/provider-setup.md'

export const PROVIDERS = [
  {
    key: 'anthropic',
    displayName: 'Anthropic',
    colorVar: 'var(--provider-anthropic)',
    badgeStyle: { color: 'var(--provider-anthropic)', background: 'color-mix(in srgb, var(--provider-anthropic) 12%, transparent)' },
    sources: [
      { id: 'anthropic-usage-api', displayName: 'Messages usage', setupHref: SETUP_HREF },
      { id: 'anthropic-cost-report', displayName: 'Cost report', setupHref: SETUP_HREF },
      { id: 'claude-code-usage-api', displayName: 'Claude Code usage', setupHref: SETUP_HREF },
      { id: 'claude-local', displayName: 'Claude local', setupHref: SETUP_HREF },
      { id: 'claude-pricing', displayName: 'Claude pricing', setupHref: SETUP_HREF },
    ],
  },
  {
    key: 'copilot',
    displayName: 'Copilot',
    colorVar: 'var(--provider-copilot)',
    badgeStyle: { color: 'var(--provider-copilot)', background: 'color-mix(in srgb, var(--provider-copilot) 12%, transparent)' },
    sources: [
      { id: 'copilot-org-report', displayName: 'Organization report', setupHref: SETUP_HREF },
      { id: 'copilot-local', displayName: 'Copilot local', setupHref: SETUP_HREF },
    ],
  },
  {
    key: 'google',
    displayName: 'Google',
    colorVar: 'var(--provider-google)',
    badgeStyle: { color: 'var(--provider-google)', background: 'color-mix(in srgb, var(--provider-google) 12%, transparent)' },
    sources: [
      { id: 'google-cloud-billing-export', displayName: 'Cloud Billing export', setupHref: SETUP_HREF },
      { id: 'google-cloud-catalog', displayName: 'Cloud catalog', setupHref: SETUP_HREF },
    ],
  },
  {
    key: 'openai',
    displayName: 'OpenAI',
    colorVar: 'var(--provider-openai)',
    badgeStyle: { color: 'var(--provider-openai)', background: 'color-mix(in srgb, var(--provider-openai) 12%, transparent)' },
    sources: [
      { id: 'openai-usage-api', displayName: 'Usage API', setupHref: SETUP_HREF },
      { id: 'openai-costs-api', displayName: 'Costs API', setupHref: SETUP_HREF },
      { id: 'codex-local', displayName: 'Codex local', setupHref: SETUP_HREF },
      { id: 'openai-pricing', displayName: 'OpenAI pricing', setupHref: SETUP_HREF },
    ],
  },
  {
    key: 'moonshot',
    displayName: 'Moonshot',
    colorVar: 'var(--provider-moonshot)',
    badgeStyle: { color: 'var(--provider-moonshot)', background: 'color-mix(in srgb, var(--provider-moonshot) 12%, transparent)' },
    sources: [
      { id: 'kimi-local', displayName: 'Kimi local', setupHref: SETUP_HREF },
      { id: 'kimi-pricing', displayName: 'Kimi pricing', setupHref: SETUP_HREF },
    ],
  },
] satisfies ProviderConfig[]

export const NON_PROVIDER_SOURCES = [
  { id: 'github-activity-api', displayName: 'Repository activity', setupHref: SETUP_HREF },
  { id: 'github-billing-api', displayName: 'GitHub billing', setupHref: SETUP_HREF },
] satisfies ProviderSource[]

/** Stable display order for provider filter chips and dropdowns. */
export const PROVIDER_ORDER: ProviderKey[] = PROVIDERS.map(p => p.key)

export function getProvider(key: string): ProviderConfig | undefined {
  return PROVIDERS.find(p => p.key === key)
}

const SOURCES = [...PROVIDERS.flatMap(provider => provider.sources), ...NON_PROVIDER_SOURCES]

export function getSource(id: string): ProviderSource | undefined {
  return SOURCES.find(source => source.id === id)
}

function readableSlug(value: string): string {
  const words = value.replace(/([a-z])([A-Z])/g, '$1 $2').replace(/[-_|]+/g, ' ').trim().toLowerCase()
  return words ? words[0].toUpperCase() + words.slice(1) : value
}

export function providerDisplayName(key: string): string {
  return getProvider(key)?.displayName ?? readableSlug(key)
}

export function sourceDisplayName(id: string): string {
  return getSource(id)?.displayName ?? readableSlug(id)
}

export function usageScopeDisplayName(scope: string): string {
  return scope === 'api' ? 'API' : readableSlug(scope)
}

export function costBasisDisplayName(basis: string): string {
  const known: Record<string, string> = {
    billed: 'Billed',
    providerEstimated: 'Provider estimate',
    listPriceEstimate: 'List-price estimate',
    notional: 'Notional',
    none: 'None',
    unknown: 'Unknown',
  }
  return known[basis] ?? readableSlug(basis)
}
