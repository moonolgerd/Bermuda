import { defineConfig } from '@playwright/test'

export default defineConfig({
  testDir: './e2e',
  fullyParallel: false,
  // Every spec drives the same live WebView2 window, so files can't run side by side.
  workers: 1,
  retries: 0,
  use: {
    trace: 'on-first-retry',
  },
  webServer: {
    command: 'aspire run',
    cwd: '../..',
    // Wait for the WPF host's CDP endpoint — it's the last resource to start
    url: 'http://localhost:9222/json',
    reuseExistingServer: true,
    timeout: 120_000,
  },
  projects: [
    { name: 'webview2-cdp' },
  ],
})
