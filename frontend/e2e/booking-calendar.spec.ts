import { expect, test } from '@playwright/test';

test('calendar finds the nearest date and supports keyboard selection across months on mobile', async ({ page }) => {
  await page.clock.setFixedTime(new Date('2026-11-17T10:00:00Z'));
  await page.setViewportSize({ width: 320, height: 720 });
  await page.route('**/api/**', route => {
    const url = new URL(route.request().url());
    if (url.pathname === '/api/auth/session') return route.fulfill({ json: { name: 'Тест', phone: '+380000000000', roles: ['User'], mustChangePassword: false } });
    if (url.pathname === '/api/inspection-slots/month') {
      const month = `${url.searchParams.get('year')}-${url.searchParams.get('month')}`;
      const count = new Date(Date.UTC(Number(url.searchParams.get('year')), Number(url.searchParams.get('month')), 0)).getUTCDate();
      return route.fulfill({ json: { days: Array.from({ length: count }, (_, i) => {
        const date = `${month}-${String(i + 1).padStart(2, '0')}`;
        return { date, available: ['2026-11-30', '2026-12-01'].includes(date) };
      }) } });
    }
    if (url.pathname === '/api/inspection-slots') {
      const date = url.searchParams.get('date');
      return route.fulfill({ json: { slots: [{ startUtc: `${date}T08:00:00Z`, endUtc: `${date}T08:30:00Z` }] } });
    }
    return route.fulfill({ json: [] });
  });
  await page.goto('/account/book');
  await expect(page.getByLabel('Дата', { exact: true })).toHaveValue('2026-11-30');
  await expect(page.getByRole('button', { name: /17 листопада 2026.*немає вільного часу/ })).toHaveAttribute('aria-disabled', 'true');
  await expect(page.getByRole('button', { name: /16 листопада 2026.*минула дата/ })).toBeVisible();
  const current = page.getByRole('button', { name: /30 листопада 2026.*є вільний час/ });
  await current.focus(); await page.keyboard.press('ArrowRight');
  const next = page.getByRole('button', { name: /1 грудня 2026.*є вільний час/ });
  await expect(next).toBeFocused(); await page.keyboard.press('Enter');
  await expect(page.getByLabel('Дата', { exact: true })).toHaveValue('2026-12-01');
  await expect(page.getByLabel('10:00', { exact: true })).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
});
