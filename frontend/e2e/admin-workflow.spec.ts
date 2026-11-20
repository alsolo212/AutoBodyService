import { expect, test } from '@playwright/test';

test.use({ timezoneId: 'America/Los_Angeles' });

test('mobile order board navigates statuses and confirms changes before sending them', async ({ page }) => {
  await page.setViewportSize({ width: 320, height: 720 });
  let status = 'Scheduled'; let transitions = 0;
  const card = () => ({ id: 'order-1', customerName: 'Тестовий клієнт', vin: '1HGCM82633A123456', make: 'Тест', model: 'Авто', inspectionStartUtc: '2026-01-01T10:00:00Z', inspectionStartLocal: '2026-01-01T12:00:00+02:00', status, repairPlan: null, version: 1 });
  await page.route('**/api/**', route => {
    const request = route.request(); const url = new URL(request.url());
    if (url.pathname === '/api/auth/session') return route.fulfill({ json: { name: 'Тест', phone: '+380000000000', roles: ['Admin'], mustChangePassword: false } });
    if (url.pathname === '/api/auth/antiforgery') return route.fulfill({ status: 204 });
    if (url.pathname === '/api/admin/appointments/order-1/transition' && request.method() === 'POST') {
      transitions++; status = request.postDataJSON().target; return route.fulfill({ status: 204 });
    }
    if (url.pathname === '/api/admin/appointments') {
      const items = url.searchParams.get('status') === status ? [card()] : [];
      return route.fulfill({ json: { items, total: items.length, page: 1, pageSize: 5 } });
    }
    return route.fulfill({ json: [] });
  });
  await page.goto('/admin/appointments');
  const navigation = page.getByRole('navigation', { name: 'Перейти до статусу' });
  await navigation.getByRole('button', { name: 'Заплановано' }).focus(); await page.keyboard.press('Enter');
  await expect(page.locator('#column-Scheduled')).toBeInViewport();
  await expect(page.locator('#column-Scheduled')).toBeFocused();
  const article = page.locator('[data-card-id="order-1"]');
  await expect(article).toBeVisible();
  await expect(article.locator('time')).toContainText('12:00');
  await article.getByRole('button', { name: 'Перевести: У роботі' }).focus(); await page.keyboard.press('Enter');
  const dialog = page.getByRole('dialog', { name: 'Змінити етап замовлення?' });
  await expect(dialog).toContainText('Заплановано'); await expect(dialog).toContainText('У роботі');
  await dialog.getByRole('button', { name: 'Скасувати' }).click();
  expect(transitions).toBe(0); await expect(article).toBeVisible();
  await article.getByRole('button', { name: 'Перевести: У роботі' }).click();
  await dialog.getByRole('button', { name: 'Змінити етап' }).click();
  await expect(page.locator('[data-status="InProgress"] [data-card-id="order-1"]')).toBeVisible();
  expect(transitions).toBe(1);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
});
