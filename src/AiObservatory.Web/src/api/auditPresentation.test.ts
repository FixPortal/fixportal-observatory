import { expect, test } from 'vitest'
import fixture from '../../../../tests/AiObservatory.Audit.Tests/Fixtures/dashboard-summary.v1.json'
import { summarizeFrozenPresentation } from './auditPresentation'
import type { AuditEvidence } from './auditTypes'

export function fixtureEvidence(): AuditEvidence[] {
  return [
    ...fixture.events.map((event): AuditEvidence => ({
      kind: 'aggregate',
      record: {
        key: {
          date: '2026-10-20',
          provider: 'provider',
          model: '',
          sourceId: 'local',
          sourceKind: 'localTelemetry',
          usageScope: 'subscription',
          costBasis: event.basis,
        },
        inputTokens: '10',
        outputTokens: '20',
        cacheReadTokens: '0',
        cacheWriteTokens: '3',
        cacheWrite1hTokens: '0',
        costUsd: event.costUsd ?? '0',
        unknownCostCount: event.costUsd === null && event.basis !== 'none' ? '1' : '0',
        cacheSavingsUsd: event.savingsUsd ?? '0',
        unknownCacheSavingsCount: event.savingsUsd === null && event.basis !== 'none' ? '1' : '0',
        requestCount: '1',
      },
    })),
    ...fixture.spend.map((entry): AuditEvidence => ({ kind: 'spend', record: entry })),
  ]
}

test('production frozen summary matches every supported literal bucket and declares the native-currency gap', () => {
  const actual = summarizeFrozenPresentation(fixtureEvidence())
  expect(actual.values).toEqual(
    fixture.expected
      .filter((v) => v.field !== 'billedNative')
      .map(({ field, groupKey, unroundedValue, formattedValue, currency }) => ({
        field,
        groupKey,
        unroundedValue,
        formattedValue,
        currency,
      })),
  )
  expect(actual.diagnosticCodes).toContain('missing_presentation_value:billedNative')
  expect(actual.values.some((value) => value.field === 'billedNative')).toBe(false)
})

test.each(['9007199254740993', '9007199254740992'])(
  'unsafe eligibility counters %s cannot produce rounded success',
  (requestCount) => {
    const evidence = fixtureEvidence()
    const first = evidence[0]!
    if (first.kind === 'aggregate') first.record.requestCount = requestCount
    expect(summarizeFrozenPresentation(evidence)).toEqual({
      values: [],
      diagnosticCodes: ['unsafe_integer_presentation_coverage'],
    })
  },
)

test('individually safe counters whose sum is unsafe cannot certify totals', () => {
  const evidence = fixtureEvidence()
  for (const row of evidence)
    if (row.kind === 'aggregate') row.record.unknownCostCount = '2000000000000000'
  expect(summarizeFrozenPresentation(evidence).diagnosticCodes).toEqual([
    'unsafe_integer_presentation_coverage',
  ])
})

test.each(['9007199254740993.01', '0.10000000000000001', '1e999'])(
  'unrepresentable money %s remains explicitly incomplete before production conversion',
  (costUsd) => {
    const evidence = fixtureEvidence()
    const first = evidence[0]!
    if (first.kind === 'aggregate') first.record.costUsd = costUsd
    expect(summarizeFrozenPresentation(evidence)).toEqual({
      values: [],
      diagnosticCodes: ['unrepresentable_presentation_value'],
    })
  },
)
