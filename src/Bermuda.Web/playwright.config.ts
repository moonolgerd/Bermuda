import { defineConfig } from '@playwright/test'

export default defineConfig({
  testDir: './e2e',
  fullyParallel: false,
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
