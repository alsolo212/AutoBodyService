import { expect, Browser, BrowserContext, Page } from '@playwright/test';
import { HubConnectionBuilder, HttpTransportType, LogLevel } from '@microsoft/signalr';

export async function login(page: Page, phone: string, password: string) {
  await page.goto('/login');
  await page.getByLabel('Номер телефону').fill(phone);
  await page.getByLabel('Пароль', { exact: true }).fill(password);
  await page.getByRole('button', { name: 'Увійти' }).click();
}
export async function register(browser: Browser, name: string, phone: string) {
  const context = await browser.newContext({ ignoreHTTPSErrors: true });
  const page = await context.newPage(); await page.goto('/register');
  await page.getByLabel('Ім’я').fill(name);
  await page.getByLabel('Номер телефону').fill(phone);
  await page.getByLabel('Пароль', { exact: true }).fill('Example-password-123!');
  await page.getByLabel('Повторіть пароль').fill('Example-password-123!');
  await page.getByRole('button', { name: 'Зареєструватися' }).click();
  await expect(page).toHaveURL(/\/account\/appointments$/);
  return { context, page };
}
export async function cookie(context: BrowserContext, baseURL: string) {
  return (await context.cookies(baseURL)).map(c => `${c.name}=${c.value}`).join('; ');
}
export async function hub(context: BrowserContext, baseURL: string) {
  const connection = new HubConnectionBuilder().withUrl(`${baseURL}/hubs/notifications`, {
    transport: HttpTransportType.WebSockets, skipNegotiation: true, headers: { Cookie: await cookie(context, baseURL), Origin: baseURL },
  }).configureLogging(LogLevel.None).build();
  await connection.start(); return connection;
}
export async function confirm(page: Page) {
  const dialog = page.locator('app-confirm-dialog dialog[open]');
  await expect(dialog).toBeVisible();
  await dialog.getByRole('button', { name: 'Підтвердити' }).click();
}
export async function find(page: Page, term: string) {
  await page.getByLabel('Ім’я або номер телефону').fill(term);
  await page.getByRole('button', { name: 'Знайти' }).click();
}
export async function changeTemporaryPassword(page: Page, temporary: string) {
  await expect(page).toHaveURL(/\/account\/change-temporary-password$/);
  await page.getByLabel('Тимчасовий пароль', { exact: true }).fill(temporary);
  await page.getByLabel('Новий пароль', { exact: true }).fill('Another-password-123!');
  await page.getByLabel('Повторіть новий пароль').fill('Another-password-123!');
  await page.getByRole('button', { name: 'Змінити пароль' }).click();
}
