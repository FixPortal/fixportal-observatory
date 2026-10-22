import { useState } from 'react'
import { useAuditCapabilities } from '../api/auditQueries'
import { isReadonly } from '../auth/msal'
import { AuditRunWizard } from '../components/audit/AuditRunWizard'
import { AuditRunHistory } from '../components/audit/AuditRunHistory'
import { AuditRunDetails } from '../components/audit/AuditRunDetails'
import { AuditRetentionPanel } from '../components/audit/AuditRetentionPanel'
import { AuditMachineSetup } from '../components/audit/AuditMachineSetup'

export function SelfAuditPage(): React.JSX.Element {
  const capabilities = useAuditCapabilities()
  const [selected, setSelected] = useState<string | null>(null)
  const demo = import.meta.env.VITE_DEMO_BANNER === 'true'
  const readOnly = isReadonly || demo
  if (capabilities.isPending) return <p role="status">Loading audit capabilities…</p>
  if (capabilities.error)
    return <p role="alert">Audit capabilities unavailable: {capabilities.error.message}</p>
  if (!capabilities.data?.enabled) return <p>Self-audit is disabled on this instance.</p>
  return (
    <div className="self-audit">
      <header>
        <p className="audit-eyebrow">Independent accounting · retained evidence</p>
        <h1>Self-audit</h1>
        <p className="audit-muted">
          Check a frozen scope, investigate discrepancies and replay the same evidence.
        </p>
        {demo ? (
          <p className="audit-notice">Synthetic audit data · read only</p>
        ) : (
          readOnly && <p className="audit-notice">Read-only audit view</p>
        )}
      </header>
      {selected && (
        <AuditRunDetails key={selected} id={selected} readOnly={readOnly} onRun={setSelected} />
      )}
      <AuditRunWizard capabilities={capabilities.data} readOnly={readOnly} onRun={setSelected} />
      <AuditRunHistory selectedId={selected} onSelect={setSelected} />
      {!readOnly && <AuditMachineSetup />}
      {!readOnly && <AuditRetentionPanel />}
    </div>
  )
}
