import { readFileSync } from 'node:fs'
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
