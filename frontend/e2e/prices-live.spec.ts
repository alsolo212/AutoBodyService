import { expect, test } from '@playwright/test';

test('public prices reflect real published records and archive without exposing admin actions', async ({ page, browser }) => {
  test.skip(process.env['E2E_LIVE_API'] !== '1', 'Requires an isolated API and PostgreSQL.');
  await page.goto('/#prices');
  const section = page.locator('#prices');
  await expect(section).toContainText('Опублікованих послуг поки немає.');
  await expect(section.locator('.price-list li')).toHaveCount(0);
  await page.goto('/login');
  await page.getByLabel('Номер телефону').fill(process.env['E2E_ADMIN_PHONE']!);
  await page.getByLabel('Пароль', { exact: true }).fill(process.env['E2E_ADMIN_PASSWORD']!);
  await page.getByRole('button', { name: 'Увійти' }).click(); await expect(page).toHaveURL(/\/admin$/);
  const data = await page.evaluate(async () => {
    await fetch('/api/auth/antiforgery');
    const token = decodeURIComponent(document.cookie.split('; ').find(c => c.startsWith('XSRF-TOKEN='))!.split('=')[1]);
    const headers = { 'Content-Type': 'application/json', RequestVerificationToken: token };
    const add = async (name: string, price: number, sortOrder: number, isPublished: boolean, isFrom: boolean) => {
      const response = await fetch('/api/admin/prices', { method: 'POST', headers, body: JSON.stringify({ name, description: 'Тестовий опис', price, sortOrder, isPublished, isFrom }) });
      if (response.status !== 201) throw new Error(`Create price: ${response.status}`);
      return response.json();
    };
    const late = await add('Послуга друга', 0, 20, true, true);
    const early = await add('Послуга перша', 725.25, 10, true, false);
    const hidden = await add('Прихована послуга', 15, 0, false, false);
    const withoutToken = await fetch(`/api/admin/prices/${late.id}`, { method: 'PATCH', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ version: late.version, isArchived: true }) });
    return { late, early, hidden, csrfStatus: withoutToken.status };
  });
  expect(data.csrfStatus).toBe(400);
  const visitorContext = await browser.newContext({ ignoreHTTPSErrors: true });
  try {
    const visitor = await visitorContext.newPage(); await visitor.goto('/#prices');
    const prices = visitor.locator('#prices .price-list li');
    await expect(prices).toHaveCount(2);
    await expect(prices.nth(0)).toContainText('Послуга перша'); await expect(prices.nth(0)).toContainText('725,25 ₴');
    await expect(prices.nth(1)).toContainText('Послуга друга'); await expect(prices.nth(1)).toContainText('від 0 ₴');
    await expect(visitor.locator('#prices')).not.toContainText('Прихована послуга');
    const statuses = await visitor.evaluate(async () => ({ prices: (await fetch('/api/prices')).status, admin: (await fetch('/api/admin/prices')).status }));
    expect(statuses.prices).toBe(200); expect(statuses.admin).toBe(401);
    const archived = await page.evaluate(async item => {
      const token = decodeURIComponent(document.cookie.split('; ').find(c => c.startsWith('XSRF-TOKEN='))!.split('=')[1]);
      const response = await fetch(`/api/admin/prices/${item.id}`, { method: 'PATCH', headers: { 'Content-Type': 'application/json', RequestVerificationToken: token }, body: JSON.stringify({ version: item.version, isArchived: true }) });
      return { status: response.status, body: await response.json() };
    }, data.early);
    expect(archived.status).toBe(200); expect(archived.body.isArchived).toBe(true); expect(archived.body.isPublished).toBe(false);
    await visitor.reload(); await expect(visitor.locator('#prices .price-list li')).toHaveCount(1);
    await expect(visitor.locator('#prices')).toContainText('Послуга друга');
    await expect(visitor.locator('#prices')).not.toContainText('Послуга перша');
    await visitor.setViewportSize({ width: 320, height: 800 });
    expect(await visitor.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  } finally { await visitorContext.close(); }
});
