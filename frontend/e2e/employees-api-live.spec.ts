import { expect, request, test } from '@playwright/test';

test('employee directory works through the live authenticated API', async ({ page }) => {
  test.skip(process.env['E2E_LIVE_API'] !== '1', 'Requires an isolated API and PostgreSQL.');
  const api = await request.newContext({ ignoreHTTPSErrors: true });
  try {
    const openapi = await api.get(`${process.env['E2E_API_TARGET']}/openapi/v1.json`);
    expect(openapi.status()).toBe(200);
    const document = await openapi.json();
    expect(Object.keys(document.paths['/api/admin/employees'])).toEqual(expect.arrayContaining(['get', 'post']));
    expect(Object.keys(document.paths['/api/admin/employees/{id}'])).toEqual(expect.arrayContaining(['get', 'patch']));
  } finally { await api.dispose(); }
  await page.goto('/login');
  await page.getByLabel('Номер телефону').fill(process.env['E2E_ADMIN_PHONE']!);
  await page.getByLabel('Пароль', { exact: true }).fill(process.env['E2E_ADMIN_PASSWORD']!);
  await page.getByRole('button', { name: 'Увійти' }).click();
  await expect(page).toHaveURL(/\/admin$/);

  const result = await page.evaluate(async () => {
    await fetch('/api/auth/antiforgery');
    const token = decodeURIComponent(document.cookie.split('; ').find(c => c.startsWith('XSRF-TOKEN='))!.split('=')[1]);
    const headers = { 'Content-Type': 'application/json', RequestVerificationToken: token };
    const path = '/api/admin/employees';
    const missingToken = await fetch(path, { method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ fullName: 'Ignored', specialization: 'Repair', isActive: true }) });
    const created = await fetch(path, { method: 'POST', headers,
      body: JSON.stringify({ fullName: 'Тестовий працівник', specialization: 'Кузовний ремонт', isActive: true }) });
    const employee = await created.json();
    const updated = await fetch(`${path}/${employee.id}`, { method: 'PATCH', headers,
      body: JSON.stringify({ version: employee.version, isActive: false }) });
    const list = await fetch(path);
    return { missingToken: missingToken.status, created: created.status, location: created.headers.get('location'),
      updated: updated.status, list: list.status, employees: await list.json(), id: employee.id };
  });
  expect(result.missingToken).toBe(400);
  expect(result.created).toBe(201);
  expect(result.location).toContain(`/api/admin/employees/${result.id}`);
  expect(result.updated).toBe(200);
  expect(result.list).toBe(200);
  expect(result.employees).toEqual([expect.objectContaining({ id: result.id, fullName: 'Тестовий працівник',
    specialization: 'Кузовний ремонт', isActive: false })]);
});
