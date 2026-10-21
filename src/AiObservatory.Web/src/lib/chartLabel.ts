/** Recharts hands formatters a label of unknown type; dates arrive as strings or numbers. */
export function chartLabel(label: unknown): string {
  return typeof label === 'string' || typeof label === 'number' ? String(label) : ''
}
