import type { DesktopAgent } from '@finos/fdc3-standard'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { graphql, useLazyLoadQuery } from 'react-relay'
import type { IncidentsDashboardQuery } from '../__generated__/IncidentsDashboardQuery.graphql'
import { bermudaHost } from '../bridge'
import { renderCountBadge } from '../bridge/overlayBadge'
import {
  type Context,
  INCIDENT_CONTEXT,
  incidentContext,
  isIncidentContext,
  VIEW_INCIDENT_INTENT,
} from '../fdc3'
import { useFdc3Listeners } from '../fdc3/useFdc3Listeners'
import { IncidentsTable } from './IncidentsTable'
import { IncidentMap } from './IncidentMap'
import styles from './IncidentsDashboard.module.css'

const query = graphql`
  query IncidentsDashboardQuery {
    incidents {
      id
      date
      location
      description
      status
      priority
      witnesses
    }
  }
`

export function IncidentsDashboard() {
  const data = useLazyLoadQuery<IncidentsDashboardQuery>(query, {})
  const [selectedId, setSelectedId] = useState<string | null>(null)

  // A user's pick is shared on the joined FDC3 user channel (a no-op when not
  // joined). Only direct selections broadcast — incidents *received* over FDC3
  // just select, so two linked windows can't echo the same context back and forth.
  const select = useCallback((id: string) => {
    setSelectedId(id)
    const incident = data.incidents.find(i => i.id === id)
    void window.fdc3?.broadcast(incidentContext(id, incident?.location))
  }, [data.incidents])

  const incidentIds = useRef(new Set<string>())
  incidentIds.current = new Set(data.incidents.map(i => i.id))

  // Incidents arriving from other apps: broadcasts on our user channel, and
  // the ViewIncident intent (declared for this app in fdc3-appd.json).
  useFdc3Listeners(useCallback((fdc3: DesktopAgent) => {
    const show = (context: Context) => {
      if (isIncidentContext(context) && incidentIds.current.has(context.id.incidentId)) {
        setSelectedId(context.id.incidentId)
      }
    }
    return [
      fdc3.addContextListener(INCIDENT_CONTEXT, show),
      fdc3.addIntentListener(VIEW_INCIDENT_INTENT, context => { show(context) }),
    ]
  }, []))

  const criticalOpenCount = useMemo(
    () => data.incidents.filter(i => i.priority === 'CRITICAL' && i.status !== 'CLOSED').length,
    [data.incidents],
  )

  // This only mounts once the query resolves (it's inside App's Suspense
  // boundary), so reaching here means the initial load is done.
  useEffect(() => {
    if (window.chrome?.webview) {
      void bermudaHost.window.setProgressState('none')
    }
  }, [])

  // Badge the taskbar icon so a critical incident stays visible even when the
  // app isn't focused. No-op outside the WebView2 host (e.g. `pnpm dev` in a browser).
  useEffect(() => {
    if (!window.chrome?.webview) return

    if (criticalOpenCount > 0) {
      void bermudaHost.window.setOverlay(
        renderCountBadge(criticalOpenCount),
        `${criticalOpenCount} critical incident${criticalOpenCount === 1 ? '' : 's'} open`,
      )
    } else {
      void window.chrome.webview.hostObjects.windowController.ClearOverlayIcon()
    }
  }, [criticalOpenCount])

  return (
    <div className={styles.layout}>
      <div className={styles.tableSection}>
        <IncidentsTable
          incidents={data.incidents}
          selectedId={selectedId}
          onSelect={select}
        />
      </div>
      <div className={styles.mapSection}>
        <IncidentMap
          incidents={data.incidents}
          selectedId={selectedId}
          onSelect={select}
        />
      </div>
    </div>
  )
}
