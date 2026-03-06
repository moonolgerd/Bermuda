import { test as base, chromium } from '@playwright/test'
import type { Page } from '@playwright/test'

/**
 * Override the default `page` fixture to connect to the live WebView2 instance
 * via CDP on port 9222. This lets tests run against the fully wired app
 * (Aspire-managed Vite + GraphQL API) without needing a separate web server.
 */
export const test = base.extend<{ page: Page }>({
  // eslint-disable-next-line no-empty-pattern
  page: async ({}, provide: (page: Page) => Promise<void>) => {
    const browser = await chromium.connectOverCDP('http://localhost:9222')
    const context = browser.contexts()[0]
    const page = context.pages()[0]

    // Only reload if the table sort state has drifted from the default
    // (date column ascending). Avoids a full reload — and visible flicker —
    // for every test while still isolating tests that mutate sort order.
    const dateHeader = page.locator('thead th').first()
    const sort = await dateHeader.getAttribute('aria-sort').catch(() => null)
    if (sort !== 'ascending') {
      await page.reload()
      await page.waitForSelector('tbody tr')
      await page.waitForSelector('.leaflet-container')
    }

    await provide(page)

    // Intentionally not calling browser.close() — that would send the CDP
    // Browser.close command and kill the host WebView2 process.
  },
})

export { expect } from '@playwright/test'
