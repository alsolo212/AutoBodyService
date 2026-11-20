import { expect, Page, test } from '@playwright/test';

async function setup(page: Page) {
  let contacts = { phone: '+380000000001', address: 'Тестова адреса', version: 1 };
  await page.route('**/api/**', async route => {
    const url = new URL(route.request().url());
    if (url.pathname === '/api/auth/session') return route.fulfill({ json: { name: 'Тест', phone: '+380000000000', roles: ['Admin'], mustChangePassword: false } });
    if (url.pathname === '/api/auth/antiforgery') return route.fulfill({ status: 204 });
    if (url.pathname === '/api/admin/workshop-contacts') {
      if (route.request().method() === 'PUT') contacts = { ...route.request().postDataJSON(), version: contacts.version + 1 };
      return route.fulfill({ json: contacts });
    }
    if (url.pathname === '/api/public/contacts') return route.fulfill({ json: { ...contacts, workingHours: null } });
    if (url.pathname === '/api/admin/appointments') return route.fulfill({ json: { page: 1, total: 0, items: [] } });
    if (url.pathname === '/api/health') return route.fulfill({ json: { status: 'Healthy' } });
    return route.fulfill({ json: [] });
  });
}

test('desktop menu follows page order and select supports keyboard selection', async ({ page }) => {
  await setup(page); await page.goto('/');
  const links = page.getByRole('navigation', { name: 'Основна навігація' }).getByRole('link');
  await expect(links).toHaveText(['Послуги', 'Як це працює', 'Ціни', 'Контакти']);
  await page.goto('/admin/appointments');
  const select = page.locator('#filter-status');
  await select.focus(); await expect(select).toBeFocused();
  await page.keyboard.press('Space'); await page.keyboard.press('ArrowDown'); await page.keyboard.press('Enter');
  await expect(select).toHaveValue('AwaitingInspection');
  expect(await select.evaluate(el => getComputedStyle(el).paddingRight)).not.toBe('0px');
});

test.describe('phone layout', () => {
  test.use({ viewport: { width: 320, height: 740 }, isMobile: true, hasTouch: true });
  test('mobile menu order and saved contacts appear on the home and booking success screens', async ({ page }) => {
    await setup(page); await page.goto('/');
    await page.getByRole('button', { name: 'Відкрити меню' }).click();
    const menu = page.getByRole('navigation', { name: 'Мобільна навігація' });
    await expect(menu.getByRole('link').filter({ hasText: /^(Послуги|Як це працює|Ціни|Контакти)$/ })).toHaveText(['Послуги', 'Як це працює', 'Ціни', 'Контакти']);
    await page.goto('/admin/prices');
    await expect(page.getByRole('heading', { name: 'Налаштування майстерні' })).toBeVisible();
    await page.getByLabel('Телефон майстерні').fill('+380000000002');
    await page.getByLabel('Адреса майстерні').fill('Нова тестова адреса');
    await page.getByRole('button', { name: 'Зберегти контакти' }).click();
    await expect(page.getByRole('status').filter({ hasText: 'Контакти збережено' })).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await page.goto('/'); await expect(page.locator('#contacts')).toContainText('+380000000002'); await expect(page.locator('#contacts')).toContainText('Нова тестова адреса');
    await page.goto('/account/book/success'); await expect(page.locator('main')).toContainText('+380000000002'); await expect(page.locator('main')).toContainText('Нова тестова адреса');
    await page.goto('/admin/appointments');
    const select = page.locator('#filter-status'); await expect(select).toBeVisible();
    await select.selectOption('Scheduled'); await expect(select).toHaveValue('Scheduled');
    expect(await select.evaluate(el => el.getBoundingClientRect().height)).toBeGreaterThanOrEqual(44);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  });
});
