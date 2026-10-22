import { useState } from 'react'
import type { AuditCapabilities, AuditCheck, AuditScope } from '../../api/auditTypes'
import { useAuditActions, useAuditReadiness } from '../../api/auditQueries'
import { systemClock, type Clock } from '../../lib/clock'
import { Button } from '../../design/Button'
import { DateRangePicker } from '../DateRangePicker'
import { CollapsiblePanel } from '../CollapsiblePanel'
import { AuditReadiness } from './AuditReadiness'
import { auditCheckLabels } from './auditLabels'

function completedDays(days: number, clock: Clock) {
  const today = clock.now()
  const end = Date.UTC(today.getUTCFullYear(), today.getUTCMonth(), today.getUTCDate()) - 86400000
  return { from: new Date(end - (days - 1) * 86400000), to: new Date(end) }
}
function toggle<T>(items: T[], item: T) {
  return items.includes(item) ? items.filter((value) => value !== item) : [...items, item]
}

export function AuditRunWizard({
  capabilities,
  readOnly,
  onRun,
  clock = systemClock,
}: {
  capabilities: AuditCapabilities
  readOnly: boolean
  onRun: (id: string) => void
  clock?: Clock
}) {
  const [dates, setDates] = useState(() => completedDays(7, clock))
  const [preset, setPreset] = useState<7 | 31 | 90 | 'custom'>(7)
  const [sources, setSources] = useState(['claude-local'])
  const [configuredSourceIds, setConfiguredSourceIds] = useState<Record<string, string>>({})
  const [checks, setChecks] = useState<AuditCheck[]>(['sourceAccounting'])
  const [machineId, setMachineId] = useState('')
  const [captureIds, setCaptureIds] = useState<string[]>([])
  const [allowPartial, setAllowPartial] = useState(false)
  const [fresh, setFresh] = useState(true)
  const actions = useAuditActions()
  const supportedSources = [
    ...new Map(
      capabilities.sources
        .filter((source) => source.supported)
        .map((source) => [source.tool, source]),
    ).values(),
  ]
  const days = (dates.to.getTime() - dates.from.getTime()) / 86400000 + 1
  const validDates = days >= 1 && days <= 31
  const selectedSourceIds = sources.map((source) => configuredSourceIds[source] ?? source)
  const validSources =
    selectedSourceIds.length > 0 &&
    new Set(selectedSourceIds).size === selectedSourceIds.length &&
    selectedSourceIds.every(
      (source) => /^[A-Za-z0-9_:.@|=-]{1,256}$/.test(source) && !source.includes('..'),
    )
  const scope: AuditScope = {
    from: dates.from.toISOString().slice(0, 10),
    to: dates.to.toISOString().slice(0, 10),
    sources: [...selectedSourceIds].sort().map((sourceId) => ({ machineId, sourceId })),
    requestedChecks: [...checks].sort(),
    excludedChecks: capabilities.checks
      .map((check) => check.code)
      .filter((code) => !checks.includes(code))
      .sort(),
    allowPartial,
    predecessorRunId: null,
    evaluationOfRunId: null,
  }
  const readiness = useAuditReadiness(scope, validDates && validSources)
  const validMachine =
    /^(?!00000000-0000-0000-0000-000000000000)[\da-f]{8}-(?:[\da-f]{4}-){3}[\da-f]{12}$/i.test(
      machineId,
    )
  const create = async () => {
    const available = new Set(readiness.data?.sources.flatMap((source) => source.captureIds))
    const run = await actions.create.mutateAsync({
      scope,
      captureIds: captureIds.filter((id) => available.has(id)),
      requestFreshCapture: fresh,
    })
    onRun(run.runId)
  }
  return (
    <section className="panel audit-scope" aria-label="Audit scope">
      <div className="audit-heading">
        <div>
          <h2>Choose the evidence to compare</h2>
          <p className="audit-muted">
            {scope.from} → {scope.to} · UTC, inclusive
          </p>
        </div>
        <DateRangePicker
          from={dates.from}
          to={dates.to}
          preset={preset}
          onPreset={(days) => {
            setPreset(days)
            setDates(completedDays(days, clock))
          }}
          onCustom={(from, to) => {
            setPreset('custom')
            setDates({ from, to })
          }}
        />
      </div>
      {!validDates && <p role="alert">Choose an inclusive range of 1–31 days.</p>}
      <div className="audit-scope-grid">
        <fieldset>
          <legend>Sources</legend>
          {supportedSources.map((source) => (
            <div key={source.tool}>
              <label className="audit-choice">
                <input
                  type="checkbox"
                  checked={sources.includes(`${source.tool}-local`)}
                  disabled={!source.supported}
                  onChange={() => {
                    setSources((items) => toggle(items, `${source.tool}-local`))
                  }}
                />
                {source.tool}-local {source.supported ? '' : '· unsupported'}
              </label>
              {sources.includes(`${source.tool}-local`) && (
                <label className="audit-field">
                  Configured source ID for {source.tool}
                  <input
                    value={configuredSourceIds[`${source.tool}-local`] ?? `${source.tool}-local`}
                    maxLength={256}
                    spellCheck={false}
                    onChange={(e) => {
                      setConfiguredSourceIds((prior) => ({
                        ...prior,
                        [`${source.tool}-local`]: e.target.value.trim(),
                      }))
                      setCaptureIds([])
                    }}
                  />
                </label>
              )}
              {source.reasonCode && <code className="audit-muted">{source.reasonCode}</code>}
            </div>
          ))}
          {capabilities.sources.some((source) => !source.supported) && (
            <CollapsiblePanel
              id="audit-unsupported"
              title="Unsupported source coverage"
              summary="View limits"
            >
              <div className="audit-machine">
                {capabilities.sources
                  .filter((source) => !source.supported)
                  .map((source) => (
                    <p key={source.tool}>
                      <strong>{source.tool} · unsupported</strong>
                      <br />
                      <code className="audit-muted">{source.reasonCode}</code>
                    </p>
                  ))}
              </div>
            </CollapsiblePanel>
          )}
          <p className="audit-muted">
            Use the exact source ID from your companion configuration and source inventory. Each
            selected source needs its own ID; the extractor name does not identify a machine.
          </p>
          {!validSources && <p role="alert">Choose distinct, nonempty configured source IDs.</p>}
        </fieldset>
        <fieldset>
          <legend>Checks</legend>
          {capabilities.checks.map((check) => (
            <div key={check.code}>
              <label className="audit-choice">
                <input
                  type="checkbox"
                  checked={checks.includes(check.code)}
                  disabled={!check.supported}
                  onChange={() => {
                    setChecks((items) => toggle(items, check.code))
                  }}
                />
                {auditCheckLabels[check.code]} {check.supported ? '' : '· unsupported'}
              </label>
              {check.reasonCode && <code className="audit-muted">{check.reasonCode}</code>}
            </div>
          ))}
        </fieldset>
      </div>
      {!readOnly && (
        <>
          <label className="audit-field">
            Machine ID for this scope
            <input
              value={machineId}
              placeholder="Registered machine UUID"
              onChange={(e) => {
                setMachineId(e.target.value.trim())
              }}
              spellCheck={false}
            />
          </label>
          <p className="audit-muted">
            Enter an existing registered machine ID, or issue one below. Retained choices can
            include other machines; the server validates ownership and scope.
          </p>
          <label className="audit-choice">
            <input
              type="checkbox"
              checked={fresh}
              onChange={(e) => {
                setFresh(e.target.checked)
              }}
            />
            Request fresh captures · wait for offline machines
          </label>
          <label className="audit-choice">
            <input
              type="checkbox"
              checked={allowPartial}
              onChange={(e) => {
                setAllowPartial(e.target.checked)
              }}
            />
            Allow partial evidence after explicit acknowledgement
          </label>
        </>
      )}
      {readiness.isPending && validDates && validSources && (
        <p role="status">Checking readiness…</p>
      )}
      {readiness.error && <p role="alert">Readiness unavailable: {readiness.error.message}</p>}
      {readiness.data && validDates && validSources && (
        <AuditReadiness
          readiness={readiness.data}
          selected={captureIds}
          {...(!readOnly
            ? {
                onSelect: (id: string) => {
                  setCaptureIds((items) => toggle(items, id))
                },
              }
            : {})}
        />
      )}
      {actions.create.error && (
        <p role="alert">Couldn’t start audit: {actions.create.error.message}</p>
      )}
      {!readOnly && (
        <Button
          disabled={
            !validDates ||
            !validMachine ||
            !validSources ||
            !checks.length ||
            !readiness.data ||
            readiness.isFetching ||
            actions.create.isPending
          }
          onClick={() => {
            void create().catch(() => undefined)
          }}
        >
          {actions.create.isPending ? 'Starting audit…' : 'Start audit'}
        </Button>
      )}
    </section>
  )
}
