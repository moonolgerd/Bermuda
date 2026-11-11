import type { Page } from '@playwright/test'
import { expect, test } from './fixtures'

/**
 * Covers toast notifications: the `showNotification` command (HostBridge →
 * NotificationService), its generated contract (ipc.d.ts) and the bridge's
 * `bermudaHost.notifications` client. Toasts really are raised on the machine running
 * the host, so every one expires after a few seconds. A human has to click a toast to
 * produce a genuine activation, so the activation path is covered by feeding the bridge
 * the same message the host pushes (MainWindow.PushEvent).
 */

type ShowResult = { shown: boolean; message: string }

// 1x1 transparent PNG.
const PNG_DATA_URL =
  'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg=='

/** Runs `body` in the page with `bermudaHost` in scope; `body` must `return` JSON-serialisable data. */
async function inPage<T>(page: Page, body: string): Promise<T> {
  return page.evaluate<T>(`(async () => {
    const { bermudaHost } = await import('/src/bridge/index.ts')
    ${body}
  })()`)
}

/** Raises a toast (expiring quickly) from a JSON-serialisable request. */
function show(page: Page, request: Record<string, unknown>) {
  const payload = JSON.stringify({ expiresInSeconds: 5, ...request })
  return inPage<ShowResult>(page, `return await bermudaHost.notifications.show(${payload})`)
}

test.describe('showNotification: basics', () => {
  test('a title and body raise a toast', async ({ page }) => {
    const result = await show(page, { title: 'Bermuda e2e', body: 'Basic toast' })
    expect(result).toEqual({ shown: true, message: '' })
  })

  test('the result has the ShowNotificationResult shape', async ({ page }) => {
    const result = await show(page, { title: 'Bermuda e2e', body: 'Shape' })
    expect(Object.keys(result).sort()).toEqual(['message', 'shown'])
  })

  test('a body alone is enough (the title falls back to the app name)', async ({ page }) => {
    expect((await show(page, { title: '', body: 'Body only' })).shown).toBe(true)
  })

  test('a title alone is enough', async ({ page }) => {
    expect((await show(page, { title: 'Title only', body: '' })).shown).toBe(true)
  })

  test('an empty notification is refused with a message', async ({ page }) => {
    const result = await show(page, { title: '  ', body: '' })
    expect(result.shown).toBe(false)
    expect(result.message).not.toBe('')
  })

  test('a missing payload is refused instead of throwing', async ({ page }) => {
    const result = await inPage<ShowResult>(page, "return await bermudaHost.invoke('showNotification')")
    expect(result.shown).toBe(false)
    expect(result.message).toContain('title')
  })

  test('the command name is matched case-insensitively', async ({ page }) => {
    const result = await inPage<ShowResult>(
      page,
      `return await bermudaHost.invoke('SHOWNOTIFICATION', { title: 'Bermuda e2e', body: 'Case', expiresInSeconds: 5 })`,
    )
    expect(result.shown).toBe(true)
  })
})

test.describe('showNotification: priority', () => {
  for (const priority of ['normal', 'high', 'urgent']) {
    test(`priority '${priority}' is accepted`, async ({ page }) => {
      expect((await show(page, { title: 'Bermuda e2e', body: `Priority ${priority}`, priority })).shown).toBe(true)
    })
  }

  test('an unknown priority is rejected by the host', async ({ page }) => {
    const result = await inPage<{ error?: string; shown?: boolean }>(
      page,
      `return await bermudaHost.invoke('showNotification', { title: 'Bermuda e2e', body: 'x', priority: 'bogus' })`,
    )
    expect(result.shown).toBeUndefined()
    expect(result.error).toBeTruthy()
  })
})

test.describe('showNotification: images', () => {
  test('a data: URL works as icon, inline image and hero image', async ({ page }) => {
    const result = await show(page, {
      title: 'Bermuda e2e',
      body: 'Images',
      icon: PNG_DATA_URL,
      circleIcon: true,
      image: PNG_DATA_URL,
      heroImage: PNG_DATA_URL,
    })
    expect(result.shown).toBe(true)
  })

  test('an image that cannot be decoded is skipped, not fatal', async ({ page }) => {
    const result = await show(page, {
      title: 'Bermuda e2e',
      body: 'Bad image',
      icon: 'data:image/png;base64,!!!not-base64!!!',
      image: 'data:text/plain,not-base64',
    })
    expect(result.shown).toBe(true)
  })

  test('an unreachable https image is skipped, not fatal', async ({ page }) => {
    const result = await show(page, { title: 'Bermuda e2e', body: 'Dead URL', icon: 'https://bermuda-e2e.invalid/icon.png' })
    expect(result.shown).toBe(true)
  })

  test('non-https remote URLs and missing files are ignored', async ({ page }) => {
    const result = await show(page, {
      title: 'Bermuda e2e',
      body: 'Unsupported sources',
      icon: 'http://example.com/icon.png',
      image: 'C:\\definitely\\not\\here.png',
      heroImage: 'relative/path.png',
    })
    expect(result.shown).toBe(true)
  })
})

test.describe('showNotification: buttons and reply', () => {
  test('buttons are accepted', async ({ page }) => {
    const result = await show(page, {
      title: 'Bermuda e2e',
      body: 'Buttons',
      buttons: [
        { id: 'ack', label: 'Acknowledge' },
        { id: 'snooze', label: 'Snooze', icon: PNG_DATA_URL },
      ],
    })
    expect(result.shown).toBe(true)
  })

  test('more than five buttons are truncated rather than rejected', async ({ page }) => {
    const buttons = Array.from({ length: 8 }, (_, i) => ({ id: `b${i}`, label: `Button ${i}` }))
    expect((await show(page, { title: 'Bermuda e2e', body: 'Many buttons', buttons })).shown).toBe(true)
  })

  test('a reply box can be combined with buttons', async ({ page }) => {
    const result = await show(page, {
      title: 'Bermuda e2e',
      body: 'Reply',
      replyPlaceholder: 'Type a reply',
      buttons: [{ id: 'send', label: 'Send' }],
    })
    expect(result.shown).toBe(true)
  })
})

test.describe('showNotification: options', () => {
  test('attribution, silent and expiry are accepted', async ({ page }) => {
    const result = await show(page, {
      title: 'Bermuda e2e',
      body: 'Options',
      attribution: 'via Bermuda',
      silent: true,
      expiresInSeconds: 3,
    })
    expect(result.shown).toBe(true)
  })

  test('reusing an id replaces the earlier toast', async ({ page }) => {
    const first = await show(page, { id: 'e2e-replace', title: 'Bermuda e2e', body: 'First' })
    const second = await show(page, { id: 'e2e-replace', title: 'Bermuda e2e', body: 'Second' })
    expect(first.shown).toBe(true)
    expect(second.shown).toBe(true)
  })

  test('very long ids are accepted', async ({ page }) => {
    expect((await show(page, { id: 'x'.repeat(300), title: 'Bermuda e2e', body: 'Long id' })).shown).toBe(true)
  })

  test('a full-featured toast', async ({ page }) => {
    const result = await show(page, {
      id: 'e2e-full',
      title: 'Incident #42',
      body: 'API latency is above threshold',
      attribution: 'via Bermuda',
      priority: 'urgent',
      icon: PNG_DATA_URL,
      heroImage: PNG_DATA_URL,
      replyPlaceholder: 'Add a note',
      buttons: [
        { id: 'ack', label: 'Acknowledge' },
        { id: 'escalate', label: 'Escalate' },
      ],
    })
    expect(result.shown).toBe(true)
  })
})

test.describe('notifications client (bermudaHost.notifications)', () => {
  test('exposes show, simulateActivation and onActivated', async ({ page }) => {
    const shape = await inPage<Record<string, string>>(
      page,
      `return Object.fromEntries(Object.entries(bermudaHost.notifications).map(([k, v]) => [k, typeof v]))`,
    )
    expect(shape).toEqual({ show: 'function', simulateActivation: 'function', onActivated: 'function' })
  })

  test('onActivated returns an unsubscribe function', async ({ page }) => {
    const type = await inPage<string>(page, 'return typeof bermudaHost.notifications.onActivated(() => {})')
    expect(type).toBe('function')
  })

  // The bridge listens on chrome.webview for host-pushed messages, so dispatching the same
  // payload MainWindow.PushEvent sends exercises the real routing code.
  const activation = (extra: Record<string, unknown> = {}) =>
    JSON.stringify({ type: 'notificationActivated', notificationId: 'n1', action: 'ack', ...extra })

  test('a pushed activation reaches subscribers', async ({ page }) => {
    const received = await inPage<unknown[]>(
      page,
      `const seen = []
       const off = bermudaHost.notifications.onActivated((a) => seen.push(a))
       chrome.webview.dispatchEvent(new MessageEvent('message', { data: ${activation({ reply: 'on it' })} }))
       off()
       return seen`,
    )
    expect(received).toEqual([{ type: 'notificationActivated', notificationId: 'n1', action: 'ack', reply: 'on it' }])
  })

  test('unsubscribing stops delivery', async ({ page }) => {
    const count = await inPage<number>(
      page,
      `let n = 0
       const off = bermudaHost.notifications.onActivated(() => { n++ })
       const push = () => chrome.webview.dispatchEvent(new MessageEvent('message', { data: ${activation()} }))
       push()
       off()
       push()
       return n`,
    )
    expect(count).toBe(1)
  })

  test('every subscriber is notified', async ({ page }) => {
    const counts = await inPage<number[]>(
      page,
      `const n = [0, 0]
       const offs = [0, 1].map((i) => bermudaHost.notifications.onActivated(() => { n[i]++ }))
       chrome.webview.dispatchEvent(new MessageEvent('message', { data: ${activation()} }))
       for (const off of offs) off()
       return n`,
    )
    expect(counts).toEqual([1, 1])
  })

  test('an activation does not disturb in-flight invoke() calls', async ({ page }) => {
    const result = await inPage<unknown>(
      page,
      `const pending = bermudaHost.invoke('ping')
       chrome.webview.dispatchEvent(new MessageEvent('message', { data: ${activation()} }))
       return await pending`,
    )
    expect(result).toEqual({ pong: true })
  })
})

/**
 * Clicks, driven through the host's test hook (`simulateNotificationActivation`). It replays the
 * arguments each toast was really built with through the same handler a Windows click uses, so these
 * cover argument building (e.g. a button click must still carry the toast id), the host's parsing
 * and the push to the web app. They do not cover Windows itself delivering a click.
 */
type Activation = { type: string; notificationId: string; action: string; reply?: string | null }

/** Shows a toast, simulates a click on it, and resolves with the activation event the web app receives. */
async function clickToast(
  page: Page,
  toast: Record<string, unknown>,
  click: { action?: string; reply?: string } = {},
) {
  return inPage<{ shown: ShowResult; delivered: { delivered: boolean; message: string }; event: Activation | null }>(
    page,
    `const toast = ${JSON.stringify({ expiresInSeconds: 5, ...toast })}
     const click = ${JSON.stringify(click)}
     const shown = await bermudaHost.notifications.show(toast)
     const received = new Promise((resolve) => {
       const timer = setTimeout(() => { off(); resolve(null) }, 3000)
       const off = bermudaHost.notifications.onActivated((a) => { clearTimeout(timer); off(); resolve(a) })
     })
     const delivered = await bermudaHost.notifications.simulateActivation({ notificationId: toast.id, ...click })
     return { shown, delivered, event: delivered.delivered ? await received : null }`,
  )
}

test.describe('simulated toast clicks', () => {
  test('clicking the toast body reports the toast id and no action', async ({ page }) => {
    const { shown, delivered, event } = await clickToast(page, { id: 'e2e-body', title: 'Bermuda e2e', body: 'Body click' })
    expect(shown.shown).toBe(true)
    expect(delivered).toEqual({ delivered: true, message: '' })
    expect(event).toMatchObject({ type: 'notificationActivated', notificationId: 'e2e-body', action: '' })
    expect(event?.reply ?? '').toBe('')
  })

  test('clicking a button reports its id and still carries the toast id', async ({ page }) => {
    const { event } = await clickToast(
      page,
      {
        id: 'e2e-button',
        title: 'Bermuda e2e',
        body: 'Button click',
        buttons: [
          { id: 'ack', label: 'Acknowledge' },
          { id: 'snooze', label: 'Snooze' },
        ],
      },
      { action: 'snooze' },
    )
    expect(event).toMatchObject({ notificationId: 'e2e-button', action: 'snooze' })
  })

  test('reply text typed into the toast comes back with the click', async ({ page }) => {
    const { event } = await clickToast(
      page,
      {
        id: 'e2e-reply',
        title: 'Bermuda e2e',
        body: 'Reply',
        replyPlaceholder: 'Type a reply',
        buttons: [{ id: 'send', label: 'Send' }],
      },
      { action: 'send', reply: 'on it' },
    )
    expect(event).toMatchObject({ notificationId: 'e2e-reply', action: 'send', reply: 'on it' })
  })

  test('a very long id survives the round trip unchanged', async ({ page }) => {
    const id = 'long-'.repeat(60)
    const { event } = await clickToast(page, { id, title: 'Bermuda e2e', body: 'Long id' })
    expect(event?.notificationId).toBe(id)
  })

  test('a reused id replaces the earlier toast and clicks resolve to the newest buttons', async ({ page }) => {
    await show(page, { id: 'e2e-replaced', title: 'Bermuda e2e', body: 'First', buttons: [{ id: 'old', label: 'Old' }] })
    const { event } = await clickToast(
      page,
      { id: 'e2e-replaced', title: 'Bermuda e2e', body: 'Second', buttons: [{ id: 'new', label: 'New' }] },
      { action: 'new' },
    )
    expect(event).toMatchObject({ notificationId: 'e2e-replaced', action: 'new' })

    const stale = await inPage<{ delivered: boolean }>(
      page,
      `return await bermudaHost.notifications.simulateActivation({ notificationId: 'e2e-replaced', action: 'old' })`,
    )
    expect(stale.delivered).toBe(false)
  })

  test('an unknown toast id is refused', async ({ page }) => {
    const result = await inPage<{ delivered: boolean; message: string }>(
      page,
      `return await bermudaHost.notifications.simulateActivation({ notificationId: 'never-shown' })`,
    )
    expect(result.delivered).toBe(false)
    expect(result.message).toContain('never-shown')
  })

  test('an unknown button is refused', async ({ page }) => {
    const { delivered, event } = await clickToast(
      page,
      { id: 'e2e-nobutton', title: 'Bermuda e2e', body: 'No such button', buttons: [{ id: 'ok', label: 'OK' }] },
      { action: 'missing' },
    )
    expect(delivered.delivered).toBe(false)
    expect(delivered.message).toContain('missing')
    expect(event).toBeNull()
  })

  test('reply text is refused when the toast has no reply box', async ({ page }) => {
    const { delivered } = await clickToast(
      page,
      { id: 'e2e-noreply', title: 'Bermuda e2e', body: 'No reply box' },
      { reply: 'hello' },
    )
    expect(delivered.delivered).toBe(false)
    expect(delivered.message).toContain('reply')
  })

  test('a missing payload is refused instead of throwing', async ({ page }) => {
    const result = await inPage<{ delivered: boolean }>(page, "return await bermudaHost.invoke('simulateNotificationActivation')")
    expect(result.delivered).toBe(false)
  })
})
