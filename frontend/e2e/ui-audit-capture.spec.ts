import { mkdirSync, writeFileSync } from 'node:fs';
import { expect, Page, test } from '@playwright/test';
import { login, register } from './admin-users-helpers';

test('capture real route states at desktop, tablet and mobile widths', async ({ page, browser }) => {
  test.setTimeout(300_000);
  test.skip(process.env['E2E_LIVE_API'] !== '1', 'Requires an isolated API and PostgreSQL.');
  const dir = process.env['E2E_UI_AUDIT_DIR'];
  if (dir) mkdirSync(dir, { recursive: true });
  const metrics: unknown[] = [];
  const contrast = (p: Page, foreground: string, background: string) => p.evaluate(({ foreground, background }) => {
    const luminance = (color: string) => {
      const rgb = color.match(/\d+/g)!.slice(0, 3).map(value => {
        const channel = Number(value) / 255;
        return channel <= 0.04045 ? channel / 12.92 : ((channel + 0.055) / 1.055) ** 2.4;
      });
      return 0.2126 * rgb[0] + 0.7152 * rgb[1] + 0.0722 * rgb[2];
    };
    const a = luminance(getComputedStyle(document.querySelector(foreground)!).color);
    const b = luminance(getComputedStyle(document.querySelector(background)!).backgroundColor);
    return (Math.max(a, b) + 0.05) / (Math.min(a, b) + 0.05);
  }, { foreground, background });
  const capture = async (p: Page, name: string) => {
    for (const width of [1440, 768, 320]) {
      await p.setViewportSize({ width, height: 900 });
      await p.evaluate(() => document.fonts.ready);
      if (dir) await p.screenshot({ path: `${dir}/${name}-${width}.png`, fullPage: true, animations: 'disabled' });
      const info = await p.evaluate(() => ({
        width: innerWidth, scrollWidth: document.documentElement.scrollWidth,
        unlabeled: [...document.querySelectorAll('input:not([type=hidden]),select,textarea')].filter(e => {
          const el = e as HTMLInputElement;
          return el.getClientRects().length && !el.labels?.length && !el.getAttribute('aria-label') && !el.getAttribute('aria-labelledby');
        }).map(e => (e as HTMLElement).outerHTML.slice(0, 300)),
      }));
      metrics.push({ name, ...info });
      if (dir) writeFileSync(`${dir}/metrics.json`, JSON.stringify(metrics, null, 2));
      expect(info.scrollWidth, `${name} at ${width}px must not overflow the page`).toBeLessThanOrEqual(width);
      expect(info.unlabeled, `${name} at ${width}px must label visible fields`).toEqual([]);
    }
  };
  const visit = async (p: Page, path: string, name: string) => {
    await p.goto(path); await expect(p.locator('h1')).toHaveCount(1);
    await p.waitForLoadState('networkidle');
    await capture(p, name);
  };
  for (const [path, name] of [['/', 'home'], ['/login', 'login'], ['/register', 'register'], ['/forgot-password', 'forgot']] as const)
    await visit(page, path, name);
  await page.goto('/#prices'); await expect(page.locator('#prices')).toBeInViewport(); await capture(page, 'home-prices');
  expect(await contrast(page, '.hero__lead', '.hero')).toBeGreaterThanOrEqual(4.5);
  expect(await contrast(page, '.hero .eyebrow', '.hero')).toBeGreaterThanOrEqual(4.5);
  expect(await contrast(page, '.process-list li > span', '.process-section')).toBeGreaterThanOrEqual(4.5);
  await page.goto('/#contacts'); await expect(page.locator('#contacts')).toBeInViewport(); await capture(page, 'home-contacts');
  await login(page, process.env['E2E_ADMIN_PHONE']!, process.env['E2E_ADMIN_PASSWORD']!);
  await expect(page).toHaveURL(/\/admin$/);
  const setup = await page.evaluate(async () => {
    const schedule = await (await fetch('/api/admin/inspection-schedule')).json();
    await fetch('/api/auth/antiforgery');
    const token = decodeURIComponent(document.cookie.split('; ').find(c => c.startsWith('XSRF-TOKEN='))!.split('=')[1]);
    const intervals = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'].flatMap(dayOfWeek => [{ dayOfWeek, start: '03:00', end: '04:00' }, { dayOfWeek, start: '09:00', end: '10:00' }]);
    const headers = { 'Content-Type': 'application/json', RequestVerificationToken: token };
    const preview = await (await fetch('/api/admin/inspection-schedule/preview', { method: 'POST', headers, body: JSON.stringify({ version: schedule.version, intervals }) })).json();
    return (await fetch('/api/admin/inspection-schedule', { method: 'PUT', headers, body: JSON.stringify({ version: schedule.version, intervals, confirmationToken: preview.confirmationToken }) })).status;
  });
  expect(setup).toBe(204);
  const customer = await register(browser, 'ДужеДовгеТестовеІмяДляПеревіркиПеренесення', '0000000051');
  await visit(customer.page, '/account/appointments', 'account-empty');
  expect(await contrast(customer.page, '.profile-intro', '.profile-page')).toBeGreaterThanOrEqual(4.5);
  await visit(customer.page, '/account/book', 'book-step1');
  const today = await customer.page.evaluate(() => new Intl.DateTimeFormat('en-CA', { timeZone: 'Europe/Kyiv', year: 'numeric', month: '2-digit', day: '2-digit' }).format(new Date()));
  const tomorrow = new Date(`${today}T12:00:00Z`); tomorrow.setUTCDate(tomorrow.getUTCDate() + 1);
  const bookingDate = tomorrow.toISOString().slice(0, 10);
  await customer.context.setOffline(true);
  await customer.page.getByLabel('Дата', { exact: true }).fill(bookingDate);
  await expect(customer.page.getByRole('alert')).toContainText('Не вдалося завантажити вільний час');
  await capture(customer.page, 'book-offline');
  await customer.context.setOffline(false);
  let releaseSlots!: () => void;
  const slotsGate = new Promise<void>(resolve => { releaseSlots = resolve; });
  await customer.page.route('**/api/inspection-slots?*', async route => { await slotsGate; await route.continue(); });
  const slotsRequested = customer.page.waitForRequest(r => r.url().includes('/api/inspection-slots?'));
  const slotsLoaded = customer.page.waitForResponse(r => r.url().includes('/api/inspection-slots?'));
  await customer.page.getByRole('button', { name: 'Спробувати ще раз' }).click();
  await slotsRequested;
  await expect(customer.page.getByText('Завантажуємо вільний час…')).toBeVisible();
  await capture(customer.page, 'book-loading');
  releaseSlots(); expect((await slotsLoaded).status()).toBe(200);
  await customer.page.unroute('**/api/inspection-slots?*');
  await expect(customer.page.getByLabel('09:00', { exact: true })).toBeVisible();
  await capture(customer.page, 'book-slots');
  await customer.page.getByLabel('09:00', { exact: true }).check();
  await customer.page.getByRole('button', { name: 'Далі' }).click(); await capture(customer.page, 'book-step2');
  await customer.page.getByLabel('VIN').fill('1HGCM82633A004352');
  await customer.page.getByLabel('Марка').fill('ДужеДовгаТестоваНазваАвтомобіля');
  await customer.page.getByLabel('Модель').fill('ДовгаТестоваМодельАвтомобіля');
  await customer.page.getByRole('button', { name: 'Далі' }).click(); await capture(customer.page, 'book-step3');
  await customer.page.getByLabel('Що потрібно оглянути?').fill('Тестовий опис для перевірки відображення замовлення');
  await customer.page.getByRole('button', { name: 'Далі' }).click(); await capture(customer.page, 'book-step4');
  let bookingRequests = 0;
  let release!: () => void;
  const gate = new Promise<void>(resolve => { release = resolve; });
  await customer.page.route('**/api/appointments', async route => {
    if (route.request().method() === 'POST') { bookingRequests++; await gate; }
    await route.continue();
  });
  const requested = customer.page.waitForRequest(r => r.url().endsWith('/api/appointments') && r.method() === 'POST');
  const created = customer.page.waitForResponse(r => r.url().endsWith('/api/appointments') && r.request().method() === 'POST');
  await customer.page.getByRole('button', { name: 'Підтвердити запис' }).click();
  await requested;
  await expect(customer.page.getByRole('button', { name: 'Створюємо запис…' })).toBeDisabled();
  expect(bookingRequests).toBe(1); release();
  expect((await created).status()).toBe(201);
  await customer.page.unroute('**/api/appointments');
  await expect(customer.page).toHaveURL(/\/account\/book\/success$/); await capture(customer.page, 'book-success');
  await visit(customer.page, '/account/appointments', 'account-appointments');
  const detail = (await customer.page.getByRole('link', { name: /Деталі запису/ }).first().getAttribute('href'))!;
  await visit(customer.page, detail, 'account-detail');
  await visit(customer.page, '/account/notifications', 'account-notifications');
  await visit(customer.page, '/account/profile', 'account-profile');
  const id = detail.split('/').pop();
  for (const [path, name] of [
    ['/admin', 'admin-dashboard'], ['/admin/appointments', 'admin-board'], [`/admin/appointments/${id}`, 'admin-detail'],
    ['/admin/assignments', 'admin-assignments'], ['/admin/inspection-calendar', 'admin-calendar'], ['/admin/employees', 'admin-employees'],
    ['/admin/prices', 'admin-prices'], ['/admin/users', 'admin-users'], ['/admin/notifications', 'admin-notifications'],
    ['/admin/profile', 'admin-profile'], ['/admin/reports', 'admin-reports'],
  ] as const) await visit(page, path, name);
  expect(await contrast(page, '.panel-eyebrow', '.workspace-panel')).toBeGreaterThanOrEqual(4.5);
  await page.getByRole('tab', { name: 'Журнал адміністративних дій' }).click(); await page.waitForLoadState('networkidle'); await capture(page, 'admin-audit');
  await page.goto('/admin/inspection-calendar'); await page.getByRole('button', { name: 'Налаштування графіка' }).click(); await capture(page, 'admin-calendar-settings');
  for (const name of ['Записи на огляд', 'Налаштування графіка']) {
    const tab = page.getByRole('button', { name, exact: true });
    const box = await tab.boundingBox();
    expect(box).not.toBeNull();
    expect(box!.x).toBeGreaterThanOrEqual(0);
    expect(box!.x + box!.width).toBeLessThanOrEqual(320);
  }
  await page.evaluate(async appointmentId => {
    await fetch('/api/auth/antiforgery');
    const token = decodeURIComponent(document.cookie.split('; ').find(c => c.startsWith('XSRF-TOKEN='))!.split('=')[1]);
    const headers = { 'Content-Type': 'application/json', RequestVerificationToken: token };
    const checked = async (response: Response, status: number) => {
      if (response.status !== status) throw new Error(`Unexpected setup status: ${response.status}`);
      return response;
    };
    const details = async () => (await (await checked(await fetch(`/api/appointments/${appointmentId}`), 200)).json());
    let item = await details();
    const today = new Intl.DateTimeFormat('en-CA', { timeZone: 'Europe/Kyiv', year: 'numeric', month: '2-digit', day: '2-digit' }).format(new Date());
    await checked(await fetch('/api/admin/repair-capacity', { method: 'PUT', headers, body: JSON.stringify({ workplaces: 2 }) }), 204);
    await checked(await fetch(`/api/admin/appointments/${appointmentId}/schedule`, { method: 'POST', headers, body: JSON.stringify({ version: item.version, price: 1234.56, start: today, durationDays: 1, inspectionPerformed: true }) }), 204);
    for (const target of ['InProgress']) {
      item = await details();
      await checked(await fetch(`/api/admin/appointments/${appointmentId}/transition`, { method: 'POST', headers, body: JSON.stringify({ version: item.version, target, inspectionPerformed: true }) }), 204);
    }
    for (const [fullName, specialization] of [['Майстер Перевірки Один', 'Маляр'], ['Майстер Перевірки Два', 'Рихтування']]) {
      const worker = await (await checked(await fetch('/api/admin/employees', { method: 'POST', headers, body: JSON.stringify({ fullName, specialization, isActive: true }) }), 201)).json();
      await checked(await fetch('/api/admin/assignments', { method: 'POST', headers, body: JSON.stringify({ appointmentId, employeeId: worker.id }) }), 201);
    }
  }, id!);
  await visit(page, '/admin/assignments', 'admin-assignments-populated');
  await expect(page.locator('#assignment-source')).toContainText('ДужеДовгаТестоваНазваАвтомобіля');
  await expect(page.locator('.assigned-card')).toHaveCount(2);
  await visit(page, '/admin/employees', 'admin-employees-populated');
  await visit(page, `/admin/appointments/${id}`, 'admin-detail-populated');
  await visit(customer.page, detail, 'account-detail-populated');
  await expect(customer.page.getByText(/1\s?234,56\s?₴/)).toBeVisible();
  await page.goto('/admin/prices');
  await page.getByLabel('Назва послуги').fill('Тестовий кузовний ремонт');
  await page.getByLabel('Короткий опис').fill('Орієнтовна вартість робіт');
  await page.getByLabel('Ціна, грн').fill('1250,35');
  await page.getByLabel('Ціна «від»').check(); await page.getByLabel('Опубліковано').check();
  const priceCreated = page.waitForResponse(r => r.url().endsWith('/api/admin/prices') && r.request().method() === 'POST');
  await page.getByRole('button', { name: 'Додати послугу' }).click();
  expect((await priceCreated).status()).toBe(201);
  await expect(page.getByText('Послугу додано.')).toBeVisible(); await capture(page, 'admin-prices-populated');
  await customer.page.goto('/#prices');
  await expect(customer.page.locator('#prices')).toContainText('від 1250,35 ₴');
  await capture(customer.page, 'home-prices-populated');
  const target = await register(browser, 'Тестовий Адміністратор', '0000000052');
  const promoted = await page.evaluate(async () => {
    const user = (await (await fetch('/api/admin/users?search=0000000052')).json())[0];
    await fetch('/api/auth/antiforgery');
    const token = decodeURIComponent(document.cookie.split('; ').find(c => c.startsWith('XSRF-TOKEN='))!.split('=')[1]);
    return (await fetch(`/api/admin/users/${user.id}/roles`, { method: 'PUT', headers: { 'Content-Type': 'application/json', RequestVerificationToken: token }, body: JSON.stringify({ version: user.version, isAdmin: true }) })).status;
  });
  expect(promoted).toBe(204); await login(target.page, '0000000052', 'Example-password-123!');
  await expect(target.page).toHaveURL(/\/admin$/); await capture(target.page, 'admin-role');
  await visit(page, '/admin/users', 'admin-users-populated');
  const resetButton = page.locator('.user-card').filter({ hasText: 'ДужеДовгеТестовеІмяДляПеревіркиПеренесення' }).getByRole('button', { name: /Скинути пароль/ });
  await resetButton.focus(); await page.keyboard.press('Enter');
  const dialog = page.getByRole('dialog'); await expect(dialog).toBeVisible();
  await expect(dialog).toHaveAttribute('aria-labelledby', 'user-action-title');
  const cancel = dialog.getByRole('button', { name: 'Скасувати' });
  const confirm = dialog.getByRole('button', { name: 'Підтвердити' });
  await expect(cancel).toBeFocused();
  await page.keyboard.press('Tab'); await expect(confirm).toBeFocused();
  const outline = await confirm.evaluate(element => getComputedStyle(element).outlineWidth);
  expect(parseFloat(outline)).toBeGreaterThanOrEqual(2);
  await page.keyboard.press('Shift+Tab'); await expect(cancel).toBeFocused();
  await capture(page, 'admin-confirm-dialog');
  const cancelBox = await cancel.boundingBox(); const confirmBox = await confirm.boundingBox();
  expect(cancelBox).not.toBeNull(); expect(confirmBox).not.toBeNull();
  expect(cancelBox!.y).toBeLessThan(confirmBox!.y);
  await page.keyboard.press('Escape'); await expect(dialog).not.toBeVisible(); await expect(resetButton).toBeFocused();
  const reset = await page.evaluate(async () => {
    const user = (await (await fetch('/api/admin/users?search=0000000051')).json())[0];
    await fetch('/api/auth/antiforgery');
    const token = decodeURIComponent(document.cookie.split('; ').find(c => c.startsWith('XSRF-TOKEN='))!.split('=')[1]);
    const r = await fetch(`/api/admin/users/${user.id}/reset-password`, { method: 'POST', headers: { 'Content-Type': 'application/json', RequestVerificationToken: token }, body: JSON.stringify({ version: user.version }) });
    return { status: r.status, body: await r.json() };
  });
  expect(reset.status).toBe(200); await login(customer.page, '0000000051', reset.body.temporaryPassword);
  await expect(customer.page).toHaveURL(/\/account\/change-temporary-password$/); await capture(customer.page, 'temporary-password');
  await target.context.close(); await customer.context.close();
});
