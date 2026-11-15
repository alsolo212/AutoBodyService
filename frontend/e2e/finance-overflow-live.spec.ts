import { expect, test } from '@playwright/test';
import { login } from './admin-users-helpers';

test('unrepresentable totals leave real expenses available for correction and recover the report', async ({ page }) => {
  test.setTimeout(120000);
  await login(page, process.env['E2E_ADMIN_PHONE']!, process.env['E2E_ADMIN_PASSWORD']!); await expect(page).toHaveURL(/\/admin$/);
  const statuses = await page.evaluate(async () => {
    await fetch('/api/auth/antiforgery');
    const token = decodeURIComponent(document.cookie.split('; ').find(c => c.startsWith('XSRF-TOKEN='))!.split('=')[1]);
    const headers = { 'Content-Type': 'application/json', RequestVerificationToken: token };
    const create = (description: string) => fetch('/api/admin/reports/expenses', { method: 'POST', headers,
      body: `{"amount":79228162514264337593543950335,"description":${JSON.stringify(description)}}` });
    return [(await create('Перша велика витрата')).status, (await create('Друга велика витрата')).status];
  });
  expect(statuses).toEqual([201, 201]);
  await page.goto('/admin/reports');
  await expect(page.getByRole('alert').filter({ hasText: 'підсумки перевищують підтримуваний діапазон' })).toBeVisible();
  const table = page.getByRole('region', { name: 'Таблиця витрат' });
  await expect(table).toContainText('Перша велика витрата'); await expect(table).toContainText('Друга велика витрата');
  await expect(page.getByLabel('Показники місяця')).toHaveCount(0);
  for (const [description, amount] of [['Перша велика витрата', '7'], ['Друга велика витрата', '8']] as const) {
    await table.getByRole('row').filter({ hasText: description }).getByRole('button', { name: /Виправити витрату/ }).click();
    await page.getByLabel('Сума, ₴', { exact: true }).fill(amount);
    const response = page.waitForResponse(r => r.url().includes('/api/admin/reports/expenses/') && r.request().method() === 'PATCH');
    await page.getByRole('button', { name: 'Зберегти виправлення' }).click(); expect((await response).status()).toBe(200);
    await expect(page.getByText('Завантажуємо витрати…')).toHaveCount(0);
  }
  await expect(page.getByLabel('Показники місяця')).toContainText('15,00 ₴');
  await expect(page.getByRole('alert').filter({ hasText: 'підсумки перевищують' })).toHaveCount(0);
});
