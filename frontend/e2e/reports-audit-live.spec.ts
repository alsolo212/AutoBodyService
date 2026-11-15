import { expect, test } from '@playwright/test';
import { login, register } from './admin-users-helpers';

test('SuperAdmin audit filters, paging, keyboard and preserved report context use real API', async ({ page, browser }) => {
  test.setTimeout(120000);
  test.skip(process.env['E2E_LIVE_API'] !== '1', 'Requires isolated API and PostgreSQL');
  await login(page, process.env['E2E_ADMIN_PHONE']!, process.env['E2E_ADMIN_PASSWORD']!);
  await expect(page).toHaveURL(/\/admin$/);
  const result = await page.evaluate(async () => {
    await fetch('/api/auth/antiforgery');
    const token = decodeURIComponent(document.cookie.split('; ').find(c => c.startsWith('XSRF-TOKEN='))!.split('=')[1]);
    const statuses = [];
    for (let i = 0; i < 12; i++) {
      const response = await fetch('/api/admin/reports/expenses', { method: 'POST', headers: { 'Content-Type': 'application/json', RequestVerificationToken: token }, body: JSON.stringify({ amount: 12.34, description: 'synthetic private description' }) });
      statuses.push(response.status);
    }
    return statuses;
  });
  expect(result).toEqual(Array(12).fill(201));
  await page.goto('/admin/reports');
  await expect(page.getByRole('tab')).toHaveCount(2);
  const finance = page.getByRole('tab', { name: 'Фінансова статистика' });
  const audit = page.getByRole('tab', { name: 'Журнал адміністративних дій' });
  await expect(finance).toHaveAttribute('aria-selected', 'true');
  await page.getByLabel('Місяць', { exact: true }).selectOption({ label: 'Вересень' });
  await page.getByLabel('Рік', { exact: true }).fill('2020');
  await page.getByRole('button', { name: 'Показати', exact: true }).click();
  await expect(page.getByText('Завантажуємо фінансові дані…')).toHaveCount(0);
  await finance.focus(); await page.keyboard.press('ArrowRight');
  await expect(audit).toBeFocused(); await expect(audit).toHaveAttribute('aria-selected', 'true');
  const table = page.getByRole('region', { name: 'Таблиця адміністративних дій' });
  await expect(table).toContainText('Додавання витрати');
  await expect(table).not.toContainText('synthetic private description');
  await expect(table).toContainText('12,34 ₴');
  const actorId = await table.locator('tbody tr').first().locator('td').nth(1).innerText();
  await page.getByLabel('Тип дії').selectOption('ExpenseCreated');
  await page.getByLabel('Ідентифікатор виконавця').fill(actorId.trim());
  await page.getByLabel('Порядок часу').selectOption('asc');
  const today = new Date().toISOString().slice(0, 10);
  await page.getByLabel('З дати (UTC)').fill(today); await page.getByLabel('До дати включно (UTC)').fill(today);
  const filtered = page.waitForResponse(r => r.url().includes('/api/admin/audit-log?') && r.status() === 200);
  await page.getByRole('button', { name: 'Застосувати', exact: true }).click();
  const url = new URL((await filtered).url());
  expect(url.searchParams.get('action')).toBe('ExpenseCreated'); expect(url.searchParams.get('actorId')).toBe(actorId.trim());
  expect(url.searchParams.get('direction')).toBe('asc'); expect(url.searchParams.get('fromUtc')).toBe(`${today}T00:00:00.000Z`);
  await expect(page.getByLabel('Сторінки журналу')).toContainText('Знайдено: 12');
  await page.getByLabel('Сторінки журналу').getByRole('button', { name: 'Наступна' }).click();
  await expect(page.getByLabel('Сторінки журналу')).toContainText('Сторінка 2');
  await expect(table.locator('tbody tr')).toHaveCount(2);
  await expect(page.locator('#audit-panel').getByRole('button', { name: /Видалити|Редагувати|Виправити/ })).toHaveCount(0);
  await page.goto('/admin'); await page.getByRole('link', { name: 'Контроль та звітність' }).click();
  await expect(audit).toHaveAttribute('aria-selected', 'true');
  await finance.click(); await expect(page.getByLabel('Рік', { exact: true })).toHaveValue('2020');
  await expect(page.getByLabel('Місяць', { exact: true }).locator('option:checked')).toHaveText('Вересень');
  await audit.click(); await page.setViewportSize({ width: 375, height: 812 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  const user = await register(browser, 'Тестовий клієнт', '0000000076');
  try {
    const responses = await user.page.evaluate(async () => Promise.all(['/api/admin/audit-log', '/api/admin/reports/finance?year=2020&month=9'].map(async url => (await fetch(url)).status)));
    expect(responses).toEqual([403, 403]);
    await user.page.goto('/admin/reports'); await expect(user.page).toHaveURL(/\/account\/appointments$/);
  } finally { await user.context.close(); }
});

test('all twenty agreed routes resolve without duplicate or phantom admin links', async ({ page, browser }) => {
  test.setTimeout(120000);
  test.skip(process.env['E2E_LIVE_API'] !== '1', 'Requires isolated API and PostgreSQL');
  for (const path of ['/', '/login', '/register', '/forgot-password']) {
    await page.goto(path, { waitUntil: 'domcontentloaded' }); await expect(page.locator('h1')).toHaveCount(1);
    await expect(page).toHaveURL(new RegExp(path === '/' ? '/$' : `${path}$`));
  }
  await login(page, process.env['E2E_ADMIN_PHONE']!, process.env['E2E_ADMIN_PASSWORD']!);
  await expect(page).toHaveURL(/\/admin$/);
  const id = '00000000-0000-4000-8000-000000000001';
  const paths = ['/account/book', '/account/appointments', `/account/appointments/${id}`, '/account/notifications', '/account/profile',
    '/admin', '/admin/appointments', `/admin/appointments/${id}`, '/admin/assignments', '/admin/inspection-calendar', '/admin/employees',
    '/admin/prices', '/admin/users', '/admin/notifications', '/admin/profile', '/admin/reports'];
  for (const path of paths) {
    await page.goto(path, { waitUntil: 'domcontentloaded' }); await expect(page).toHaveURL(new RegExp(`${path}$`));
    await expect(page.locator('h1')).toHaveCount(1);
    await expect(page.getByText('Розділ у підготовці', { exact: true })).toHaveCount(0);
  }
  await expect(page.locator('a[href="/admin/audit-log"], a[href="/admin/finance"]')).toHaveCount(0);
  const admin = await register(browser, 'Працівник тест', '0000000077');
  try {
    const promoted = await page.evaluate(async () => {
      const user = (await (await fetch('/api/admin/users?search=0000000077')).json())[0];
      await fetch('/api/auth/antiforgery');
      const token = decodeURIComponent(document.cookie.split('; ').find(c => c.startsWith('XSRF-TOKEN='))!.split('=')[1]);
      return (await fetch(`/api/admin/users/${user.id}/roles`, { method: 'PUT', headers: { 'Content-Type': 'application/json', RequestVerificationToken: token }, body: JSON.stringify({ version: user.version, isAdmin: true }) })).status;
    });
    expect(promoted).toBe(204);
    await login(admin.page, '0000000077', 'Example-password-123!'); await expect(admin.page).toHaveURL(/\/admin$/);
    await expect(admin.page.getByRole('link', { name: 'Контроль та звітність' })).toHaveCount(0);
    expect(await admin.page.evaluate(async () => (await fetch('/api/admin/audit-log')).status)).toBe(403);
    await admin.page.goto('/admin/reports'); await expect(admin.page).toHaveURL(/\/admin$/);
    for (const path of ['/account/book', '/account/appointments', '/account/notifications', '/account/profile']) {
      await admin.page.goto(path); await expect(admin.page).toHaveURL(new RegExp(`${path}$`));
    }
  } finally { await admin.context.close(); }
});
