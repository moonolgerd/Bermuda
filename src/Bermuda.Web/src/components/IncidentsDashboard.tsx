import { useEffect, useMemo, useState } from 'react'
import { graphql, useLazyLoadQuery } from 'react-relay'
import type { IncidentsDashboardQuery } from '../__generated__/IncidentsDashboardQuery.graphql'
import { bermudaHost } from '../bridge'
import { renderCountBadge } from '../bridge/overlayBadge'
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
          onSelect={setSelectedId}
        />
      </div>
      <div className={styles.mapSection}>
        <IncidentMap
          incidents={data.incidents}
          selectedId={selectedId}
          onSelect={setSelectedId}
        />
      </div>
    </div>
  )
}
