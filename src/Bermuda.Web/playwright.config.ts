import { defineConfig } from '@playwright/test'

// CI picks free ports per run (CDP_PORT, WEB_PORT) so it cannot collide with, or accidentally test, another
// Bermuda or Aspire instance on the same machine; locally the defaults apply.
const cdpPort = process.env.CDP_PORT ?? '9222'

export default defineConfig({
  testDir: './e2e',
  fullyParallel: false,
  // Every test drives the same live app window (CDP), so tests must not overlap —
  // the host-api specs move, minimize and resize it.
  workers: 1,
  retries: 0,
  use: {
    trace: 'on-first-retry',
  },
  webServer: {
    command: 'aspire run',
    cwd: '../..',
    // Wait for the WPF host's CDP endpoint — it's the last resource to start
    url: `http://localhost:${cdpPort}/json`,
    // Locally an already running `aspire run` is reused; CI always starts its own.
    reuseExistingServer: !process.env.CI,
    timeout: 120_000,
  },
  projects: [
    { name: 'webview2-cdp' },
  ],
})
