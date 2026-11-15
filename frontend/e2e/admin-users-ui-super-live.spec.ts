import { expect, test } from '@playwright/test';
import { changeTemporaryPassword, confirm, cookie, find, hub, login, register } from './admin-users-helpers';

test('SuperAdmin manages an Admin while old windows, cookies and Hub sessions are revoked', async ({ page, browser, baseURL, request }) => {
  test.setTimeout(120000);
  test.skip(process.env['E2E_LIVE_API'] !== '1', 'Requires isolated API and PostgreSQL');
  await login(page, process.env['E2E_ADMIN_PHONE']!, process.env['E2E_ADMIN_PASSWORD']!);
  await expect(page).toHaveURL(/\/admin$/);
  const target = await register(browser, 'Адміністратор тест', '0000000063');
  try {
    await page.goto('/admin/users'); await find(page, '0000000063');
    const card = page.locator('.user-card').filter({ hasText: 'Адміністратор тест' });
    await expect(card).toBeVisible();
    await card.getByRole('button', { name: 'Призначити адміністратором: Адміністратор тест' }).click(); await confirm(page);
    await expect(card.locator('.badge').first()).toHaveText('Адміністратор');
    await login(target.page, '0000000063', 'Example-password-123!'); await expect(target.page).toHaveURL(/\/admin$/);
    await target.page.goto('/admin/users');
    const secondWindow = await target.context.newPage(); await secondWindow.goto('/admin/users');
    await expect(secondWindow.getByRole('heading', { name: 'Користувачі', exact: true })).toBeVisible();
    let oldCookie = await cookie(target.context, baseURL!); let connection = await hub(target.context, baseURL!);
    await card.getByRole('button', { name: 'Скинути пароль: Адміністратор тест' }).click(); await confirm(page);
    const dialog = page.locator('.secret-dialog'); await expect(dialog).toBeVisible();
    const temporary = (await dialog.locator('.temporary-password').textContent())!.trim();
    await dialog.getByRole('button', { name: 'Зрозуміло, закрити' }).click();
    await expect(page.locator('.temporary-password')).toHaveCount(0);
    await expect.poll(() => connection.state).not.toBe('Connected');
    expect((await request.get(`${baseURL}/api/auth/session`, { headers: { Cookie: oldCookie } })).status()).toBe(401);
    await expect(target.page).toHaveURL(/\/login$/); await expect(secondWindow).toHaveURL(/\/login$/);
    await connection.stop();
    await login(target.page, '0000000063', temporary); await changeTemporaryPassword(target.page, temporary);
    await expect(target.page).toHaveURL(/\/admin$/);
    const notices = await target.page.evaluate(async () => (await fetch('/api/notifications')).json());
    expect(notices.some((n: { type: string }) => n.type === 'AccountPasswordReset')).toBe(true);
    expect(JSON.stringify(notices)).not.toContain(temporary);
    await target.page.goto('/admin/users'); oldCookie = await cookie(target.context, baseURL!); connection = await hub(target.context, baseURL!);
    // Password change updates the version held by the operator's older list.
    await card.getByRole('button', { name: 'Відкликати роль адміністратора: Адміністратор тест' }).click(); await confirm(page);
    await expect(page.getByRole('alert')).toContainText('Дані користувача змінилися');
    await card.getByRole('button', { name: 'Відкликати роль адміністратора: Адміністратор тест' }).click(); await confirm(page);
    await expect(card).toContainText('Користувач'); await expect.poll(() => connection.state).not.toBe('Connected');
    expect((await request.get(`${baseURL}/api/auth/session`, { headers: { Cookie: oldCookie } })).status()).toBe(401);
    await expect(target.page).toHaveURL(/\/login$/); await connection.stop();
    await login(target.page, '0000000063', 'Another-password-123!'); await expect(target.page).toHaveURL(/\/account\/appointments$/);
    oldCookie = await cookie(target.context, baseURL!); connection = await hub(target.context, baseURL!);
    await card.getByRole('button', { name: 'Призначити адміністратором: Адміністратор тест' }).click(); await confirm(page);
    await expect(card.locator('.badge').first()).toHaveText('Адміністратор'); await expect.poll(() => connection.state).not.toBe('Connected');
    expect((await request.get(`${baseURL}/api/auth/session`, { headers: { Cookie: oldCookie } })).status()).toBe(401);
    await connection.stop();
    await login(target.page, '0000000063', 'Another-password-123!'); await expect(target.page).toHaveURL(/\/admin$/);
    await target.page.goto('/admin/users'); oldCookie = await cookie(target.context, baseURL!); connection = await hub(target.context, baseURL!);
    await card.getByRole('button', { name: 'Заблокувати: Адміністратор тест' }).click(); await confirm(page);
    await expect(card).toContainText('Заблоковано'); await expect.poll(() => connection.state).not.toBe('Connected');
    expect((await request.get(`${baseURL}/api/auth/session`, { headers: { Cookie: oldCookie } })).status()).toBe(401);
    await expect(target.page).toHaveURL(/\/login$/); await connection.stop();
    await card.getByRole('button', { name: 'Розблокувати: Адміністратор тест' }).click(); await confirm(page);
    await expect(card).toContainText('Активний');
    expect((await request.get(`${baseURL}/api/auth/session`, { headers: { Cookie: oldCookie } })).status()).toBe(401);
    const audit = await page.evaluate(async () => {
      const target = (await (await fetch('/api/admin/users?search=0000000063')).json())[0];
      const history = await (await fetch('/api/admin/audit-log?pageSize=100')).json();
      return history.items.filter((entry: { objectId: string }) => entry.objectId === target.id);
    });
    for (const action of ['AccountPasswordReset', 'AccountAdminChanged', 'AccountBlockedChanged'])
      expect(audit.some((entry: { action: string }) => entry.action === action)).toBe(true);
    expect(JSON.stringify(audit)).not.toContain(temporary);
    await login(target.page, '0000000063', 'Another-password-123!'); await expect(target.page).toHaveURL(/\/admin$/);
    const securityNotices = await target.page.evaluate(async () => (await fetch('/api/notifications')).json());
    for (const action of ['AccountPasswordReset', 'AccountAdminChanged', 'AccountBlockedChanged'])
      expect(securityNotices.some((notice: { type: string }) => notice.type === action)).toBe(true);
    expect(JSON.stringify(securityNotices)).not.toContain(temporary);
    await page.setViewportSize({ width: 375, height: 812 });
    await expect(page.getByLabel('Ім’я або номер телефону')).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await page.screenshot({ path: '/tmp/autobody-users-ui-mobile.png', fullPage: true });
    const button = card.getByRole('button', { name: 'Заблокувати: Адміністратор тест' });
    await button.focus(); await page.keyboard.press('Enter');
    const prompt = page.locator('app-confirm-dialog dialog'); await expect(prompt).toBeVisible();
    await expect(prompt).toHaveAttribute('aria-labelledby', 'user-action-title');
    await page.keyboard.press('Escape'); await expect(prompt).not.toBeVisible();
    await expect(button).toBeFocused();
  } finally { await target.context.close(); }
});
