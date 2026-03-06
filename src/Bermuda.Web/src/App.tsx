import { Suspense } from 'react'
import { IncidentsDashboard } from './components/IncidentsDashboard'
import './App.css'

function App() {
  return (
    <div>
      <h1>Bermuda</h1>
      <Suspense fallback={<p>Loading incidents…</p>}>
        <IncidentsDashboard />
      </Suspense>
    </div>
  )
}

export default App

