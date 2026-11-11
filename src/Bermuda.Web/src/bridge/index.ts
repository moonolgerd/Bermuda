import type {
  HostResponse,
  IpcRequest,
  NotificationActivation,
  ShowNotificationRequest,
  ShowNotificationResult,
  SimulateNotificationActivationRequest,
  SimulateNotificationActivationResult,
  TaskbarProgressState,
  UpdateApplyResult,
  UpdateCheckResult,
  UpdateStatus,
  WindowStateSnapshot,
} from '../ipc/__generated__/ipc'

export type {
  NotificationActivation,
  ShowNotificationRequest,
  ShowNotificationResult,
  SimulateNotificationActivationRequest,
  SimulateNotificationActivationResult,
  TaskbarProgressState,
  UpdateApplyResult,
  UpdateCheckResult,
  UpdateStatus,
}

const pending = new Map<string, (result: unknown) => void>()
const activationListeners = new Set<(activation: NotificationActivation) => void>()

/** A host-initiated message (no request id) — e.g. an FDC3 context delivery. Discriminated by `type`. */
export interface HostEvent {
  type: string
}

const eventHandlers = new Set<(event: HostEvent) => void>()

window.chrome?.webview?.addEventListener('message', (e: Event) => {
  const msg = (e as MessageEvent).data as HostResponse | NotificationActivation | HostEvent
  // Host-pushed events carry a `type` instead of a request `id`.
  if (!('id' in msg)) {
    if (msg.type === 'notificationActivated') {
      for (const listener of activationListeners) listener(msg as NotificationActivation)
    } else {
      for (const handler of eventHandlers) handler(msg)
    }
    return
  }
  const response = msg as HostResponse
  pending.get(response.id)?.(response.result)
  pending.delete(response.id)
})

/** Subscribe to host push events; returns an unsubscribe function. */
function onEvent(handler: (event: HostEvent) => void): () => void {
  eventHandlers.add(handler)
  return () => eventHandlers.delete(handler)
}

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
  /** True when the web app should draw the title bar; false when the host shows the native one (TITLE_BAR setting). */
  usesClientTitleBar: (): Promise<boolean> => windowController().UsesClientTitleBar,
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

// App updates go through the invoke() channel (the work is async on the host side).
// Only an MSIX installed from the .appinstaller can update; anywhere else the status is 'unsupported'.
const updatesApi = {
  /** Ask Windows whether a newer version is published at the .appinstaller URL. */
  check: (): Promise<UpdateCheckResult> => invoke<UpdateCheckResult>('checkForUpdate'),
  /** Install the newer version. Windows closes the app to swap the package; relaunch it afterwards. */
  apply: (): Promise<UpdateApplyResult> => invoke<UpdateApplyResult>('applyUpdate'),
} as const

export type UpdatesApi = typeof updatesApi

// Windows toasts. Clicking a toast or one of its buttons raises onActivated: `action` is the
// clicked button's id (empty for the toast body) and `reply` the reply-box text, if any.
const notificationsApi = {
  /** Raise a toast. Never rejects; check `shown`/`message` for failures. */
  show: (request: ShowNotificationRequest): Promise<ShowNotificationResult> =>
    invoke<ShowNotificationResult>('showNotification', request),
  /**
   * Test hook, only in dev/test runs (host started with CDP_PORT): behaves as if the user clicked the toast
   * body (no `action`) or one of its buttons, optionally with reply text. Goes through the host's real
   * click handling and arrives via onActivated.
   */
  simulateActivation: (request: SimulateNotificationActivationRequest): Promise<SimulateNotificationActivationResult> =>
    invoke<SimulateNotificationActivationResult>('simulateNotificationActivation', request),
  /** Subscribe to toast clicks. Returns an unsubscribe function. */
  onActivated: (listener: (activation: NotificationActivation) => void): (() => void) => {
    activationListeners.add(listener)
    return () => {
      activationListeners.delete(listener)
    }
  },
} as const

export type NotificationsApi = typeof notificationsApi

export const bermudaHost = {
  invoke,
  onEvent,
  window: windowApi,
  updates: updatesApi,
  notifications: notificationsApi,
}
