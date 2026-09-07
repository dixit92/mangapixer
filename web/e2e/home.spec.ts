import { test, expect } from '@playwright/test';

/**
 * Full-stack smoke tests against a running MangaPlex instance (see baseURL in
 * playwright.config.ts — the local dev container by default). These exercise the
 * real SPA + API, so the instance must be set up (an admin already created).
 *
 * Credentials come from env so the suite is not pinned to one deployment:
 *   E2E_ADMIN_USER / E2E_ADMIN_PASSWORD (default admin / AdminPass123!).
 */
const ADMIN_USER = process.env['E2E_ADMIN_USER'] ?? 'admin';
const ADMIN_PASSWORD = process.env['E2E_ADMIN_PASSWORD'] ?? 'AdminPass123!';

test('unauthenticated visit redirects to the login screen', async ({ page }) => {
  await page.goto('/');
  await expect(page).toHaveURL(/\/login$/);
  await expect(page.getByText('MangaPlex Login')).toBeVisible();
});

test('admin can log in and reach the app', async ({ page }) => {
  await page.goto('/login');

  await page.getByLabel('Username').fill(ADMIN_USER);
  await page.getByLabel('Password').fill(ADMIN_PASSWORD);
  await page.getByRole('button', { name: /log ?in|sign ?in/i }).click();

  // Lands off the login route and shows the authenticated shell brand.
  await expect(page).not.toHaveURL(/\/login$/);
  await expect(page.getByRole('link', { name: 'MangaPlex' })).toBeVisible();
});
