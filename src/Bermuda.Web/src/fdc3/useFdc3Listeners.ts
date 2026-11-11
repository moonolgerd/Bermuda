import type { DesktopAgent, Listener } from '@finos/fdc3-standard'
import { useEffect, useRef } from 'react'

/**
 * Registers FDC3 listeners for the component's lifetime. `register` must be
 * stable (wrap it in useCallback) — it re-runs whenever it changes.
 *
 * Setup and teardown are chained so a remount (StrictMode, or `register`
 * changing) never adds a listener before the previous one's unsubscribe has
 * reached the host — otherwise e.g. a second addIntentListener for the same
 * intent would race the first one's removal and fail with IntentListenerConflict.
 */
export function useFdc3Listeners(register: (fdc3: DesktopAgent) => Promise<Listener>[]) {
  const chain = useRef<Promise<unknown>>(Promise.resolve())

  useEffect(() => {
    const fdc3 = window.fdc3
    if (!fdc3) return

    const listeners = chain.current.then(() => Promise.allSettled(register(fdc3)))
    chain.current = listeners

    return () => {
      chain.current = listeners.then(results =>
        Promise.allSettled(
          results.flatMap(r => (r.status === 'fulfilled' ? [r.value.unsubscribe()] : [])),
        ),
      )
    }
  }, [register])
}
