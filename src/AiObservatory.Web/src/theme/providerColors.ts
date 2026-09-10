import { getProvider } from '../config/providers'

// Maps a provider key to its categorical CSS-var colour (defined in index.css,
// re-themed per [data-theme]). Used for recharts fills. Unknown -> --provider-other.
export function providerColor(provider: string): string {
  return getProvider(provider)?.colorVar ?? 'var(--provider-other)'
}

// The Opus judge gets its own categorical tone; reviewers use their vendor color.
export function participantColor(reviewer: string, role: string): string {
  return role === 'judge' ? 'var(--provider-judge)' : providerColor(reviewer)
}
