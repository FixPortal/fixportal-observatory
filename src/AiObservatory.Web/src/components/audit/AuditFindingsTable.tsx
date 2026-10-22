import { useAuditFindings } from '../../api/auditQueries'
import { Button } from '../../design/Button'
import { auditCheckLabels } from './auditLabels'

export function AuditFindingsTable({ id, ready }: { id: string; ready: boolean }) {
  const findings = useAuditFindings(id, ready)
  if (!ready) return null
  return (
    <section aria-label="Discrepancy findings">
      <h3>Findings and coverage diagnostics</h3>
      {findings.isPending && <p role="status">Loading findings…</p>}
      {findings.error && <p role="alert">Findings unavailable: {findings.error.message}</p>}
      {findings.data && (
        <div
          className="audit-table-scroll"
          tabIndex={0}
          role="region"
          aria-label="Scrollable findings"
        >
          <table className="audit-table" aria-label="Audit findings">
            <thead>
              <tr>
                {[
                  'Check / diagnostic',
                  'Field',
                  'Expected',
                  'Actual',
                  'Delta',
                  'Unit / currency',
                  'Basis',
                  'Count',
                ].map((label) => (
                  <th key={label}>{label}</th>
                ))}
              </tr>
            </thead>
            <tbody>
              {findings.data.pages.flatMap((page, pageIndex) =>
                page.items.map((finding, index) => (
                  <tr key={`${pageIndex}-${index}`}>
                    <td>
                      <strong>{auditCheckLabels[finding.value.checkCode]}</strong>
                      <br />
                      <code>{finding.value.diagnosticCode}</code>
                      <br />
                      <span className="audit-muted">{finding.value.ruleCode}</span>
                    </td>
                    <td>
                      <code>{finding.value.field}</code>
                    </td>
                    <td className="audit-exact">
                      {finding.value.expected?.value ?? 'Not observed'}
                    </td>
                    <td className="audit-exact">{finding.value.actual?.value ?? 'Not observed'}</td>
                    <td className="audit-exact">
                      {finding.value.delta?.value ?? 'Not comparable'}
                    </td>
                    <td>
                      {finding.value.unit}
                      <br />
                      {finding.value.currency ?? '—'}
                    </td>
                    <td>{finding.value.basis ?? '—'}</td>
                    <td className="audit-exact">{finding.count}</td>
                  </tr>
                )),
              )}
            </tbody>
          </table>
        </div>
      )}
      {findings.data?.pages.every((page) => !page.items.length) && <p>No retained findings.</p>}
      {findings.hasNextPage && (
        <Button
          variant="ghost"
          disabled={findings.isFetchingNextPage}
          onClick={() => {
            void findings.fetchNextPage()
          }}
        >
          Load more findings
        </Button>
      )}
    </section>
  )
}
