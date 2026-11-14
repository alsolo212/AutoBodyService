import { expect, test } from '@playwright/test';
import { execFileSync } from 'node:child_process';

test('role loss preserves personal inbox while hiding admin history across reconnect and account switch', async ({ page: manager, browser }) => {
  test.skip(process.env['E2E_LIVE_API'] !== '1', 'Requires isolated API and PostgreSQL.');
  const container = process.env['PROFILE_VERIFY_CONTAINER'] ?? '';
  if (!/^autobody-ui-verify-[0-9a-f]+$/.test(container)) throw new Error('Missing isolated test database');
  const label = execFileSync('docker', ['inspect', '--format', '{{index .Config.Labels "autobody.auth-e2e.disposable"}}', container], { encoding: 'utf8' }).trim();
  expect(label).toBe('true');
  const sql = (statement: string) => execFileSync('docker', ['exec', container, 'psql', '-v', 'ON_ERROR_STOP=1', '-U', 'auth_test', '-d', 'auth_test', '-t', '-A', '-c', statement], { encoding: 'utf8' }).trim();
  await manager.goto('/login');
  await manager.getByLabel('Номер телефону').fill(process.env['E2E_ADMIN_PHONE']!);
  await manager.getByLabel('Пароль', { exact: true }).fill(process.env['E2E_ADMIN_PASSWORD']!);
  await manager.getByRole('button', { name: 'Увійти' }).click();
  await expect(manager).toHaveURL(/\/admin$/);
  expect(await manager.evaluate(async () => {
    const schedule = await (await fetch('/api/admin/inspection-schedule')).json();
    await fetch('/api/auth/antiforgery');
    const token = decodeURIComponent(document.cookie.split('; ').find(c => c.startsWith('XSRF-TOKEN='))!.split('=')[1]);
    const intervals = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'].map(dayOfWeek => ({ dayOfWeek, start: '09:00', end: '11:00' }));
    return (await fetch('/api/admin/inspection-schedule', { method: 'PUT', headers: { 'Content-Type': 'application/json', RequestVerificationToken: token }, body: JSON.stringify({ version: schedule.version, intervals }) })).status;
  })).toBe(204);
  const context = await browser.newContext({ ignoreHTTPSErrors: true });
  await context.addInitScript(() => {
    const Native = window.WebSocket; const sockets: WebSocket[] = [];
    (window as unknown as { notificationSockets: WebSocket[] }).notificationSockets = sockets;
    class ObservedSocket extends Native {
      constructor(url: string | URL, protocols?: string | string[]) {
        super(url, protocols);
        if (new URL(String(url), location.href).pathname === '/hubs/notifications') sockets.push(this);
      }
    }
    window.WebSocket = ObservedSocket;
  });
  const page = await context.newPage();
  try {
    await page.goto('/register');
    await page.getByLabel('Ім’я').fill('Тестовий клієнт');
    await page.getByLabel('Номер телефону').fill('0000000073');
    await page.getByLabel('Пароль', { exact: true }).fill('Example-password-123!');
    await page.getByLabel('Повторіть пароль').fill('Example-password-123!');
    await page.getByRole('button', { name: 'Зареєструватися' }).click();
    await expect(page).toHaveURL(/\/account\/appointments$/);
    await page.getByRole('button', { name: 'Вийти' }).click(); await expect(page).toHaveURL(/\/login$/);
    sql(`INSERT INTO "AspNetUserRoles" ("UserId", "RoleId") SELECT u."Id", r."Id" FROM "AspNetUsers" u CROSS JOIN "AspNetRoles" r WHERE u."PhoneNormalized" = '+380000000073' AND r."Name" = 'Admin'`);
    const login = async (phone: string, password: string, expected: RegExp) => {
      await page.getByLabel('Номер телефону').fill(phone); await page.getByLabel('Пароль', { exact: true }).fill(password);
      await page.getByRole('button', { name: 'Увійти' }).click(); await expect(page).toHaveURL(expected);
    };
    await login('0000000073', 'Example-password-123!', /\/admin$/);
    await page.goto('/account/notifications');
    const booking = await page.evaluate(async () => {
      const slots = await (await fetch('/api/inspection-slots?date=2099-01-15')).json(); await fetch('/api/auth/antiforgery');
      const token = decodeURIComponent(document.cookie.split('; ').find(c => c.startsWith('XSRF-TOKEN='))!.split('=')[1]);
      const response = await fetch('/api/appointments', { method: 'POST', headers: { 'Content-Type': 'application/json', RequestVerificationToken: token }, body: JSON.stringify({ inspectionStartUtc: slots.slots[0].startUtc, vin: '1HGCM82633A004352', make: 'Test', model: 'Model', problemDescription: 'Тестовий опис' }) });
      return { status: response.status, body: await response.json() };
    });
    expect(booking.status).toBe(201);
    const bell = page.locator('app-notification-bell summary');
    await expect(bell).toHaveAttribute('aria-label', /непрочитаних: 2/);
    const initial = await page.evaluate(async () => await (await fetch('/api/notifications')).json());
    const personal = initial.find((n: { type: string }) => n.type === 'AppointmentCreated');
    const role = initial.find((n: { type: string }) => n.type === 'AdminAppointmentCreated');
    expect(personal.link).toBe(`/account/appointments/${booking.body.id}`);
    expect(role.link).toBe(`/admin/appointments/${booking.body.id}`);
    await page.getByRole('button', { name: 'Вийти' }).click(); await expect(page).toHaveURL(/\/login$/);
    sql(`DELETE FROM "AspNetUserRoles" WHERE "UserId" = (SELECT "Id" FROM "AspNetUsers" WHERE "PhoneNormalized" = '+380000000073') AND "RoleId" = (SELECT "Id" FROM "AspNetRoles" WHERE "Name" = 'Admin')`);
    await login('0000000073', 'Example-password-123!', /\/account\/appointments$/);
    await page.goto('/account/notifications');
    await expect(page.locator('.inbox__item')).toHaveCount(1);
    await expect(page.locator('.inbox__item')).toContainText('Запис на огляд створено');
    await expect(bell).toHaveAttribute('aria-label', /непрочитаних: 1/);
    const result = await page.evaluate(async hidden => {
      const history = await (await fetch('/api/notifications')).json(); await fetch('/api/auth/antiforgery');
      const token = decodeURIComponent(document.cookie.split('; ').find(c => c.startsWith('XSRF-TOKEN='))!.split('=')[1]);
      const denied = await fetch(`/api/notifications/${hidden}/read`, { method: 'PATCH', headers: { RequestVerificationToken: token } });
      return { history, denied: denied.status };
    }, role.id);
    expect(result.denied).toBe(404); expect(result.history.map((n: { id: string }) => n.id)).toEqual([personal.id]);
    await page.getByRole('button', { name: 'Позначити всі прочитаними' }).click();
    await expect(bell).toHaveAttribute('aria-label', /непрочитаних: 0/);
    expect(sql(`SELECT count(*) FROM "Notifications" WHERE "Id" = '${role.id}' AND "ReadAtUtc" IS NULL`)).toBe('1');
    await expect.poll(() => page.evaluate(() => (window as unknown as { notificationSockets: WebSocket[] }).notificationSockets.at(-1)?.readyState)).toBe(1);
    await page.evaluate(() => (window as unknown as { notificationSockets: WebSocket[] }).notificationSockets.at(-1)!.close(4000, 'reconnect test'));
    const cancelled = await manager.evaluate(async id => {
      const detail = await (await fetch(`/api/appointments/${id}`)).json(); await fetch('/api/auth/antiforgery');
      const token = decodeURIComponent(document.cookie.split('; ').find(c => c.startsWith('XSRF-TOKEN='))!.split('=')[1]);
      return (await fetch(`/api/admin/appointments/${id}/cancel`, { method: 'POST', headers: { 'Content-Type': 'application/json', RequestVerificationToken: token }, body: JSON.stringify({ version: detail.version, confirmed: true, reason: 'Test' }) })).status;
    }, booking.body.id);
    expect(cancelled).toBe(204);
    await expect(page.locator('.inbox').getByText('Запис скасовано', { exact: true })).toBeVisible({ timeout: 15000 });
    await expect(bell).toHaveAttribute('aria-label', /непрочитаних: 1/);
    await page.getByRole('button', { name: 'Вийти' }).click(); await expect(page).toHaveURL(/\/login$/);
    await login(process.env['E2E_ADMIN_PHONE']!, process.env['E2E_ADMIN_PASSWORD']!, /\/admin$/);
    await page.goto('/account/notifications');
    const switched = await page.evaluate(async () => await (await fetch('/api/notifications')).json());
    expect(switched.some((n: { id: string }) => n.id === personal.id || n.id === role.id)).toBe(false);
  } finally { await context.close(); }
});
