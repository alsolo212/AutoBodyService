import { execFileSync } from 'node:child_process';
import { expect, test } from '@playwright/test';

test('profile and mandatory password changes work with the real API', async ({ page }) => {
  test.skip(process.env['E2E_LIVE_PROFILE'] !== '1', 'Requires an isolated API and database.');
  const container = process.env['PROFILE_VERIFY_CONTAINER'];
  if (!container?.startsWith('autobody-ui-verify-')) throw new Error('An isolated test container is required.');
  const phone = '0000' + String(Date.now() % 1_000_000).padStart(6, '0');
  const oldPassword = 'Example-password-123!';
  const nextPassword = 'Another-password-123!';
  const finalPassword = 'Another-password-456!';
  await page.goto('/register');
  await page.getByLabel('Ім’я').fill('Тест');
  await page.getByLabel('Номер телефону').fill(phone);
  await page.getByLabel('Пароль', { exact: true }).fill(oldPassword);
  await page.getByLabel('Повторіть пароль').fill(oldPassword);
  await page.getByRole('button', { name: 'Зареєструватися' }).click();
  await expect(page).toHaveURL(/\/account\/appointments$/);
  await page.goto('/account/profile');
  await expect(page.getByLabel('Ім’я')).toHaveValue('Тест');
  await page.getByLabel('Ім’я').fill('Оновлено');
  await page.getByRole('button', { name: 'Зберегти ім’я' }).click();
  await expect(page.getByRole('status')).toContainText('Ім’я збережено');
  await page.getByLabel('Поточний пароль').fill(oldPassword);
  await page.getByLabel('Новий пароль', { exact: true }).fill(nextPassword);
  await page.getByLabel('Повторіть новий пароль').fill(nextPassword);
  await page.getByRole('button', { name: 'Змінити пароль' }).click();
  await expect(page.getByRole('status').filter({ hasText: 'Пароль змінено' })).toBeVisible();
  // Simulate the already-established administrative reset flag in this disposable database only.
  const normalized = '+38' + phone;
  execFileSync('docker', ['exec', container, 'psql', '-U', 'auth_test', '-d', 'auth_test', '-v', 'ON_ERROR_STOP=1', '-c', `UPDATE "AspNetUsers" SET "MustChangePassword" = TRUE WHERE "PhoneNormalized" = '${normalized}'`], { stdio: 'ignore' });
  await page.goto('/account/profile');
  await expect(page).toHaveURL(/\/account\/change-temporary-password$/);
  for (const path of ['/login', '/register']) {
    await page.goto(path);
    await expect(page).toHaveURL(/\/account\/change-temporary-password$/);
  }
  await page.getByLabel('Тимчасовий пароль').fill(nextPassword);
  await page.getByLabel('Новий пароль', { exact: true }).fill(finalPassword);
  await page.getByLabel('Повторіть новий пароль').fill(finalPassword);
  await page.getByRole('button', { name: 'Змінити пароль' }).click();
  await expect(page).toHaveURL(/\/account\/appointments$/);
  await page.goto('/account/profile');
  await expect(page.getByLabel('Ім’я')).toHaveValue('Оновлено');
  await page.getByRole('button', { name: 'Вийти', exact: true }).click();
  await expect(page).toHaveURL(/\/login$/);
  await expect(page.getByLabel('Номер телефону')).toHaveClass(/ng-untouched/);
  await page.getByLabel('Номер телефону').fill(phone);
  await page.getByLabel('Пароль', { exact: true }).fill(finalPassword);
  await page.getByRole('button', { name: 'Увійти' }).click();
  await expect(page).toHaveURL(/\/account\/appointments$/);
});
