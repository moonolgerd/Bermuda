import type { Channel, DesktopAgent } from '@finos/fdc3-standard'
import { type CSSProperties, useCallback, useEffect, useState } from 'react'
import { useFdc3Listeners } from '../fdc3/useFdc3Listeners'
import styles from './ChannelPicker.module.css'

/**
 * FDC3 user channel selector: joining a channel links this window with every
 * other app on the same channel (selected incidents are broadcast on it, and
 * incidents broadcast by others are selected here). Hidden outside the host.
 */
export function ChannelPicker() {
  const [channels, setChannels] = useState<Channel[]>([])
  const [currentId, setCurrentId] = useState<string | null>(null)

  useEffect(() => {
    const fdc3 = window.fdc3
    if (!fdc3) return

    void fdc3.getUserChannels().then(setChannels)
    void fdc3.getCurrentChannel().then(c => setCurrentId(c?.id ?? null))
  }, [])

  // Keeps the selection right however the channel changes (including from code elsewhere).
  useFdc3Listeners(useCallback((fdc3: DesktopAgent) => [
    fdc3.addEventListener('userChannelChanged', e => setCurrentId(e.details?.currentChannelId ?? null)),
  ], []))

  if (channels.length === 0) return null

  const toggle = (id: string) => {
    const fdc3 = window.fdc3
    if (!fdc3) return
    void (id === currentId ? fdc3.leaveCurrentChannel() : fdc3.joinUserChannel(id))
  }

  return (
    <fieldset className={styles.picker} aria-label="FDC3 channel">
      {channels.map(channel => (
        <button
          key={channel.id}
          type="button"
          className={styles.channel}
          style={{ '--channel-color': channel.displayMetadata?.color ?? 'gray' } as CSSProperties}
          aria-pressed={channel.id === currentId}
          title={channel.id === currentId
            ? `Leave ${channel.displayMetadata?.name ?? channel.id}`
            : `Join ${channel.displayMetadata?.name ?? channel.id}`}
          onClick={() => toggle(channel.id)}
        >
          {channel.displayMetadata?.glyph}
        </button>
      ))}
    </fieldset>
  )
}
