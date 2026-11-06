import { expect, test } from '@playwright/test';

test('loads the Ukrainian public shell and opens the account shell', async ({ page }) => {
  await page.goto('/');
  await expect(page).toHaveTitle('AutoBodyService');
  await expect(page.getByRole('heading', { name: /Турбота про авто/ })).toBeVisible();
  await page.getByRole('link', { name: /Особистий кабінет/ }).click();
  await expect(page.getByRole('heading', { name: 'Мої записи' })).toBeVisible();
  await expect(page.getByText('Розділ у підготовці')).toBeVisible();
});
