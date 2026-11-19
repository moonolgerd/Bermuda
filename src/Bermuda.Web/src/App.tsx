import { Suspense, useEffect } from 'react'
import { ChannelPicker } from './components/ChannelPicker'
import { OpenWindows } from './components/OpenWindows'
import logo from './assets/bermuda.svg'
import { IncidentsDashboard } from './components/IncidentsDashboard'
import { UpdateBanner } from './components/UpdateBanner'
import { sidebarCollapsedKey, useCollapsiblePane } from './components/useCollapsiblePane'
import { bermudaHost } from './bridge'
import styles from './App.module.css'
import toggleStyles from './components/paneToggle.module.css'
import './App.css'

function SidebarIcon() {
  return (
    <svg viewBox="0 0 24 24" width="18" height="18" aria-hidden="true" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <rect x="3" y="3" width="18" height="18" rx="2" />
      <path d="M9 3v18" />
    </svg>
  )
}

function App() {
  const {
    collapsed: sidebarCollapsed,
    toggle: toggleSidebar,
    paneRef: sidebarPaneRef,
    toggleRef: sidebarToggleRef,
  } = useCollapsiblePane(sidebarCollapsedKey, 'KeyB')

  // Indeterminate taskbar progress while the initial incidents query is in
  // flight; IncidentsDashboard clears it once it mounts (data is ready, since
  // it only renders past the Suspense boundary below).
  useEffect(() => {
    if (window.chrome?.webview) {
      void bermudaHost.window.setProgressState('indeterminate')
    }
  }, [])

  const sidebarLabel = sidebarCollapsed ? 'Expand sidebar' : 'Collapse sidebar'

  return (
    <div className={styles.shell}>
      <div
        className={styles.sidebar}
        data-pane="sidebar"
        data-collapsed={sidebarCollapsed ? 'true' : undefined}
      >
        <button
          ref={sidebarToggleRef}
          type="button"
          className={toggleStyles.toggle}
          aria-expanded={!sidebarCollapsed}
          aria-controls="app-sidebar"
          aria-keyshortcuts="Control+B"
          aria-label={sidebarLabel}
          title={`${sidebarLabel} (Ctrl+B)`}
          onClick={toggleSidebar}
        >
          <SidebarIcon />
        </button>
        <aside
          id="app-sidebar"
          ref={sidebarPaneRef}
          className={styles.pane}
          aria-label="Sidebar"
          inert={sidebarCollapsed ? true : undefined}
          aria-hidden={sidebarCollapsed ? true : undefined}
        >
          <h1 className={styles.brand}>
            <img className={styles.logo} src={logo} alt="" />
            Bermuda
          </h1>
          <div className={styles.nav}>
            <ChannelPicker />
            <div className={styles.windows}>
              <OpenWindows />
            </div>
          </div>
        </aside>
      </div>
      <main className="content">
        <UpdateBanner />
        <Suspense fallback={<p>Loading incidents…</p>}>
          <IncidentsDashboard />
        </Suspense>
      </main>
    </div>
  )
}

export default App
