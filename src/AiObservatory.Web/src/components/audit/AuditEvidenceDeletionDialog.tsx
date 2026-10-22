import { useEffect, useRef, useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { ApiError } from '../../api/client'
import { deleteAuditCapture } from '../../api/auditClient'
import type { AuditDeletionImpact } from '../../api/auditTypes'
import { Button } from '../../design/Button'

export function AuditEvidenceDeletionDialog({
  impact,
  onClose,
  onRefresh,
}: {
  readonly impact: AuditDeletionImpact
  readonly onClose: () => void
  readonly onRefresh: () => void
}) {
  const [confirmed, setConfirmed] = useState(false)
  const heading = useRef<HTMLHeadingElement>(null)
  useEffect(() => {
    heading.current?.focus()
  }, [])
  const client = useQueryClient()
  const deletion = useMutation({
    mutationFn: () => deleteAuditCapture(impact),
    onSuccess: async () => {
      await client.invalidateQueries({
        predicate: (q) => typeof q.queryKey[0] === 'string' && q.queryKey[0].startsWith('audit-'),
      })
      onClose()
    },
    onError: () => {
      setConfirmed(false)
    },
  })
  const stale = deletion.error instanceof ApiError && deletion.error.status === 412
  return (
    <section
      className="audit-notice"
      role="dialog"
      aria-labelledby="audit-deletion-heading"
      onKeyDown={(e) => {
        if (e.key === 'Escape' && !deletion.isPending) onClose()
      }}
    >
      <h3 id="audit-deletion-heading" ref={heading} tabIndex={-1}>
        Delete retained evidence
      </h3>
      <p className="audit-exact">Capture: {impact.captureId}</p>
      <p>
        This capture affects {impact.affectedRunIds.length} audit runs. Replay and evidence
        downloads will be lost immediately; active downloads will be cancelled. Ordinary usage and
        spend records remain.
      </p>
      <ul>
        {impact.affectedRunIds.map((id) => (
          <li className="audit-exact" key={id}>
            {id}
          </li>
        ))}
      </ul>
      <p>
        Save any bundle you need first. Existing downloaded copies and backups are outside this
        deletion; restoring a backup may restore evidence. A retained report is a sanitized summary
        and cannot recreate the evidence.
      </p>
      <label className="audit-choice">
        <input
          type="checkbox"
          checked={confirmed}
          disabled={deletion.isPending || stale}
          onChange={(e) => {
            setConfirmed(e.target.checked)
          }}
        />
        I understand replay will be lost and want to delete this evidence.
      </label>
      <div className="audit-actions">
        <Button
          variant="danger"
          disabled={!confirmed || deletion.isPending || stale}
          onClick={() => {
            deletion.mutate()
          }}
        >
          {deletion.isPending ? 'Cancelling downloads...' : 'Delete evidence permanently'}
        </Button>
        <Button variant="ghost" disabled={deletion.isPending} onClick={onClose}>
          Keep evidence
        </Button>
        {stale && (
          <Button variant="ghost" onClick={onRefresh}>
            Refresh impact
          </Button>
        )}
      </div>
      {deletion.error && (
        <p role="alert">
          {stale
            ? 'Affected runs changed. Refresh impact and confirm again.'
            : deletion.error.message}
        </p>
      )}
    </section>
  )
}
