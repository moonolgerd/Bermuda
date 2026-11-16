import { test, expect } from './fixtures'
import { launchSecondInstance } from './hostProcess'

// Bermuda is one process per user: a second launch (jump list task, startup relaunch) hands its activation to
// the running one and exits. These specs launch that second process for real and check what the running
// instance did with it.

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
