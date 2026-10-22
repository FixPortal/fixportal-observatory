/** Audit counts, tokens and amounts stay lossless on the wire and in the UI. */
export type AuditToken = string
export type AuditVerdict = 'matched' | 'mismatched' | 'incomplete' | 'unsupported'
export type AuditCheck =
  | 'sourceAccounting'
  | 'dailyAttribution'
  | 'eventAggregation'
  | 'pricingArithmetic'
  | 'presentationTotals'
  | 'billingReconciliation'
export interface AuditScope {
  from: string
  to: string
  sources: { machineId: string; sourceId: string }[]
  requestedChecks: AuditCheck[]
  excludedChecks: AuditCheck[]
  allowPartial: boolean
  predecessorRunId: string | null
  evaluationOfRunId: string | null
}
export interface AuditCapabilities {
  schemaVersion: 1
  enabled: boolean
  schemaVersions: number[]
  verifierVersions: string[]
  sources: {
    tool: string
    supported: boolean
    format: string | null
    version: string | null
    reasonCode: string | null
  }[]
  checks: { code: AuditCheck; supported: boolean; reasonCode: string | null }[]
}
export interface AuditReadinessDto {
  schemaVersion: 1
  sources: {
    sourceId: string
    machineId: string | null
    captureIds: string[]
    diagnosticCodes: string[]
    lastSuccessAt: string | null
  }[]
  maximumRunWindowDays: number
  maximumSnapshotBytes: number
}
export interface AuditCoverageDto {
  comparedGroups: AuditToken
  totalGroups: AuditToken
  diagnosticCodes: string[]
}
export interface AuditRun {
  schemaVersion: 1
  id: string
  state: string
  stage: string
  attempts: number
  diagnosticCode: string | null
  createdAt: string
  scope: AuditScope
  verdict: AuditVerdict | null
  coverage: AuditCoverageDto | null
  verifierVersion: string | null
  ruleVersion: string | null
  inputContentDigest: string | null
  serverCutoff: string | null
  replayExpiresAt: string | null
  evidenceExpiresAt: string | null
  summaryExpiresAt: string | null
  predecessorRunId: string | null
  evaluationOfRunId: string | null
}
export interface AuditPage<T> {
  schemaVersion: 1
  items: T[]
  nextCursor: string | null
}
export interface AuditFinding {
  count: AuditToken
  value: {
    checkCode: AuditCheck
    ruleCode: string
    field: string
    expected: { kind: 'integer' | 'decimal'; value: string } | null
    actual: { kind: 'integer' | 'decimal'; value: string } | null
    delta: { kind: 'integer' | 'decimal'; value: string } | null
    unit: string
    currency: string | null
    basis: string | null
    diagnosticCode: string
  }
}
export interface FrozenAggregateRecord {
  key: {
    date: string
    provider: string
    model: string
    sourceId: string
    sourceKind: string
    usageScope: string
    costBasis: string
  }
  inputTokens: string
  outputTokens: string
  cacheReadTokens: string
  cacheWriteTokens: string
  cacheWrite1hTokens: string
  costUsd: string
  unknownCostCount: string
  cacheSavingsUsd: string
  unknownCacheSavingsCount: string
  requestCount: string
}
export type AuditEvidence =
  | { kind: 'aggregate'; record: FrozenAggregateRecord }
  | { kind: 'spend'; record: { amountGbp: string } }
  | {
      kind:
        | 'capture'
        | 'pricingCatalog'
        | 'sourceRecord'
        | 'event'
        | 'sourceStatus'
        | 'fx'
        | 'financialObservation'
        | 'sourceQualification'
        | 'presentationProbe'
        | 'finding'
        | 'pricingEquation'
      record: Record<string, unknown>
    }
export interface AuditEvidencePage extends AuditPage<AuditEvidence> {
  frozenSnapshotHash: string
  summaryInputDigest: string
}
export interface PresentationValue {
  field: string
  groupKey: string
  unroundedValue: string | null
  formattedValue: string | null
  currency: string
}
export interface PresentationProbe {
  frozenSnapshotHash: string
  frontendBuildId: string
  frontendSourceId: string
  summaryInputDigest: string
  values: PresentationValue[]
}
export interface AuditCreated {
  schemaVersion: 1
  runId: string
}
export interface AuditCredential {
  machineId: string
  credentialId: string
  secret: string
}
