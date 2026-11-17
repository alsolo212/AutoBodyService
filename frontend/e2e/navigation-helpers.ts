import { Page } from '@playwright/test';

export async function signOut(page: Page) {
  const menu = page.getByRole('button', { name: 'Відкрити меню' });
  if (await menu.isVisible()) await menu.click();
  await page.getByRole('button', { name: 'Вийти', exact: true }).click();
  await page.getByRole('dialog', { name: 'Вийти з облікового запису?' }).getByRole('button', { name: 'Вийти', exact: true }).click();
}
