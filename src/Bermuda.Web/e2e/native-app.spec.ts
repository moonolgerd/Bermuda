import type { Page } from '@playwright/test'
import type { DesktopAgent, Listener } from '@finos/fdc3-standard'
import type { Context } from '../src/fdc3'
import { test, expect } from './fixtures'

// Exercises the native side app (src/Bermuda.NativeApp), which Aspire starts
// next to the host. It connects to the host's gRPC named pipe as
// "native-incidents", joins fdc3.channel.1 and answers every
// bermuda.incident context / ViewIncident intent with a bermuda.native.ack
// that carries the incident it saw. So a round trip here proves both
// directions: web -> native (the incident arrives) and native -> web (the ack).

const NATIVE = 'native-incidents'
const channelButtons = (page: Page) => page.locator('fieldset[aria-label="FDC3 channel"] button')

const incident = (id: string) => ({ type: 'bermuda.incident', id: { incidentId: id } }) as Context

type Ack = { type: string; of: { id: { incidentId: string } } }

test.describe('FDC3 native side app (gRPC named pipe)', () => {
  test.beforeEach(async ({ page }) => {
    // Aspire starts the native app after the host, so give it time to connect.
    await expect
      .poll(
        () => page.evaluate(async (appId) => (await (window.fdc3 as DesktopAgent).findInstances({ appId })).length, NATIVE),
        { timeout: 60_000, message: 'native app never connected to the host agent' },
      )
      .toBe(1)
  })

  test('is in the app directory and reports its metadata', async ({ page }) => {
    const metadata = await page.evaluate(async (appId) => {
      const [instance] = await (window.fdc3 as DesktopAgent).findInstances({ appId })
      return (window.fdc3 as DesktopAgent).getAppMetadata(instance)
    }, NATIVE)
    expect(metadata).toMatchObject({ appId: NATIVE, title: 'Native Incidents' })
    expect(metadata.instanceId).toBeTruthy()
  })

  test('a web broadcast reaches the native app, which answers on the channel', async ({ page }) => {
    const first = channelButtons(page).first()
    await first.click()
    await expect(first).toHaveAttribute('aria-pressed', 'true')

    try {
      await page.evaluate(async () => {
        const w = window as unknown as { acks: { ack: unknown; source?: string }[]; ackListener: Listener }
        w.acks = []
        w.ackListener = await (window.fdc3 as DesktopAgent).addContextListener('bermuda.native.ack', (ack, metadata) => {
          w.acks.push({ ack, source: (metadata?.source as { appId?: string } | undefined)?.appId })
        })
      })

      await page.evaluate((ctx) => (window.fdc3 as DesktopAgent).broadcast(ctx), incident('native-1'))

      // The listener may first be replayed an older ack the channel still holds
      // (no source); the one for this broadcast is the one to look for.
      const ackFor = () =>
        page.evaluate(
          () =>
            (window as unknown as { acks: { ack: Ack; source?: string }[] }).acks.find(
              (a) => a.ack.of?.id?.incidentId === 'native-1',
            ),
        )
      await expect.poll(ackFor).toBeDefined()
      const received = (await ackFor())!
      expect(received.source).toBe(NATIVE)
      expect(received.ack.type).toBe('bermuda.native.ack')
    } finally {
      await page.evaluate(() => (window as unknown as { ackListener?: Listener }).ackListener?.unsubscribe())
      await first.click() // leave the channel again
      await expect(first).toHaveAttribute('aria-pressed', 'false')
    }
  })

  test('an intent raised at the native app returns its result', async ({ page }) => {
    const result = await page.evaluate(
      async ([ctx, appId]) => {
        const agent = window.fdc3 as DesktopAgent
        const [instance] = await agent.findInstances({ appId })
        const resolution = await agent.raiseIntent('ViewIncident', ctx as Context, instance)
        return { source: resolution.source, intent: resolution.intent, result: await resolution.getResult() }
      },
      [incident('native-2'), NATIVE] as const,
    )

    expect(result.source.appId).toBe(NATIVE)
    expect(result.intent).toBe('ViewIncident')
    expect(result.result).toMatchObject({ type: 'bermuda.native.ack', of: { id: { incidentId: 'native-2' } } })
  })

  test('findIntent lists the native app next to the web app', async ({ page }) => {
    const apps = await page.evaluate(async () => (await (window.fdc3 as DesktopAgent).findIntent('ViewIncident')).apps.map((a) => a.appId))
    expect(apps).toEqual(expect.arrayContaining([NATIVE, 'bermuda-incidents']))
  })
})
