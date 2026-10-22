import type { AuditCoverageDto, AuditVerdict } from '../../api/auditTypes'

export function AuditCoverage({
  coverage,
  verdict,
}: {
  coverage: AuditCoverageDto | null
  verdict: AuditVerdict | null
}) {
  return (
    <div className="audit-coverage">
      <strong className={`audit-verdict audit-verdict--${verdict ?? 'pending'}`}>
        {verdict ?? 'Pending evaluation'}
      </strong>
      {coverage ? (
        <>
          <p className="audit-exact">
            {coverage.comparedGroups} / {coverage.totalGroups} groups compared
          </p>
          <ul className="audit-diagnostics">
            {coverage.diagnosticCodes.map((code) => (
              <li key={code}>
                <code>{code}</code>
              </li>
            ))}
          </ul>
        </>
      ) : (
        <p className="audit-muted">
          Coverage is available after evaluation. A selected range is not a completeness claim.
        </p>
      )}
    </div>
  )
}
