import { expect, test } from '@playwright/test';

test('loads the public shell and directs guests to sign in', async ({ page }) => {
  await page.route('**/api/auth/session', route => route.fulfill({ status: 401 }));
  await page.goto('/');
  await expect(page).toHaveTitle('AutoBodyService');
  await expect(page.getByRole('heading', { name: /Турбота про авто/ })).toBeVisible();
  await page.getByRole('link', { name: /Особистий кабінет/ }).click();
  await expect(page).toHaveURL(/\/login$/);
  await expect(page.getByRole('heading', { name: 'Вітаємо знову.' })).toBeVisible();
});
