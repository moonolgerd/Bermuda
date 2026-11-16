import { spawn } from 'node:child_process'
import { existsSync, statSync } from 'node:fs'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { test, expect } from './fixtures'

// Bermuda is one process per user: a second launch (jump list task, startup relaunch) hands its activation to
// the running one and exits. These specs launch that second process for real and check what the running
// instance did with it.

// The key the Aspire host gives the app under test (AppHost.cs: SINGLE_INSTANCE_KEY). A second process must use
// the same one to find it.
const SINGLE_INSTANCE_KEY = 'Bermuda.AppHost'

/** The Bermuda.exe Aspire started: BERMUDA_EXE, else the newest build of the host project. */
function findHostExe(): string {
  if (process.env.BERMUDA_EXE) return process.env.BERMUDA_EXE
  const candidates = ['Debug', 'Release']
    .map((config) => resolve(dirname(fileURLToPath(import.meta.url)), `../../Bermuda/bin/${config}/net10.0-windows10.0.19041.0/Bermuda.exe`))
    .filter(existsSync)
    .sort((a, b) => statSync(b).mtimeMs - statSync(a).mtimeMs)
  if (candidates.length === 0) throw new Error('Bermuda.exe not found; build src/Bermuda or set BERMUDA_EXE.')
  return candidates[0]
}

/** Starts a second Bermuda process and resolves with its exit code (it must exit by itself). */
function launchSecondInstance(args: string[]): Promise<number | null> {
  return new Promise((resolveExit, reject) => {
    const child = spawn(findHostExe(), args, {
      env: { ...process.env, SINGLE_INSTANCE_KEY, FDC3_GRPC_ENABLED: 'false', NATIVE_APP_AUTOSTART: 'false' },
      stdio: 'ignore',
    })
    const timer = setTimeout(() => {
      child.kill()
      reject(new Error('The second Bermuda did not exit: it did not hand over to the running instance.'))
    }, 20_000)
    child.once('error', reject)
    child.once('exit', (code) => {
      clearTimeout(timer)
      resolveExit(code)
    })
  })
}

test.describe('Single instance', () => {
  test('a second launch with --open opens that app in the running instance', async ({ page }) => {
    const context = page.context()
    const probeOpened = context.waitForEvent('page')

    await launchSecondInstance(['--open', 'fdc3-probe'])

    const probe = await probeOpened
    try {
      await expect(probe).toHaveURL(/fdc3-probe\.html$/)
      await expect(probe.locator('#status')).toHaveText('fdc3 ready')
    } finally {
      // The shared fixture drives pages()[0], so the extra window must really be gone.
      const closed = probe.waitForEvent('close')
      await probe.evaluate(() => window.chrome.webview.hostObjects.windowController.Close()).catch(() => {})
      await closed
    }

    // Closing a window that is not the last must not end the app.
    await expect(page.locator('tbody tr').first()).toBeVisible()
  })

  test('a plain second launch opens no new window and leaves the first one running', async ({ page }) => {
    const context = page.context()
    const pagesBefore = context.pages().length

    await launchSecondInstance([])
    // Give a wrongly started second window time to appear.
    await page.waitForTimeout(1500)

    expect(context.pages()).toHaveLength(pagesBefore)
    await expect(page.locator('tbody tr').first()).toBeVisible()
  })
})
