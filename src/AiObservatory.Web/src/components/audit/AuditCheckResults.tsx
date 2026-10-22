import type { AuditCheckResult } from '../../api/auditTypes'
import { auditCheckLabels } from './auditLabels'

export function AuditCheckResults({
  checks,
}: Readonly<{ checks: AuditCheckResult[] | null | undefined }>) {
  return (
    <section className="audit-check-results" aria-label="Check results">
      <h2>Check results</h2>
      {!checks?.length ? (
        <p className="audit-muted">Per-check results are unavailable for this report.</p>
      ) : (
        <ul className="audit-check-list">
          {checks.map((check) => (
            <li key={check.code}>
              <div className="audit-heading">
                <h3>{auditCheckLabels[check.code]}</h3>
                <strong className={`audit-verdict audit-verdict--${check.verdict}`}>
                  {check.verdict}
                </strong>
              </div>
              <p className="audit-muted">
                {check.verdict === 'excluded'
                  ? 'Excluded from this scope'
                  : !check.supported
                    ? 'Unsupported for this evidence'
                    : check.executed
                      ? 'Supported · executed'
                      : 'Not executed'}
              </p>
              <p className="audit-exact">
                {check.coverage.comparedGroups} / {check.coverage.totalGroups} groups compared
              </p>
              {check.coverage.detailsAvailable === false ? (
                <p className="audit-muted">
                  Coverage qualifications are unavailable in this legacy summary.
                </p>
              ) : (
                <>
                  <ul
                    className="audit-diagnostics"
                    aria-label={`${auditCheckLabels[check.code]} qualifications`}
                  >
                    {check.coverage.diagnosticCodes.map((code) => (
                      <li key={code}>
                        <code>{code}</code>
                      </li>
                    ))}
                  </ul>
                  {!!check.coverage.exclusions?.length && (
                    <div>
                      <p className="audit-muted">Excluded fields</p>
                      <ul className="audit-diagnostics">
                        {check.coverage.exclusions.map((exclusion) => (
                          <li key={`${exclusion.field}:${exclusion.diagnosticCode}`}>
                            <code>{exclusion.field}</code> · {exclusion.diagnosticCode}
                          </li>
                        ))}
                      </ul>
                    </div>
                  )}
                </>
              )}
            </li>
          ))}
        </ul>
      )}
    </section>
  )
}
