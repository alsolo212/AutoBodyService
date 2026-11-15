import { expect, Page, test } from '@playwright/test';
import { login, register } from './admin-users-helpers';

async function createOwn(page: Page, index: number, make: string) {
  return page.evaluate(async ({ index, make }) => {
    const slots = await (await fetch('/api/inspection-slots?date=2099-01-15')).json();
    await fetch('/api/auth/antiforgery');
    const token = decodeURIComponent(document.cookie.split('; ').find(c => c.startsWith('XSRF-TOKEN='))!.split('=')[1]);
    const response = await fetch('/api/appointments', { method: 'POST', headers: { 'Content-Type': 'application/json', RequestVerificationToken: token },
      body: JSON.stringify({ inspectionStartUtc: slots.slots[index].startUtc, vin: '1HGCM82633A123456', make, model: 'Авто', problemDescription: 'Тестовий опис власника' }) });
    if (response.status !== 201) throw new Error(`Creating own order failed: ${response.status}`);
    return (await response.json()).id as string;
  }, { index, make });
}

test('personal order details enforce ownership for User, Admin and SuperAdmin while admin views remain available', async ({ page, browser }) => {
  test.setTimeout(120000);
  await login(page, process.env['E2E_ADMIN_PHONE']!, process.env['E2E_ADMIN_PASSWORD']!); await expect(page).toHaveURL(/\/admin$/);
  const schedule = await page.evaluate(async () => {
    const current = await (await fetch('/api/admin/inspection-schedule')).json(); await fetch('/api/auth/antiforgery');
    const token = decodeURIComponent(document.cookie.split('; ').find(c => c.startsWith('XSRF-TOKEN='))!.split('=')[1]);
    const intervals = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'].map(dayOfWeek => ({ dayOfWeek, start: '09:00', end: '11:00' }));
    return (await fetch('/api/admin/inspection-schedule', { method: 'PUT', headers: { 'Content-Type': 'application/json', RequestVerificationToken: token }, body: JSON.stringify({ version: current.version, intervals }) })).status;
  });
  expect(schedule).toBe(204);
  const customer = await register(browser, 'Клієнт тест', '0000000081');
  const admin = await register(browser, 'Адміністратор тест', '0000000082');
  try {
    const promotion = await page.evaluate(async () => {
      const target = (await (await fetch('/api/admin/users?search=0000000082')).json())[0]; await fetch('/api/auth/antiforgery');
      const token = decodeURIComponent(document.cookie.split('; ').find(c => c.startsWith('XSRF-TOKEN='))!.split('=')[1]);
      return (await fetch(`/api/admin/users/${target.id}/roles`, { method: 'PUT', headers: { 'Content-Type': 'application/json', RequestVerificationToken: token }, body: JSON.stringify({ version: target.version, isAdmin: true }) })).status;
    });
    expect(promotion).toBe(204);
    await login(admin.page, '0000000082', 'Example-password-123!'); await expect(admin.page).toHaveURL(/\/admin$/);
    const customerId = await createOwn(customer.page, 0, 'Клієнтська');
    const adminId = await createOwn(admin.page, 0, 'Адмінська');
    const superId = await createOwn(page, 0, 'Суперська');
    for (const [client, ownId, otherId, make] of [
      [customer.page, customerId, adminId, 'Клієнтська'], [admin.page, adminId, customerId, 'Адмінська'], [page, superId, customerId, 'Суперська'],
    ] as const) {
      await client.goto(`/account/appointments/${ownId}`);
      await expect(client.getByRole('heading', { name: `${make} Авто` })).toBeVisible();
      await expect(client.getByText('Тестовий опис власника')).toBeVisible();
      const response = client.waitForResponse(r => r.url().includes(`/api/appointments/my/${otherId}`));
      await client.goto(`/account/appointments/${otherId}`);
      expect((await response).status()).toBe(404);
      await expect(client.getByRole('heading', { name: 'Запис не знайдено' })).toBeVisible();
      await expect(client.getByText('Тестовий опис власника')).toHaveCount(0);
      await expect(client.getByText('1HGCM82633A123456')).toHaveCount(0);
    }
    for (const client of [admin.page, page]) {
      await client.goto(`/admin/appointments/${customerId}`);
      await expect(client.getByRole('heading', { name: 'Клієнтська Авто' })).toBeVisible();
      await expect(client.getByText('Тестовий опис власника')).toBeVisible();
    }
    expect(await customer.page.evaluate(async id => (await fetch(`/api/appointments/${id}`)).status, adminId)).toBe(404);
  } finally { await customer.context.close(); await admin.context.close(); }
});
