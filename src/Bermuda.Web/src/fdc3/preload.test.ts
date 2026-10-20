// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

type Listener = (e: MessageEvent) => void

/** A stand-in for WebView2's chrome.webview that records posts and lets the test play host. */
function stubWebView() {
  const listeners = new Set<Listener>()
  const posted: { id: string; command: string; payload?: unknown }[] = []
  window.chrome = {
    webview: {
      postMessage: (m: unknown) => { posted.push(m as (typeof posted)[number]) },
      addEventListener: (_: 'message', l: Listener) => { listeners.add(l) },
      removeEventListener: (_: 'message', l: Listener) => { listeners.delete(l) },
      hostObjects: {} as never,
    },
  }
  return {
    posted,
    deliver: (data: unknown) => { for (const l of listeners) l({ data } as MessageEvent) },
  }
}

describe('fdc3 preload', () => {
  beforeEach(() => {
    vi.resetModules()
    window.fdc3 = undefined
  })

  afterEach(() => {
    window.fdc3 = undefined
    Reflect.deleteProperty(window, 'chrome')
  })

  it('installs window.fdc3 and fires fdc3Ready, with no help from the app bundle', async () => {
    const host = stubWebView()
    const ready = vi.fn()
    window.addEventListener('fdc3Ready', ready, { once: true })

    await import('./preload')

    expect(window.fdc3).toBeDefined()
    expect(ready).toHaveBeenCalledTimes(1)

    const info = window.fdc3?.getInfo()
    expect(host.posted).toHaveLength(1)
    expect(host.posted[0].command).toBe('fdc3.getInfo')
    host.deliver({
      id: host.posted[0].id,
      result: { fdc3Version: '2.2', provider: 'Bermuda', optionalFeatures: {}, appMetadata: { appId: 'x', name: null } },
    })
    await expect(info).resolves.toMatchObject({ fdc3Version: '2.2', appMetadata: { appId: 'x' } })
  })

  it('ignores responses to the host calls it did not make, and non-fdc3 pushes', async () => {
    const host = stubWebView()
    await import('./preload')
    const handler = vi.fn()
    const added = window.fdc3?.addContextListener(null, handler)
    host.deliver({ id: host.posted[0].id, result: null })
    await added

    host.deliver({ id: 'someone-elses', result: { pong: true } })
    host.deliver({ type: 'other.event', listenerId: (host.posted[0].payload as { listenerId: string }).listenerId })

    expect(handler).not.toHaveBeenCalled()
  })

  it('dispatches fdc3.context pushes to listeners', async () => {
    const host = stubWebView()
    await import('./preload')
    const handler = vi.fn()
    const added = window.fdc3?.addContextListener('bermuda.incident', handler)
    const { id, payload } = host.posted[0]
    host.deliver({ id, result: null })
    await added

    host.deliver({
      type: 'fdc3.context',
      listenerId: (payload as { listenerId: string }).listenerId,
      context: { type: 'bermuda.incident', id: { incidentId: '7' } },
      source: { appId: 'map', instanceId: 'i1' },
    })

    expect(handler).toHaveBeenCalledWith(
      { type: 'bermuda.incident', id: { incidentId: '7' } },
      { source: { appId: 'map', instanceId: 'i1' } },
    )
  })

  it('does nothing outside the host', async () => {
    await import('./preload')
    expect(window.fdc3).toBeUndefined()
  })

  it('leaves an existing window.fdc3 alone', async () => {
    stubWebView()
    const existing = {} as never
    window.fdc3 = existing

    await import('./preload')

    expect(window.fdc3).toBe(existing)
  })
})
