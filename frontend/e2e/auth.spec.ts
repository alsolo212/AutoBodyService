import { expect, test } from '@playwright/test';

const phone = '0000000000';
const password = 'Example-password-123!';
const session = (roles: string[], mustChangePassword = false) => ({ name: 'Test', phone: '+380000000000', roles, mustChangePassword });

test.beforeEach(async ({ page }) => {
  await page.route('**/api/auth/session', route => route.fulfill({ status: 401 }));
});

test('validates registration and shows a non-enumerating response', async ({ page }) => {
  let body: unknown;
  await page.route('**/api/auth/antiforgery', route => route.fulfill({ status: 204 }));
  await page.route('**/api/auth/register', async route => { body = route.request().postDataJSON(); await route.fulfill({ status: 202 }); });
  await page.route('**/api/auth/login', route => route.fulfill({ status: 401 }));
  await page.goto('/register');
  await page.getByRole('button', { name: 'Зареєструватися' }).click();
  await expect(page.getByText('Вкажіть ім’я.')).toBeVisible();
  await page.getByLabel('Ім’я').fill(' Test ');
  await page.getByLabel('Номер телефону').fill(phone);
  await page.getByLabel('Пароль', { exact: true }).fill(password);
  await page.getByLabel('Повторіть пароль').fill('other');
  await page.getByRole('button', { name: 'Зареєструватися' }).click();
  await expect(page.getByText('Паролі мають збігатися.')).toBeVisible();
  await page.getByLabel('Повторіть пароль').fill(password);
  await page.getByRole('button', { name: 'Зареєструватися' }).click();
  await expect(page.getByRole('status')).toContainText('Запит прийнято');
  expect(body).toEqual({ name: 'Test', phone, password });
});

test('password visibility is independently controlled on registration and login', async ({ page }) => {
  await page.goto('/register');
  const passwordInput = page.getByLabel('Пароль', { exact: true });
  const confirmInput = page.getByLabel('Повторіть пароль');
  await passwordInput.fill(password); await confirmInput.fill(password);
  await expect(passwordInput).toHaveAttribute('type', 'password');
  await page.getByRole('button', { name: 'Показати пароль', exact: true }).click();
  await expect(passwordInput).toHaveAttribute('type', 'text');
  await expect(confirmInput).toHaveAttribute('type', 'password');
  await page.getByRole('button', { name: 'Показати повторений пароль' }).click();
  await expect(confirmInput).toHaveAttribute('type', 'text');
  await page.getByRole('button', { name: 'Приховати пароль', exact: true }).click();
  await expect(passwordInput).toHaveAttribute('type', 'password');
  await page.goto('/login');
  const loginPassword = page.getByLabel('Пароль', { exact: true });
  await loginPassword.fill(password);
  await page.getByRole('button', { name: 'Показати пароль' }).click();
  await expect(loginPassword).toHaveAttribute('type', 'text');
  await expect(loginPassword).toHaveValue(password);
});

for (const [role, url] of [['User', '/account/appointments'], ['Admin', '/admin'], ['SuperAdmin', '/admin']] as const) {
  test(`routes ${role} after login`, async ({ page }) => {
    await page.route('**/api/auth/antiforgery', route => route.fulfill({ status: 204 }));
    let signedIn = false;
    await page.route('**/api/auth/login', route => { signedIn = true; return route.fulfill({ status: 204 }); });
    await page.route('**/api/auth/session', route => signedIn ? route.fulfill({ json: session([role]) }) : route.fulfill({ status: 401 }));
    await page.route('**/api/health', route => route.fulfill({ json: { status: 'Healthy' } }));
    await page.goto('/login');
    await page.getByLabel('Номер телефону').fill(phone);
    await page.getByLabel('Пароль', { exact: true }).fill(password);
    await page.getByRole('button', { name: 'Увійти' }).click();
    await expect(page).toHaveURL(new RegExp(url + '$'));
  });
}

test('forces temporary password screen and keeps it protected', async ({ page }) => {
  await page.route('**/api/auth/session', route => route.fulfill({ json: session(['Admin'], true) }));
  await page.goto('/admin');
  await expect(page).toHaveURL(/\/account\/change-temporary-password$/);
  await expect(page.getByRole('heading', { name: 'Потрібно змінити пароль.' })).toBeVisible();
  await expect(page.getByLabel('Тимчасовий пароль')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Змінити пароль' })).toBeVisible();
});

test('blocks a user from administration and explains forgotten password', async ({ page }) => {
  await page.route('**/api/auth/session', route => route.fulfill({ json: session(['User']) }));
  await page.route('**/api/health', route => route.fulfill({ json: { status: 'Healthy' } }));
  await page.goto('/admin');
  await expect(page).toHaveURL(/\/account\/appointments$/);
  await page.goto('/forgot-password');
  await expect(page.getByText(/Зверніться до персоналу СТО/)).toBeVisible();
});

test('shows generic login errors and server connectivity errors', async ({ page }) => {
  await page.route('**/api/auth/antiforgery', route => route.fulfill({ status: 204 }));
  await page.route('**/api/auth/login', route => route.fulfill({ status: 401 }));
  await page.goto('/login');
  await page.getByLabel('Номер телефону').fill(phone);
  await page.getByLabel('Пароль', { exact: true }).fill(password);
  await page.getByRole('button', { name: 'Увійти' }).click();
  await expect(page.getByRole('alert')).toContainText('Невірний номер телефону або пароль');
  await page.unroute('**/api/auth/session');
  await page.route('**/api/auth/session', route => route.abort());
  await page.goto('/admin');
  await expect(page.getByRole('alert')).toContainText('Не вдалося перевірити сесію');
});

test('auth pages fit a narrow screen', async ({ page }) => {
  await page.setViewportSize({ width: 320, height: 720 });
  await page.goto('/register');
  await expect(page.getByRole('heading', { name: 'Створіть обліковий запис.' })).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
});

test('guest and expired session can open both guest pages', async ({ page }) => {
  for (const [path, heading] of [['/login', 'Вітаємо знову.'], ['/register', 'Створіть обліковий запис.']] as const) {
    await page.goto(path);
    await expect(page).toHaveURL(new RegExp(path + '$'));
    await expect(page.getByRole('heading', { name: heading })).toBeVisible();
    await page.reload();
    await expect(page.getByRole('heading', { name: heading })).toBeVisible();
  }
});

for (const [role, mustChange, destination] of [
  ['User', false, '/account/appointments'], ['Admin', false, '/admin'],
  ['SuperAdmin', false, '/admin'], ['User', true, '/account/change-temporary-password'],
  ['SuperAdmin', true, '/account/change-temporary-password'],
] as const) {
  test(`${role} session, mandatory change ${mustChange}, redirects guest routes after reload`, async ({ page }) => {
    await page.route('**/api/auth/session', route => route.fulfill({ json: session([role], mustChange) }));
    await page.route('**/api/health', route => route.fulfill({ json: { status: 'Healthy' } }));
    for (const path of ['/login', '/register']) {
      await page.goto(path);
      await expect(page).toHaveURL(new RegExp(destination + '$'));
      await page.reload();
      await expect(page).toHaveURL(new RegExp(destination + '$'));
      await expect(page.getByRole('heading', { name: 'Вітаємо знову.' })).toHaveCount(0);
      await expect(page.getByRole('heading', { name: 'Створіть обліковий запис.' })).toHaveCount(0);
    }
  });
}

test('does not render a guest form while session restoration is pending', async ({ page }) => {
  let release!: () => void;
  const pending = new Promise<void>(resolve => release = resolve);
  let requested!: () => void;
  const received = new Promise<void>(resolve => requested = resolve);
  await page.route('**/api/auth/session', async route => { requested(); await pending; await route.fulfill({ json: session(['User']) }); });
  await page.route('**/api/health', route => route.fulfill({ json: { status: 'Healthy' } }));
  await page.goto('/login');
  await received;
  await expect(page.getByRole('heading', { name: 'Вітаємо знову.' })).toHaveCount(0);
  release();
  await expect(page).toHaveURL(/\/account\/appointments$/);
});
