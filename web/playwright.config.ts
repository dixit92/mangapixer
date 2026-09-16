import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: './e2e',
  fullyParallel: false,
  forbidOnly: true,
  // One retry in CI (GitHub Actions sets CI=true) to absorb rare cold-start
  // flakes against a freshly provisioned container; zero locally so a flake
  // surfaces instead of being masked.
  retries: process.env['CI'] ? 1 : 0,
  workers: 1,
  // 'list' prints per-test results to the console (the html reporter alone is
  // silent on stdout, so CI logs would otherwise show no counts); html is the
  // uploaded-on-failure artifact.
  reporter: [['list'], ['html', { open: 'never' }]],
  use: {
    // Target a running MangaPixer instance. Defaults to the local dev container
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
