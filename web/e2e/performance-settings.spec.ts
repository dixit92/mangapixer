import { test, expect, Page, APIRequestContext } from '@playwright/test';
import { expectFitsScreen } from './layout';

/**
 * Administration > Performance (1.42.0): the three settings show their defaults, a change is saved at once and
 * survives a reload, choosing the default resets it, every control is the element at its own centre (nothing covers
 * it), and the card fits phone and desktop widths. The server's own state is checked through the API.
 */
const ADMIN_USER = process.env['E2E_ADMIN_USER'] ?? 'admin';
const ADMIN_PASSWORD = process.env['E2E_ADMIN_PASSWORD'] ?? 'AdminPass123!';
const SHOTS = process.env['E2E_SCREENSHOT_DIR'];

async function login(page: Page): Promise<void> {
  await page.goto('/login');
  await page.getByLabel('Username').fill(ADMIN_USER);
  await page.getByLabel('Password').fill(ADMIN_PASSWORD);
  await page.getByRole('button', { name: /log ?in|sign ?in/i }).click();
  await expect(page).not.toHaveURL(/\/login$/);
}

async function csrf(request: APIRequestContext): Promise<Record<string, string>> {
  const { token } = await (await request.get('/api/v1/auth/csrf')).json();
  return { 'X-MangaPixer-Csrf': token };
}

async function settings(page: Page): Promise<Record<string, unknown>> {
  return (await page.request.get('/api/v1/admin/performance')).json();
}

async function resetAll(page: Page): Promise<void> {
  await page.request.put('/api/v1/admin/performance', {
    headers: await csrf(page.request), data: { reset: ['mediaWorkers', 'pageCacheBytes', 'backgroundWhileReading'] },
  });
}

async function openCard(page: Page, width: number, height: number) {
  await page.setViewportSize({ width, height });
  await page.goto('/admin');
  const card = page.getByTestId('performance-card');
  await expect(card).toBeVisible();
  await expect(card.getByTestId('perf-background')).toBeVisible();
  await card.scrollIntoViewIfNeeded();
  return card;
}

test.describe('Administration > Performance', () => {
  test.beforeEach(async ({ page }) => {
    await login(page);
    await resetAll(page);
  });
  test.afterEach(async ({ page }) => { await resetAll(page); });

  test('shows the defaults, saves a change at once, keeps it over a reload, and the default resets it', async ({ page }) => {
    const card = await openCard(page, 1280, 900);
    const s = await settings(page);
    await expect(card.getByTestId('perf-workers')).toContainText(`${s['mediaWorkersDefault']}`);
    await expect(card.getByTestId('perf-cache')).toContainText('1 GB');
    await expect(card.getByTestId('perf-background')).toContainText('Slow');

    // Background: Slow -> Pause with the keyboard on the closed select (Pause is the option just above).
    const background = card.getByTestId('perf-background');
    const saved = page.waitForResponse((r) => r.url().endsWith('/api/v1/admin/performance') && r.request().method() === 'PUT');
    await background.focus();
    await page.keyboard.press('ArrowUp');
    expect((await saved).ok()).toBeTruthy();
    await expect(background).toContainText('Pause');
    expect((await settings(page))['backgroundWhileReading']).toBe('pause');
    expect((await settings(page))['backgroundWhileReadingSource']).toBe('settings');
    await expect(card.getByRole('status')).toContainText('Saved');

    await page.reload();
    const again = page.getByTestId('performance-card');
    await expect(again.getByTestId('perf-background')).toContainText('Pause');

    // Back to the default (Slow, just below): stored value cleared.
    const reset = page.waitForResponse((r) => r.url().endsWith('/api/v1/admin/performance') && r.request().method() === 'PUT');
    await again.getByTestId('perf-background').focus();
    await page.keyboard.press('ArrowDown');
    await reset;
    await expect.poll(async () => (await settings(page))['backgroundWhileReadingSource']).toBe('default');
  });

  for (const [width, height] of [[390, 844], [820, 1180], [1280, 900]] as const) {
    test(`${width} px: every control is the element at its centre and the page fits`, async ({ page }) => {
      const card = await openCard(page, width, height);
      for (const id of ['perf-workers', 'perf-cache', 'perf-background']) {
        const control = card.getByTestId(id);
        await control.scrollIntoViewIfNeeded();
        const hit = await control.evaluate((el) => {
          const r = el.getBoundingClientRect();
          const top = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
          return !!top && (el === top || el.contains(top) || top.closest('mat-form-field')?.contains(el) === true);
        });
        expect(hit, `${id} is covered at ${width} px`).toBe(true);
      }
      await expectFitsScreen(page, `Administration with the Performance card at ${width} px`);
      if (SHOTS) await card.screenshot({ path: `${SHOTS}/performance-card-${width}.png` });
    });
  }
});
