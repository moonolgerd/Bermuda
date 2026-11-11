// Entry point of fdc3-preload.js: the script the WPF host injects into every
// page it shows (CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync, see
// MainWindow.OnLoaded) before any of the page's own scripts run. That way any
// App Directory app gets the standard window.fdc3 + fdc3Ready, not only pages
// built from this bundle.
//
// Built by `pnpm build:preload` (vite.preload.config.ts) into
// src/Bermuda/Preload/fdc3-preload.js, which is committed like ipc.d.ts so the
// host builds without Node. It talks to the host over chrome.webview directly
// (no bridge/index.ts) so it has no dependency on the rest of the app.
import type { HostResponse, IpcRequest } from '../ipc/__generated__/ipc'
import { type Fdc3Transport, HostDesktopAgent } from './hostDesktopAgent'

function install() {
  const webview = window.chrome?.webview
  // Top-level documents only: frames don't get their own instance.
  if (!webview || window.top !== window || window.fdc3) return

  // Long enough for slow intent handlers (getIntentResult waits on them), short
  // enough that a response the host dropped (e.g. across a reload) can't hang forever.
  const CALL_TIMEOUT_MS = 10 * 60 * 1000
  const pending = new Map<string, { resolve: (result: unknown) => void; timer: ReturnType<typeof setTimeout> }>()
  const eventHandlers = new Set<(event: { type: string }) => void>()

  webview.addEventListener('message', (e: MessageEvent) => {
    const msg = e.data as HostResponse | { type: string }
    if ('id' in msg) {
      // Responses to the page's own invoke() calls aren't ours; leave them be.
      const entry = pending.get(msg.id)
      if (!entry) return
      pending.delete(msg.id)
      clearTimeout(entry.timer)
      entry.resolve(msg.result)
      return
    }
    if (typeof msg.type === 'string' && msg.type.startsWith('fdc3.')) {
      for (const handler of eventHandlers) handler(msg)
    }
  })

  const transport: Fdc3Transport = {
    call: (command, payload) =>
      new Promise((resolve, reject) => {
        const id = crypto.randomUUID()
        const timer = setTimeout(() => {
          pending.delete(id)
          reject(new Error(`${command} timed out`))
        }, CALL_TIMEOUT_MS)
        pending.set(id, { resolve, timer })
        const request: IpcRequest = { id, command, payload }
        webview.postMessage(request)
      }),
    onEvent: handler => {
      eventHandlers.add(handler)
      return () => eventHandlers.delete(handler)
    },
  }

  window.fdc3 = new HostDesktopAgent(transport)
  window.dispatchEvent(new Event('fdc3Ready'))
}

install()
