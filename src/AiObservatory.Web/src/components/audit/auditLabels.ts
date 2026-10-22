import type { AuditCheck } from '../../api/auditTypes'

export const auditCheckLabels: Record<AuditCheck, string> = {
  sourceAccounting: 'Source accounting',
  dailyAttribution: 'Daily attribution',
  eventAggregation: 'Event aggregation',
  pricingArithmetic: 'Pricing arithmetic',
  presentationTotals: 'Displayed totals',
  billingReconciliation: 'Billing reconciliation',
}

const readinessNotes: Record<string, string> = {
  capture_missing: 'No retained capture covers this range.',
  capture_choices_truncated: 'More captures are available; only the newest choices are shown.',
  pricing_unverified: 'Pricing evidence has not been qualified.',
  pricing_stale: 'Pricing observations are stale.',
  source_unavailable: 'The source is unavailable.',
  source_refresh_failed: 'The latest source refresh failed.',
  source_stale: 'The last source observation is stale or missing.',
  revision_unverified: 'The native source revision has not been verified.',
  session_closure_unverified: 'Session closure has not been verified.',
  incomplete_capture: 'Some files were skipped or changed during capture.',
  unsupported_source_format: 'This captured format is unsupported.',
}

export const readinessNote = (code: string) =>
  readinessNotes[code] ?? 'This source has an additional evidence limitation.'
