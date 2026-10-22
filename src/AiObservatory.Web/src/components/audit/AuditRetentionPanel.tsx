import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import {
  getAuditDeletionImpact,
  getAuditRetention,
  updateAuditRetention,
} from '../../api/auditClient'
import type { AuditDeletionImpact, AuditRetention } from '../../api/auditTypes'
import { Button } from '../../design/Button'
import { CollapsiblePanel } from '../CollapsiblePanel'
import { AuditEvidenceDeletionDialog } from './AuditEvidenceDeletionDialog'

const supported = (value: number | null) =>
  value === null || (Number.isInteger(value) && value >= 1 && value <= 36500)
function RetentionFields({
  value,
  onChange,
}: {
  readonly value: AuditRetention
  readonly onChange: (v: AuditRetention) => void
}) {
  return (
    <div className="audit-scope-grid">
      {(
        [
          {
            key: 'evidenceDays',
            label: 'Detailed evidence',
            indefinite: 'Keep detailed evidence indefinitely',
          },
          {
            key: 'summaryDays',
            label: 'Report summary',
            indefinite: 'Keep report summaries indefinitely',
          },
        ] as const
      ).map((field) => (
        <div key={field.key}>
          <label className="audit-field">
            {field.label} days
            <input
              type="number"
              min="1"
              max="36500"
              value={value[field.key] ?? ''}
              disabled={value[field.key] === null}
              onChange={(e) => {
                onChange({
                  ...value,
                  [field.key]: e.target.value === '' ? 0 : Number(e.target.value),
                })
              }}
            />
          </label>
          <label className="audit-choice">
            <input
              type="checkbox"
              checked={value[field.key] === null}
              onChange={(e) => {
                onChange({
                  ...value,
                  [field.key]: e.target.checked ? null : field.key === 'evidenceDays' ? 90 : 365,
                })
              }}
            />
            {field.indefinite}
          </label>
        </div>
      ))}
    </div>
  )
}
export function AuditRetentionPanel() {
  const query = useQuery({
    queryKey: ['audit-retention'],
    queryFn: getAuditRetention,
    retry: false,
  })
  const client = useQueryClient()
  const [draft, setDraft] = useState<AuditRetention | null>(null)
  const [captureId, setCaptureId] = useState('')
  const [impact, setImpact] = useState<AuditDeletionImpact | null>(null)
  const save = useMutation({
    mutationFn: updateAuditRetention,
    onSuccess: (value) => {
      client.setQueryData(['audit-retention'], value)
      setDraft(null)
    },
  })
  const review = useMutation({ mutationFn: getAuditDeletionImpact, onSuccess: setImpact })
  const value = draft ?? query.data
  const validId = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(captureId)
  return (
    <CollapsiblePanel
      id="audit-retention"
      title="Evidence retention"
      summary="Retention and deletion"
    >
      <p>
        Settings apply to future seals and completions. Existing deadlines and reused captures keep
        their original expiry. Detailed evidence defaults to 90 days from sealing; summaries default
        to 365 days from completion.
      </p>
      {query.isPending && <p role="status">Loading retention...</p>}
      {query.error && <p role="alert">{query.error.message}</p>}
      {value && (
        <>
          <RetentionFields value={value} onChange={setDraft} />
          <p className="audit-muted">
            Choose 1 to 36,500 days, or explicitly keep indefinitely. There are no hidden retention
            pins.
          </p>
          <Button
            variant="ghost"
            disabled={
              save.isPending || !supported(value.evidenceDays) || !supported(value.summaryDays)
            }
            onClick={() => {
              save.mutate(value)
            }}
          >
            Save retention
          </Button>
        </>
      )}
      {save.isSuccess && <p role="status">Future retention updated.</p>}
      {save.error && <p role="alert">{save.error.message}</p>}
      <h3>Delete a retained capture</h3>
      <p>
        Review every affected run before deleting shared evidence. Audit deletion leaves ordinary
        usage and spend records unchanged.
      </p>
      <label className="audit-field">
        Capture ID to delete
        <input
          disabled={review.isPending || impact !== null}
          value={captureId}
          spellCheck={false}
          onChange={(e) => {
            setCaptureId(e.target.value.trim())
            setImpact(null)
          }}
        />
      </label>
      <Button
        variant="ghost"
        disabled={!validId || review.isPending}
        onClick={() => {
          review.mutate(captureId)
        }}
      >
        Review deletion impact
      </Button>
      {review.error && <p role="alert">{review.error.message}</p>}
      {impact && (
        <AuditEvidenceDeletionDialog
          key={impact.impactDigest}
          impact={impact}
          onClose={() => {
            setImpact(null)
          }}
          onRefresh={() => {
            setImpact(null)
            review.mutate(captureId)
          }}
        />
      )}
    </CollapsiblePanel>
  )
}
