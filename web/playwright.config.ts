import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: './e2e',
  fullyParallel: false,
  forbidOnly: true,
  retries: 0,
  workers: 1,
  reporter: [['html', { open: 'never' }]],
  use: {
    // Target a running MangaPlex instance. Defaults to the local dev container
    // (deploy/Dockerfile on 127.0.0.1:8091); override with E2E_BASE_URL to point
    // at `ng serve` (:4200) or any deployed instance.
    baseURL: process.env['E2E_BASE_URL'] ?? 'http://127.0.0.1:8091',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'retain-on-failure',
  },
  projects: [
    {
      name: 'chromium',
      use: { browserName: 'chromium' },
    },
  ],
});
