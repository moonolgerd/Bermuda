import { useEffect, useState } from 'react'
import { bermudaHost } from '../bridge'
import type { UpdateStatus } from '../bridge'
import styles from './UpdateBanner.module.css'

/**
 * Checks once on startup whether a newer MSIX is published at the .appinstaller URL
 * and, if so, offers to install it. Renders nothing outside the packaged host
 * (browser dev server, dev runs of the host, or an MSIX installed without an .appinstaller).
 */
export function UpdateBanner() {
  const [status, setStatus] = useState<UpdateStatus>('unsupported')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')

  useEffect(() => {
    if (!window.chrome?.webview) return
    let cancelled = false
    void bermudaHost.updates.check().then((result) => {
      if (!cancelled) setStatus(result.status)
    })
    return () => {
      cancelled = true
    }
  }, [])

  if (status !== 'available' && status !== 'required') return null

  const install = async () => {
    setBusy(true)
    setError('')
    const result = await bermudaHost.updates.apply()
    // On success Windows shuts the app down, so we only get here on failure.
    if (!result.started) setError(result.message || 'The update could not be started.')
    setBusy(false)
  }

  return (
    <div className={styles.banner} role="status">
      <span>{status === 'required' ? 'A required update is available.' : 'A new version of Bermuda is available.'}</span>
      {error && <span>{error}</span>}
      <button type="button" onClick={install} disabled={busy}>
        {busy ? 'Updating…' : 'Update now'}
      </button>
    </div>
  )
}
