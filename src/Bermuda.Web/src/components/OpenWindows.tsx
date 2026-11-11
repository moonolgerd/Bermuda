/**
 * Buttons that open more FDC3 app windows (fdc3.open), for trying out
 * channels, context sharing and intents between windows. Hidden outside the host.
 */
const APPS = [
  { appId: 'bermuda-incidents', label: 'New window' },
  { appId: 'fdc3-probe', label: 'Open probe' },
]

export function OpenWindows() {
  const fdc3 = window.fdc3
  if (!fdc3) return null

  return (
    <div className="open-windows">
      {APPS.map(({ appId, label }) => (
        <button key={appId} type="button" onClick={() => void fdc3.open({ appId }).catch(() => {})}>
          {label}
        </button>
      ))}
    </div>
  )
}
