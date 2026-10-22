import type { AuditReadinessDto } from '../../api/auditTypes'
import { readinessNote } from './auditLabels'

export function AuditReadiness({
  readiness,
  selected,
  onSelect,
}: {
  readiness: AuditReadinessDto
  selected: string[]
  onSelect?: (id: string) => void
}) {
  const bytes = BigInt(readiness.maximumSnapshotBytes)
  const snapshotLimit =
    bytes % 1048576n === 0n ? `${String(bytes / 1048576n)} MiB` : `${String(bytes)} bytes`
  return (
    <section aria-label="Audit readiness" className="audit-readiness">
      <h3>Readiness evidence</h3>
      <p className="audit-muted">
        Readiness is observational. Offline machines can remain waiting for up to 24 hours; fresh
        captures and source alignment are evaluated by the run.
      </p>
      {readiness.sources.map((source) => (
        <div key={source.sourceId} className="audit-source">
          <strong>{source.sourceId}</strong>
          <p className="audit-muted">
            Last successful observation:{' '}
            <span className="audit-exact">{source.lastSuccessAt ?? 'Not reported'}</span>
          </p>
          <ul className="audit-readiness-notes">
            {source.diagnosticCodes.map((code) => (
              <li key={code}>
                <span>{readinessNote(code)}</span>
                <br />
                <code className="audit-muted">{code}</code>
              </li>
            ))}
          </ul>
          {source.captureIds.map((id) => (
            <label className="audit-choice" key={id}>
              <input
                type="checkbox"
                checked={selected.includes(id)}
                disabled={!onSelect}
                onChange={() => onSelect?.(id)}
              />
              Retained capture {id}
            </label>
          ))}
        </div>
      ))}
      <p className="audit-muted">
        Inclusive window limit: {readiness.maximumRunWindowDays} days. Snapshot limit:{' '}
        <span className="audit-exact">{snapshotLimit}</span>.
      </p>
    </section>
  )
}
