import { useCallback, useEffect, useRef, useState } from 'react'

/** localStorage keys for the two panes. Absent or any value other than "true" means expanded. */
export const sidebarCollapsedKey = 'bermuda.sidebarCollapsed'
export const mapCollapsedKey = 'bermuda.mapCollapsed'

function readCollapsed(key: string): boolean {
  try {
    return window.localStorage.getItem(key) === 'true'
  } catch {
    // Private mode and some WebView policies throw; start expanded.
    return false
  }
}

function writeCollapsed(key: string, collapsed: boolean) {
  try {
    window.localStorage.setItem(key, String(collapsed))
  } catch {
    // The pane still toggles for this session when storage is unavailable.
  }
}

function isEditable(target: EventTarget | null): boolean {
  if (!(target instanceof HTMLElement)) return false
  if (target.isContentEditable) return true
  return target.tagName === 'INPUT' || target.tagName === 'TEXTAREA' || target.tagName === 'SELECT'
}

/**
 * Collapsed flag for one pane, persisted under `storageKey`.
 * `shortcutCode` is a KeyboardEvent.code (for example "KeyB") fired with Ctrl.
 * Collapsing moves focus out of the pane first, so it is not left on a control
 * that is about to become inert.
 */
export function useCollapsiblePane<T extends HTMLElement = HTMLElement>(
  storageKey: string,
  shortcutCode?: string,
  onExpand?: () => void,
) {
  const [collapsed, setCollapsed] = useState(() => readCollapsed(storageKey))
  const collapsedRef = useRef(collapsed)
  const paneRef = useRef<T>(null)
  const toggleRef = useRef<HTMLButtonElement>(null)
  const onExpandRef = useRef(onExpand)
  onExpandRef.current = onExpand

  const toggle = useCallback(() => {
    const next = !collapsedRef.current
    collapsedRef.current = next
    const pane = paneRef.current
    const active = document.activeElement
    if (next && pane && active instanceof Node && pane.contains(active)) {
      toggleRef.current?.focus({ focusVisible: true })
    }
    // Write before re-rendering so a reload in the same turn still sees the new value.
    writeCollapsed(storageKey, next)
    setCollapsed(next)
    if (!next) onExpandRef.current?.()
  }, [storageKey])

  useEffect(() => {
    if (!shortcutCode) return
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.repeat || !event.ctrlKey || event.altKey || event.shiftKey || event.metaKey) return
      if (event.code !== shortcutCode) return
      if (isEditable(event.target)) return
      event.preventDefault()
      toggle()
    }
    // Capture so a focused child (the map, for example) cannot swallow the shortcut.
    window.addEventListener('keydown', onKeyDown, true)
    return () => window.removeEventListener('keydown', onKeyDown, true)
  }, [shortcutCode, toggle])

  return { collapsed, toggle, paneRef, toggleRef }
}
