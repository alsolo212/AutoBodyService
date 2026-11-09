import { randomInt } from 'node:crypto';
import { expect, Page, test } from '@playwright/test';

const password = 'Example-password-123!';
const phoneNumber = () => '000' + String(randomInt(10_000_000)).padStart(7, '0');

async function register(page: Page, phone: string) {
  await expect(page.getByLabel('Ім’я')).toHaveClass(/ng-untouched/);
  await page.getByLabel('Ім’я').fill('Тестовий користувач');
  await page.getByLabel('Номер телефону').fill(phone);
  await page.getByLabel('Пароль', { exact: true }).fill(password);
  await page.getByLabel('Повторіть пароль').fill(password);
  const response = page.waitForResponse(r => r.url().endsWith('/api/auth/register') && r.request().method() === 'POST');
  await page.getByRole('button', { name: 'Зареєструватися' }).click();
  expect((await response).status()).toBe(202);
  await expect(page.getByRole('status')).toContainText('Запит прийнято');
}

async function openLoginFromRegistration(page: Page) {
  await page.getByRole('link', { name: /Перейти до входу/ }).click();
  await expect(page).toHaveURL(/\/login$/);
  await expect(page.getByLabel('Номер телефону')).toHaveClass(/ng-untouched/);
}

test.describe('real authentication', () => {
  test.skip(process.env['E2E_REAL_AUTH'] !== '1', 'Run scripts/test-auth-e2e.sh with its disposable database.');

  test('home to registration, persistent session, profile update and logout', async ({ page, context }) => {
    const phone = phoneNumber();
    await page.goto('/');
    await expect(page.getByRole('heading', { name: /Турбота про авто/ })).toBeVisible();
    await page.getByRole('link', { name: /Особистий кабінет/ }).click();
    await expect(page).toHaveURL(/\/login$/);
    await page.getByRole('link', { name: 'Зареєструватися' }).click();
    await expect(page).toHaveURL(/\/register$/);
    await register(page, phone);
    await openLoginFromRegistration(page);
    await page.getByLabel('Номер телефону').fill(phone);
    await page.getByLabel('Пароль', { exact: true }).fill(password);
    const login = page.waitForResponse(r => r.url().endsWith('/api/auth/login') && r.request().method() === 'POST');
    await page.getByRole('button', { name: 'Увійти' }).click();
    expect((await login).status()).toBe(204);
    await expect(page).toHaveURL(/\/account\/appointments$/);
    const authCookie = (await context.cookies()).find(cookie => cookie.name === '.AspNetCore.Identity.Application');
    expect(authCookie).toBeDefined();
    expect(authCookie?.httpOnly).toBe(true);
    expect(authCookie?.secure).toBe(true);
    expect(authCookie?.sameSite).toBe('Lax');
    expect(authCookie!.expires).toBeGreaterThan(Date.now() / 1000 + 6 * 24 * 60 * 60);
    await page.getByRole('link', { name: 'Мій профіль' }).click();
    await expect(page).toHaveURL(/\/account\/profile$/);
    await expect(page.getByLabel('Ім’я')).toHaveValue('Тестовий користувач');
    await expect(page.getByText('+38' + phone)).toBeVisible();
    const session = page.waitForResponse(r => r.url().endsWith('/api/auth/session') && r.request().method() === 'GET');
    await page.reload();
    expect((await session).status()).toBe(200);
    await expect(page).toHaveURL(/\/account\/profile$/);
    await expect(page.getByLabel('Ім’я')).toHaveValue('Тестовий користувач');
    await page.getByLabel('Ім’я').fill('Оновлене ім’я');
    const update = page.waitForResponse(r => r.url().endsWith('/api/profile') && r.request().method() === 'PATCH');
    await page.getByRole('button', { name: 'Зберегти ім’я' }).click();
    expect((await update).status()).toBe(204);
    await expect(page.getByRole('status')).toContainText('Ім’я збережено');
    await page.reload();
    await expect(page.getByLabel('Ім’я')).toHaveValue('Оновлене ім’я');
    await expect(page.getByText('+38' + phone)).toBeVisible();
    const logout = page.waitForResponse(r => r.url().endsWith('/api/auth/logout') && r.request().method() === 'POST');
    await page.getByRole('button', { name: 'Вийти', exact: true }).click();
    expect((await logout).status()).toBe(204);
    await expect(page).toHaveURL(/\/login$/);
    expect((await context.cookies()).some(cookie => cookie.name === '.AspNetCore.Identity.Application')).toBe(false);
    expect(await page.evaluate(async () => (await fetch('/api/auth/session')).status)).toBe(401);
    await page.goto('/account/profile');
    await expect(page).toHaveURL(/\/login$/);
  });

  test('wrong password shows a generic error and a correct password still works', async ({ page, context }) => {
    const phone = phoneNumber();
    await page.goto('/register');
    await register(page, phone);
    await openLoginFromRegistration(page);
    await page.getByLabel('Номер телефону').fill(phone);
    await page.getByLabel('Пароль', { exact: true }).fill('Incorrect-password-123!');
    const denied = page.waitForResponse(r => r.url().endsWith('/api/auth/login') && r.request().method() === 'POST');
    await page.getByRole('button', { name: 'Увійти' }).click();
    expect((await denied).status()).toBe(401);
    await expect(page.getByRole('alert')).toContainText('Невірний номер телефону або пароль');
    await expect(page).toHaveURL(/\/login$/);
    expect((await context.cookies()).some(cookie => cookie.name === '.AspNetCore.Identity.Application')).toBe(false);
    await page.getByLabel('Пароль', { exact: true }).fill(password);
    await page.getByRole('button', { name: 'Увійти' }).click();
    await expect(page).toHaveURL(/\/account\/appointments$/);
    await page.goto('/admin');
    await expect(page).toHaveURL(/\/account\/appointments$/);
  });

  test('super administrator enters the admin area and can open the personal cabinet', async ({ page }) => {
    const phone = process.env['E2E_ADMIN_PHONE'];
    const adminPassword = process.env['E2E_ADMIN_PASSWORD'];
    if (!phone || !adminPassword) throw new Error('The isolated administrator fixture is missing.');
    await page.goto('/login');
    await page.getByLabel('Номер телефону').fill(phone);
    await page.getByLabel('Пароль', { exact: true }).fill(adminPassword);
    await page.getByRole('button', { name: 'Увійти' }).click();
    await expect(page).toHaveURL(/\/admin$/);
    const current = await page.evaluate(async () => (await fetch('/api/auth/session')).json());
    expect(current.roles).toContain('SuperAdmin');
    await page.getByRole('link', { name: 'Мій профіль' }).click();
    await expect(page).toHaveURL(/\/admin\/profile$/);
    await page.getByRole('link', { name: 'Особистий кабінет' }).click();
    await expect(page).toHaveURL(/\/account\/appointments$/);
    const after = await page.evaluate(async () => (await fetch('/api/auth/session')).json());
    expect(after.roles).toContain('SuperAdmin');
    expect(after.phone).toBe(current.phone);
  });

  test('invalid forms do not submit and anonymous protected routes redirect', async ({ page }) => {
    let writes = 0;
    page.on('request', request => {
      if (request.method() === 'POST' && /\/api\/auth\/(register|login)$/.test(request.url())) writes++;
    });
    await page.goto('/account/profile');
    await expect(page).toHaveURL(/\/login$/);
    await page.getByRole('button', { name: 'Увійти' }).click();
    await expect(page.getByText('Введіть коректний номер України.')).toBeVisible();
    await expect(page.getByText('Введіть пароль.')).toBeVisible();
    await page.goto('/register');
    await page.getByRole('button', { name: 'Зареєструватися' }).click();
    await expect(page.getByText('Вкажіть ім’я.')).toBeVisible();
    await page.getByLabel('Ім’я').fill('Тест');
    await page.getByLabel('Номер телефону').fill('123');
    await page.getByLabel('Пароль', { exact: true }).fill('weak');
    await page.getByLabel('Повторіть пароль').fill('mismatch');
    await page.getByRole('button', { name: 'Зареєструватися' }).click();
    await expect(page.getByText('Введіть коректний номер України.')).toBeVisible();
    await expect(page.getByText('Паролі мають збігатися.')).toBeVisible();
    await expect(page.locator('#password-help')).toHaveClass(/field__error/);
    expect(writes).toBe(0);
    await page.goto('/admin');
    await expect(page).toHaveURL(/\/login$/);
    expect(await page.evaluate(async () => (await fetch('/api/profile')).status)).toBe(401);
  });
});
