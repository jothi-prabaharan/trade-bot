import { defineConfig, devices } from '@playwright/test';
import { READY_PORT, UI_PORT } from './e2e/env.mjs';

/** E2E against the real API + PostgreSQL (see e2e/stack.mjs). Tests share one seeded database, so they run serially. */
export default defineConfig({
  testDir: './e2e',
  fullyParallel: false,
  workers: 1,
  retries: 0,
  timeout: 60_000,
  expect: { timeout: 10_000 },
  reporter: [['list']],
  use: {
    baseURL: `http://127.0.0.1:${UI_PORT}`,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 1000 } } }],
  webServer: [
    { command: 'node e2e/stack.mjs', url: `http://127.0.0.1:${READY_PORT}/`, timeout: 300_000, reuseExistingServer: false, stdout: 'pipe' },
    { command: `npx ng serve --port ${UI_PORT} --host 127.0.0.1`, url: `http://127.0.0.1:${UI_PORT}`, timeout: 240_000, reuseExistingServer: !process.env['CI'] },
  ],
});
