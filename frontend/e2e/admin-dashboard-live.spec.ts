import { execFileSync } from 'node:child_process';
import { expect, Page, test } from '@playwright/test';

test('common dashboard reflects real orders and manual contacts for Admin and SuperAdmin', async ({ page, browser }, testInfo) => {
  test.skip(process.env['E2E_LIVE_API'] !== '1', 'Requires an isolated API and PostgreSQL.');
  test.setTimeout(90_000);
  const login = async (target: Page, phone: string, password: string) => {
    await target.goto('/login'); await target.getByLabel('Номер телефону').fill(phone); await target.getByLabel('Пароль', { exact: true }).fill(password);
    await target.getByRole('button', { name: 'Увійти' }).click(); await expect(target).toHaveURL(/\/admin$/);
  };
  await login(page, process.env['E2E_ADMIN_PHONE']!, process.env['E2E_ADMIN_PASSWORD']!);
  const ids = await page.evaluate(async () => {
    await fetch('/api/auth/antiforgery');
    const headers = () => ({ 'Content-Type': 'application/json', RequestVerificationToken: decodeURIComponent(document.cookie.split('; ').find(c => c.startsWith('XSRF-TOKEN='))!.split('=')[1]) });
    const schedule = await (await fetch('/api/admin/inspection-schedule')).json();
    const intervals = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'].map(dayOfWeek => ({ dayOfWeek, start: '09:00', end: '12:00' }));
    const preview = await fetch('/api/admin/inspection-schedule/preview', { method: 'POST', headers: headers(), body: JSON.stringify({ version: schedule.version, intervals }) });
    if (!preview.ok) throw new Error(`Preview ${preview.status}`);
    const update = await fetch('/api/admin/inspection-schedule', { method: 'PUT', headers: headers(), body: JSON.stringify({ version: schedule.version, intervals, confirmationToken: (await preview.json()).confirmationToken }) });
    if (update.status !== 204) throw new Error(`Schedule ${update.status}`);
    const slots = (await (await fetch('/api/inspection-slots?date=2099-09-10')).json()).slots;
    if (slots.length < 5) throw new Error('Expected five slots');
    const result: string[] = [];
    for (let i = 0; i < 5; i++) {
      const response = await fetch('/api/appointments', { method: 'POST', headers: headers(), body: JSON.stringify({ inspectionStartUtc: slots[i].startUtc, vin: '1HGCM82633A123456', make: 'Тестова', model: `Dashboard${i}`, problemDescription: 'Опис для перевірки' }) });
      if (response.status !== 201) throw new Error(`Booking ${response.status}`);
      result.push((await response.json()).id);
    }
    for (let i = 1; i <= 3; i++) {
      const id = result[i]; let item = await (await fetch(`/api/appointments/${id}`)).json();
      const plan = await fetch(`/api/admin/appointments/${id}/repair-plan`, { method: 'PUT', headers: headers(), body: JSON.stringify({ version: item.version, price: 1299, plannedStartUtc: '2099-09-11T07:00:00Z', estimatedFinishUtc: '2099-09-12T07:00:00Z', inspectionPerformed: true }) });
      if (plan.status !== 204) throw new Error(`Plan ${plan.status}`);
      for (const target of ['Scheduled', 'InProgress', 'AwaitingPayment'].slice(0, i)) {
        item = await (await fetch(`/api/appointments/${id}`)).json();
        const transition = await fetch(`/api/admin/appointments/${id}/transition`, { method: 'POST', headers: headers(), body: JSON.stringify({ version: item.version, target, inspectionPerformed: true }) });
        if (transition.status !== 204) throw new Error(`Transition ${target}: ${transition.status}`);
      }
    }
    const last = await (await fetch(`/api/appointments/${result[4]}`)).json();
    const cancel = await fetch(`/api/admin/appointments/${result[4]}/cancel`, { method: 'POST', headers: headers(), body: JSON.stringify({ version: last.version, confirmed: true, reason: 'Тестова причина' }) });
    if (cancel.status !== 204) throw new Error(`Cancel ${cancel.status}`);
    return result;
  });

  let dashboardRequests = 0;
  page.on('request', request => { if (request.url().endsWith('/api/admin/dashboard') && request.method() === 'GET') dashboardRequests++; });
  await page.goto('/admin');
  await expect(page.getByRole('heading', { name: 'Замовлення в роботі' })).toBeVisible();
  await expect(page.locator('.summary-grid strong')).toHaveText(['1', '1', '1', '1']);
  expect(dashboardRequests).toBe(1);
  const dashboard = page.getByRole('main');
  await expect(dashboard.getByRole('heading', { name: 'Найближчі огляди' })).toBeVisible();
  await expect(dashboard.getByRole('heading', { name: 'Нові записи' })).toBeVisible();
  await expect(dashboard.getByRole('heading', { name: 'Скасування' })).toBeVisible();
  await expect(dashboard.locator('section[aria-labelledby="cancel-title"]')).toContainText('Dashboard4');
  await expect(dashboard).toContainText('UTC+03:00');
  await expect(dashboard).not.toContainText('1 299');
  const task = dashboard.locator('.contact-entry').filter({ hasText: 'Dashboard0' }).filter({ hasText: 'Нова заявка' });
  await expect(task).toBeVisible();
  const refresh = page.waitForResponse(response => response.url().endsWith('/api/admin/dashboard') && response.request().method() === 'GET');
  await task.getByRole('button').focus(); await page.keyboard.press('Enter');
  await expect(page.getByRole('status').filter({ hasText: 'Ручний контакт відмічено' })).toBeVisible();
  expect((await refresh).status()).toBe(200);
  await expect(page.getByRole('heading', { name: 'Ручний зв’язок' })).toBeVisible();
  await expect(task).toHaveCount(0);
  expect(dashboardRequests).toBe(2);
  const details = await page.evaluate(async id => (await (await fetch(`/api/appointments/${id}`)).json()).manualContacts, ids[0]);
  expect(details.some((event: { reason: string; actorName: string; recordedAtUtc: string }) => event.reason === 'NewAppointment' && event.actorName === 'E2E test' && !!event.recordedAtUtc)).toBe(true);
  for (const [path, label] of [['assignments', 'Розподіл робіт'], ['employees', 'Працівники'], ['prices', 'Послуги та ціни']]) {
    await page.goto('/admin'); await expect(page.getByRole('heading', { name: 'Замовлення в роботі' })).toBeVisible();
    const beforePlaceholder = dashboardRequests;
    await page.locator('.quick-links').getByRole('link', { name: new RegExp(label) }).click();
    await expect(page).toHaveURL(new RegExp(`/admin/${path}$`));
    await expect(page.getByText('Розділ у підготовці')).toBeVisible();
    await expect(page.getByText('Перейти до вмісту')).toHaveAttribute('href', `/admin/${path}#workspace-main`);
    expect(dashboardRequests).toBe(beforePlaceholder);
  }
  await page.goto('/admin');
  await page.getByRole('link', { name: /Дошка замовлень/ }).click(); await expect(page).toHaveURL(/\/admin\/appointments$/);
  await page.goto('/admin');
  await page.getByRole('link', { name: /Календар оглядів/ }).first().click(); await expect(page).toHaveURL(/\/admin\/inspection-calendar$/);

  const userContext = await browser.newContext({ ignoreHTTPSErrors: true });
  try {
    const user = await userContext.newPage(); await user.goto('/register');
    await user.getByLabel('Ім’я').fill('Тестовий адміністратор'); await user.getByLabel('Номер телефону').fill('0000000098');
    await user.getByLabel('Пароль', { exact: true }).fill('Example-password-123!'); await user.getByLabel('Повторіть пароль').fill('Example-password-123!');
    await user.getByRole('button', { name: 'Зареєструватися' }).click(); await expect(user).toHaveURL(/\/account\/appointments$/);
    expect(await user.evaluate(async () => (await fetch('/api/admin/dashboard')).status)).toBe(403);
    await user.goto('/admin'); await expect(user).toHaveURL(/\/account\/appointments$/);
  } finally { await userContext.close(); }

  const container = process.env['PROFILE_VERIFY_CONTAINER'] ?? '';
  if (!/^autobody-ui-verify-[a-f0-9]+$/.test(container)) throw new Error('Expected the disposable E2E database container');
  const label = execFileSync('docker', ['inspect', '-f', '{{ index .Config.Labels "autobody.auth-e2e.disposable" }}', container], { encoding: 'utf8' }).trim();
  if (label !== 'true') throw new Error('The E2E database is not marked disposable');
  const sql = `INSERT INTO "AspNetUserRoles" ("UserId", "RoleId") SELECT "Id", 'role-admin' FROM "AspNetUsers" WHERE "PhoneNormalized" = '+380000000098'`;
  const promoted = execFileSync('docker', ['exec', container, 'psql', '-U', 'auth_test', '-d', 'auth_test', '-v', 'ON_ERROR_STOP=1', '-c', sql], { encoding: 'utf8' });
  expect(promoted).toContain('INSERT 0 1');
  const adminContext = await browser.newContext({ ignoreHTTPSErrors: true });
  try {
    const admin = await adminContext.newPage(); await login(admin, '0000000098', 'Example-password-123!');
    await expect(admin.locator('.summary-grid strong')).toHaveText(['1', '1', '1', '1']);
    const response = await admin.evaluate(async () => { const r = await fetch('/api/admin/dashboard'); return { status: r.status, body: await r.text() }; });
    expect(response.status).toBe(200); expect(response.body).not.toMatch(/agreedPrice|revenue|expenses|password|audit/i);
    await expect(admin.getByRole('main')).not.toContainText('1 299');
    await admin.setViewportSize({ width: 768, height: 900 }); expect(await admin.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await admin.setViewportSize({ width: 320, height: 800 }); expect(await admin.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await admin.screenshot({ path: testInfo.outputPath('dashboard-mobile.png'), fullPage: true });
  } finally { await adminContext.close(); }
});
