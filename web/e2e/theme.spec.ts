import { test, expect, APIRequestContext, Browser, Page } from '@playwright/test';

/**
 * Appearance (1.40.0): the base theme and accent chosen on Settings paint `<html data-theme / data-accent>`, are stored per
 * user on the server (a reload with an empty localStorage still shows them), System follows the device live, the boot
 * script in index.html paints the device copy before the app starts, and another user keeps their own (default: Dark).
 * Runs with two throwaway users, so the admin account other specs use is never re-themed.
 */
const ADMIN_USER = process.env['E2E_ADMIN_USER'] ?? 'admin';
const ADMIN_PASSWORD = process.env['E2E_ADMIN_PASSWORD'] ?? 'AdminPass123!';
const START_PASSWORD = 'ThemeStart123!';
const PASSWORD = 'ThemeUser123!';
const APPEARANCE = '/api/v1/reading/preferences/appearance';

async function csrf(request: APIRequestContext): Promise<Record<string, string>> {
  const res = await request.get('/api/v1/auth/csrf');
  const { token } = await res.json();
  return { 'X-MangaPixer-Csrf': token };
}

/** Creates a reader account as the admin and gets it past its first forced password change. Returns the username. */
async function createUser(request: APIRequestContext, prefix: string): Promise<string> {
  const username = `${prefix}${Date.now().toString(36)}${Math.floor(Math.random() * 1000)}`;
  const login = await request.post('/api/v1/auth/login', { data: { username: ADMIN_USER, password: ADMIN_PASSWORD } });
  expect(login.ok(), await login.text()).toBeTruthy();
  const created = await request.post('/api/v1/admin/users', {
    headers: await csrf(request),
    data: { username, password: START_PASSWORD, isAdmin: false },
  });
  expect(created.ok(), await created.text()).toBeTruthy();
  await request.post('/api/v1/auth/logout', { headers: await csrf(request) });

  expect((await request.post('/api/v1/auth/login', { data: { username, password: START_PASSWORD } })).ok()).toBeTruthy();
  const changed = await request.post('/api/v1/auth/change-password', {
    headers: await csrf(request),
    data: { currentPassword: START_PASSWORD, newPassword: PASSWORD },
  });
  expect(changed.ok(), await changed.text()).toBeTruthy();
  await request.post('/api/v1/auth/logout', { headers: await csrf(request) });
  return username;
}

/** Signs the page's context in as `username` (cookie session) and opens `path`. */
async function signIn(page: Page, username: string, path = '/'): Promise<void> {
  const res = await page.request.post('/api/v1/auth/login', { data: { username, password: PASSWORD } });
  expect(res.ok(), await res.text()).toBeTruthy();
  await page.goto(path);
}

async function newPage(browser: Browser): Promise<Page> {
  const context = await browser.newContext({ baseURL: test.info().project.use.baseURL });
  return context.newPage();
}

const html = (page: Page) => page.locator('html');

function savedAppearance(page: Page) {
  return page.waitForResponse((r) => r.url().endsWith(APPEARANCE) && r.request().method() === 'PUT');
}

test.describe('appearance', () => {
  test('Light chosen on Settings survives a reload from the server copy; another user still sees Dark', async ({ browser, request }) => {
    const first = await createUser(request, 'themea');
    const second = await createUser(request, 'themeb');

    const page = await newPage(browser);
    await signIn(page, first, '/settings');
    await expect(html(page)).toHaveAttribute('data-theme', 'dark');
    await expect(page.getByRole('radiogroup', { name: 'Theme' })).toBeVisible();

    const saved = savedAppearance(page);
    await page.getByRole('radio', { name: /^Light\b/ }).click();
    expect((await saved).status()).toBe(204);
    await expect(html(page)).toHaveAttribute('data-theme', 'light');
    expect(await html(page).evaluate((el) => el.style.colorScheme)).toBe('light');
    await expect(page.getByRole('radio', { name: /^Light\b/ })).toHaveAttribute('aria-checked', 'true');

    // The server copy: forget the device copy, reload - the app paints dark first, then the user's stored Light.
    await page.evaluate(() => localStorage.clear());
    await page.reload();
    await expect(html(page)).toHaveAttribute('data-theme', 'light');
    expect(await page.evaluate(() => localStorage.getItem('mangapixer-theme'))).toBe('light');

    // Another user, in another browser whose device copy says Light: the server copy (never chosen = Dark) wins.
    const other = await newPage(browser);
    await other.goto('/login');
    await other.evaluate(() => localStorage.setItem('mangapixer-theme', 'light'));
    await signIn(other, second, '/settings');
    await expect(html(other)).toHaveAttribute('data-theme', 'dark');
    await expect(other.getByRole('radio', { name: /^Dark\b/ })).toHaveAttribute('aria-checked', 'true');

    // ... and the first user's choice is untouched by the second user.
    await page.reload();
    await expect(html(page)).toHaveAttribute('data-theme', 'light');
    await page.context().close();
    await other.context().close();
  });

  test('System follows the device colour scheme live; an accent sets data-accent', async ({ browser, request }) => {
    const username = await createUser(request, 'themes');
    const page = await newPage(browser);
    await page.emulateMedia({ colorScheme: 'light' });
    await signIn(page, username, '/settings');

    let saved = savedAppearance(page);
    await page.getByRole('radio', { name: /^System\b/ }).click();
    await saved;
    await expect(html(page)).toHaveAttribute('data-theme', 'light');

    await page.emulateMedia({ colorScheme: 'dark' });
    await expect(html(page)).toHaveAttribute('data-theme', 'dark');
    await page.emulateMedia({ colorScheme: 'light' });
    await expect(html(page)).toHaveAttribute('data-theme', 'light');
    expect(await page.evaluate(() => localStorage.getItem('mangapixer-theme'))).toBe('system');

    saved = savedAppearance(page);
    await page.getByRole('radio', { name: 'Teal' }).click();
    await saved;
    await expect(html(page)).toHaveAttribute('data-accent', 'teal');
    await page.evaluate(() => localStorage.clear());
    await page.reload();
    await expect(html(page)).toHaveAttribute('data-accent', 'teal');
    await expect(html(page)).toHaveAttribute('data-theme', 'light');
    await page.context().close();
  });

  test('the boot script paints the device copy before the app starts', async ({ browser }) => {
    const page = await newPage(browser);
    await page.goto('/login');
    await page.evaluate(() => {
      localStorage.setItem('mangapixer-theme', 'sepia');
      localStorage.setItem('mangapixer-accent', 'amber');
    });
    // No app at all: only index.html's inline script can set the attributes.
    await page.route(/\/main-[^/]*\.js$/, (route) => route.abort());
    await page.goto('/login', { waitUntil: 'domcontentloaded' });
    await expect(html(page)).toHaveAttribute('data-theme', 'sepia');
    await expect(html(page)).toHaveAttribute('data-accent', 'amber');
    expect(await html(page).evaluate((el) => el.style.colorScheme)).toBe('light');

    // A value outside the vocabulary falls back to the defaults.
    await page.evaluate(() => localStorage.setItem('mangapixer-theme', 'neon'));
    await page.goto('/login', { waitUntil: 'domcontentloaded' });
    await expect(html(page)).toHaveAttribute('data-theme', 'dark');
    await page.context().close();
  });
});
