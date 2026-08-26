import type { HostResponse, IpcRequest, TaskbarProgressState, WindowStateSnapshot } from '../__generated__/ipc'

export type { TaskbarProgressState }

const pending = new Map<string, (result: unknown) => void>()

window.chrome?.webview?.addEventListener('message', (e: Event) => {
  const msg = (e as MessageEvent).data as HostResponse
  pending.get(msg.id)?.(msg.result)
  pending.delete(msg.id)
})

function invoke<T>(command: string, payload?: unknown): Promise<T> {
  const id = crypto.randomUUID()
  return new Promise<T>((resolve) => {
    pending.set(id, resolve as (r: unknown) => void)
    const request: IpcRequest = { id, command, payload }
    window.chrome.webview.postMessage(request)
  })
}

// Window control goes straight through the WebView2 host object projection
// (chrome.webview.hostObjects.windowController — see WindowScriptHost.cs):
// WebView2 already turns its public methods/properties into identically
// named (PascalCase) promises, so call window.chrome.webview.hostObjects
// .windowController.Whatever() directly for anything with no extra logic
// below. Only functions that do real work — combining calls, defaulting a
// param, narrowing a type — live here.
function windowController() {
  return window.chrome.webview.hostObjects.windowController
}

const windowApi = {
  /** Fetch the current window bounds and state. */
  getState: async (): Promise<WindowStateSnapshot> => {
    const c = windowController()
    const [left, top, width, height, isMaximized, isMinimized, isFocused] = await Promise.all([
      c.Left, c.Top, c.Width, c.Height, c.IsMaximized, c.IsMinimized, c.IsFocused,
    ])
    return { bounds: { left, top, width, height }, isMaximized, isMinimized, isFocused }
  },
  /** Badge the taskbar icon with a PNG (raw base64 or a data: URL, e.g. from a canvas). */
  setOverlay: (iconPng: string, description = '') => windowController().SetOverlayIcon(iconPng, description),
  /** Sets the taskbar progress bar's state ('none' hides it). */
  setProgressState: (state: TaskbarProgressState) => windowController().SetProgressState(state),
} as const

export type WindowApi = typeof windowApi

export const bermudaHost = {
  invoke,
  window: windowApi,
}
