import { expect, test } from '@playwright/test';
import { register } from './admin-users-helpers';

test('persistent cookie restores a closed context and CSRF rejects an unprotected mutation', async ({ browser }) => {
  test.skip(process.env['E2E_LIVE_API'] !== '1', 'Requires isolated API and PostgreSQL.');
  const { context, page } = await register(browser, 'Тестовий користувач', '0000000083');
  const session = await page.evaluate(async () => (await (await fetch('/api/auth/session')).json()));
  expect(session.roles).toEqual(['User']);
  expect(session.phone).toMatch(/^\+380\d{9}$/);
  const denied = await page.evaluate(async () => (await fetch('/api/profile', {
    method: 'PATCH', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ name: 'Несанкціонована зміна' }),
  })).status);
  expect(denied).toBe(400);
  const state = await context.storageState();
  const auth = state.cookies.find(cookie => cookie.name === '.AspNetCore.Identity.Application');
  expect(auth).toMatchObject({ secure: true, httpOnly: true, sameSite: 'Lax' });
  expect(auth!.expires).toBeGreaterThan(Date.now() / 1000);
  await context.close();
  const restored = await browser.newContext({ ignoreHTTPSErrors: true, storageState: state });
  try {
    const resumed = await restored.newPage();
    await resumed.goto('/account/profile');
    await expect(resumed).toHaveURL(/\/account\/profile$/);
    await expect(resumed.getByLabel('Ім’я')).toHaveValue('Тестовий користувач');
    expect((await resumed.evaluate(async () => (await fetch('/api/auth/session')).status))).toBe(200);
  } finally { await restored.close(); }
});
