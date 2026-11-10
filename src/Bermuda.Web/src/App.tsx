import { Suspense, useEffect } from 'react'
import logo from './assets/bermuda.svg'
import { IncidentsDashboard } from './components/IncidentsDashboard'
import { bermudaHost } from './bridge'
import './App.css'

function App() {
  // Indeterminate taskbar progress while the initial incidents query is in
  // flight; IncidentsDashboard clears it once it mounts (data is ready, since
  // it only renders past the Suspense boundary below).
  useEffect(() => {
    if (window.chrome?.webview) {
      void bermudaHost.window.setProgressState('indeterminate')
    }
  }, [])

  return (
    <>
      <main className="content">
        <h1>
          <img className="heading-logo" src={logo} alt="" />
          Bermuda
        </h1>
        <Suspense fallback={<p>Loading incidents…</p>}>
          <IncidentsDashboard />
        </Suspense>
      </main>
    </>
  )
}

export default App
