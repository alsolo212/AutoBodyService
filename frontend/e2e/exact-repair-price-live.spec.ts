import { expect, test } from '@playwright/test';
import { login } from './admin-users-helpers';

test('exact repair price survives UI, API, PostgreSQL and completion-based revenue', async ({ page }) => {
  test.setTimeout(120000);
  await login(page, process.env['E2E_ADMIN_PHONE']!, process.env['E2E_ADMIN_PASSWORD']!);
  await expect(page).toHaveURL(/\/admin$/);
  const id = await page.evaluate(async () => {
    const headers = () => ({ 'Content-Type': 'application/json', RequestVerificationToken: decodeURIComponent(document.cookie.split('; ').find(c => c.startsWith('XSRF-TOKEN='))!.split('=')[1]) });
    await fetch('/api/auth/antiforgery');
    const schedule = await (await fetch('/api/admin/inspection-schedule')).json();
    const intervals = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'].map(dayOfWeek => ({ dayOfWeek, start: '09:00', end: '10:00' }));
    const updated = await fetch('/api/admin/inspection-schedule', { method: 'PUT', headers: headers(), body: JSON.stringify({ version: schedule.version, intervals }) });
    if (updated.status !== 204) throw new Error(`Schedule: ${updated.status}`);
    const slots = await (await fetch('/api/inspection-slots?date=2099-09-10')).json();
    const response = await fetch('/api/appointments', { method: 'POST', headers: headers(), body: JSON.stringify({ inspectionStartUtc: slots.slots[0].startUtc, vin: '1HGCM82633A123456', make: 'Тестова', model: 'Точність', problemDescription: 'Перевірка точної ціни' }) });
    if (response.status !== 201) throw new Error(`Booking: ${response.status}`);
    return (await response.json()).id as string;
  });
  await page.goto(`/admin/appointments/${id}`);
  await page.getByRole('button', { name: 'Узгодити план' }).click();
  await page.getByLabel('Узгоджена вартість, ₴').fill('9007199254740993.01');
  await page.getByLabel('Плановий початок за Києвом').fill('2099-09-11T10:00');
  await page.getByLabel('Орієнтовне завершення за Києвом').fill('2099-09-12T10:00');
  await page.locator('form').filter({ has: page.locator('#plan-price') }).getByLabel('Підтверджую, що огляд проведено').check();
  const request = page.waitForRequest(r => r.url().endsWith('/repair-plan') && r.method() === 'PUT');
  const response = page.waitForResponse(r => r.url().endsWith('/repair-plan') && r.request().method() === 'PUT');
  await page.getByRole('button', { name: 'Зберегти план' }).click();
  expect((await request).postData()).toContain('"price":9007199254740993.01');
  expect((await response).status()).toBe(204);
  await page.reload();
  await expect(page.getByText('9 007 199 254 740 993,01 ₴')).toBeVisible();
  for (const path of ['/account/appointments', `/account/appointments/${id}`, '/admin/appointments']) {
    await page.goto(path);
    await expect(page.getByText('9 007 199 254 740 993,01 ₴')).toBeVisible();
  }
  const completed = await page.evaluate(async id => {
    const get = async () => (await (await fetch(`/api/appointments/${id}`)).json());
    const exact = (await get()).repairPlan.agreedPriceExact;
    await fetch('/api/auth/antiforgery');
    const token = decodeURIComponent(document.cookie.split('; ').find(c => c.startsWith('XSRF-TOKEN='))!.split('=')[1]);
    for (const target of ['Scheduled', 'InProgress', 'AwaitingPayment', 'Completed']) {
      const version = (await get()).version;
      const response = await fetch(`/api/admin/appointments/${id}/transition`, { method: 'POST', headers: { 'Content-Type': 'application/json', RequestVerificationToken: token }, body: JSON.stringify({ version, target, inspectionPerformed: true, paymentReceived: true, vehicleHandedOver: true }) });
      if (response.status !== 204) throw new Error(`${target}: ${response.status}`);
    }
    return { before: exact, after: (await get()).repairPlan.agreedPriceExact };
  }, id);
  expect(completed).toEqual({ before: '9007199254740993.01', after: '9007199254740993.01' });
  await page.goto('/admin/reports');
  await expect(page.getByLabel('Показники місяця')).toContainText('9 007 199 254 740 993,01 ₴');
  const report = await page.evaluate(async () => {
    const parts = new Intl.DateTimeFormat('en-GB', { timeZone: 'Europe/Kyiv', year: 'numeric', month: 'numeric' }).formatToParts(new Date());
    const year = parts.find(p => p.type === 'year')!.value, month = parts.find(p => p.type === 'month')!.value;
    return (await (await fetch(`/api/admin/reports/finance?year=${year}&month=${month}`)).json());
  });
  expect(report.revenueExact).toBe('9007199254740993.01');
  expect(report.orders[0].amountExact).toBe('9007199254740993.01');
});
