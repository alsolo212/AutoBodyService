import { expect, test } from '@playwright/test';
import { HubConnectionBuilder, HttpTransportType, LogLevel } from '@microsoft/signalr';

test('administrative reset and block revoke an already connected account', async ({ page, browser, baseURL }) => {
  test.skip(process.env['E2E_LIVE_API'] !== '1', 'Requires isolated API and PostgreSQL');
  await page.goto('/login');
  await page.getByLabel('Номер телефону').fill(process.env['E2E_ADMIN_PHONE']!);
  await page.getByLabel('Пароль', { exact: true }).fill(process.env['E2E_ADMIN_PASSWORD']!);
  await page.getByRole('button', { name: 'Увійти' }).click();
  await expect(page).toHaveURL(/\/admin$/);
  const context = await browser.newContext({ ignoreHTTPSErrors: true });
  const customer = await context.newPage();
  try {
    await customer.goto('/register');
    await customer.getByLabel('Ім’я').fill('Тестовий клієнт');
    await customer.getByLabel('Номер телефону').fill('0000000077');
    await customer.getByLabel('Пароль', { exact: true }).fill('Example-password-123!');
    await customer.getByLabel('Повторіть пароль').fill('Example-password-123!');
    await customer.getByRole('button', { name: 'Зареєструватися' }).click();
    await expect(customer).toHaveURL(/\/account\/appointments$/);
    async function connect() {
      const cookie = (await context.cookies(baseURL!)).map(c => `${c.name}=${c.value}`).join('; ');
      const hub = new HubConnectionBuilder().withUrl(`${baseURL}/hubs/notifications`, {
        transport: HttpTransportType.WebSockets, skipNegotiation: true, headers: { Cookie: cookie, Origin: baseURL! },
      }).configureLogging(LogLevel.None).build();
      await hub.start(); return hub;
    }
    const first = await connect(); const messages: unknown[][] = [];
    first.on('NotificationsChanged', (...args: unknown[]) => messages.push(args));
    const reset = await page.evaluate(async () => {
      const user = (await (await fetch('/api/admin/users?search=0000000077')).json())[0];
      await fetch('/api/auth/antiforgery');
      const token = decodeURIComponent(document.cookie.split('; ').find(c => c.startsWith('XSRF-TOKEN='))!.split('=')[1]);
      const response = await fetch(`/api/admin/users/${user.id}/reset-password`, { method: 'POST', headers: { 'Content-Type': 'application/json', RequestVerificationToken: token }, body: JSON.stringify({ version: user.version }) });
      return { status: response.status, cache: response.headers.get('cache-control'), body: await response.json() };
    });
    expect(reset.status).toBe(200); expect(reset.cache).toContain('no-store');
    await expect.poll(() => first.state).not.toBe('Connected'); expect(messages).toHaveLength(0);
    expect(await customer.evaluate(async () => (await fetch('/api/profile')).status)).toBe(401);
    const changed = await customer.evaluate(async temporary => {
      async function token() { await fetch('/api/auth/antiforgery'); return decodeURIComponent(document.cookie.split('; ').find(c => c.startsWith('XSRF-TOKEN='))!.split('=')[1]); }
      let csrf = await token();
      const login = await fetch('/api/auth/login', { method: 'POST', headers: { 'Content-Type': 'application/json', RequestVerificationToken: csrf }, body: JSON.stringify({ phone: '0000000077', password: temporary }) });
      const restricted = await fetch('/api/profile'); csrf = await token();
      const change = await fetch('/api/auth/change-password', { method: 'POST', headers: { 'Content-Type': 'application/json', RequestVerificationToken: csrf }, body: JSON.stringify({ currentPassword: temporary, newPassword: 'Another-password-123!' }) });
      return { login: login.status, restricted: restricted.status, change: change.status };
    }, reset.body.temporaryPassword);
    expect(changed).toEqual({ login: 204, restricted: 403, change: 204 });
    const second = await connect(); second.on('NotificationsChanged', (...args: unknown[]) => messages.push(args));
    const blocked = await page.evaluate(async () => {
      const user = (await (await fetch('/api/admin/users?search=0000000077')).json())[0];
      await fetch('/api/auth/antiforgery');
      const token = decodeURIComponent(document.cookie.split('; ').find(c => c.startsWith('XSRF-TOKEN='))!.split('=')[1]);
      return (await fetch(`/api/admin/users/${user.id}/block`, { method: 'PATCH', headers: { 'Content-Type': 'application/json', RequestVerificationToken: token }, body: JSON.stringify({ version: user.version, isBlocked: true }) })).status;
    });
    expect(blocked).toBe(204); await expect.poll(() => second.state).not.toBe('Connected');
    expect(await customer.evaluate(async () => (await fetch('/api/notifications')).status)).toBe(401);
    await first.stop(); await second.stop();
  } finally { await context.close(); }
});
