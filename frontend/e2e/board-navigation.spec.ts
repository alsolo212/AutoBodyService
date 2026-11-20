import { expect, Page, test } from '@playwright/test';

const statusNames = ['AwaitingInspection', 'Scheduled', 'InProgress', 'AwaitingPayment', 'Completed', 'Cancelled'];
const visibleNames = ['Очікує огляду', 'Заплановано', 'У роботі', 'Очікує оплати'];
const id = '00000000-0000-0000-0000-000000000001';

async function setup(page: Page) {
  let total = 12;
  const requests: { status: string | null; page: string | null; search: string | null; sort: string | null; from: string | null; to: string | null; direction: string | null; pageSize: string | null }[] = [];
  const card = { id, customerName: 'Клієнт', vin: null, make: 'Тест', model: 'Авто', inspectionStartUtc: '2026-12-01T10:00:00Z', inspectionStartLocal: '2026-12-01T12:00:00+02:00', status: 'InProgress', repairPlan: null, version: 4 };
  await page.route('**/api/**', route => {
    const url = new URL(route.request().url());
    if (url.pathname === '/api/auth/session') return route.fulfill({ json: { name: 'Тест', phone: '+380000000000', roles: ['Admin'], mustChangePassword: false } });
    if (url.pathname === '/api/admin/dashboard') return route.fulfill({ json: { counts: { awaitingInspection: 1, scheduled: 2, inProgress: 12, awaitingPayment: 3 }, upcomingInspections: [], newAppointments: [], cancellations: [], contactTasks: [] } });
    if (url.pathname === '/api/admin/appointments') {
      const p = url.searchParams;
      requests.push({ status: p.get('status'), page: p.get('page'), search: p.get('search'), sort: p.get('sort'), from: p.get('from'), to: p.get('to'), direction: p.get('direction'), pageSize: p.get('pageSize') });
      const columnStatus = p.get('status');
      const columnTotal = columnStatus === 'InProgress' ? total : columnStatus === 'AwaitingInspection' ? 12 : 0;
      const item = columnStatus === 'InProgress' ? card : { ...card, id: '00000000-0000-0000-0000-000000000002', status: 'AwaitingInspection' };
      const items = columnTotal && Number(p.get('page')) <= Math.ceil(columnTotal / Number(p.get('pageSize'))) ? [item] : [];
      return route.fulfill({ json: { items, total: columnTotal, page: Number(p.get('page')), pageSize: Number(p.get('pageSize')) } });
    }
    if (url.pathname === `/api/appointments/${id}`) return route.fulfill({ json: { ...card, customerPhone: '+380000000000', problemDescription: 'Пошкоджено двері', history: [], participants: [], manualContacts: [], assignments: [], paymentConfirmation: null } });
    if (url.pathname === '/api/health') return route.fulfill({ json: { status: 'Healthy' } });
    return route.fulfill({ json: [] });
  });
  return { requests, setTotal: (value: number) => { total = value; } };
}

test('dashboard status links open the matching board column', async ({ page }) => {
  const state = await setup(page); await page.setViewportSize({ width: 1280, height: 720 });
  for (const [index, label] of visibleNames.entries()) {
    await page.goto('/admin');
    await page.locator('.summary-grid').getByRole('link', { name: new RegExp(label) }).click();
    const status = statusNames[index];
    await expect(page).toHaveURL(new RegExp(`status=${status}$`));
    await expect(page.locator('#filter-status')).toHaveValue(status);
    await expect(page.locator(`#column-${status}`)).toBeFocused();
    await expect(page.locator(`#column-${status}`)).toBeInViewport();
    expect(state.requests.at(-1)?.status).toBe(status);
  }
});

for (const [name, width, height] of [['desktop', 1280, 600], ['mobile', 320, 720]] as const) {
  test(`${name} restores applied filters, draft, page and scroll after opening an order`, async ({ page }) => {
    const state = await setup(page); await page.setViewportSize({ width, height });
    await page.goto('/admin/appointments');
    const form = page.getByRole('form', { name: 'Фільтри замовлень' });
    if (name === 'mobile') {
      const toggle = page.getByRole('button', { name: 'Додаткові фільтри' });
      await expect(toggle).toHaveAttribute('aria-expanded', 'false');
      await expect(form.getByLabel('Статус')).toBeHidden(); await toggle.click();
    }
    await form.getByLabel('Пошук клієнта або авто').fill('Alpha');
    await form.getByLabel('Статус').selectOption('InProgress');
    await form.getByLabel('Огляд від').fill('2026-01-01'); await form.getByLabel('Огляд до').fill('2026-12-31');
    await form.getByLabel('Сортувати за').selectOption('created'); await form.getByLabel('Порядок').selectOption('desc'); await form.getByLabel('На сторінці').selectOption({ label: '10' });
    await form.getByRole('button', { name: 'Застосувати' }).click();
    await expect(page.locator('[data-status="InProgress"] .card')).toBeVisible();
    await page.getByRole('button', { name: 'Наступна сторінка: У роботі' }).click();
    await expect(page.locator('section').filter({ has: page.locator('#column-InProgress') }).getByText('Стор. 2 / 2')).toBeVisible();
    await form.getByLabel('Пошук клієнта або авто').fill('Draft');
    const link = page.locator(`[data-card-id="${id}"]`).getByRole('link', { name: 'Тест Авто' });
    await link.scrollIntoViewIfNeeded();
    if (name === 'desktop') await page.locator('.board-scroll').evaluate((element: HTMLElement) => { element.scrollLeft = 160; });
    const before = await page.evaluate(() => ({ y: window.scrollY, x: document.querySelector<HTMLElement>('.board-scroll')!.scrollLeft }));
    expect(before.y).toBeGreaterThan(0);
    if (name === 'desktop') expect(before.x).toBeGreaterThan(0);
    await link.click(); await expect(page).toHaveURL(new RegExp(`/admin/appointments/${id}$`));
    await page.goBack(); await expect(page).toHaveURL(/\/admin\/appointments$/);
    await expect(form.getByLabel('Пошук клієнта або авто')).toHaveValue('Draft');
    await expect(form.getByLabel('Статус')).toHaveValue('InProgress');
    await expect(form.getByLabel('Сортувати за')).toHaveValue('created');
    await expect(form.getByLabel('Порядок')).toHaveValue('desc');
    await expect(form.getByLabel('На сторінці')).toHaveJSProperty('selectedIndex', 1);
    await expect(form.getByLabel('Огляд від')).toHaveValue('2026-01-01');
    await expect(form.getByLabel('Огляд до')).toHaveValue('2026-12-31');
    await expect(page.getByText('Стор. 2 / 2')).toBeVisible();
    await expect.poll(async () => page.evaluate(() => ({ y: Math.round(window.scrollY), x: Math.round(document.querySelector<HTMLElement>('.board-scroll')!.scrollLeft) }))).toEqual({ y: Math.round(before.y), x: Math.round(before.x) });
    expect(state.requests.at(-1)).toMatchObject({ status: 'InProgress', page: '2', search: 'Alpha', sort: 'created', direction: 'desc', from: '2026-01-01', to: '2026-12-31', pageSize: '10' });
    expect(page.url()).not.toContain('Alpha'); expect(page.url()).not.toContain('Draft');
  });
}

test('mobile filters are collapsible and status shortcuts move keyboard focus', async ({ page }) => {
  await setup(page); await page.setViewportSize({ width: 320, height: 720 }); await page.goto('/admin/appointments');
  const form = page.getByRole('form', { name: 'Фільтри замовлень' });
  await expect(form.getByLabel('Пошук клієнта або авто')).toBeVisible();
  await expect(form.getByLabel('Статус')).toBeHidden();
  await page.getByRole('button', { name: 'Додаткові фільтри' }).focus(); await page.keyboard.press('Enter');
  await expect(form.getByLabel('Статус')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Сховати додаткові фільтри' })).toHaveAttribute('aria-expanded', 'true');
  await page.getByRole('button', { name: 'Сховати додаткові фільтри' }).click();
  const shortcut = page.getByRole('navigation', { name: 'Перейти до статусу' }).getByRole('button', { name: 'У роботі' });
  await shortcut.focus(); await page.keyboard.press('Enter');
  await expect(page.locator('#column-InProgress')).toBeFocused(); await expect(page.locator('#column-InProgress')).toBeInViewport();
  const empty = page.locator('section').filter({ has: page.locator('#column-Scheduled') });
  expect((await empty.boundingBox())!.height).toBeLessThan(120);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
});

test.describe('touch phone', () => {
  test.use({ viewport: { width: 320, height: 720 }, isMobile: true, hasTouch: true });
  test('touch controls reveal filters and move to the selected status', async ({ page }) => {
    await setup(page); await page.goto('/admin/appointments');
    await page.getByRole('button', { name: 'Додаткові фільтри' }).tap();
    await expect(page.getByLabel('Статус', { exact: true })).toBeVisible();
    await page.getByRole('button', { name: 'Сховати додаткові фільтри' }).tap();
    await page.getByRole('navigation', { name: 'Перейти до статусу' }).getByRole('button', { name: 'У роботі' }).tap();
    await expect(page.locator('#column-InProgress')).toBeFocused();
    await expect(page.locator('#column-InProgress')).toBeInViewport();
  });
});

test('a restored page is clamped when records disappear', async ({ page }) => {
  const state = await setup(page); await page.goto('/admin/appointments?status=InProgress');
  await page.getByRole('button', { name: 'Наступна сторінка: У роботі' }).click();
  await page.locator(`[data-card-id="${id}"]`).getByRole('link', { name: 'Тест Авто' }).click();
  await expect(page).toHaveURL(new RegExp(`/admin/appointments/${id}$`));
  state.setTotal(3); await page.goBack();
  await expect.poll(() => state.requests.slice(-2).map(item => item.page)).toEqual(['2', '1']);
  await expect(page.locator('[data-status="InProgress"] .card')).toBeVisible();
  await expect(page.getByText('Стор. 2 / 1')).toHaveCount(0);
});

test('returning to the board restores independent pages of different columns', async ({ page }) => {
  const state = await setup(page); await page.goto('/admin/appointments');
  await page.getByRole('button', { name: 'Наступна сторінка: Очікує огляду' }).click();
  const waiting = page.locator('section').filter({ has: page.locator('#column-AwaitingInspection') });
  await expect(waiting.getByText('Стор. 2 / 3')).toBeVisible();
  const progress = page.locator('section').filter({ has: page.locator('#column-InProgress') });
  await page.getByRole('button', { name: 'Наступна сторінка: У роботі' }).click();
  await expect(progress.getByText('Стор. 2 / 3')).toBeVisible();
  await page.getByRole('button', { name: 'Наступна сторінка: У роботі' }).click();
  await expect(progress.getByText('Стор. 3 / 3')).toBeVisible();
  await page.locator(`[data-card-id="${id}"]`).getByRole('link', { name: 'Тест Авто' }).click();
  await expect(page).toHaveURL(new RegExp(`/admin/appointments/${id}$`));
  await page.goBack();
  await expect(waiting.getByText('Стор. 2 / 3')).toBeVisible(); await expect(progress.getByText('Стор. 3 / 3')).toBeVisible();
  expect(state.requests.slice(-6).find(item => item.status === 'AwaitingInspection')?.page).toBe('2');
  expect(state.requests.slice(-6).find(item => item.status === 'InProgress')?.page).toBe('3');
});

test('breadcrumb returns to the saved search and a later dashboard link starts a fresh status view', async ({ page }) => {
  const state = await setup(page); await page.goto('/admin/appointments');
  await page.getByLabel('Пошук клієнта або авто').fill('Alpha'); await page.getByRole('button', { name: 'Застосувати' }).click();
  await page.locator(`[data-card-id="${id}"]`).getByRole('link', { name: 'Тест Авто' }).click();
  await expect(page).toHaveURL(new RegExp(`/admin/appointments/${id}$`));
  await page.getByRole('navigation', { name: 'Шлях сторінки' }).getByRole('link', { name: 'Замовлення' }).click();
  await expect(page.getByLabel('Пошук клієнта або авто')).toHaveValue('Alpha');
  await expect.poll(() => state.requests.at(-1)?.search).toBe('Alpha');
  await page.getByRole('navigation', { name: 'Шлях сторінки' }).getByRole('link', { name: 'Панель керування' }).click();
  await page.locator('.summary-grid').getByRole('link', { name: /Заплановано/ }).click();
  await expect(page.locator('#filter-status')).toHaveValue('Scheduled');
  await expect(page.getByLabel('Пошук клієнта або авто')).toHaveValue('');
  await expect.poll(() => state.requests.at(-1)?.status).toBe('Scheduled');
  expect(state.requests.at(-1)?.search).toBeNull();
});
