import { useAuditRuns } from '../../api/auditQueries'
import { Button } from '../../design/Button'

export function AuditRunHistory({
  selectedId,
  onSelect,
}: {
  selectedId: string | null
  onSelect: (id: string) => void
}) {
  const history = useAuditRuns()
  const runs = history.data?.pages.flatMap((page) => page.items) ?? []
  return (
    <section className="panel" aria-label="Audit history">
      <h2>Retained audits</h2>
      <p className="audit-muted">
        Each replay or displayed-total observation creates a linked evaluation.
      </p>
      {history.isPending && <p role="status">Loading audits…</p>}
      {history.error && <p role="alert">History unavailable: {history.error.message}</p>}
      {!history.isPending && !history.error && !runs.length && (
        <p>No retained audits. Choose a scope above to start.</p>
      )}
      <ol className="audit-history">
        {runs.map((run) => (
          <li key={run.id}>
            <button
              className={`audit-history-row${selectedId === run.id ? ' audit-history-row--selected' : ''}`}
              type="button"
              aria-label={`Open audit ${run.id}`}
              aria-pressed={selectedId === run.id}
              onClick={() => {
                onSelect(run.id)
              }}
            >
              <span>
                <strong>{run.verdict ?? run.state}</strong>
                <span className="audit-muted audit-exact">
                  {run.scope.from} → {run.scope.to}
                </span>
              </span>
              <span className="audit-muted audit-exact">
                {run.createdAt}
                <br />
                {run.id}
              </span>
            </button>
          </li>
        ))}
      </ol>
      {history.hasNextPage && (
        <Button
          variant="ghost"
          disabled={history.isFetchingNextPage}
          onClick={() => {
            void history.fetchNextPage()
          }}
        >
          Load older audits
        </Button>
      )}
    </section>
  )
}
