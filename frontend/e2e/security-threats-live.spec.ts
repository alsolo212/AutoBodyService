import { expect, test } from '@playwright/test';
import { login, register } from './admin-users-helpers';

test('stored markup is inert, searches treat SQL payloads as data and controller writes require CSRF', async ({ page, browser, request, baseURL }) => {
  test.skip(process.env['E2E_LIVE_API'] !== '1', 'Requires isolated API and PostgreSQL.');
  test.setTimeout(90_000);
  await page.addInitScript(() => { (window as unknown as { securityProbe: number }).securityProbe = 0; });
  await login(page, process.env['E2E_ADMIN_PHONE']!, process.env['E2E_ADMIN_PASSWORD']!);
  await expect(page).toHaveURL(/\/admin$/);
  const customer = await register(browser, 'Клієнт безпека', '0000000074');
  const description = 'Подряпина <img src=x onerror="window.securityProbe=1">';
  try {
    const fixture = await page.evaluate(async problemDescription => {
      await fetch('/api/auth/antiforgery');
      const token = decodeURIComponent(document.cookie.split('; ').find(c => c.startsWith('XSRF-TOKEN='))!.split('=')[1]);
      const headers = { 'Content-Type': 'application/json', RequestVerificationToken: token };
      const schedule = await (await fetch('/api/admin/inspection-schedule')).json();
      const intervals = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'].map(dayOfWeek => ({ dayOfWeek, start: '09:00', end: '10:00' }));
      const updated = await fetch('/api/admin/inspection-schedule', { method: 'PUT', headers, body: JSON.stringify({ version: schedule.version, intervals }) });
      if (updated.status !== 204) throw new Error(`Schedule: ${updated.status}`);
      const slots = await (await fetch('/api/inspection-slots?date=2099-09-10')).json();
      const create = await fetch('/api/appointments', { method: 'POST', headers, body: JSON.stringify({ inspectionStartUtc: slots.slots[0].startUtc, vin: '1HGCM82633A123456', make: 'Безпека', model: 'Тест', problemDescription }) });
      if (create.status !== 201) throw new Error(`Booking: ${create.status}`);
      const id = (await create.json()).id as string;
      const account = (await (await fetch('/api/admin/users?search=0000000074')).json())[0];
      const current = await (await fetch('/api/admin/inspection-schedule')).json();
      return { id, account, scheduleVersion: current.version };
    }, description);
    await page.goto(`/account/appointments/${fixture.id}`);
    await expect(page.locator('.problem')).toHaveText(description);
    await expect(page.locator('.problem img')).toHaveCount(0);
    expect(await page.evaluate(() => (window as unknown as { securityProbe: number }).securityProbe)).toBe(0);
    await page.goto(`/admin/appointments/${fixture.id}`);
    await expect(page.locator('dd.problem')).toHaveText(description);
    await expect(page.locator('dd.problem img')).toHaveCount(0);
    expect(await page.evaluate(() => (window as unknown as { securityProbe: number }).securityProbe)).toBe(0);
    const searches = await page.evaluate(async () => {
      const get = async (path: string, term: string) => { const response = await fetch(`${path}?search=${encodeURIComponent(term)}`); return { status: response.status, data: await response.json() }; };
      return { users: await get('/api/admin/users', 'Клієнт безпека'), usersSql: await get('/api/admin/users', "' OR 1=1 --"), usersWildcard: await get('/api/admin/users', '%'), orders: await get('/api/admin/appointments', 'Безпека'), ordersSql: await get('/api/admin/appointments', "' OR 1=1 --"), ordersWildcard: await get('/api/admin/appointments', '%') };
    });
    for (const value of Object.values(searches)) expect(value.status).toBe(200);
    expect(searches.users.data).toHaveLength(1);
    expect(searches.usersSql.data).toEqual([]); expect(searches.usersWildcard.data).toEqual([]);
    expect(searches.orders.data.total).toBe(1);
    expect(searches.ordersSql.data.total).toBe(0); expect(searches.ordersWildcard.data.total).toBe(0);
    const denied = await page.evaluate(async ({ id, account, scheduleVersion }) => {
      const guid = '00000000-0000-0000-0000-000000000001';
      const plan = { version: 1, price: 1, plannedStartUtc: '2099-09-11T07:00:00Z', estimatedFinishUtc: '2099-09-12T07:00:00Z', inspectionPerformed: true };
      const tests: [string, string, unknown][] = [
        ['register', '/api/auth/register', { name: 'Тест', phone: '0000000073', password: 'Example-password-123!' }],
        ['login', '/api/auth/login', { phone: '0000000074', password: 'Example-password-123!' }],
        ['change-password', '/api/auth/change-password', { currentPassword: 'Example-password-123!', newPassword: 'Another-password-123!' }],
        ['logout', '/api/auth/logout', {}],
        ['appointment-create', '/api/appointments', { inspectionStartUtc: '2099-09-10T07:30:00Z', vin: '1HGCM82633A123456', make: 'Тест', model: 'Авто', problemDescription: 'Пошкоджено бампер' }],
        ['appointment-cancel', `/api/appointments/${id}/cancel`, {}],
        ['notifications-read-all', '/api/notifications/read-all', {}],
        ['manual-contact', `/api/admin/appointments/${id}/manual-contact`, { reason: 'NewAppointment' }],
        ['transition', `/api/admin/appointments/${id}/transition`, { version: 1, target: 'Scheduled', inspectionPerformed: true }],
        ['admin-cancel', `/api/admin/appointments/${id}/cancel`, { version: 1, confirmed: true, reason: 'Тест' }],
        ['schedule-preview', '/api/admin/inspection-schedule/preview', { version: scheduleVersion, intervals: [] }],
        ['exception-preview', '/api/admin/inspection-exceptions/preview', { version: scheduleVersion, date: '2099-09-10', exception: null }],
        ['employee-create', '/api/admin/employees', { fullName: 'Тест', specialization: 'Майстер', isActive: true }],
        ['assignment-create', '/api/admin/assignments', { appointmentId: id, employeeId: guid }],
        ['price-create', '/api/admin/prices', { name: 'Тест', price: 1, isFrom: false, sortOrder: 0, isPublished: true }],
        ['expense-create', '/api/admin/reports/expenses', { amount: 1 }],
        ['reset-password', `/api/admin/users/${account.id}/reset-password`, { version: account.version }],
      ];
      const additional: [string, string, string, unknown][] = [
        ['profile', 'PATCH', '/api/profile', { name: 'Несанкціонована зміна' }],
        ['notification-read', 'PATCH', `/api/notifications/${guid}/read`, {}],
        ['repair-plan', 'PUT', `/api/admin/appointments/${id}/repair-plan`, plan],
        ['schedule', 'PUT', '/api/admin/inspection-schedule', { version: scheduleVersion, intervals: [] }],
        ['exception', 'PUT', '/api/admin/inspection-exceptions', { version: scheduleVersion, date: '2099-09-10', exception: null }],
        ['employee-edit', 'PATCH', `/api/admin/employees/${guid}`, { version: 1, fullName: 'Тест' }],
        ['assignment-release', 'PATCH', `/api/admin/assignments/${guid}/release`, {}],
        ['price-edit', 'PATCH', `/api/admin/prices/${guid}`, { version: 1, name: 'Тест' }],
        ['expense-edit', 'PATCH', `/api/admin/reports/expenses/${guid}`, { version: 1, amount: 1 }],
        ['roles', 'PUT', `/api/admin/users/${account.id}/roles`, { version: account.version, isAdmin: true }],
        ['block', 'PATCH', `/api/admin/users/${account.id}/block`, { version: account.version, isBlocked: true }],
      ];
      const results: { name: string; status: number }[] = [];
      for (const [name, path, body] of tests) results.push({ name, status: (await fetch(path, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) })).status });
      for (const [name, method, path, body] of additional) results.push({ name, status: (await fetch(path, { method, headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) })).status });
      return results;
    }, fixture);
    expect(denied).toHaveLength(28);
    expect(denied.filter(result => result.status !== 400)).toEqual([]);
    const after = await page.evaluate(async ({ id, account }) => ({
      session: (await fetch('/api/auth/session')).status,
      own: await (await fetch(`/api/appointments/my/${id}`)).json(),
      users: await (await fetch('/api/admin/users?search=0000000074')).json(),
      prices: await (await fetch('/api/prices')).json(),
    }), fixture);
    expect(after.session).toBe(200); expect(after.own.problemDescription).toBe(description);
    expect(after.own.status).toBe('AwaitingInspection');
    expect(after.users[0].version).toBe(fixture.account.version);
    expect(after.users[0].isAdmin).toBe(false); expect(after.users[0].isBlocked).toBe(false);
    expect(after.prices).toEqual([]);
    const cors = await request.fetch(`${baseURL}/api/auth/session`, { method: 'OPTIONS', headers: { Origin: 'https://evil.invalid', 'Access-Control-Request-Method': 'GET' } });
    expect(cors.headers()['access-control-allow-origin']).toBeUndefined();
  } finally { await customer.context.close(); }
});
