import type { Page } from '@playwright/test'
import { test, expect } from './fixtures'
import { launchSecondInstance } from './hostProcess'

// With the tray icon on (the default), closing the main window only hides it: Bermuda keeps running and its
// page stays alive. A second launch (or the tray's Show) brings the window back.

const isVisible = (page: Page) => page.evaluate(async () => await window.chrome.webview.hostObjects.windowController.IsVisible)

test.describe('Tray icon', () => {
  test('closing the main window hides it to the tray instead of ending the app', async ({ page }) => {
    expect(await isVisible(page)).toBe(true)

    try {
      await page.evaluate(() => window.chrome.webview.hostObjects.windowController.Close())
      await expect.poll(() => isVisible(page)).toBe(false)

      // Still running: the page answers and has not been disposed.
      await expect(page.locator('tbody tr').first()).toBeAttached()
    } finally {
      // A second launch asks the running instance to bring its window forward.
      await launchSecondInstance([])
      await expect.poll(() => isVisible(page)).toBe(true)
    }
  })
})
