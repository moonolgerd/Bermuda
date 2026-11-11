import type { DesktopAgent } from '@finos/fdc3-standard'
import { bermudaHost } from '../bridge'
import { type Context, HostDesktopAgent } from './hostDesktopAgent'

export type { Context }

/** App-specific context type for an incident (FDC3 lets apps define their own `<namespace>.<name>` types). */
export const INCIDENT_CONTEXT = 'bermuda.incident'

/** Raised to bring an incident into view in whichever app handles it (see fdc3-appd.json). */
export const VIEW_INCIDENT_INTENT = 'ViewIncident'

export interface IncidentContext extends Context {
  type: typeof INCIDENT_CONTEXT
  id: { incidentId: string }
  name?: string
}

export function incidentContext(incidentId: string, name?: string): IncidentContext {
  return { type: INCIDENT_CONTEXT, id: { incidentId }, name }
}

export function isIncidentContext(context: Context): context is IncidentContext {
  return context.type === INCIDENT_CONTEXT && typeof context.id?.incidentId === 'string'
}

/**
 * Installs window.fdc3 (backed by the host's Desktop Agent) and fires the
 * standard `fdc3Ready` event, the way container-injected agents do — so any
 * FDC3 code, ours or a library's, can find it the usual way. No-op outside
 * the WebView2 host (e.g. `pnpm dev` in a plain browser).
 */
export function installFdc3(): DesktopAgent | undefined {
  if (!window.chrome?.webview) return undefined
  if (window.fdc3) return window.fdc3

  window.fdc3 = new HostDesktopAgent({
    call: (command, payload) => bermudaHost.invoke(command, payload),
    onEvent: handler => bermudaHost.onEvent(handler),
  })
  window.dispatchEvent(new Event('fdc3Ready'))
  return window.fdc3
}
