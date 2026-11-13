import { expect, test } from '@playwright/test';

test('notification history is durable and scoped for customer and administrator', async ({ page, browser }) => {
  test.skip(process.env['E2E_LIVE_API'] !== '1', 'Requires isolated API and PostgreSQL.');
  await page.goto('/login');
  await page.getByLabel('Номер телефону').fill(process.env['E2E_ADMIN_PHONE']!);
  await page.getByLabel('Пароль', { exact: true }).fill(process.env['E2E_ADMIN_PASSWORD']!);
  await page.getByRole('button', { name: 'Увійти' }).click();
  await expect(page).toHaveURL(/\/admin$/);
  const configured = await page.evaluate(async () => {
    const schedule = await (await fetch('/api/admin/inspection-schedule')).json();
    await fetch('/api/auth/antiforgery');
    const token = decodeURIComponent(document.cookie.split('; ').find(c => c.startsWith('XSRF-TOKEN='))!.split('=')[1]);
    const intervals = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'].map(dayOfWeek => ({ dayOfWeek, start: '09:00', end: '10:00' }));
    return (await fetch('/api/admin/inspection-schedule', { method: 'PUT', headers: { 'Content-Type': 'application/json', RequestVerificationToken: token }, body: JSON.stringify({ version: schedule.version, intervals }) })).status;
  });
  expect(configured).toBe(204);
  const customerContext = await browser.newContext({ ignoreHTTPSErrors: true });
  try {
    const customer = await customerContext.newPage();
    await customer.goto('/register');
    await customer.getByLabel('Ім’я').fill('Тестовий клієнт');
    await customer.getByLabel('Номер телефону').fill('0000000081');
    await customer.getByLabel('Пароль', { exact: true }).fill('Example-password-123!');
    await customer.getByLabel('Повторіть пароль').fill('Example-password-123!');
    await customer.getByRole('button', { name: 'Зареєструватися' }).click();
    await expect(customer).toHaveURL(/\/account\/appointments$/);
    const result = await customer.evaluate(async () => {
      const slots = await (await fetch('/api/inspection-slots?date=2099-01-15')).json();
      await fetch('/api/auth/antiforgery');
      const token = decodeURIComponent(document.cookie.split('; ').find(c => c.startsWith('XSRF-TOKEN='))!.split('=')[1]);
      const created = await fetch('/api/appointments', { method: 'POST', headers: { 'Content-Type': 'application/json', RequestVerificationToken: token }, body: JSON.stringify({ inspectionStartUtc: slots.slots[0].startUtc, vin: '1HGCM82633A004352', make: 'Test', model: 'Model', problemDescription: 'Issue' }) });
      const appointment = await created.json();
      const history = await (await fetch('/api/notifications')).json();
      return { status: created.status, appointment, history };
    });
    expect(result.status).toBe(201);
    expect(result.history).toHaveLength(1);
    expect(result.history[0].type).toBe('AppointmentCreated');
    expect(result.history[0].link).toBe(`/account/appointments/${result.appointment.id}`);
    const adminHistory = await page.evaluate(async () => await (await fetch('/api/notifications')).json());
    expect(adminHistory).toHaveLength(1);
    expect(adminHistory[0].type).toBe('AdminAppointmentCreated');
    expect(adminHistory[0].link).toBe(`/admin/appointments/${result.appointment.id}`);
    const forbidden = await page.evaluate(async (foreignId) => {
      await fetch('/api/auth/antiforgery');
      const token = decodeURIComponent(document.cookie.split('; ').find(c => c.startsWith('XSRF-TOKEN='))!.split('=')[1]);
      return (await fetch(`/api/notifications/${foreignId}/read`, { method: 'PATCH', headers: { RequestVerificationToken: token } })).status;
    }, result.history[0].id);
    expect(forbidden).toBe(404);
    await customer.reload();
    const persisted = await customer.evaluate(async () => await (await fetch('/api/notifications?unread=true')).json());
    expect(persisted[0].id).toBe(result.history[0].id);
    const marked = await customer.evaluate(async (id) => {
      await fetch('/api/auth/antiforgery');
      const token = decodeURIComponent(document.cookie.split('; ').find(c => c.startsWith('XSRF-TOKEN='))!.split('=')[1]);
      return (await fetch(`/api/notifications/${id}/read`, { method: 'PATCH', headers: { RequestVerificationToken: token } })).status;
    }, result.history[0].id);
    expect(marked).toBe(204);
    expect(await customer.evaluate(async () => await (await fetch('/api/notifications?unread=true')).json())).toHaveLength(0);
  } finally { await customerContext.close(); }
});
