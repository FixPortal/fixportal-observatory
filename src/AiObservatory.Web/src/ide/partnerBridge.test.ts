import { beforeEach, describe, expect, test, vi } from 'vitest'
import { createEmbeddedBridge, type WebViewPort } from './partnerBridge'

describe('embedded partner bridge', () => {
  let listener: ((event: MessageEvent) => void) | null
  let port: WebViewPort
  const postMessage = vi.fn()

  beforeEach(() => {
    listener = null
    postMessage.mockReset()
    port = {
      postMessage,
      addEventListener: (_kind, next) => { listener = next },
      removeEventListener: (_kind, next) => { if (listener === next) listener = null },
    }
  })

  test('announces readiness without an action capability', () => {
    const bridge = createEmbeddedBridge(port)

    bridge.postReady()

    expect(postMessage).toHaveBeenCalledWith(expect.objectContaining({
      contractVersion: '1.0',
      kind: 'partner.ready',
      authority: 'workspace',
      payload: expect.objectContaining({ capabilities: [], connection: 'ready' }),
    }))
  })

  test('accepts only the bounded host context message', () => {
    const bridge = createEmbeddedBridge(port)
    const observed: Array<string | null> = []
    bridge.subscribe(context => observed.push(context?.repository ?? null))

    listener?.(new MessageEvent('message', { data: {
      contractVersion: '1.0',
      kind: 'host.context',
      payload: {
        partnerId: { value: '753cb584-cd0b-4e16-9f08-6c0ce130a84a' },
        contextMode: 0,
        context: {
          missionId: { value: '11111111-1111-1111-1111-111111111111' },
          repository: 'fixportal-ide',
        },
        capabilities: [],
        connection: 1,
        observedAt: '2026-08-04T12:00:00Z',
      },
    } }))

    expect(bridge.context).toEqual({
      missionId: '11111111-1111-1111-1111-111111111111',
      repository: 'fixportal-ide',
    })
    expect(observed).toEqual(['fixportal-ide'])
  })
})
