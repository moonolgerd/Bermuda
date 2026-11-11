import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { RelayEnvironmentProvider } from 'react-relay'
import { relayEnvironment } from './graphql/client'
import './index.css'
import App from './App.tsx'
import { installFdc3 } from './fdc3'

installFdc3()

const rootElement = document.getElementById('root')
if (!rootElement) throw new Error('#root element not found')

createRoot(rootElement).render(
  <StrictMode>
    <RelayEnvironmentProvider environment={relayEnvironment}>
      <App />
    </RelayEnvironmentProvider>
  </StrictMode>,
)
