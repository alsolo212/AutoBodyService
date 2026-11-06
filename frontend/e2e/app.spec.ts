import { expect, test } from '@playwright/test';

test('loads the application and displays the API health status', async ({ page }) => {
  await page.route('https://localhost:7097/api/health', (route) =>
    route.fulfill({ json: { status: 'Healthy' } }),
  );

  await page.goto('/');

  await expect(page).toHaveTitle('AutoBodyService');
  await expect(page.getByRole('heading', { name: 'AutoBodyService', level: 1 })).toBeVisible();
  await expect(page.getByTestId('api-status')).toHaveText('API: Healthy');
});
