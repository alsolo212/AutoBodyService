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
  await expect(page.locator('[data-status="AwaitingInspection"]')).toContainText('Статус відфільтровано.');
  await expect(page.locator(`[data-card-id="${ids[2]}"]`)).toHaveCount(0);
  await expect(page.locator(`[data-card-id="${ids[0]}"]`)).toBeVisible();
  await page.getByLabel('Статус', { exact: true }).selectOption('');
  await page.getByRole('button', { name: 'Застосувати' }).click();
  await expect(page.locator('[data-status="AwaitingInspection"]').locator(`[data-card-id="${ids[2]}"]`)).toBeVisible();
  await expect(page.getByRole('button', { name: 'Застосувати' })).toBeEnabled();
  await expect(page.locator(`[data-card-id="${ids[0]}"]`)).toBeVisible();

  let requests = 0;
  page.on('request', request => { if (request.url().endsWith('/transition')) requests++; });
  const scheduled = page.locator('[data-status="Scheduled"]');
  const drag = async (id: string, targetStatus: string) => {
    const source = page.locator(`[data-card-id="${id}"] .drag-handle`);
    const target = page.locator(`[data-status="${targetStatus}"]`);
    await expect(source).toBeVisible(); await expect(target).toBeVisible();
    const from = await source.boundingBox(); const to = await target.boundingBox();
    expect(from).not.toBeNull(); expect(to).not.toBeNull();
    if (!from || !to) throw new Error('The loaded card or drop target has no visible bounds.');
    await page.mouse.move(from.x + from.width / 2, from.y + from.height / 2);
    await page.mouse.down();
    await page.mouse.move(from.x + from.width / 2 + 10, from.y + from.height / 2, { steps: 3 });
    await page.mouse.move(to.x + to.width / 2, to.y + Math.min(60, to.height / 2), { steps: 12 });
    await page.mouse.up();
  };
  await drag(ids[0], 'AwaitingInspection');
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
  await drag(ids[0], 'InProgress');
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
    await drag(ids[1], 'InProgress');
    expect((await conflict).status()).toBe(409);
    await expect(page.getByRole('alert').filter({ hasText: 'Замовлення змінилося' })).toBeVisible();
    await expect(scheduled.locator(`[data-card-id="${ids[1]}"]`)).toContainText('1 700');
    await expect(page.locator('[data-status="InProgress"]').locator(`[data-card-id="${ids[1]}"]`)).toHaveCount(0);
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
  const terminalCheck = await page.evaluate(async orderIds => {
    const headers = () => ({ 'Content-Type': 'application/json', RequestVerificationToken: decodeURIComponent(document.cookie.split('; ').find(c => c.startsWith('XSRF-TOKEN='))!.split('=')[1]) });
    const schedule = await (await fetch('/api/admin/inspection-schedule')).json();
    const intervals = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'].map(dayOfWeek => ({ dayOfWeek, start: '14:00', end: '15:00' }));
    const preview = await fetch('/api/admin/inspection-schedule/preview', { method: 'POST', headers: headers(), body: JSON.stringify({ version: schedule.version, intervals }) });
    if (!preview.ok) throw new Error(`Schedule preview: ${preview.status}`);
    const result = await preview.json();
    const changed = await fetch('/api/admin/inspection-schedule', { method: 'PUT', headers: headers(), body: JSON.stringify({ version: schedule.version, intervals, confirmationToken: result.confirmationToken }) });
    if (changed.status !== 204) throw new Error(`Schedule update: ${changed.status}`);
    const dashboard = await (await fetch('/api/admin/dashboard')).json();
    const calendar = await (await fetch('/api/admin/inspection-calendar?date=2099-09-10')).json();
    const outcomes = [];
    for (const id of orderIds) {
      const before = await (await fetch(`/api/appointments/${id}`)).json();
      const response = await fetch(`/api/admin/appointments/${id}/manual-contact`, { method: 'POST', headers: headers(), body: JSON.stringify({ reason: 'ScheduleConflictOrReschedule' }) });
      const after = await (await fetch(`/api/appointments/${id}`)).json();
      outcomes.push({ status: response.status, before: before.status, after: after.status, beforeContacts: before.manualContacts.length, afterContacts: after.manualContacts.length, beforeVersion: before.version, afterVersion: after.version });
    }
    return { completedConflict: result.conflicts.some((item: { id: string }) => item.id === orderIds[0]),
      tasks: dashboard.contactTasks.map((task: { appointmentId: string }) => task.appointmentId),
      calendar: calendar.map((item: { id: string }) => item.id), outcomes };
  }, [ids[0], ids[2]]);
  expect(terminalCheck.completedConflict).toBe(true);
  expect(terminalCheck.tasks).not.toContain(ids[0]); expect(terminalCheck.tasks).not.toContain(ids[2]);
  expect(terminalCheck.calendar).toContain(ids[0]); expect(terminalCheck.calendar).toContain(ids[2]);
  expect(terminalCheck.outcomes.map(item => item.status)).toEqual([409, 409]);
  expect(terminalCheck.outcomes.map(item => item.before)).toEqual(['Completed', 'Cancelled']);
  for (const item of terminalCheck.outcomes) {
    expect(item.after).toBe(item.before); expect(item.afterContacts).toBe(item.beforeContacts); expect(item.afterVersion).toBe(item.beforeVersion);
  }
  await page.goto('/admin');
  await expect(page.getByRole('heading', { name: 'Ручний зв’язок' })).toBeVisible();
  await expect(page.locator('.contact-entry').filter({ hasText: 'Board0' })).toHaveCount(0);
  await expect(page.locator('.contact-entry').filter({ hasText: 'Board2' })).toHaveCount(0);
  await expect(page.locator('.contact-entry').filter({ hasText: 'Board3' }).first()).toBeVisible();
  await page.setViewportSize({ width: 320, height: 800 });
  await page.goto('/admin/appointments');
  await expect(page.getByRole('heading', { name: 'Замовлення', exact: true })).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.screenshot({ path: testInfo.outputPath('board-mobile.png') });
});

test('a customer cannot enter the board and an administrator sees the foreign customer details', async ({ page, browser }) => {
  test.skip(process.env['E2E_LIVE_API'] !== '1', 'Requires an isolated API and PostgreSQL.');
  test.setTimeout(60_000);
  const adminContext = await browser.newContext({ ignoreHTTPSErrors: true });
  try {
    const admin = await adminContext.newPage();
    await admin.goto('/login');
    await admin.getByLabel('Номер телефону').fill(process.env['E2E_ADMIN_PHONE']!);
    await admin.getByLabel('Пароль', { exact: true }).fill(process.env['E2E_ADMIN_PASSWORD']!);
    await admin.getByRole('button', { name: 'Увійти' }).click();
    await expect(admin).toHaveURL(/\/admin$/);
    const fixture = await admin.evaluate(async () => {
      await fetch('/api/auth/antiforgery');
      const headers = () => ({ 'Content-Type': 'application/json', RequestVerificationToken: decodeURIComponent(document.cookie.split('; ').find(c => c.startsWith('XSRF-TOKEN='))!.split('=')[1]) });
      const schedule = await (await fetch('/api/admin/inspection-schedule')).json();
      const intervals = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'].map(dayOfWeek => ({ dayOfWeek, start: '09:00', end: '10:00' }));
      const preview = await fetch('/api/admin/inspection-schedule/preview', { method: 'POST', headers: headers(), body: JSON.stringify({ version: schedule.version, intervals }) });
      if (!preview.ok) throw new Error(`Preview: ${preview.status}`);
      const updated = await fetch('/api/admin/inspection-schedule', { method: 'PUT', headers: headers(), body: JSON.stringify({ version: schedule.version, intervals, confirmationToken: (await preview.json()).confirmationToken }) });
      if (updated.status !== 204) throw new Error(`Schedule: ${updated.status}`);
      const slots = (await (await fetch('/api/inspection-slots?date=2099-10-18')).json()).slots;
      if (slots.length < 2) throw new Error('Two free inspection slots are required.');
      const foreign = await fetch('/api/appointments', { method: 'POST', headers: headers(), body: JSON.stringify({ inspectionStartUtc: slots[0].startUtc, vin: '1HGCM82633A123456', make: 'Тестова', model: 'ForeignOwner', problemDescription: 'Інший власник' }) });
      if (foreign.status !== 201) throw new Error(`Foreign booking: ${foreign.status}`);
      return { foreignId: (await foreign.json()).id as string, customerSlot: slots[1].startUtc as string };
    });
    await page.goto('/register');
    await page.getByLabel('Ім’я').fill('Клієнт Перевірка');
    await page.getByLabel('Номер телефону').fill('0000000097');
    await page.getByLabel('Пароль', { exact: true }).fill('Example-password-123!');
    await page.getByLabel('Повторіть пароль').fill('Example-password-123!');
    await page.getByRole('button', { name: 'Зареєструватися' }).click();
    await expect(page).toHaveURL(/\/account\/appointments$/);
    const customerId = await page.evaluate(async slot => {
      await fetch('/api/auth/antiforgery');
      const token = decodeURIComponent(document.cookie.split('; ').find(c => c.startsWith('XSRF-TOKEN='))!.split('=')[1]);
      const response = await fetch('/api/appointments', { method: 'POST', headers: { 'Content-Type': 'application/json', RequestVerificationToken: token }, body: JSON.stringify({ inspectionStartUtc: slot, vin: '1HGCM82633A123456', make: 'Тестова', model: 'CustomerBoard', problemDescription: 'Пошкоджені двері' }) });
      if (response.status !== 201) throw new Error(`Customer booking: ${response.status}`);
      return (await response.json()).id as string;
    }, fixture.customerSlot);
    const denied = await page.evaluate(async foreignId => ({
      board: (await fetch('/api/admin/appointments')).status,
      details: (await fetch(`/api/appointments/${foreignId}`)).status,
    }), fixture.foreignId);
    expect(denied).toEqual({ board: 403, details: 404 });
    await page.goto('/admin/appointments'); await expect(page).toHaveURL(/\/account\/appointments$/);
    await page.goto(`/account/appointments/${customerId}`);
    await expect(page.getByText('Пошкоджені двері')).toBeVisible();
    await expect(page.getByText('+380000000097')).toHaveCount(0);
    await admin.goto('/admin/appointments');
    await admin.getByLabel('Пошук клієнта або авто').fill('CustomerBoard');
    await admin.getByRole('button', { name: 'Застосувати' }).click();
    const card = admin.locator(`[data-card-id="${customerId}"]`);
    await expect(card).toContainText('Клієнт Перевірка');
    await expect(card).toContainText('Тестова CustomerBoard');
    await card.getByRole('link', { name: 'Тестова CustomerBoard' }).click();
    await expect(admin).toHaveURL(new RegExp(`/admin/appointments/${customerId}$`));
    for (const text of ['Клієнт Перевірка', '+380000000097', '1HGCM82633A123456', 'Пошкоджені двері']) await expect(admin.getByText(text, { exact: true }).first()).toBeVisible();
  } finally { await adminContext.close(); }
});
