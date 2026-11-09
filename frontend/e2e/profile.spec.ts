import { expect, test } from '@playwright/test';

const oldPassword = 'Example-password-123!';
const newPassword = 'Another-password-123!';
const account = (roles: string[], mustChangePassword = false) => ({ name: 'Тест', phone: '+380000000000', roles, mustChangePassword });

for (const role of ['User', 'Admin', 'SuperAdmin']) {
  test(`${role} views and updates the shared profile`, async ({ page }) => {
    let updated: unknown;
    await page.route('**/api/auth/session', route => route.fulfill({ json: account([role]) }));
    await page.route('**/api/auth/antiforgery', route => route.fulfill({ status: 204 }));
    await page.route('**/api/profile', route => {
      if (route.request().method() === 'PATCH') { updated = route.request().postDataJSON(); return route.fulfill({ status: 204 }); }
      return route.fulfill({ json: { name: 'Тест', phone: '+380000000000' } });
    });
    await page.goto(role === 'User' ? '/account/profile' : '/admin/profile');
    await expect(page.getByRole('heading', { name: 'Мій профіль' })).toBeVisible();
    await expect(page.getByText('+380000000000')).toBeVisible();
    await expect(page.getByText('Номер телефону змінюється через персонал СТО.')).toBeVisible();
    await page.getByLabel('Ім’я').fill('  Нове ім’я  ');
    await page.getByRole('button', { name: 'Зберегти ім’я' }).click();
    await expect(page.getByRole('status')).toContainText('Ім’я збережено.');
    expect(updated).toEqual({ name: 'Нове ім’я' });
    if (role !== 'User') {
      await page.route('**/api/health', route => route.fulfill({ json: { status: 'Healthy' } }));
      await page.getByRole('link', { name: 'Особистий кабінет' }).click();
      await expect(page).toHaveURL(/\/account\/appointments$/);
    }
  });
}

test('profile name and password forms validate and display API errors', async ({ page }) => {
  await page.route('**/api/auth/session', route => route.fulfill({ json: account(['User']) }));
  await page.route('**/api/auth/antiforgery', route => route.fulfill({ status: 204 }));
  await page.route('**/api/profile', route => route.fulfill({ json: { name: 'Тест', phone: '+380000000000' } }));
  await page.route('**/api/auth/change-password', route => route.fulfill({ status: 400 }));
  await page.goto('/account/profile');
  await page.getByLabel('Ім’я').fill('   ');
  await page.getByRole('button', { name: 'Зберегти ім’я' }).click();
  await expect(page.getByText('Вкажіть ім’я (не більше 200 символів).')).toBeVisible();
  await page.getByLabel('Поточний пароль').fill(oldPassword);
  await page.getByLabel('Новий пароль', { exact: true }).fill(newPassword);
  await page.getByLabel('Повторіть новий пароль').fill('not matching');
  await page.getByRole('button', { name: 'Змінити пароль' }).click();
  await expect(page.getByText('Паролі мають збігатися.')).toBeVisible();
  await page.getByLabel('Повторіть новий пароль').fill(newPassword);
  await page.getByRole('button', { name: 'Змінити пароль' }).click();
  await expect(page.getByRole('alert')).toContainText('Перевірте поточний пароль');
});

test('temporary password blocks ordinary profiles until a successful change', async ({ page }) => {
  let required = true;
  await page.route('**/api/auth/session', route => route.fulfill({ json: account(['Admin'], required) }));
  await page.route('**/api/auth/antiforgery', route => route.fulfill({ status: 204 }));
  await page.route('**/api/auth/change-password', async route => { required = false; await route.fulfill({ status: 204 }); });
  await page.route('**/api/health', route => route.fulfill({ json: { status: 'Healthy' } }));
  await page.goto('/admin/profile');
  await expect(page).toHaveURL(/\/account\/change-temporary-password$/);
  await page.getByLabel('Тимчасовий пароль').fill(oldPassword);
  await page.getByLabel('Новий пароль', { exact: true }).fill(newPassword);
  await page.getByLabel('Повторіть новий пароль').fill(newPassword);
  await page.getByRole('button', { name: 'Змінити пароль' }).click();
  await expect(page).toHaveURL(/\/admin$/);
});

test('profile loading error supports retry and narrow screens', async ({ page }) => {
  let attempts = 0;
  await page.setViewportSize({ width: 320, height: 700 });
  await page.route('**/api/auth/session', route => route.fulfill({ json: account(['User']) }));
  await page.route('**/api/profile', route => ++attempts === 1 ? route.abort() : route.fulfill({ json: { name: 'Тест', phone: '+380000000000' } }));
  await page.goto('/account/profile');
  await expect(page.getByRole('alert')).toContainText('Не вдалося завантажити профіль');
  await page.getByRole('button', { name: 'Спробувати ще раз' }).click();
  await expect(page.getByLabel('Ім’я')).toHaveValue('Тест');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
});

test('after changing a temporary password, session refresh can be retried safely', async ({ page }) => {
  let changed = false;
  let passwordRequests = 0;
  let refreshAttempts = 0;
  await page.route('**/api/auth/session', route => {
    if (changed && ++refreshAttempts === 1) return route.abort();
    return route.fulfill({ json: account(['User'], !changed) });
  });
  await page.route('**/api/auth/antiforgery', route => route.fulfill({ status: 204 }));
  await page.route('**/api/auth/change-password', route => { passwordRequests++; changed = true; return route.fulfill({ status: 204 }); });
  await page.route('**/api/health', route => route.fulfill({ json: { status: 'Healthy' } }));
  await page.goto('/account/change-temporary-password');
  await page.getByLabel('Тимчасовий пароль').fill(oldPassword);
  await page.getByLabel('Новий пароль', { exact: true }).fill(newPassword);
  await page.getByLabel('Повторіть новий пароль').fill(newPassword);
  await page.getByRole('button', { name: 'Змінити пароль' }).click();
  await expect(page.getByRole('alert')).toContainText('Пароль змінено, але не вдалося перевірити сесію');
  await expect(page.getByRole('button', { name: 'Змінити пароль' })).toHaveCount(0);
  await page.getByRole('button', { name: 'Перевірити сесію ще раз' }).click();
  await expect(page).toHaveURL(/\/account\/appointments$/);
  expect(passwordRequests).toBe(1);
});
