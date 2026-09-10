import { useEffect, useState, type ReactNode } from 'react'
import { isEmbedded } from '../auth/msal'
import { createEmbeddedBridge, type HostContext, windowWebViewPort } from './partnerBridge'

export default function EmbeddedContext({ children }: { children: ReactNode }) {
  const [context, setContext] = useState<HostContext | null>(null)

  useEffect(() => {
    const port = isEmbedded ? windowWebViewPort() : null
    const bridge = port ? createEmbeddedBridge(port) : null
    const unsubscribe = bridge?.subscribe(setContext)
    bridge?.postReady()
    return () => {
      unsubscribe?.()
      bridge?.dispose()
    }
  }, [])

  if (!isEmbedded) return <>{children}</>
  const label = context?.missionId
    ? `Following mission ${context.missionId}`
    : context?.repository
      ? `Following repository ${context.repository}`
      : 'Global Observatory'
  return <><div className="embedded-context" role="status">{label}</div>{children}</>
}
