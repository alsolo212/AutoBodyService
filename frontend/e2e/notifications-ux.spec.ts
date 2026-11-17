import { expect, Page, test } from '@playwright/test';

const ids = Array.from({ length: 6 }, (_, i) => `00000000-0000-4000-8000-${String(i + 1).padStart(12, '0')}`);
const orderId = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa';

async function setup(page: Page, role: 'User' | 'Admin' = 'User') {
  const items = ids.map((id, i) => ({ id, type: 'Test', title: `Сповіщення ${i + 1}`, message: `Текст повідомлення ${i + 1}`, createdAtUtc: `2030-01-0${6 - i}T10:00:00Z`, readAtUtc: null as string | null, link: `/account/appointments/${orderId}` }));
  const reads: string[] = []; let failNext = false;
  let release: (() => void) | undefined; let gate: Promise<void> | undefined;
  await page.route('**/api/**', async route => {
    const request = route.request(); const url = new URL(request.url());
    if (url.pathname === '/api/auth/session') return route.fulfill({ json: { name: 'Тест', phone: '+380000000000', roles: [role], mustChangePassword: false } });
    if (url.pathname === '/api/auth/antiforgery') return route.fulfill({ status: 204 });
    if (url.pathname === '/api/notifications' && request.method() === 'GET') return route.fulfill({ json: items });
    if (url.pathname.startsWith('/api/appointments/my/')) return route.fulfill({ status: 404 });
    const match = url.pathname.match(/^\/api\/notifications\/([^/]+)\/read$/);
    if (match && request.method() === 'PATCH') {
      reads.push(match[1]);
      if (gate) { const pending = gate; gate = undefined; await pending; }
      if (failNext) { failNext = false; return route.fulfill({ status: 503 }); }
      const item = items.find(value => value.id === match[1]); if (item) item.readAtUtc = '2030-01-10T10:00:00Z';
      return route.fulfill({ status: 204 });
    }
    if (url.pathname === '/api/health') return route.fulfill({ json: { status: 'Healthy' } });
    return route.fulfill({ json: [] });
  });
  return { reads, items, fail: () => { failNext = true; }, hold: () => { gate = new Promise<void>(resolve => { release = resolve; }); }, release: () => release?.() };
}

test('bell keeps the all link at the top and opening one notification marks just that item', async ({ page }) => {
  const server = await setup(page);
  await page.setViewportSize({ width: 320, height: 720 }); await page.goto('/account/notifications');
  const bell = page.locator('app-notification-bell > details > summary');
  await expect(bell).toHaveAttribute('aria-label', /непрочитаних: 6/); await bell.click();
  const panel = page.getByRole('region', { name: 'Останні сповіщення' });
  const all = panel.getByRole('link', { name: 'Усі сповіщення' });
  await expect(all).toHaveAttribute('href', '/account/notifications');
  await panel.evaluate(element => { element.scrollTop = element.scrollHeight; });
  const insidePanel = await all.evaluate(element => { const link = element.getBoundingClientRect(); const panel = element.closest('.bell__panel')!.getBoundingClientRect(); return link.top >= panel.top && link.bottom <= panel.bottom; });
  expect(insidePanel).toBe(true);
  await panel.evaluate(element => { element.scrollTop = 0; });
  const item = panel.locator('.bell__item').first();
  await item.locator('summary').focus(); await page.keyboard.press('Enter');
  await expect(item.getByText('Текст повідомлення 1')).toBeVisible();
  await expect(bell).toHaveAttribute('aria-label', /непрочитаних: 5/);
  expect(server.reads).toEqual([ids[0]]); expect(server.items.filter(value => value.readAtUtc !== null)).toHaveLength(1);
  await all.click(); await expect(panel).toBeHidden(); expect(server.reads).toEqual([ids[0]]);
});

test('unread inbox keeps an opened item visible, supports retry, and preserves the read request during navigation', async ({ page }) => {
  const server = await setup(page); await page.goto('/account/notifications');
  const bell = page.locator('app-notification-bell > details > summary');
  await expect(bell).toHaveAttribute('aria-label', /непрочитаних: 6/);
  await page.getByRole('button', { name: /Непрочитані/ }).click();
  const first = page.locator('.inbox__item').filter({ hasText: 'Сповіщення 1' });
  server.fail(); await first.locator('summary').click();
  await expect(page.getByRole('alert')).toContainText('Не вдалося оновити');
  await expect(bell).toHaveAttribute('aria-label', /непрочитаних: 6/);
  await first.getByRole('button', { name: 'Позначити прочитаним' }).click();
  await expect(bell).toHaveAttribute('aria-label', /непрочитаних: 5/);
  await expect(first.getByText('Текст повідомлення 1')).toBeVisible();
  await first.locator('summary').click(); await expect(first).toHaveCount(0);
  const second = page.locator('.inbox__item').filter({ hasText: 'Сповіщення 2' });
  server.hold(); const patch = page.waitForRequest(request => request.method() === 'PATCH' && request.url().includes(ids[1]));
  await second.locator('summary').click(); await patch;
  await second.getByRole('link', { name: /Відкрити замовлення/ }).click();
  await expect(page).toHaveURL(new RegExp(`/account/appointments/${orderId}$`));
  server.release();
  await expect.poll(() => server.items[1].readAtUtc).not.toBeNull();
  expect(server.reads.filter(id => id === ids[1])).toHaveLength(1);
});

test('admin bell links to the administrative inbox', async ({ page }) => {
  await setup(page, 'Admin'); await page.goto('/admin/notifications');
  const bell = page.locator('app-notification-bell > details > summary'); await bell.click();
  await expect(page.getByRole('region', { name: 'Останні сповіщення' }).getByRole('link', { name: 'Усі сповіщення' })).toHaveAttribute('href', '/admin/notifications');
});
