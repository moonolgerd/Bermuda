import type { Page } from '@playwright/test'
import type { Channel, DesktopAgent } from '@finos/fdc3-standard'
import type { Context } from '../src/fdc3'
import { test, expect } from './fixtures'

// Exercises the host's FDC3 Desktop Agent end to end: window.fdc3 in the main
// window, a second app instance launched with fdc3.open (a second host
// window, so a second CDP page), user-channel linking between them, and an
// intent targeted at the second instance.

const channelButtons = (page: Page) => page.locator('fieldset[aria-label="FDC3 channel"] button')
const row = (page: Page, date: string) => page.locator('tbody tr', { hasText: date })

test.describe('FDC3', () => {
  test('window.fdc3 describes the Bermuda desktop agent', async ({ page }) => {
    const info = await page.evaluate(() => (window.fdc3 as DesktopAgent).getInfo())
    expect(info.fdc3Version).toBe('2.2')
    expect(info.provider).toBe('Bermuda')
    expect(info.appMetadata.appId).toBe('bermuda-incidents')
    await expect(channelButtons(page)).toHaveCount(8)
  })

  test('two linked instances share incident selection and intents', async ({ page }) => {
    const first = channelButtons(page).first()
    await first.click()
    await expect(first).toHaveAttribute('aria-pressed', 'true')

    const [second, opened] = await Promise.all([
      page.context().waitForEvent('page'),
      page.evaluate(() => (window.fdc3 as DesktopAgent).open({ appId: 'bermuda-incidents' })),
    ])

    try {
      await second.waitForSelector('tbody tr')
      await channelButtons(second).first().click()

      // Selecting in the second window broadcasts on the shared channel…
      await row(second, '2024-07-19').click()
      await expect(row(page, '2024-07-19')).toHaveAttribute('data-selected', 'true')

      // …and the channel now holds that incident as its current context.
      const context = await page.evaluate(async () =>
        (await (window.fdc3 as DesktopAgent).getCurrentChannel() as Channel).getCurrentContext('bermuda.incident'))
      expect(context).toMatchObject({ type: 'bermuda.incident', name: '26.1°N 72.8°W' })

      // ViewIncident raised at the second instance selects it there.
      await row(second, '2025-02-14').click()
      await expect(row(second, '2025-02-14')).toHaveAttribute('data-selected', 'true')
      const resolution = await page.evaluate(
        async ([ctx, instanceId]) => {
          const r = await (window.fdc3 as DesktopAgent).raiseIntent('ViewIncident', ctx as Context, { appId: 'bermuda-incidents', instanceId })
          return { appId: r.source.appId, instanceId: r.source.instanceId, result: await r.getResult() }
        },
        [context, opened.instanceId] as const,
      )
      expect(resolution).toMatchObject({ appId: 'bermuda-incidents', instanceId: opened.instanceId })
      await expect(row(second, '2024-07-19')).toHaveAttribute('data-selected', 'true')
    } finally {
      // The shared fixture drives pages()[0], so the extra window must really be gone.
      const closed = second.waitForEvent('close')
      await second.evaluate(() => window.chrome.webview.hostObjects.windowController.Close()).catch(() => {})
      await closed
      await first.click() // leave the channel again
      await expect(first).toHaveAttribute('aria-pressed', 'false')
    }
  })

  test('any hosted page gets window.fdc3, not just ones built from the app bundle', async ({ page }) => {
    // fdc3-probe.html is plain HTML with no Bermuda scripts. open() with a
    // context only resolves once the opened page registers a matching
    // listener — impossible unless the host injected window.fdc3 into it.
    const [probe, opened] = await Promise.all([
      page.context().waitForEvent('page'),
      page.evaluate(() =>
        (window.fdc3 as DesktopAgent).open(
          { appId: 'fdc3-probe' },
          { type: 'bermuda.incident', id: { incidentId: 'probe-7' } } as Context,
        )),
    ])

    try {
      expect(opened.appId).toBe('fdc3-probe')
      await expect(probe.locator('#status')).toHaveText('fdc3 ready')
      await expect(probe.locator('#received li')).toHaveText(['probe-7'])
    } finally {
      const closed = probe.waitForEvent('close')
      await probe.evaluate(() => window.chrome.webview.hostObjects.windowController.Close()).catch(() => {})
      await closed
    }
  })
})
