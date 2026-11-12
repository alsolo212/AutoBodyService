import { expect, test } from '@playwright/test';

test('board uses real guarded transitions, server pages and confirmation forms', async ({ page, browser }, testInfo) => {
  test.skip(process.env['E2E_LIVE_API'] !== '1', 'Requires an isolated API and PostgreSQL.');
  test.setTimeout(90_000);
  const login = async (target: typeof page) => {
    await target.goto('/login');
    await target.getByLabel('Номер телефону').fill(process.env['E2E_ADMIN_PHONE']!);
    await target.getByLabel('Пароль', { exact: true }).fill(process.env['E2E_ADMIN_PASSWORD']!);
    await target.getByRole('button', { name: 'Увійти' }).click();
    await expect(target).toHaveURL(/\/admin$/);
  };
  await login(page);
  const ids = await page.evaluate(async () => {
    const headers = () => ({ 'Content-Type': 'application/json', RequestVerificationToken: decodeURIComponent(document.cookie.split('; ').find(c => c.startsWith('XSRF-TOKEN='))!.split('=')[1]) });
    await fetch('/api/auth/antiforgery');
    const schedule = await (await fetch('/api/admin/inspection-schedule')).json();
    const intervals = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'].map(dayOfWeek => ({ dayOfWeek, start: '09:00', end: '15:00' }));
    const preview = await fetch('/api/admin/inspection-schedule/preview', { method: 'POST', headers: headers(), body: JSON.stringify({ version: schedule.version, intervals }) });
    if (!preview.ok) throw new Error(`Schedule preview: ${preview.status}`);
    const updated = await fetch('/api/admin/inspection-schedule', { method: 'PUT', headers: headers(), body: JSON.stringify({ version: schedule.version, intervals, confirmationToken: (await preview.json()).confirmationToken }) });
    if (updated.status !== 204) throw new Error(`Schedule update: ${updated.status}`);
    const slots = (await (await fetch('/api/inspection-slots?date=2099-09-10')).json()).slots;
    if (slots.length < 9) throw new Error('Nine inspection slots are required.');
    const ids: string[] = [];
    for (let i = 0; i < 9; i++) {
      const response = await fetch('/api/appointments', { method: 'POST', headers: headers(), body: JSON.stringify({ inspectionStartUtc: slots[i].startUtc, vin: '1HGCM82633A123456', make: 'Тестова', model: `Board${i}`, problemDescription: 'Опис для перевірки' }) });
      if (response.status !== 201) throw new Error(`Booking ${i}: ${response.status}`);
      ids.push((await response.json()).id);
    }
    for (const id of ids.slice(0, 2)) {
      let item = await (await fetch(`/api/appointments/${id}`)).json();
      const plan = await fetch(`/api/admin/appointments/${id}/repair-plan`, { method: 'PUT', headers: headers(), body: JSON.stringify({ version: item.version, price: 1200, plannedStartUtc: '2099-09-11T07:00:00Z', estimatedFinishUtc: '2099-09-12T07:00:00Z', inspectionPerformed: true }) });
      if (plan.status !== 204) throw new Error(`Plan: ${plan.status}`);
      item = await (await fetch(`/api/appointments/${id}`)).json();
      const transition = await fetch(`/api/admin/appointments/${id}/transition`, { method: 'POST', headers: headers(), body: JSON.stringify({ version: item.version, target: 'Scheduled', inspectionPerformed: true }) });
      if (transition.status !== 204) throw new Error(`Schedule: ${transition.status}`);
    }
    return ids;
  });
  await page.goto('/admin/appointments');
  await expect(page.getByRole('heading', { name: 'Замовлення', exact: true })).toBeVisible();
  for (const status of ['Очікує огляду', 'Заплановано', 'У роботі', 'Очікує оплати', 'Завершено', 'Скасовано']) await expect(page.getByRole('heading', { name: status, exact: true })).toBeVisible();
  await page.getByLabel('Пошук клієнта або авто').fill('Board');
  await page.getByRole('button', { name: 'Застосувати' }).click();
  await expect(page.locator(`[data-card-id="${ids[2]}"]`)).toBeVisible();
  await page.getByRole('button', { name: 'Наступна сторінка: Очікує огляду' }).click();
  await expect(page.locator(`[data-card-id="${ids[7]}"]`)).toBeVisible();
  await expect(page.locator(`[data-card-id="${ids[2]}"]`)).toHaveCount(0);
  await page.getByRole('button', { name: 'Попередня сторінка: Очікує огляду' }).click();
  await expect(page.locator(`[data-card-id="${ids[2]}"]`)).toBeVisible();
  await page.getByLabel('Статус', { exact: true }).selectOption('Scheduled');
  await page.getByRole('button', { name: 'Застосувати' }).click();
  await expect(page.locator(`[data-card-id="${ids[2]}"]`)).toHaveCount(0);
  await expect(page.locator(`[data-card-id="${ids[0]}"]`)).toBeVisible();
  await page.getByLabel('Статус', { exact: true }).selectOption('');
  await page.getByRole('button', { name: 'Застосувати' }).click();
  await expect(page.locator(`[data-card-id="${ids[0]}"]`)).toBeVisible();

  let requests = 0;
  page.on('request', request => { if (request.url().endsWith('/transition')) requests++; });
  const scheduled = page.locator('[data-status="Scheduled"]');
  const drag = async (targetStatus: string) => {
    const source = page.locator(`[data-card-id="${ids[0]}"] .drag-handle`);
    const target = page.locator(`[data-status="${targetStatus}"]`);
    const from = (await source.boundingBox())!; const to = (await target.boundingBox())!;
    await page.mouse.move(from.x + from.width / 2, from.y + from.height / 2);
    await page.mouse.down();
    await page.mouse.move(from.x + from.width / 2 + 10, from.y + from.height / 2, { steps: 3 });
    await page.mouse.move(to.x + to.width / 2, to.y + Math.min(60, to.height / 2), { steps: 12 });
    await page.mouse.up();
  };
  await drag('AwaitingInspection');
  await expect(scheduled.locator(`[data-card-id="${ids[0]}"]`)).toBeVisible();
  expect(requests).toBe(0);
  const invalid = await page.evaluate(async id => {
    const item = await (await fetch(`/api/appointments/${id}`)).json();
    const token = decodeURIComponent(document.cookie.split('; ').find(c => c.startsWith('XSRF-TOKEN='))!.split('=')[1]);
    return (await fetch(`/api/admin/appointments/${id}/transition`, { method: 'POST', headers: { 'Content-Type': 'application/json', RequestVerificationToken: token }, body: JSON.stringify({ version: item.version, target: 'Completed', paymentReceived: true, vehicleHandedOver: true }) })).status;
  }, ids[0]);
  expect(invalid).toBe(409);
  let releaseTransition!: () => void;
  const gate = new Promise<void>(resolve => { releaseTransition = resolve; });
  const transitionUrl = `**/${ids[0]}/transition`;
  await page.route(transitionUrl, async route => { await gate; await route.continue(); });
  const pending = page.waitForRequest(r => r.url().endsWith(`/${ids[0]}/transition`));
  const moved = page.waitForResponse(r => r.url().endsWith(`/${ids[0]}/transition`));
  await drag('InProgress');
  await pending;
  await expect(scheduled.locator(`[data-card-id="${ids[0]}"]`)).toBeVisible();
  await expect(page.locator('[data-status="InProgress"]').locator(`[data-card-id="${ids[0]}"]`)).toHaveCount(0);
  releaseTransition();
  expect((await moved).status()).toBe(204);
  await page.unroute(transitionUrl);
  await expect(page.locator('[data-status="InProgress"]').locator(`[data-card-id="${ids[0]}"]`)).toBeVisible();

  const otherContext = await browser.newContext({ ignoreHTTPSErrors: true });
  try {
    const other = await otherContext.newPage(); await login(other);
    const changed = await other.evaluate(async id => {
      const item = await (await fetch(`/api/appointments/${id}`)).json(); await fetch('/api/auth/antiforgery');
      const token = decodeURIComponent(document.cookie.split('; ').find(c => c.startsWith('XSRF-TOKEN='))!.split('=')[1]);
      return (await fetch(`/api/admin/appointments/${id}/repair-plan`, { method: 'PUT', headers: { 'Content-Type': 'application/json', RequestVerificationToken: token }, body: JSON.stringify({ version: item.version, price: 1700, plannedStartUtc: item.repairPlan.plannedStartUtc, estimatedFinishUtc: item.repairPlan.estimatedFinishUtc, inspectionPerformed: true }) })).status;
    }, ids[1]);
    expect(changed).toBe(204);
    const conflict = page.waitForResponse(r => r.url().endsWith(`/${ids[1]}/transition`));
    await scheduled.locator(`[data-card-id="${ids[1]}"]`).getByRole('button', { name: 'Перевести: У роботі' }).click();
    expect((await conflict).status()).toBe(409);
    await expect(page.getByRole('alert').filter({ hasText: 'Замовлення змінилося' })).toBeVisible();
    await expect(scheduled.locator(`[data-card-id="${ids[1]}"]`)).toContainText('1 700');
    expect((await other.evaluate(async id => (await (await fetch(`/api/appointments/${id}`)).json()).status, ids[1]))).toBe('Scheduled');
  } finally { await otherContext.close(); }

  const finishWork = page.waitForResponse(r => r.url().endsWith(`/${ids[0]}/transition`));
  await page.locator(`[data-card-id="${ids[0]}"]`).getByRole('button', { name: 'Перевести: Очікує оплати' }).click();
  expect((await finishWork).status()).toBe(204);
  await expect(page.locator('[data-status="AwaitingPayment"]').locator(`[data-card-id="${ids[0]}"]`)).toBeVisible();
  await page.locator(`[data-card-id="${ids[0]}"]`).getByRole('button', { name: 'Перевести: Завершено' }).click();
  await expect(page.getByLabel('Оплату отримано')).not.toBeChecked();
  await expect(page.getByLabel('Автомобіль передано клієнту')).not.toBeChecked();
  await page.getByLabel('Оплату отримано').check();
  await page.getByRole('button', { name: 'Перевести: Завершено' }).click();
  await expect(page.getByRole('alert').filter({ hasText: 'Підтвердіть оплату і передачу' })).toBeVisible();
  await page.getByLabel('Автомобіль передано клієнту').check();
  const completed = page.waitForResponse(r => r.url().endsWith(`/${ids[0]}/transition`));
  await page.getByRole('button', { name: 'Перевести: Завершено' }).click();
  expect((await completed).status()).toBe(204);
  await page.goto('/admin/appointments');
  await expect(page.locator(`[data-card-id="${ids[2]}"]`)).toBeVisible();

  const awaiting = page.locator(`[data-card-id="${ids[2]}"]`);
  await awaiting.getByRole('button', { name: 'Перевести: Заплановано' }).focus(); await page.keyboard.press('Enter');
  await expect(page).toHaveURL(new RegExp(`/admin/appointments/${ids[2]}\\?`));
  await expect(page.getByLabel('Узгоджена вартість, ₴')).toBeVisible();
  await page.goto('/admin/appointments');
  await expect(page.locator(`[data-card-id="${ids[2]}"]`)).toBeVisible();
  const menu = page.locator(`[data-card-id="${ids[2]}"]`).getByLabel('Дії: Тестова Board2');
  await menu.focus(); await page.keyboard.press('Enter');
  await page.locator(`[data-card-id="${ids[2]}"]`).getByRole('button', { name: 'Скасувати' }).click();
  await expect(page.getByLabel('Причина скасування')).toBeFocused();
  await page.getByLabel('Причина скасування').fill('Домовились із клієнтом');
  const cancel = page.waitForResponse(r => r.url().endsWith(`/${ids[2]}/cancel`));
  await page.getByRole('dialog').getByRole('button', { name: 'Підтвердити' }).click();
  expect((await cancel).status()).toBe(204);
  await page.setViewportSize({ width: 320, height: 800 });
  await page.goto('/admin/appointments');
  await expect(page.getByRole('heading', { name: 'Замовлення', exact: true })).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.screenshot({ path: testInfo.outputPath('board-mobile.png') });
});
