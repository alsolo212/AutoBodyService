import { expect, test, BrowserContext, Page } from '@playwright/test';
import { execFileSync } from 'node:child_process';
import { HubConnection, HubConnectionBuilder, HttpTransportType, LogLevel } from '@microsoft/signalr';

test('cookie SignalR isolates recipients, revalidates sessions and reconnects after committed writes', async ({ page, browser, baseURL }) => {
  test.skip(process.env['E2E_LIVE_API'] !== '1', 'Requires the isolated API and PostgreSQL.');
  await page.goto('/login');
  await page.getByLabel('Номер телефону').fill(process.env['E2E_ADMIN_PHONE']!);
  await page.getByLabel('Пароль', { exact: true }).fill(process.env['E2E_ADMIN_PASSWORD']!);
  await page.getByRole('button', { name: 'Увійти' }).click();
  await expect(page).toHaveURL(/\/admin$/);
  const configured = await page.evaluate(async () => {
    const schedule = await (await fetch('/api/admin/inspection-schedule')).json();
    await fetch('/api/auth/antiforgery');
    const token = decodeURIComponent(document.cookie.split('; ').find(c => c.startsWith('XSRF-TOKEN='))!.split('=')[1]);
    const intervals = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'].map(dayOfWeek => ({ dayOfWeek, start: '09:00', end: '11:00' }));
    return (await fetch('/api/admin/inspection-schedule', { method: 'PUT', headers: { 'Content-Type': 'application/json', RequestVerificationToken: token }, body: JSON.stringify({ version: schedule.version, intervals }) })).status;
  });
  expect(configured).toBe(204);
  async function register(phone: string): Promise<{context: BrowserContext, page: Page, requests: () => number, sockets: string[], frames: string[]}> {
    const context = await browser.newContext({ ignoreHTTPSErrors: true }); const customer = await context.newPage();
    let historyRequests = 0; const sockets: string[] = []; const frames: string[] = [];
    customer.on('websocket', socket => { if (new URL(socket.url()).pathname === '/hubs/notifications') { sockets.push(socket.url()); socket.on('framereceived', frame => frames.push(String(frame.payload))); } });
    customer.on('response', response => { if (response.request().method() === 'GET' && response.url().endsWith('/api/notifications')) historyRequests++; });
    await customer.goto('/register');
    await customer.getByLabel('Ім’я').fill('Тестовий клієнт');
    await customer.getByLabel('Номер телефону').fill(phone);
    await customer.getByLabel('Пароль', { exact: true }).fill('Example-password-123!');
    await customer.getByLabel('Повторіть пароль').fill('Example-password-123!');
    await customer.getByRole('button', { name: 'Зареєструватися' }).click();
    await expect(customer).toHaveURL(/\/account\/appointments$/);
    await expect.poll(() => historyRequests).toBeGreaterThan(0);
    await expect.poll(() => sockets.length).toBeGreaterThan(0);
    expect(new URL(sockets[0]).search).toBe('');
    return { context, page: customer, requests: () => historyRequests, sockets, frames };
  }
  async function connect(context: BrowserContext, origin = baseURL!): Promise<HubConnection> {
    const cookie = (await context.cookies(baseURL!)).map(c => `${c.name}=${c.value}`).join('; ');
    const connection = new HubConnectionBuilder().withUrl(`${baseURL}/hubs/notifications`, {
      transport: HttpTransportType.WebSockets, skipNegotiation: true, headers: { Cookie: cookie, Origin: origin },
    }).withAutomaticReconnect([0, 100, 500, 1000, 2000]).configureLogging(LogLevel.None).build();
    await connection.start(); return connection;
  }
  async function book(customer: Page, slot: number) {
    return customer.evaluate(async index => {
      const slots = await (await fetch('/api/inspection-slots?date=2099-01-15')).json();
      await fetch('/api/auth/antiforgery');
      const token = decodeURIComponent(document.cookie.split('; ').find(c => c.startsWith('XSRF-TOKEN='))!.split('=')[1]);
      const response = await fetch('/api/appointments', { method: 'POST', headers: { 'Content-Type': 'application/json', RequestVerificationToken: token }, body: JSON.stringify({ inspectionStartUtc: slots.slots[index].startUtc, vin: '1HGCM82633A004352', make: 'Test', model: 'Model', problemDescription: 'Issue' }) });
      return { status: response.status, body: await response.json() };
    }, slot);
  }
  const a = await register('0000000081'); const b = await register('0000000082'); const c = await register('0000000083');
  let first: HubConnection | undefined; let second: HubConnection | undefined; let third: HubConnection | undefined;
  try {
    const unauth = new HubConnectionBuilder().withUrl(`${baseURL}/hubs/notifications`, { transport: HttpTransportType.WebSockets, skipNegotiation: true }).configureLogging(LogLevel.None).build();
    await expect(unauth.start()).rejects.toThrow();
    await expect(connect(a.context, 'https://evil.invalid')).rejects.toThrow();
    first = await connect(a.context); second = await connect(b.context); third = await connect(c.context);
    const receivedA: unknown[][] = []; const receivedB: unknown[][] = []; const receivedC: unknown[][] = [];
    first.on('NotificationsChanged', (...args: unknown[]) => receivedA.push(args));
    second.on('NotificationsChanged', (...args: unknown[]) => receivedB.push(args));
    third.on('NotificationsChanged', (...args: unknown[]) => receivedC.push(args));
    await expect(first.invoke('JoinGroup', 'user:another')).rejects.toThrow();
    const beforeHistoryRequests = a.requests();
    const aBooking = await book(a.page, 0); expect(aBooking.status).toBe(201);
    await expect.poll(() => receivedA.length).toBe(1);
    await expect.poll(() => a.requests()).toBeGreaterThan(beforeHistoryRequests);
    await expect.poll(() => a.frames.some(frame => frame.includes('NotificationsChanged'))).toBe(true); expect(receivedA[0]).toEqual([]);
    expect(receivedB).toHaveLength(0);
    const history = await a.page.evaluate(async () => await (await fetch('/api/notifications')).json());
    expect(history.some((n: { link: string }) => n.link === `/account/appointments/${aBooking.body.id}`)).toBe(true);
    const bBooking = await book(b.page, 0); expect(bBooking.status).toBe(201);
    await expect.poll(() => receivedB.length).toBe(1); expect(receivedB[0]).toEqual([]);
    expect(receivedA).toHaveLength(1);

    const reconnected = new Promise<void>(resolve => first!.onreconnected(() => resolve()));
    // Force a real transport interruption; the official client must reconnect automatically.
    const transport = (first as unknown as { connection: { transport: { _webSocket: { terminate(): void } } } }).connection.transport;
    transport._webSocket.terminate();
    await Promise.race([reconnected, new Promise<never>((_, reject) => setTimeout(() => reject(new Error('reconnect timeout')), 10000))]);
    const again = await book(a.page, 0); expect(again.status).toBe(201);
    await expect.poll(() => receivedA.length).toBe(2);
    const cBooking = await book(c.page, 0); expect(cBooking.status).toBe(201);
    await expect.poll(() => receivedC.length).toBe(1);

    await a.page.goto('/account/profile');
    await a.page.getByLabel('Поточний пароль').fill('Example-password-123!');
    await a.page.getByLabel('Новий пароль', { exact: true }).fill('Another-password-123!');
    await a.page.getByLabel('Повторіть новий пароль').fill('Another-password-123!');
    const change = a.page.waitForResponse(response => response.url().endsWith('/api/auth/change-password') && response.request().method() === 'POST');
    await a.page.getByRole('button', { name: 'Змінити пароль' }).click();
    expect((await change).status()).toBe(204);
    await expect(a.page.getByText('Пароль змінено.', { exact: true })).toBeVisible();
    await expect.poll(() => first!.state).not.toBe('Connected');
    expect(receivedA).toHaveLength(2);
    await first.stop();
    first = await connect(a.context);
    first.on('NotificationsChanged', (...args: unknown[]) => receivedA.push(args));

    const logout = await a.page.evaluate(async () => {
      await fetch('/api/auth/antiforgery');
      const token = decodeURIComponent(document.cookie.split('; ').find(c => c.startsWith('XSRF-TOKEN='))!.split('=')[1]);
      return (await fetch('/api/auth/logout', { method: 'POST', headers: { RequestVerificationToken: token } })).status;
    });
    expect(logout).toBe(204);
    const cancelled = await page.evaluate(async id => {
      const detail = await (await fetch(`/api/appointments/${id}`)).json();
      await fetch('/api/auth/antiforgery');
      const token = decodeURIComponent(document.cookie.split('; ').find(c => c.startsWith('XSRF-TOKEN='))!.split('=')[1]);
      return (await fetch(`/api/admin/appointments/${id}/cancel`, { method: 'POST', headers: { 'Content-Type': 'application/json', RequestVerificationToken: token }, body: JSON.stringify({ version: detail.version, confirmed: true, reason: 'Test' }) })).status;
    }, aBooking.body.id);
    expect(cancelled).toBe(204);
    await expect.poll(() => first!.state).not.toBe('Connected');
    expect(receivedA).toHaveLength(2);
    const detail = await page.evaluate(async id => await (await fetch(`/api/appointments/${id}`)).json(), aBooking.body.id);
    expect(detail.status).toBe('Cancelled');

    const container = process.env['PROFILE_VERIFY_CONTAINER'] ?? '';
    if (!/^autobody-ui-verify-[0-9a-f]+$/.test(container)) throw new Error('Missing isolated test database');
    execFileSync('docker', ['exec', container, 'psql', '-U', 'auth_test', '-d', 'auth_test', '-c',
      `UPDATE "AspNetUsers" SET "IsBlocked" = true WHERE "PhoneNormalized" = '+380000000082'`], { stdio: 'ignore' });
    const blockedCancellation = await page.evaluate(async id => {
      const detail = await (await fetch(`/api/appointments/${id}`)).json();
      await fetch('/api/auth/antiforgery');
      const token = decodeURIComponent(document.cookie.split('; ').find(c => c.startsWith('XSRF-TOKEN='))!.split('=')[1]);
      return (await fetch(`/api/admin/appointments/${id}/cancel`, { method: 'POST', headers: { 'Content-Type': 'application/json', RequestVerificationToken: token }, body: JSON.stringify({ version: detail.version, confirmed: true, reason: 'Test' }) })).status;
    }, bBooking.body.id);
    expect(blockedCancellation).toBe(204);
    await expect.poll(() => second!.state).not.toBe('Connected');
    expect(receivedB).toHaveLength(1);
    execFileSync('docker', ['exec', container, 'psql', '-U', 'auth_test', '-d', 'auth_test', '-c',
      `DELETE FROM "AspNetUserRoles" WHERE "UserId" = (SELECT "Id" FROM "AspNetUsers" WHERE "PhoneNormalized" = '+380000000083')`], { stdio: 'ignore' });
    const roleCancellation = await page.evaluate(async id => {
      const detail = await (await fetch(`/api/appointments/${id}`)).json();
      await fetch('/api/auth/antiforgery');
      const token = decodeURIComponent(document.cookie.split('; ').find(c => c.startsWith('XSRF-TOKEN='))!.split('=')[1]);
      return (await fetch(`/api/admin/appointments/${id}/cancel`, { method: 'POST', headers: { 'Content-Type': 'application/json', RequestVerificationToken: token }, body: JSON.stringify({ version: detail.version, confirmed: true, reason: 'Test' }) })).status;
    }, cBooking.body.id);
    expect(roleCancellation).toBe(204);
    await expect.poll(() => third!.state).not.toBe('Connected');
    expect(receivedC).toHaveLength(1);
  } finally {
    await first?.stop(); await second?.stop(); await third?.stop(); await a.context.close(); await b.context.close(); await c.context.close();
  }
});
