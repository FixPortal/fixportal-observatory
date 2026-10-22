import { useEffect, useRef, useState } from 'react'
import { downloadAudit, getAuditEvidence } from '../../api/auditClient'
import { isActiveAudit, useAuditActions, useAuditRun } from '../../api/auditQueries'
import type { AuditEvidencePage } from '../../api/auditTypes'
import { systemClock } from '../../lib/clock'
import { Button } from '../../design/Button'
import { CollapsiblePanel } from '../CollapsiblePanel'
import { AuditCoverage } from './AuditCoverage'
import { AuditFindingsTable } from './AuditFindingsTable'
import { auditCheckLabels } from './auditLabels'

export function AuditRunDetails({
  id,
  readOnly,
  onRun,
}: {
  id: string
  readOnly: boolean
  onRun: (id: string) => void
}) {
  const query = useAuditRun(id)
  const heading = useRef<HTMLHeadingElement>(null)
  useEffect(() => {
    if (query.isSuccess) heading.current?.focus()
  }, [id, query.isSuccess])
  const actions = useAuditActions()
  const [acknowledged, setAcknowledged] = useState(false)
  const [downloadError, setDownloadError] = useState('')
  const [downloading, setDownloading] = useState(false)
  const [probeNotice, setProbeNotice] = useState('')
  const [evidence, setEvidence] = useState<AuditEvidencePage | null>(null)
  const [loadingEvidence, setLoadingEvidence] = useState(false)
  const run = query.data
  const download = async (kind: 'report' | 'bundle') => {
    setDownloadError('')
    setDownloading(true)
    try {
      await downloadAudit(id, kind)
    } catch (error) {
      setDownloadError(error instanceof Error ? error.message : 'Download unavailable')
    } finally {
      setDownloading(false)
    }
  }
  const showEvidence = async () => {
    setLoadingEvidence(true)
    setDownloadError('')
    try {
      const page = await getAuditEvidence(id, evidence ? evidence.nextCursor : null)
      setEvidence((prior) => (prior ? { ...page, items: [...prior.items, ...page.items] } : page))
    } catch (error) {
      setDownloadError(error instanceof Error ? error.message : 'Evidence unavailable')
    } finally {
      setLoadingEvidence(false)
    }
  }
  if (query.isPending)
    return (
      <section className="panel" role="status">
        Loading audit…
      </section>
    )
  if (!run)
    return (
      <section className="panel" role="alert">
        Audit unavailable: {query.error.message}
      </section>
    )
  const active = isActiveAudit(run)
  const presentationRequested =
    run.scope.requestedChecks.includes('presentationTotals') &&
    !run.scope.excludedChecks.includes('presentationTotals')
  const collecting = run.state === 'collectingEvidence' || run.state === 'collecting'
  const expired =
    run.replayExpiresAt !== null &&
    new Date(run.replayExpiresAt).getTime() <= systemClock.now().getTime()
  const actionError =
    actions.cancel.error ?? actions.proceed.error ?? actions.replay.error ?? actions.probe.error
  return (
    <section className="panel audit-details" aria-label="Selected audit">
      <div className="audit-heading">
        <div>
          <h2 ref={heading} tabIndex={-1}>
            {active ? 'Audit in progress' : 'Audit outcome'}
          </h2>
          <p className="audit-muted audit-exact">{run.id}</p>
        </div>
        <span className="audit-stage">{run.state}</span>
      </div>
      {query.error && (
        <p role="alert">Refresh failed; retained data may be stale: {query.error.message}</p>
      )}
      <p className="audit-exact">
        {run.scope.from} → {run.scope.to} · UTC
      </p>
      <p className="audit-muted">
        Sources: {run.scope.sources.map((s) => s.sourceId).join(', ')}. Checks:{' '}
        {run.scope.requestedChecks.map((check) => auditCheckLabels[check]).join(', ')}.
      </p>
      <AuditCoverage coverage={run.coverage} verdict={run.verdict} />
      {active && (
        <p role="status">
          {collecting
            ? 'Waiting for machines to seal requested captures. Offline machines remain waiting until the deadline, cancellation or explicit partial proceed.'
            : `Current stage: ${run.stage}. Active runs refresh every 2 seconds while this tab is visible.`}
        </p>
      )}
      {run.diagnosticCode && (
        <p>
          <code>{run.diagnosticCode}</code>
        </p>
      )}
      <dl className="audit-metadata">
        <div>
          <dt>Server cutoff</dt>
          <dd>{run.serverCutoff ?? 'Not frozen yet'}</dd>
        </div>
        <div>
          <dt>Verifier / rules</dt>
          <dd>
            {run.verifierVersion ?? 'Pending'} / {run.ruleVersion ?? 'Pending'}
          </dd>
        </div>
        <div>
          <dt>Replay deadline</dt>
          <dd>
            {run.replayExpiresAt ?? 'Not reported'}
            {expired && ' · expired'}
          </dd>
        </div>
        <div>
          <dt>Input digest</dt>
          <dd>{run.inputContentDigest ?? 'Not frozen yet'}</dd>
        </div>
      </dl>
      {run.evaluationOfRunId && (
        <Button
          variant="text"
          onClick={() => {
            if (run.evaluationOfRunId) onRun(run.evaluationOfRunId)
          }}
        >
          Open original audit {run.evaluationOfRunId}
        </Button>
      )}
      {run.predecessorRunId && (
        <Button
          variant="text"
          onClick={() => {
            if (run.predecessorRunId) onRun(run.predecessorRunId)
          }}
        >
          Open predecessor {run.predecessorRunId}
        </Button>
      )}
      {!readOnly && (
        <div className="audit-operations">
          {collecting && run.scope.allowPartial && (
            <>
              <label className="audit-choice">
                <input
                  type="checkbox"
                  checked={acknowledged}
                  onChange={(e) => {
                    setAcknowledged(e.target.checked)
                  }}
                />
                I acknowledge missing captures and incomplete coverage
              </label>
              <Button
                variant="ghost"
                disabled={!acknowledged || actions.proceed.isPending}
                onClick={() => {
                  actions.proceed.mutate(id)
                }}
              >
                Proceed with partial evidence
              </Button>
            </>
          )}
          <div className="audit-actions">
            {active && (
              <Button
                variant="danger"
                disabled={actions.cancel.isPending}
                onClick={() => {
                  actions.cancel.mutate(id)
                }}
              >
                {actions.cancel.isPending ? 'Cancelling…' : 'Cancel audit'}
              </Button>
            )}
            {run.state === 'completed' && (
              <>
                <Button
                  variant="ghost"
                  disabled={expired || actions.replay.isPending}
                  onClick={() => {
                    actions.replay.mutate(id, {
                      onSuccess: (value) => {
                        onRun(value.runId)
                      },
                    })
                  }}
                >
                  Replay retained inputs
                </Button>
                <Button
                  disabled={expired || !presentationRequested || actions.probe.isPending}
                  onClick={() => {
                    actions.probe.mutate(id, {
                      onSuccess: (value) => {
                        setProbeNotice(value.diagnosticCodes.join(', '))
                        onRun(value.runId)
                      },
                    })
                  }}
                >
                  {actions.probe.isPending
                    ? 'Verifying displayed totals…'
                    : 'Verify displayed totals'}
                </Button>
                <Button
                  variant="ghost"
                  disabled={downloading}
                  onClick={() => {
                    void download('report')
                  }}
                >
                  Download report
                </Button>
                <Button
                  variant="ghost"
                  disabled={downloading}
                  onClick={() => {
                    void download('bundle')
                  }}
                >
                  Export evidence bundle
                </Button>
              </>
            )}
          </div>
          {run.state === 'completed' && (
            <p className="audit-muted">
              Displayed-total verification observes this frontend’s production summary against all
              frozen aggregate and spend pages. Native-currency totals are unavailable in that
              summary and remain incomplete. This is a declared observation, not external
              attestation.
            </p>
          )}
        </div>
      )}
      {!readOnly && run.state === 'completed' && !presentationRequested && (
        <p className="audit-muted">
          Include Displayed totals in a new audit scope before submitting a presentation
          observation.
        </p>
      )}
      {probeNotice && <p role="status">{probeNotice}</p>}
      {(actionError ?? downloadError) && (
        <p role="alert">{actionError?.message ?? downloadError}</p>
      )}
      <AuditFindingsTable id={id} ready={run.state === 'completed'} />
      {!readOnly && run.state === 'completed' && (
        <CollapsiblePanel
          id={`audit-evidence-${id}`}
          title="Detailed sanitized evidence"
          summary="Administrator only"
        >
          <div className="audit-machine">
            <p className="audit-muted">
              Frozen records and reference mapping. Counts and money retain their exact wire
              strings.
            </p>
            {evidence?.items.map((item, index) => (
              <details key={index}>
                <summary>
                  {item.kind} · record {String(index + 1)}
                </summary>
                <pre className="audit-json">{JSON.stringify(item.record, null, 2)}</pre>
              </details>
            ))}
            {(!evidence || evidence.nextCursor) && (
              <Button
                variant="ghost"
                disabled={loadingEvidence}
                onClick={() => {
                  void showEvidence()
                }}
              >
                {loadingEvidence ? 'Loading evidence…' : 'Load detailed evidence'}
              </Button>
            )}
          </div>
        </CollapsiblePanel>
      )}
    </section>
  )
}
