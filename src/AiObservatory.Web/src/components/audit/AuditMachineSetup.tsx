import { useState } from 'react'
import { useMutation } from '@tanstack/react-query'
import {
  issueAuditCredential,
  revokeAuditCredential,
  rotateAuditCredential,
} from '../../api/auditClient'
import type { AuditCredential } from '../../api/auditTypes'
import { Button } from '../../design/Button'
import { CollapsiblePanel } from '../CollapsiblePanel'

export function AuditMachineSetup() {
  const [machineId, setMachineId] = useState('')
  const [credential, setCredential] = useState<AuditCredential | null>(null)
  const [notice, setNotice] = useState('')
  const issued = (value: AuditCredential) => {
    setMachineId(value.machineId)
    setCredential(value)
    setNotice('')
  }
  const issue = useMutation({ mutationFn: issueAuditCredential, onSuccess: issued })
  const rotate = useMutation({ mutationFn: rotateAuditCredential, onSuccess: issued })
  const revoke = useMutation({
    mutationFn: revokeAuditCredential,
    onSuccess: () => {
      setCredential(null)
      setNotice('Credential revoked. Existing capture leases can no longer upload.')
    },
  })
  const pending = issue.isPending || rotate.isPending || revoke.isPending
  const error = issue.error ?? rotate.error ?? revoke.error
  return (
    <CollapsiblePanel id="audit-machines" title="Machine credentials" summary="Companion setup">
      <div className="audit-machine">
        <p>
          Issue one scoped credential for the installed capture companion. The secret is shown once
          and is held only in this open view. Store it in the companion’s protected configuration;
          use the X-Observatory-Audit-Key header.
        </p>
        <Button
          variant="ghost"
          disabled={pending}
          onClick={() => {
            issue.mutate()
          }}
        >
          Issue machine credential
        </Button>
        <label className="audit-field">
          Machine ID to manage
          <input
            value={machineId}
            onChange={(e) => {
              setMachineId(e.target.value.trim())
              setCredential(null)
            }}
            spellCheck={false}
          />
        </label>
        <div className="audit-actions">
          <Button
            variant="ghost"
            disabled={!machineId || pending}
            onClick={() => {
              setCredential(null)
              rotate.mutate(machineId)
            }}
          >
            Rotate credential
          </Button>
          <Button
            variant="danger"
            disabled={!machineId || pending}
            onClick={() => {
              revoke.mutate(machineId)
            }}
          >
            Revoke credential
          </Button>
        </div>
        {credential && (
          <div className="audit-secret" role="status">
            <strong>Save this credential now. It cannot be retrieved again.</strong>
            <p className="audit-exact">Machine ID: {credential.machineId}</p>
            <code>{credential.secret}</code>
            <div>
              <Button
                variant="ghost"
                onClick={() => {
                  setCredential(null)
                }}
              >
                I saved this credential
              </Button>
            </div>
          </div>
        )}
        {notice && <p role="status">{notice}</p>}
        {error && <p role="alert">Credential action failed: {error.message}</p>}
      </div>
    </CollapsiblePanel>
  )
}
