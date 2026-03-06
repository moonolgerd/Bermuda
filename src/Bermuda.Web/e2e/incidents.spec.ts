import { test, expect } from './fixtures'

const INCIDENTS = [
  { date: '2024-03-12', location: '25.0°N 71.0°W', status: 'Investigating', priority: 'High',     witnesses: '2' },
  { date: '2024-06-04', location: '24.5°N 77.2°W', status: 'Open',          priority: 'Critical', witnesses: '0' },
  { date: '2024-07-19', location: '26.1°N 72.8°W', status: 'Closed',        priority: 'Low',      witnesses: '5' },
  { date: '2024-09-01', location: '25.7°N 75.3°W', status: 'Investigating', priority: 'Medium',   witnesses: '3' },
  { date: '2024-10-28', location: '23.9°N 76.5°W', status: 'Open',          priority: 'High',     witnesses: '7' },
  { date: '2025-01-05', location: '27.0°N 70.1°W', status: 'Investigating', priority: 'Critical', witnesses: '0' },
  { date: '2025-02-14', location: '24.0°N 73.4°W', status: 'Closed',        priority: 'High',     witnesses: '1' },
  { date: '2025-04-30', location: '26.8°N 74.7°W', status: 'Open',          priority: 'Medium',   witnesses: '4' },
]

test.describe('incidents table', () => {
  test('renders all 8 incidents', async ({ page }) => {
    const rows = page.locator('tbody tr')
    await expect(rows).toHaveCount(8)
  })

  test('default sort is date ascending', async ({ page }) => {
    const dateHeader = page.locator('thead th').first()
    await expect(dateHeader).toHaveAttribute('aria-sort', 'ascending')

    const firstDate = page.locator('tbody tr:first-child td:first-child')
    const lastDate  = page.locator('tbody tr:last-child td:first-child')
    await expect(firstDate).toHaveText('2024-03-12')
    await expect(lastDate).toHaveText('2025-04-30')
  })

  test('each row contains correct date, location, status, witnesses', async ({ page }) => {
    for (const incident of INCIDENTS) {
      const row = page.locator('tbody tr', { hasText: incident.date })
      await expect(row.locator('td').nth(0)).toHaveText(incident.date)
      await expect(row.locator('td').nth(1)).toHaveText(incident.location)
      await expect(row.locator('td').nth(5)).toHaveText(incident.witnesses)
    }
  })

  test('priority badges display correct text', async ({ page }) => {
    for (const incident of INCIDENTS) {
      const row = page.locator('tbody tr', { hasText: incident.date })
      const badge = row.locator('td').nth(4).locator('span')
      await expect(badge).toHaveText(incident.priority, { ignoreCase: true })
    }
  })

  test('status badges display correct text', async ({ page }) => {
    for (const incident of INCIDENTS) {
      const row = page.locator('tbody tr', { hasText: incident.date })
      const badge = row.locator('td').nth(3).locator('span')
      await expect(badge).toHaveText(incident.status, { ignoreCase: true })
    }
  })
})

test.describe('row selection', () => {
  for (let i = 0; i < INCIDENTS.length; i++) {
    const incident = INCIDENTS[i]

    test(`clicking row for ${incident.date} selects it`, async ({ page }) => {
      const row = page.locator('tbody tr', { hasText: incident.date })
      await row.click()

      // Row gets selected class
      await expect(row).toHaveClass(/selected/)

      // No other row is selected
      const allRows = page.locator('tbody tr')
      const count = await allRows.count()
      for (let j = 0; j < count; j++) {
        const r = allRows.nth(j)
        const text = await r.locator('td').first().textContent()
        if (text === incident.date) {
          await expect(r).toHaveClass(/selected/)
        } else {
          await expect(r).not.toHaveClass(/selected/)
        }
      }
    })
  }

  test('clicking a different row moves selection', async ({ page }) => {
    const firstRow  = page.locator('tbody tr', { hasText: '2024-03-12' })
    const secondRow = page.locator('tbody tr', { hasText: '2024-06-04' })

    await firstRow.click()
    await expect(firstRow).toHaveClass(/selected/)

    await secondRow.click()
    await expect(secondRow).toHaveClass(/selected/)
    await expect(firstRow).not.toHaveClass(/selected/)
  })
})

test.describe('sorting', () => {
  test('clicking Date header sorts ascending then descending', async ({ page }) => {
    const dateHeader = page.locator('thead th').first()

    // Already ascending by default — click for descending
    await dateHeader.click()
    await expect(dateHeader).toHaveAttribute('aria-sort', 'descending')
    await expect(page.locator('tbody tr:first-child td:first-child')).toHaveText('2025-04-30')
    await expect(page.locator('tbody tr:last-child td:first-child')).toHaveText('2024-03-12')

    // Click again to go back to ascending
    await dateHeader.click()
    await expect(dateHeader).toHaveAttribute('aria-sort', 'ascending')
    await expect(page.locator('tbody tr:first-child td:first-child')).toHaveText('2024-03-12')
  })

  test('clicking Witnesses header sorts numerically', async ({ page }) => {
    const witnessesHeader = page.locator('thead th').nth(5)
    await witnessesHeader.click()
    await expect(witnessesHeader).toHaveAttribute('aria-sort', 'ascending')

    const firstWitnesses = page.locator('tbody tr:first-child td').nth(5)
    const lastWitnesses  = page.locator('tbody tr:last-child td').nth(5)
    await expect(firstWitnesses).toHaveText('0')
    await expect(lastWitnesses).toHaveText('7')

    await witnessesHeader.click()
    await expect(witnessesHeader).toHaveAttribute('aria-sort', 'descending')
    await expect(page.locator('tbody tr:first-child td').nth(5)).toHaveText('7')
  })

  test('clicking Priority header sorts alphabetically', async ({ page }) => {
    const priorityHeader = page.locator('thead th').nth(4)
    await priorityHeader.click()
    await expect(priorityHeader).toHaveAttribute('aria-sort', 'ascending')

    // Critical < High < Low < Medium alphabetically
    const firstBadge = page.locator('tbody tr:first-child td').nth(4).locator('span')
    await expect(firstBadge).toHaveText(/critical/i)
  })

  test('clicking Status header sorts alphabetically', async ({ page }) => {
    const statusHeader = page.locator('thead th').nth(3)
    await statusHeader.click()
    await expect(statusHeader).toHaveAttribute('aria-sort', 'ascending')

    // Closed < Investigating < Open alphabetically
    const firstBadge = page.locator('tbody tr:first-child td').nth(3).locator('span')
    await expect(firstBadge).toHaveText(/closed/i)
  })

  test('sorting resets to ascending when a new column is clicked', async ({ page }) => {
    const dateHeader     = page.locator('thead th').nth(0)
    const locationHeader = page.locator('thead th').nth(1)

    // Sort date desc
    await dateHeader.click()
    await expect(dateHeader).toHaveAttribute('aria-sort', 'descending')

    // Switch to Location — should start at ascending, not inherit desc
    await locationHeader.click()
    await expect(locationHeader).toHaveAttribute('aria-sort', 'ascending')
    await expect(dateHeader).toHaveAttribute('aria-sort', 'none')
  })

  test('all column headers are sortable', async ({ page }) => {
    const headers = page.locator('thead th')
    const count = await headers.count()
    for (let i = 0; i < count; i++) {
      await expect(headers.nth(i)).toHaveAttribute('aria-sort')
    }
  })
})

test.describe('map', () => {
  test('renders a Leaflet map with 8 markers', async ({ page }) => {
    await expect(page.locator('.leaflet-container')).toBeVisible()
    await expect(page.locator('.leaflet-interactive')).toHaveCount(8)
  })

  test('selecting a row highlights the corresponding map marker', async ({ page }) => {
    const row = page.locator('tbody tr', { hasText: '2024-10-28' })
    await row.click()
    await page.waitForTimeout(300) // wait for React re-render + Leaflet update

    // Selected CircleMarker gets fillColor #38bdf8 set as SVG fill attribute
    const count = await page.evaluate(() =>
      Array.from(document.querySelectorAll('.leaflet-interactive'))
        .filter(el => el.getAttribute('fill') === '#38bdf8')
        .length
    )
    expect(count).toBe(1)
  })
})
