import type { Page } from '@playwright/test'
import { test, expect } from './fixtures'
import { mapCollapsedKey, sidebarCollapsedKey } from '../src/components/useCollapsiblePane'

const sidebarToggle = (page: Page) => page.getByRole('button', { name: /sidebar/i })
const mapToggle = (page: Page) => page.getByRole('button', { name: /map/i })

async function resetPanes(page: Page) {
  await page.evaluate(([sidebarKey, mapKey]) => {
    localStorage.removeItem(sidebarKey)
    localStorage.removeItem(mapKey)
  }, [sidebarCollapsedKey, mapCollapsedKey] as const)
  await page.reload()
  await page.waitForSelector('tbody tr')
  await page.waitForSelector('.leaflet-container')
}

test.describe('collapsible panes', () => {
  test.beforeEach(async ({ page }) => {
    await resetPanes(page)
    await expect(sidebarToggle(page)).toHaveAttribute('aria-expanded', 'true')
    await expect(mapToggle(page)).toHaveAttribute('aria-expanded', 'true')
  })

  // The shared WebView2 keeps localStorage. Leave both panes open for the rest of the suite.
  test.afterEach(async ({ page }) => {
    await resetPanes(page)
  })

  test('collapses and expands the sidebar and the map', async ({ page }) => {
    const sidebar = page.locator('[data-pane="sidebar"]')
    const map = page.locator('[data-pane="map"]')
    const sidebarButton = sidebarToggle(page)
    const mapButton = mapToggle(page)

    expect(await sidebar.evaluate(el => getComputedStyle(el).transitionProperty)).toContain('width')
    expect(await map.evaluate(el => getComputedStyle(el).transitionProperty)).toMatch(/width|flex-basis/)

    // Focus was inside the sidebar, so collapsing with Ctrl+B moves it to the toggle.
    await page.getByRole('button', { name: 'New window' }).focus()
    await page.evaluate(() => {
      const el = document.querySelector('[data-pane="sidebar"]')
      const widths: number[] = []
      ;(window as unknown as { __paneWidths?: number[] }).__paneWidths = widths
      if (!el) return
      const start = performance.now()
      const tick = () => {
        widths.push(el.getBoundingClientRect().width)
        if (performance.now() - start < 420) requestAnimationFrame(tick)
      }
      requestAnimationFrame(tick)
    })
    await page.keyboard.press('Control+b')
    await page.waitForTimeout(450)
    const duration = await sidebar.evaluate(el => getComputedStyle(el).transitionDuration)
    if (!duration.split(',').every(part => part.trim() === '0s')) {
      const widths = await page.evaluate(() => (window as unknown as { __paneWidths: number[] }).__paneWidths)
      const openWidth = Math.max(...widths)
      expect(widths.some(width => width < openWidth - 24 && width > 56)).toBe(true)
    }
    await expect(sidebarButton).toHaveAttribute('aria-expanded', 'false')
    await expect(sidebarButton).toBeFocused()
    await expect(page.locator('#app-sidebar')).toBeHidden()
    await expect.poll(() => sidebar.evaluate(el => el.getBoundingClientRect().width)).toBeLessThan(56)
    await expect(page.locator('tbody tr')).toHaveCount(8)
    await expect(mapButton).toHaveAttribute('aria-expanded', 'true')

    await page.locator('.leaflet-control-zoom-in').focus()
    await page.keyboard.press('Control+Backslash')
    await expect(mapButton).toHaveAttribute('aria-expanded', 'false')
    await expect(mapButton).toBeFocused()
    await expect(page.locator('#incident-map')).toBeHidden()
    await expect.poll(async () => {
      const rect = await map.evaluate(el => el.getBoundingClientRect())
      return Math.min(rect.width, rect.height)
    }).toBeLessThan(56)

    await sidebarButton.click()
    await expect(sidebarButton).toHaveAttribute('aria-expanded', 'true')
    await expect(page.locator('#app-sidebar')).toBeVisible()
    await expect(page.getByRole('button', { name: 'Open probe' })).toBeVisible()
    await expect.poll(() => sidebar.evaluate(el => el.getBoundingClientRect().width)).toBeGreaterThan(200)

    await mapButton.click()
    await expect(mapButton).toHaveAttribute('aria-expanded', 'true')
    await expect(page.locator('.leaflet-container')).toBeVisible()
    await expect(page.locator('.leaflet-interactive')).toHaveCount(8)
  })

  test('keeps each pane collapsed after reload', async ({ page }) => {
    await sidebarToggle(page).click()
    await mapToggle(page).click()
    await expect(sidebarToggle(page)).toHaveAttribute('aria-expanded', 'false')
    await expect(mapToggle(page)).toHaveAttribute('aria-expanded', 'false')
    await expect.poll(() => page.evaluate(([sidebarKey, mapKey]) => ({
      sidebar: localStorage.getItem(sidebarKey),
      map: localStorage.getItem(mapKey),
    }), [sidebarCollapsedKey, mapCollapsedKey] as const)).toEqual({ sidebar: 'true', map: 'true' })

    await page.reload()
    await page.waitForSelector('tbody tr')
    await expect(sidebarToggle(page)).toHaveAttribute('aria-expanded', 'false')
    await expect(mapToggle(page)).toHaveAttribute('aria-expanded', 'false')
    await expect(page.locator('#app-sidebar')).toBeHidden()
    await expect(page.locator('#incident-map')).toBeHidden()

    await sidebarToggle(page).click()
    await mapToggle(page).click()
    await expect.poll(() => page.evaluate(([sidebarKey, mapKey]) => ({
      sidebar: localStorage.getItem(sidebarKey),
      map: localStorage.getItem(mapKey),
    }), [sidebarCollapsedKey, mapCollapsedKey] as const)).toEqual({ sidebar: 'false', map: 'false' })

    await page.reload()
    await page.waitForSelector('tbody tr')
    await page.waitForSelector('.leaflet-container')
    await expect(sidebarToggle(page)).toHaveAttribute('aria-expanded', 'true')
    await expect(mapToggle(page)).toHaveAttribute('aria-expanded', 'true')
  })
})
