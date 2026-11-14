import { expect, test, BrowserContext, Page } from '@playwright/test';
import { execFileSync } from 'node:child_process';

test('inbox and bell synchronize through the live API and reconnect without leaking another account', async ({ page: admin, browser }) => {
  test.skip(process.env['E2E_LIVE_API'] !== '1', 'Requires isolated API and PostgreSQL.');
  await admin.goto('/login');
  await admin.getByLabel('Номер телефону').fill(process.env['E2E_ADMIN_PHONE']!);
  await admin.getByLabel('Пароль', { exact: true }).fill(process.env['E2E_ADMIN_PASSWORD']!);
  await admin.getByRole('button', { name: 'Увійти' }).click();
  await expect(admin).toHaveURL(/\/admin$/);
  expect(await admin.evaluate(async () => {
    const schedule = await (await fetch('/api/admin/inspection-schedule')).json(); await fetch('/api/auth/antiforgery');
    const token = decodeURIComponent(document.cookie.split('; ').find(c => c.startsWith('XSRF-TOKEN='))!.split('=')[1]);
    const intervals = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'].map(dayOfWeek => ({ dayOfWeek, start: '09:00', end: '11:00' }));
    return (await fetch('/api/admin/inspection-schedule', { method: 'PUT', headers: { 'Content-Type': 'application/json', RequestVerificationToken: token }, body: JSON.stringify({ version: schedule.version, intervals }) })).status;
  })).toBe(204);

  async function customer(phone: string): Promise<{ context: BrowserContext; page: Page }> {
    const context = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 320, height: 800 } });
    await context.addInitScript(() => {
      const Native = window.WebSocket;
      const sockets: WebSocket[] = [];
      (window as unknown as { notificationSockets: WebSocket[] }).notificationSockets = sockets;
      class TestSocket extends Native {
        constructor(url: string | URL, protocols?: string | string[]) {
          super(url, protocols);
          if (new URL(String(url), location.href).pathname === '/hubs/notifications') sockets.push(this);
        }
      }
      window.WebSocket = TestSocket;
    });
    const page = await context.newPage(); await page.goto('/register');
    await page.getByLabel('Ім’я').fill('Тестовий клієнт'); await page.getByLabel('Номер телефону').fill(phone);
    await page.getByLabel('Пароль', { exact: true }).fill('Example-password-123!');
    await page.getByLabel('Повторіть пароль').fill('Example-password-123!');
    await page.getByRole('button', { name: 'Зареєструватися' }).click(); await expect(page).toHaveURL(/\/account\/appointments$/);
    await expect.poll(() => page.evaluate(() => (window as unknown as { notificationSockets: WebSocket[] }).notificationSockets.some(s => s.readyState === WebSocket.OPEN))).toBe(true);
    return { context, page };
  }
  async function book(page: Page) {
    return page.evaluate(async () => {
      const slots = await (await fetch('/api/inspection-slots?date=2099-01-15')).json(); await fetch('/api/auth/antiforgery');
      const token = decodeURIComponent(document.cookie.split('; ').find(c => c.startsWith('XSRF-TOKEN='))!.split('=')[1]);
      const response = await fetch('/api/appointments', { method: 'POST', headers: { 'Content-Type': 'application/json', RequestVerificationToken: token }, body: JSON.stringify({ inspectionStartUtc: slots.slots[0].startUtc, vin: '1HGCM82633A004352', make: 'Test', model: 'Model', problemDescription: 'Тестовий опис' }) });
      return { status: response.status, body: await response.json() };
    });
  }
  async function cancel(id: string) {
    return admin.evaluate(async appointmentId => {
      const detail = await (await fetch(`/api/appointments/${appointmentId}`)).json(); await fetch('/api/auth/antiforgery');
      const token = decodeURIComponent(document.cookie.split('; ').find(c => c.startsWith('XSRF-TOKEN='))!.split('=')[1]);
      return (await fetch(`/api/admin/appointments/${appointmentId}/cancel`, { method: 'POST', headers: { 'Content-Type': 'application/json', RequestVerificationToken: token }, body: JSON.stringify({ version: detail.version, confirmed: true, reason: 'Тест' }) })).status;
    }, id);
  }

  const first = await customer('0000000071'); let second: { context: BrowserContext; page: Page } | undefined;
  try {
    await first.page.goto('/account/notifications');
    await expect(first.page.locator('.inbox').getByText('Сповіщень поки немає.')).toBeVisible();
    const bell = first.page.locator('app-notification-bell summary');
    await bell.focus(); await first.page.keyboard.press('Enter');
    await expect(first.page.getByRole('region', { name: 'Останні сповіщення' })).toContainText('Сповіщень поки немає');
    await first.page.keyboard.press('Escape'); await expect(bell).toBeFocused();
    const firstBooking = await book(first.page); expect(firstBooking.status).toBe(201);
    await expect(first.page.locator('.inbox__item')).toHaveCount(1);
    await expect(bell).toHaveAttribute('aria-label', /непрочитаних: 1/);
    await first.page.getByRole('button', { name: 'Позначити прочитаним' }).click();
    await expect(bell).toHaveAttribute('aria-label', /непрочитаних: 0/);
    await first.page.getByRole('button', { name: /Непрочитані/ }).click();
    await expect(first.page.getByText('Непрочитаних сповіщень немає.')).toBeVisible();
    const secondBooking = await book(first.page); expect(secondBooking.status).toBe(201);
    await expect(first.page.locator('.inbox__item')).toHaveCount(1);
    await expect(bell).toHaveAttribute('aria-label', /непрочитаних: 1/);
    expect(await first.page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    const foreignId = await first.page.evaluate(async () => (await (await fetch('/api/notifications')).json())[0].id as string);
    await first.page.goto('/');
    await expect(first.page.locator('.site-header app-notification-bell summary')).toHaveAttribute('aria-label', /непрочитаних: 1/);
    await first.page.goto('/account/notifications');
    await expect(first.page.locator('.inbox__item')).toHaveCount(2);

    await admin.goto('/admin/notifications');
    await expect(admin.locator('.inbox__item')).toHaveCount(2);
    await admin.getByRole('button', { name: 'Позначити всі прочитаними' }).click();
    await expect(admin.locator('app-notification-bell summary')).toHaveAttribute('aria-label', /непрочитаних: 0/);
    await admin.goto('/account/notifications');
    expect((await book(admin)).status).toBe(201);
    await expect(admin.locator('app-notification-bell summary')).toHaveAttribute('aria-label', /непрочитаних: 2/);
    const adminHistory = await admin.evaluate(async () => await (await fetch('/api/notifications')).json());
    expect(adminHistory.some((n: { id: string }) => n.id === foreignId)).toBe(false);
    expect(adminHistory.some((n: { type: string }) => n.type === 'AppointmentCreated')).toBe(true);
    expect(adminHistory.some((n: { type: string }) => n.type === 'AdminAppointmentCreated')).toBe(true);

    second = await customer('0000000072'); await second.page.goto('/account/notifications');
    await expect(second.page.locator('.inbox').getByText('Сповіщень поки немає.')).toBeVisible();
    const foreignRead = await second.page.evaluate(async id => {
      await fetch('/api/auth/antiforgery'); const token = decodeURIComponent(document.cookie.split('; ').find(c => c.startsWith('XSRF-TOKEN='))!.split('=')[1]);
      return (await fetch(`/api/notifications/${id}/read`, { method: 'PATCH', headers: { RequestVerificationToken: token } })).status;
    }, foreignId);
    expect(foreignRead).toBe(404);
    await second.page.goto('/admin/notifications');
    await expect(second.page).toHaveURL(/\/account\/appointments$/);
    await second.page.goto('/account/notifications');

    await expect.poll(() => first.page.evaluate(() => (window as unknown as { notificationSockets: WebSocket[] }).notificationSockets.at(-1)?.readyState)).toBe(1);
    await first.page.evaluate(() => (window as unknown as { notificationSockets: WebSocket[] }).notificationSockets.at(-1)!.close(4000, 'test interruption'));
    expect(await cancel(secondBooking.body.id)).toBe(204);
    await expect(first.page.locator('.inbox').getByText('Запис скасовано', { exact: true })).toHaveCount(0);
    await expect(first.page.locator('.inbox').getByText('Запис скасовано', { exact: true })).toBeVisible({ timeout: 15000 });
    await expect(bell).toHaveAttribute('aria-label', /непрочитаних: 2/);
    await expect(first.page).toHaveURL(/\/account\/notifications$/);

    await first.page.goto('/account/profile');
    await first.page.getByLabel('Поточний пароль').fill('Example-password-123!');
    await first.page.getByLabel('Новий пароль', { exact: true }).fill('Another-password-123!');
    await first.page.getByLabel('Повторіть новий пароль').fill('Another-password-123!');
    await first.page.getByRole('button', { name: 'Змінити пароль' }).click();
    await expect(first.page.getByText('Пароль змінено.', { exact: true })).toBeVisible();
    await expect(first.page.locator('app-notification-bell summary')).toBeVisible();
    await first.page.goto('/account/notifications');
    await expect(first.page.locator('.inbox').getByText('Пароль змінено', { exact: true })).toBeVisible();
    await first.page.getByRole('button', { name: 'Позначити всі прочитаними' }).click();
    await expect(first.page.locator('app-notification-bell summary')).toHaveAttribute('aria-label', /непрочитаних: 0/);
    await first.page.getByRole('button', { name: 'Вийти' }).click();
    await expect(first.page).toHaveURL(/\/login$/);
    await expect(first.page.locator('app-notification-bell summary')).toHaveCount(0);
    await first.page.getByLabel('Номер телефону').fill('0000000071');
    await first.page.getByLabel('Пароль', { exact: true }).fill('Another-password-123!');
    await first.page.getByRole('button', { name: 'Увійти' }).click();
    await expect(first.page).toHaveURL(/\/account\/appointments$/);
    await first.page.goto('/account/notifications');
    await expect(first.page.locator('.inbox').getByText('Пароль змінено', { exact: true })).toBeVisible();
    await expect(first.page.locator('app-notification-bell summary')).toBeVisible();
    await expect.poll(() => first.page.evaluate(() => (window as unknown as { notificationSockets: WebSocket[] }).notificationSockets.at(-1)?.readyState)).toBe(1);

    const secondBookingForRole = await book(second.page); expect(secondBookingForRole.status).toBe(201);
    await expect(second.page.locator('.inbox__item')).toHaveCount(1);
    const container = process.env['PROFILE_VERIFY_CONTAINER'] ?? '';
    if (!/^autobody-ui-verify-[0-9a-f]+$/.test(container)) throw new Error('Missing isolated test database');
    execFileSync('docker', ['exec', container, 'psql', '-U', 'auth_test', '-d', 'auth_test', '-c',
      `DELETE FROM "AspNetUserRoles" WHERE "UserId" = (SELECT "Id" FROM "AspNetUsers" WHERE "PhoneNormalized" = '+380000000072')`], { stdio: 'ignore' });
    expect(await cancel(secondBookingForRole.body.id)).toBe(204);
    await expect(second.page.locator('app-notification-bell summary')).toHaveCount(0);
    await expect(second.page.locator('.inbox').getByText('Сесію завершено.', { exact: false })).toBeVisible();

    execFileSync('docker', ['exec', container, 'psql', '-U', 'auth_test', '-d', 'auth_test', '-c',
      `UPDATE "AspNetUsers" SET "IsBlocked" = true WHERE "PhoneNormalized" = '+380000000071'`], { stdio: 'ignore' });
    expect(await cancel(firstBooking.body.id)).toBe(204);
    await expect(first.page.locator('app-notification-bell summary')).toHaveCount(0);
    await expect(first.page.locator('.inbox').getByText('Сесію завершено.', { exact: false })).toBeVisible();
  } finally { await first.context.close(); await second?.context.close(); }
});
