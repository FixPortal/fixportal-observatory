export type HostContext = {
  missionId: string | null
  repository: string | null
}

export type WebViewPort = {
  postMessage(message: unknown): void
  addEventListener(kind: 'message', listener: (event: MessageEvent) => void): void
  removeEventListener(kind: 'message', listener: (event: MessageEvent) => void): void
}

export type EmbeddedBridge = {
  readonly context: HostContext | null
  postReady(): void
  subscribe(listener: (context: HostContext | null) => void): () => void
  dispose(): void
}

const partnerId = '753cb584-cd0b-4e16-9f08-6c0ce130a84a'

export function createEmbeddedBridge(port: WebViewPort): EmbeddedBridge {
  let current: HostContext | null = null
  const subscribers = new Set<(context: HostContext | null) => void>()
  const receive = (event: MessageEvent) => {
    const next = readContext(event.data)
    if (!next) return
    current = next
    subscribers.forEach(listener => { listener(current) })
  }
  port.addEventListener('message', receive)
  return {
    get context() { return current },
    postReady() {
      port.postMessage({
        contractVersion: '1.0',
        kind: 'partner.ready',
        authority: 'workspace',
        payload: { capabilities: [], connection: 'ready', observedAt: new Date().toISOString() },
      })
    },
    subscribe(listener) {
      subscribers.add(listener)
      return () => { subscribers.delete(listener) }
    },
    dispose() {
      subscribers.clear()
      port.removeEventListener('message', receive)
    },
  }
}

export function windowWebViewPort(): WebViewPort | null {
  const host = window as Window & { chrome?: { webview?: WebViewPort } }
  return host.chrome?.webview ?? null
}

function readContext(value: unknown): HostContext | null {
  if (!value || typeof value !== 'object') return null
  const message = value as Record<string, unknown>
  if (message.contractVersion !== '1.0' || message.kind !== 'host.context') return null
  const payload = message.payload
  if (!payload || typeof payload !== 'object') return null
  const state = payload as Record<string, unknown>
  const id = state.partnerId as { value?: unknown } | undefined
  if (id?.value !== partnerId) return null
  const context = state.context
  if (context === null) return { missionId: null, repository: null }
  if (!context || typeof context !== 'object') return null
  const fields = context as Record<string, unknown>
  const mission = fields.missionId as { value?: unknown } | null | undefined
  const missionId = typeof mission?.value === 'string' ? mission.value : null
  const repository = typeof fields.repository === 'string' ? fields.repository : null
  return { missionId, repository }
}
