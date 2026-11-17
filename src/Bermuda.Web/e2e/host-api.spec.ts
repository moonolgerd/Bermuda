import { readFileSync } from 'node:fs'
import type { DesktopAgent } from '@finos/fdc3-standard'
import type { Page } from '@playwright/test'
import { expect, test } from './fixtures'

/**
 * Covers the Bermuda host API: the C# contract (Bermuda.IpcContract, HostBridge,
 * WindowScriptHost) as it is generated into TypeScript (src/ipc/__generated__/ipc.d.ts)
 * and consumed through `bermudaHost` (src/bridge). Everything runs inside the live
 * WebView2 page, importing the real bridge module from the Vite dev server, so a drift
 * between the C# host and the TS client fails here.
 */

type Outcome<T> = { ok: true; value: T } | { ok: false; message: string }

/**
 * Runs `body` inside the page with `bermudaHost` and `renderCountBadge` in scope and
 * resolves with its return value. Written as a string so Playwright doesn't transpile
 * the dynamic imports; `body` must `return` a JSON-serialisable value.
 */
async function inPage<T>(page: Page, body: string): Promise<T> {
  return page.evaluate<T>(`(async () => {
    const { bermudaHost } = await import('/src/bridge/index.ts')
    const { renderCountBadge } = await import('/src/bridge/overlayBadge.ts')
    ${body}
  })()`)
}

/** Like inPage, but turns a rejected host call into { ok: false, message } instead of throwing. */
async function attempt<T>(page: Page, expression: string): Promise<Outcome<T>> {
  return inPage<Outcome<T>>(
    page,
    `try { return { ok: true, value: await (${expression}) } } catch (e) { return { ok: false, message: String(e?.message ?? e) } }`,
  )
}

type Snapshot = {
  bounds: { left: number; top: number; width: number; height: number }
  isMaximized: boolean
  isMinimized: boolean
  isFocused: boolean
}

const getState = (page: Page) => inPage<Snapshot>(page, 'return await bermudaHost.window.getState()')

test.describe('invoke() commands', () => {
  test('ping answers pong', async ({ page }) => {
    expect(await inPage(page, "return await bermudaHost.invoke('ping')")).toEqual({ pong: true })
  })

  test('command names are matched case-insensitively', async ({ page }) => {
    expect(await inPage(page, "return await bermudaHost.invoke('PING')")).toEqual({ pong: true })
  })

  test('an unknown command is rejected by the allow-list', async ({ page }) => {
    const result = await inPage<{ error: string }>(page, "return await bermudaHost.invoke('definitelyNotACommand')")
    expect(result.error).toBe('Unknown command: definitelyNotACommand')
  })

  test('concurrent invocations are correlated to the right caller', async ({ page }) => {
    const results = await inPage<unknown[]>(
      page,
      `return await Promise.all([
        bermudaHost.invoke('ping'),
        bermudaHost.invoke('nope-1'),
        bermudaHost.invoke('ping'),
        bermudaHost.invoke('nope-2'),
      ])`,
    )
    expect(results).toEqual([
      { pong: true },
      { error: 'Unknown command: nope-1' },
      { pong: true },
      { error: 'Unknown command: nope-2' },
    ])
  })
})

test.describe('generated contract vs live host object', () => {
  // ipc.d.ts is generated from WindowScriptHost; WebView2 projects the live C# object into
  // the page. Every member the generated interface declares must exist on the real thing.
  const dts = readFileSync('src/ipc/__generated__/ipc.d.ts', 'utf8')
  const block = dts.match(/interface WindowControllerHostObject \{([\s\S]*?)\n\}/)?.[1] ?? ''
  const properties = [...block.matchAll(/readonly (\w+):/g)].map(m => m[1])
  const methods = [...block.matchAll(/^\s+(\w+)\(/gm)].map(m => m[1])

  test('the generated interface was parsed', () => {
    expect(properties.length).toBeGreaterThan(0)
    expect(methods.length).toBeGreaterThan(0)
  })

  for (const name of properties) {
    test(`property ${name} resolves on the host object`, async ({ page }) => {
      const value = await inPage<unknown>(
        page,
        `return (await chrome.webview.hostObjects.windowController.${name}) ?? null`,
      )
      expect(value).not.toBeNull()
    })
  }

  for (const name of methods) {
    test(`method ${name}() exists on the host object`, async ({ page }) => {
      const type = await inPage<string>(page, `return typeof chrome.webview.hostObjects.windowController.${name}`)
      expect(type).toBe('function')
    })
  }
})

test.describe('updates', () => {
  // The e2e host runs unpackaged, so there is no .appinstaller to check against.
  test('checkForUpdate reports unsupported outside an installed package', async ({ page }) => {
    const result = await inPage<{ status: string; currentVersion: string; message: string }>(
      page,
      'return await bermudaHost.updates.check()',
    )
    expect(Object.keys(result).sort()).toEqual(['currentVersion', 'message', 'status'])
    expect(result.status).toBe('unsupported')
    expect(result.currentVersion).toBe('dev')
  })

  test('applyUpdate refuses to start without an update source', async ({ page }) => {
    const result = await inPage<{ started: boolean; message: string }>(page, 'return await bermudaHost.updates.apply()')
    expect(result.started).toBe(false)
    expect(result.message).not.toBe('')
  })
})

test.describe('window.getState()', () => {
  test('returns the WindowStateSnapshot shape', async ({ page }) => {
    const state = await getState(page)

    expect(Object.keys(state).sort()).toEqual(['bounds', 'isFocused', 'isMaximized', 'isMinimized'])
    expect(Object.keys(state.bounds).sort()).toEqual(['height', 'left', 'top', 'width'])
    for (const v of Object.values(state.bounds)) expect(Number.isInteger(v)).toBe(true)
    expect(state.bounds.width).toBeGreaterThan(0)
    expect(state.bounds.height).toBeGreaterThan(0)
    expect(typeof state.isMaximized).toBe('boolean')
    expect(typeof state.isMinimized).toBe('boolean')
    expect(typeof state.isFocused).toBe('boolean')
  })

  test('the window is never both maximized and minimized', async ({ page }) => {
    const { isMaximized, isMinimized } = await getState(page)
    expect(isMaximized && isMinimized).toBe(false)
  })

  test('usesClientTitleBar() reports a boolean', async ({ page }) => {
    expect(typeof (await inPage<boolean>(page, 'return await bermudaHost.window.usesClientTitleBar()'))).toBe('boolean')
  })
})

test.describe('window control', () => {
  let original: Snapshot

  test.beforeEach(async ({ page }) => {
    original = await getState(page)
  })

  // Put the window back the way the developer had it.
  test.afterEach(async ({ page }) => {
    await inPage(
      page,
      `const c = chrome.webview.hostObjects.windowController
       await c.Restore()
       await c.Resize(${original.bounds.width}, ${original.bounds.height})
       await c.Move(${original.bounds.left}, ${original.bounds.top})
       ${original.isMaximized ? 'await c.Maximize()' : ''}`,
    )
  })

  test('Maximize then Restore', async ({ page }) => {
    await inPage(page, 'await chrome.webview.hostObjects.windowController.Maximize()')
    expect((await getState(page)).isMaximized).toBe(true)

    await inPage(page, 'await chrome.webview.hostObjects.windowController.Restore()')
    expect((await getState(page)).isMaximized).toBe(false)
  })

  test('ToggleMaximize flips the maximized state both ways', async ({ page }) => {
    await inPage(page, 'await chrome.webview.hostObjects.windowController.Restore()')

    await inPage(page, 'await chrome.webview.hostObjects.windowController.ToggleMaximize()')
    expect((await getState(page)).isMaximized).toBe(true)

    await inPage(page, 'await chrome.webview.hostObjects.windowController.ToggleMaximize()')
    expect((await getState(page)).isMaximized).toBe(false)
  })

  test('Minimize then Restore', async ({ page }) => {
    await inPage(page, 'await chrome.webview.hostObjects.windowController.Minimize()')
    expect((await getState(page)).isMinimized).toBe(true)

    await inPage(page, 'await chrome.webview.hostObjects.windowController.Restore()')
    const state = await getState(page)
    expect(state.isMinimized).toBe(false)
    expect(state.isMaximized).toBe(false)
  })

  test('Resize sets the window size (and un-maximizes)', async ({ page }) => {
    await inPage(page, 'await chrome.webview.hostObjects.windowController.Maximize()')
    await inPage(page, 'await chrome.webview.hostObjects.windowController.Resize(1000, 700)')

    const state = await getState(page)
    expect(state.isMaximized).toBe(false)
    expect(state.bounds.width).toBe(1000)
    expect(state.bounds.height).toBe(700)
  })

  test('Move sets the window position', async ({ page }) => {
    await inPage(page, 'await chrome.webview.hostObjects.windowController.Restore()')
    await inPage(page, 'await chrome.webview.hostObjects.windowController.Move(120, 80)')

    const { bounds } = await getState(page)
    expect(bounds.left).toBe(120)
    expect(bounds.top).toBe(80)
  })

  test('Hide then Show', async ({ page }) => {
    await inPage(page, 'await chrome.webview.hostObjects.windowController.Hide()')
    // The page keeps running while the window is hidden.
    expect(await inPage<string>(page, "return 'alive'")).toBe('alive')

    await inPage(page, 'await chrome.webview.hostObjects.windowController.Show()')
    expect((await getState(page)).isMinimized).toBe(false)
  })
})

test.describe('taskbar progress', () => {
  test.afterEach(async ({ page }) => {
    await inPage(page, "await bermudaHost.window.setProgressState('none')")
  })

  for (const state of ['none', 'indeterminate', 'normal', 'error', 'paused']) {
    test(`setProgressState('${state}') is accepted`, async ({ page }) => {
      const result = await attempt(page, `bermudaHost.window.setProgressState('${state}')`)
      expect(result.ok).toBe(true)
    })
  }

  test('state names are case-insensitive on the host', async ({ page }) => {
    expect((await attempt(page, "bermudaHost.window.setProgressState('Normal')")).ok).toBe(true)
  })

  test('an unknown state is rejected', async ({ page }) => {
    expect((await attempt(page, "bermudaHost.window.setProgressState('bogus')")).ok).toBe(false)
  })

  for (const value of [0, 0.5, 1, -3, 7]) {
    test(`setProgressValue(${value}) is accepted (out-of-range values are clamped)`, async ({ page }) => {
      const result = await attempt(page, `chrome.webview.hostObjects.windowController.SetProgressValue(${value})`)
      expect(result.ok).toBe(true)
    })
  }
})

test.describe('taskbar overlay badge', () => {
  test.afterEach(async ({ page }) => {
    await inPage(page, 'await chrome.webview.hostObjects.windowController.ClearOverlayIcon()')
  })

  for (const count of [1, 9, 42, 150]) {
    test(`renderCountBadge(${count}) produces a 32x32 PNG data URL`, async ({ page }) => {
      const info = await inPage<{ prefix: string; width: number; height: number }>(
        page,
        `const url = renderCountBadge(${count})
         const img = new Image()
         img.src = url
         await img.decode()
         return { prefix: url.slice(0, 22), width: img.naturalWidth, height: img.naturalHeight }`,
      )
      expect(info).toEqual({ prefix: 'data:image/png;base64,', width: 32, height: 32 })
    })
  }

  test('setOverlay accepts a rendered badge (data URL) and a description', async ({ page }) => {
    const result = await attempt(page, "bermudaHost.window.setOverlay(renderCountBadge(3), '3 critical incidents open')")
    expect(result.ok).toBe(true)
  })

  test('setOverlay accepts raw base64 without the data: prefix', async ({ page }) => {
    const result = await attempt(page, "bermudaHost.window.setOverlay(renderCountBadge(3).split(',')[1])")
    expect(result.ok).toBe(true)
  })

  test('setOverlay rejects data that is not an image', async ({ page }) => {
    const result = await attempt(page, "bermudaHost.window.setOverlay('data:image/png;base64,bm90IGFuIGltYWdl')")
    expect(result.ok).toBe(false)
  })

  test('ClearOverlayIcon works with and without an overlay set', async ({ page }) => {
    const clearOnly = await attempt(page, 'chrome.webview.hostObjects.windowController.ClearOverlayIcon()')
    expect(clearOnly.ok).toBe(true)

    await inPage(page, 'await bermudaHost.window.setOverlay(renderCountBadge(1), "x")')
    const clearAfterSet = await attempt(page, 'chrome.webview.hostObjects.windowController.ClearOverlayIcon()')
    expect(clearAfterSet.ok).toBe(true)
  })
})

async function closeHostPage(popup: Page) {
  if (popup.isClosed()) return
  const closed = popup.waitForEvent('close')
  await popup.evaluate(() => window.chrome.webview.hostObjects.windowController.Close()).catch(() => {})
  await closed
}

type CdpTarget = { url?: string; webSocketDebuggerUrl?: string }

function cdpPort() {
  return process.env.CDP_PORT ?? '9222'
}

async function frameText(data: unknown): Promise<string> {
  if (typeof data === 'string') return data
  if (data instanceof Blob) return data.text()
  if (data instanceof ArrayBuffer) return new TextDecoder().decode(data)
  return String(data)
}

async function listTargets(): Promise<CdpTarget[]> {
  return (await fetch(`http://127.0.0.1:${cdpPort()}/json`).then((response) => response.json())) as CdpTarget[]
}

/**
 * window.open's WebView2 target never becomes a Playwright page on the opener's CDP connection
 * (that connection only sees a blank target that closes). Drive the probe through its own
 * target websocket instead.
 */
class ProbeSession {
  private nextId = 0
  private readonly pending = new Map<number, { resolve: (value: unknown) => void; reject: (error: Error) => void }>()

  private constructor(private readonly ws: WebSocket) {
    ws.addEventListener('message', (event) => {
      void frameText(event.data).then((text) => {
        const message = JSON.parse(text) as {
          id?: number
          error?: { message?: string }
          result?: {
            result?: { value?: unknown }
            exceptionDetails?: { text?: string; exception?: { description?: string } }
          }
        }
        if (message.id === undefined) return
        const waiter = this.pending.get(message.id)
        if (!waiter) return
        this.pending.delete(message.id)
        const details = message.result?.exceptionDetails
        if (message.error) waiter.reject(new Error(message.error.message ?? 'cdp error'))
        else if (details) waiter.reject(new Error(details.exception?.description ?? details.text ?? 'evaluate failed'))
        else waiter.resolve(message.result?.result?.value)
      })
    })
  }

  static async open(urlFragment: string): Promise<ProbeSession> {
    const deadline = Date.now() + 15_000
    let socketUrl: string | undefined
    while (Date.now() < deadline) {
      socketUrl = (await listTargets()).find((target) => target.url?.includes(urlFragment))?.webSocketDebuggerUrl
      if (socketUrl) break
      await new Promise((resolve) => setTimeout(resolve, 200))
    }
    if (!socketUrl) throw new Error('child window did not load')

    const ws = new WebSocket(socketUrl)
    await new Promise<void>((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error('cdp websocket timed out')), 5_000)
      ws.addEventListener(
        'open',
        () => {
          clearTimeout(timer)
          resolve()
        },
        { once: true },
      )
      ws.addEventListener(
        'error',
        () => {
          clearTimeout(timer)
          reject(new Error('cdp websocket failed'))
        },
        { once: true },
      )
    })
    return new ProbeSession(ws)
  }

  evaluate<T>(expression: string): Promise<T> {
    const id = ++this.nextId
    const result = new Promise<T>((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id)
        reject(new Error('cdp evaluate timed out'))
      }, 5_000)
      this.pending.set(id, {
        resolve: (value) => {
          clearTimeout(timer)
          resolve(value as T)
        },
        reject: (error) => {
          clearTimeout(timer)
          reject(error)
        },
      })
    })
    this.ws.send(
      JSON.stringify({
        id,
        method: 'Runtime.evaluate',
        params: { expression, awaitPromise: true, returnByValue: true },
      }),
    )
    return result
  }

  /** window.close() is handled by the host. The socket dying as the window goes away is success. */
  async close() {
    await this.evaluate('window.close()').catch(() => {})
    this.ws.close()
  }

  async gone(urlFragment: string) {
    const deadline = Date.now() + 10_000
    while (Date.now() < deadline) {
      if (!(await listTargets()).some((target) => target.url?.includes(urlFragment))) return
      await new Promise((resolve) => setTimeout(resolve, 200))
    }
    throw new Error('child window stayed open')
  }
}

test.describe('child windows', () => {
  test('openChild opens a sized same-origin window as a new instance of this app', async ({ page }) => {
    const parent = await page.evaluate(async () => {
      const metadata = (await (window.fdc3 as DesktopAgent).getInfo()).appMetadata
      return { appId: metadata.appId, instanceId: metadata.instanceId }
    })

    let child: Page | undefined
    try {
      ;[child] = await Promise.all([
        page.context().waitForEvent('page'),
        inPage(page, "await bermudaHost.window.openChild({ url: '/fdc3-probe.html', width: 720, height: 500 })"),
      ])
      await expect(child.locator('#status')).toHaveText('fdc3 ready')
      const info = await child.evaluate(async () => {
        const metadata = (await (window.fdc3 as DesktopAgent).getInfo()).appMetadata
        const controller = window.chrome.webview.hostObjects.windowController
        return {
          appId: metadata.appId,
          instanceId: metadata.instanceId,
          width: await controller.Width,
          height: await controller.Height,
          // openChild navigates the child itself, so there is no window.opener. window.open covers that.
          hasOpener: window.opener !== null,
        }
      })
      expect(info.appId).toBe(parent.appId)
      expect(info.instanceId).toBeTruthy()
      expect(info.instanceId).not.toBe(parent.instanceId)
      expect(info.width).toBe(720)
      expect(info.height).toBe(500)
      expect(info.hasOpener).toBe(false)
    } finally {
      if (child) await closeHostPage(child)
    }
  })

  test('openChild rejects a cross-origin url', async ({ page }) => {
    const before = page.context().pages().length
    const crossOrigin = await attempt(
      page,
      "bermudaHost.window.openChild({ url: 'https://example.com/', width: 400, height: 300 })",
    )
    const scriptUrl = await attempt(
      page,
      "bermudaHost.window.openChild({ url: 'javascript:alert(1)', width: 400, height: 300 })",
    )

    expect(crossOrigin.ok).toBe(false)
    expect(scriptUrl.ok).toBe(false)
    if (!crossOrigin.ok) expect(crossOrigin.message).toMatch(/same-origin/i)
    expect(page.context().pages().length).toBe(before)
  })

  test('window.open stays same-origin and keeps window.opener', async ({ page }) => {
    test.setTimeout(60_000)
    const before = page.context().pages().length
    await page.evaluate(() => {
      window.open('https://example.com/')
    })
    expect(page.context().pages().length).toBe(before)

    let child: ProbeSession | undefined
    try {
      const probe = ProbeSession.open('via=open')
      await page.evaluate(() => {
        window.open(`${location.origin}/fdc3-probe.html?via=open`, '_blank', 'width=640,height=480')
      })
      child = await probe
      await expect
        .poll(() => child?.evaluate<string | null>(`document.getElementById('status')?.textContent ?? null`), {
          timeout: 10_000,
        })
        .toBe('fdc3 ready')
      const openerHref = await child.evaluate<string | null>(`window.opener?.location.href ?? null`)
      expect(openerHref).toBe(page.url())
      // width/height on window.open size the WPF window. The viewport is that size minus the
      // frame, and it stays under the 800x600 default used when the features omit a size.
      const box = await child.evaluate<{ w: number; h: number }>(`({ w: window.innerWidth, h: window.innerHeight })`)
      expect(box.w).toBeGreaterThan(500)
      expect(box.w).toBeLessThanOrEqual(640)
      expect(box.h).toBeGreaterThan(360)
      expect(box.h).toBeLessThanOrEqual(480)
    } finally {
      await child?.close()
    }
  })

  test('a child window closes with the window that owns it', async ({ page }) => {
    test.setTimeout(60_000)
    let child: Page | undefined
    let grandchild: ProbeSession | undefined
    try {
      ;[child] = await Promise.all([
        page.context().waitForEvent('page'),
        inPage(page, "await bermudaHost.window.openChild({ url: '/fdc3-probe.html', width: 720, height: 520 })"),
      ])
      await expect(child.locator('#status')).toHaveText('fdc3 ready')
      const probe = ProbeSession.open('via=grandchild')
      await child.evaluate(() => {
        window.open(`${location.origin}/fdc3-probe.html?via=grandchild`, '_blank', 'width=500,height=400')
      })
      grandchild = await probe
      await expect
        .poll(() => grandchild?.evaluate<string | null>(`document.getElementById('status')?.textContent ?? null`), {
          timeout: 10_000,
        })
        .toBe('fdc3 ready')

      const childClosed = child.waitForEvent('close')
      await child.evaluate(() => window.chrome.webview.hostObjects.windowController.Close()).catch(() => {})
      await childClosed
      await grandchild.gone('via=grandchild')
      grandchild = undefined
    } finally {
      await grandchild?.close()
      if (child) await closeHostPage(child)
    }
  })
})

test.describe('taskbar flash', () => {
  let original: Snapshot

  test.beforeEach(async ({ page }) => {
    await inPage(page, 'await chrome.webview.hostObjects.windowController.StopFlash()')
    original = await getState(page)
  })

  // Flash tests minimize the window so it is not the foreground window. Put it back afterwards.
  test.afterEach(async ({ page }) => {
    await inPage(
      page,
      `const c = chrome.webview.hostObjects.windowController
       await c.StopFlash()
       await c.Restore()
       await c.Resize(${original.bounds.width}, ${original.bounds.height})
       await c.Move(${original.bounds.left}, ${original.bounds.top})
       ${original.isMaximized ? 'await c.Maximize()' : ''}`,
    )
  })

  const isFlashing = (page: Page) =>
    inPage<boolean>(page, 'return await chrome.webview.hostObjects.windowController.IsFlashing')

  test('window.stopFlash is safe when the taskbar is not flashing', async ({ page }) => {
    await inPage(page, 'await chrome.webview.hostObjects.windowController.StopFlash()')
    expect(await isFlashing(page)).toBe(false)
  })

  test('window.flash starts flashing in the background and window.stopFlash stops it', async ({ page }) => {
    await inPage(page, 'await chrome.webview.hostObjects.windowController.Minimize()')
    expect(await inPage<boolean>(page, 'return await chrome.webview.hostObjects.windowController.IsFocused')).toBe(false)

    await inPage(page, 'await chrome.webview.hostObjects.windowController.Flash()')
    expect(await isFlashing(page)).toBe(true)

    await inPage(page, 'await chrome.webview.hostObjects.windowController.StopFlash()')
    expect(await isFlashing(page)).toBe(false)
  })

  test('window.flash does not leave a focused window flashing', async ({ page }) => {
    await inPage(page, 'await chrome.webview.hostObjects.windowController.Restore()')
    const focused = await inPage<boolean>(page, 'return await chrome.webview.hostObjects.windowController.IsFocused')
    test.skip(!focused, 'This session did not leave the Bermuda window in the foreground.')

    await inPage(page, 'await chrome.webview.hostObjects.windowController.Flash()')
    expect(await isFlashing(page)).toBe(false)
  })

  test('bermudaHost.window.flash and stopFlash call the host object', async ({ page }) => {
    await inPage(page, 'await chrome.webview.hostObjects.windowController.Minimize()')
    await inPage(page, 'await bermudaHost.window.flash()')
    expect(await inPage<boolean>(page, 'return await bermudaHost.window.isFlashing()')).toBe(true)

    await inPage(page, 'await bermudaHost.window.stopFlash()')
    expect(await inPage<boolean>(page, 'return await bermudaHost.window.isFlashing()')).toBe(false)
  })

  test('a background toast starts flashing and activation stops it', async ({ page }) => {
    await inPage(page, 'await chrome.webview.hostObjects.windowController.Minimize()')
    expect(await inPage<boolean>(page, 'return await chrome.webview.hostObjects.windowController.IsFocused')).toBe(false)

    const shown = await inPage<{ shown: boolean; message: string }>(
      page,
      `return await bermudaHost.notifications.show({ id: 'e2e-flash', title: 'Bermuda e2e', body: 'Flash the taskbar', expiresInSeconds: 4 })`,
    )
    expect(shown).toEqual({ shown: true, message: '' })
    expect(await isFlashing(page)).toBe(true)

    const delivered = await inPage<{ delivered: boolean; message: string }>(
      page,
      `return await bermudaHost.notifications.simulateActivation({ notificationId: 'e2e-flash' })`,
    )
    expect(delivered).toEqual({ delivered: true, message: '' })
    expect(await isFlashing(page)).toBe(false)
  })

  test('a toast does not flash the taskbar when the window is focused', async ({ page }) => {
    await inPage(page, 'await chrome.webview.hostObjects.windowController.Restore()')
    const focused = await inPage<boolean>(page, 'return await chrome.webview.hostObjects.windowController.IsFocused')
    test.skip(!focused, 'This session did not leave the Bermuda window in the foreground.')

    const shown = await inPage<{ shown: boolean }>(
      page,
      `return await bermudaHost.notifications.show({ id: 'e2e-flash-focused', title: 'Bermuda e2e', body: 'Already here', expiresInSeconds: 4 })`,
    )
    expect(shown.shown).toBe(true)
    expect(await isFlashing(page)).toBe(false)
  })
})
