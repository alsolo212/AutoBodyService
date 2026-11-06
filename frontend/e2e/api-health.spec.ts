import { expect, test } from '@playwright/test';

test('shows an error and retries when the API request fails', async ({ page }) => {
  let attempts = 0;
  await page.route('**/api/health', route => {
    attempts++;
    return attempts === 1 ? route.abort('failed') : route.fulfill({ json: { status: 'Healthy' } });
  });
  await page.goto('/account/appointments');
  await expect(page.getByRole('alert')).toContainText('Не вдалося підключитися до сервісу');
  await page.getByRole('button', { name: 'Спробувати ще раз' }).click();
  await expect(page.getByText('З’єднання із сервісом встановлено.')).toBeVisible();
  expect(attempts).toBe(2);
});

test('connects to the real API through the HTTPS development proxy', async ({ page }) => {
  test.skip(process.env['E2E_LIVE_API'] !== '1', 'Requires the local API on https://localhost:7097.');
  const response = page.waitForResponse(response => response.url().endsWith('/api/health'));
  await page.goto('/account/appointments');
  const health = await response;
  expect(health.url()).toBe('https://localhost:4173/api/health');
  expect(health.status()).toBe(200);
  expect(await health.json()).toEqual({ status: 'Healthy' });
  await expect(page.getByText('З’єднання із сервісом встановлено.')).toBeVisible();
});
