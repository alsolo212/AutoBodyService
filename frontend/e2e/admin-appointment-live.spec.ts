import { expect, test } from '@playwright/test';

test('administrative order handles a real version conflict and both completion facts', async ({ page, browser }, testInfo) => {
  test.skip(process.env['E2E_LIVE_API'] !== '1', 'Requires an isolated API and PostgreSQL.');
  await page.setViewportSize({ width: 320, height: 800 });
  const login = async (target: typeof page) => {
    await target.goto('/login');
    await target.getByLabel('Номер телефону').fill(process.env['E2E_ADMIN_PHONE']!);
    await target.getByLabel('Пароль', { exact: true }).fill(process.env['E2E_ADMIN_PASSWORD']!);
    await target.getByRole('button', { name: 'Увійти' }).click();
    await expect(target).toHaveURL(/\/admin$/);
  };
  await login(page);
  const created = await page.evaluate(async () => {
    const headers = () => ({ 'Content-Type': 'application/json', RequestVerificationToken: decodeURIComponent(document.cookie.split('; ').find(c => c.startsWith('XSRF-TOKEN='))!.split('=')[1]) });
    await fetch('/api/auth/antiforgery');
    const schedule = await (await fetch('/api/admin/inspection-schedule')).json();
    const intervals = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'].map(dayOfWeek => ({ dayOfWeek, start: '09:00', end: '10:00' }));
    const preview = await fetch('/api/admin/inspection-schedule/preview', { method: 'POST', headers: headers(), body: JSON.stringify({ version: schedule.version, intervals }) });
    if (!preview.ok) throw new Error(`Schedule preview failed: ${preview.status}`);
    const token = (await preview.json()).confirmationToken;
    const updated = await fetch('/api/admin/inspection-schedule', { method: 'PUT', headers: headers(), body: JSON.stringify({ version: schedule.version, intervals, confirmationToken: token }) });
    if (updated.status !== 204) throw new Error(`Schedule update failed: ${updated.status}`);
    if ((await fetch('/api/admin/repair-capacity', { method: 'PUT', headers: headers(), body: JSON.stringify({ workplaces: 2 }) })).status !== 204) throw new Error('Capacity update failed');
    const today = new Intl.DateTimeFormat('en-CA', { timeZone: 'Europe/Kyiv', year: 'numeric', month: '2-digit', day: '2-digit' }).format(new Date());
    const tomorrow = new Date(`${today}T12:00:00Z`); tomorrow.setUTCDate(tomorrow.getUTCDate() + 1);
    const slots = await (await fetch(`/api/inspection-slots?date=${tomorrow.toISOString().slice(0, 10)}`)).json();
    if (slots.slots.length < 2) throw new Error('Two inspection slots are required.');
    const create = async (slot: string, model: string) => {
      const result = await fetch('/api/appointments', { method: 'POST', headers: headers(), body: JSON.stringify({ inspectionStartUtc: slot, vin: '1HGCM82633A123456', make: 'Тестова', model, problemDescription: 'Опис для перевірки' }) });
      if (result.status !== 201) throw new Error(`Booking failed: ${result.status}`);
      return (await result.json()).id as string;
    };
    return [await create(slots.slots[0].startUtc, 'Перша'), await create(slots.slots[1].startUtc, 'Друга')];
  });
  await page.goto(`/admin/appointments/${created[0]}`);
  await expect(page.getByRole('heading', { name: 'Тестова Перша' })).toBeVisible();
  await expect(page.locator('section[aria-labelledby="customer-heading"] time')).toContainText('09:00');
  await page.getByRole('button', { name: 'Узгодити план' }).click();
  await page.getByLabel('Орієнтовна вартість, ₴').fill('1200');
  await page.getByLabel('Підтверджую, що огляд проведено і умови погоджено').check();
  await expect(page.getByText('За планом місце доступне на весь період.')).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.locator('section').filter({ has: page.locator('#schedule-price') }).screenshot({ path: testInfo.outputPath('mobile-plan.png') });
  const saved = page.waitForResponse(r => r.url().endsWith('/schedule') && r.request().method() === 'POST');
  await page.getByRole('button', { name: 'Підтвердити і запланувати' }).click(); expect((await saved).status()).toBe(204);
  await expect(page.getByText('Ремонт заплановано.')).toBeVisible();
  await page.getByRole('button', { name: 'Змінити вартість або терміни' }).click();
  await page.getByLabel('Узгоджена вартість, ₴').fill('1600');
  await page.getByLabel('Причина зміни вартості (якщо змінилася)').fill('Додаткові роботи');

  const otherContext = await browser.newContext({ ignoreHTTPSErrors: true });
  try {
    const other = await otherContext.newPage(); await login(other);
    const changed = await other.evaluate(async id => {
      const item = await (await fetch(`/api/appointments/${id}`)).json();
      await fetch('/api/auth/antiforgery');
      const token = decodeURIComponent(document.cookie.split('; ').find(c => c.startsWith('XSRF-TOKEN='))!.split('=')[1]);
      return (await fetch(`/api/admin/appointments/${id}/repair-plan`, { method: 'PUT', headers: { 'Content-Type': 'application/json', RequestVerificationToken: token },
        body: JSON.stringify({ version: item.version, price: 1700, priceReason: 'Додаткові роботи', plannedStartUtc: item.repairPlan.plannedStartUtc, estimatedFinishUtc: item.repairPlan.estimatedFinishUtc, inspectionPerformed: true }) })).status;
    }, created[0]);
    expect(changed).toBe(204);
    const conflict = page.waitForResponse(r => r.url().endsWith('/repair-plan') && r.request().method() === 'PUT');
    await page.getByRole('button', { name: 'Зберегти план' }).click(); expect((await conflict).status()).toBe(409);
    await expect(page.getByRole('alert').filter({ hasText: 'Замовлення або зайнятість змінилися' })).toBeVisible();
    await expect(page.getByLabel('Узгоджена вартість, ₴')).toHaveValue('1600');
    await expect(page.getByText('1 700,00 ₴', { exact: true })).toBeVisible();
    const currentPrice = await other.evaluate(async id => (await (await fetch(`/api/appointments/${id}`)).json()).repairPlan.agreedPrice, created[0]);
    expect(currentPrice).toBe(1700);
  } finally { await otherContext.close(); }

  await page.getByRole('button', { name: 'Закрити', exact: true }).click();
  await page.getByLabel('Привід зв’язку').selectOption('Agreement');
  await page.getByRole('button', { name: 'Зв’язатися з клієнтом' }).click();
  const manualContact = page.getByRole('dialog', { name: 'Зв’язатися з клієнтом' });
  await expect(manualContact).toContainText('Тестова Перша');
  const contact = page.waitForResponse(r => r.url().endsWith('/manual-contact') && r.request().method() === 'POST');
  await manualContact.getByRole('button', { name: 'Позначити, що зв’язалися' }).click(); expect((await contact).status()).toBe(204);
  await expect(page.getByRole('heading', { name: 'Історія зв’язку' })).toBeVisible();
  await expect(page.getByText('E2E test').first()).toBeVisible();
  for (const label of ['У роботі', 'Очікує оплати']) {
    const response = page.waitForResponse(r => r.url().endsWith('/transition') && r.request().method() === 'POST');
    await page.getByRole('button', { name: `Перевести: ${label}` }).click();
    await page.getByRole('dialog').getByRole('button', { name: 'Змінити етап' }).click();
    expect((await response).status()).toBe(204);
    await expect(page.getByText('Статус замовлення оновлено.')).toBeVisible();
  }
  await page.getByLabel('Оплату отримано').check();
  await page.getByRole('button', { name: 'Перевести: Завершено' }).click();
  await expect(page.getByRole('alert').filter({ hasText: 'Підтвердіть оплату і передачу' })).toBeVisible();
  await page.getByLabel('Автомобіль передано клієнту').check();
  const completion = page.waitForResponse(r => r.url().endsWith('/transition') && r.request().method() === 'POST');
  await page.getByRole('button', { name: 'Перевести: Завершено' }).click(); expect((await completion).status()).toBe(204);
  await expect(page.getByRole('heading', { name: 'Історія підтверджень оплати та видачі' })).toBeVisible();
  await expect(page.getByLabel('Дії із замовленням')).toHaveCount(0);
  await expect(page.locator('main form')).toHaveCount(0);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);

  await page.goto(`/admin/appointments/${created[1]}`);
  const menu = page.getByLabel('Дії із замовленням'); await menu.focus(); await page.keyboard.press('Enter');
  await page.getByRole('button', { name: 'Скасувати', exact: true }).click();
  await expect(page.getByLabel('Причина скасування')).toBeFocused();
  await page.getByLabel('Причина скасування').fill('Домовились із клієнтом');
  const cancellation = page.waitForResponse(r => r.url().endsWith('/cancel') && r.request().method() === 'POST');
  await page.getByRole('dialog').getByRole('button', { name: 'Підтвердити' }).click(); expect((await cancellation).status()).toBe(204);
  await expect(page.getByText('Причина: Домовились із клієнтом')).toBeVisible();
  await expect(page.getByLabel('Дії із замовленням')).toHaveCount(0);
});
