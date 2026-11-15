import { execFileSync } from 'node:child_process';
import { expect, Page, test } from '@playwright/test';
import { login, register } from './admin-users-helpers';

async function prepareBooking(page: Page, make: string) {
  await page.goto('/account/book');
  await page.getByLabel('Дата', { exact: true }).fill('2099-10-25');
  await expect(page.getByLabel('03:00 (UTC+03:00)', { exact: true })).toBeVisible();
  await expect(page.getByLabel('03:00 (UTC+02:00)', { exact: true })).toBeVisible();
  await page.getByLabel('09:00', { exact: true }).check();
  await page.getByRole('button', { name: 'Далі' }).click();
  await page.getByLabel('VIN').fill('1HGCM82633A004352');
  await page.getByLabel('Марка').fill(make);
  await page.getByLabel('Модель').fill('Тест');
  await page.getByRole('button', { name: 'Далі' }).click();
  await page.getByLabel('Що потрібно оглянути?').fill('Пошкоджено бампер');
  await page.getByRole('button', { name: 'Далі' }).click();
  await expect(page.getByRole('heading', { name: 'Перевірте дані' })).toBeVisible();
}

test('two devices compete for one autumn slot and an elapsed inspection cannot be cancelled', async ({ page: admin, browser }) => {
  test.skip(process.env['E2E_LIVE_API'] !== '1', 'Requires isolated API and PostgreSQL.');
  test.setTimeout(90_000);
  await login(admin, process.env['E2E_ADMIN_PHONE']!, process.env['E2E_ADMIN_PASSWORD']!);
  await expect(admin).toHaveURL(/\/admin$/);
  const setup = await admin.evaluate(async () => {
    const schedule = await (await fetch('/api/admin/inspection-schedule')).json();
    await fetch('/api/auth/antiforgery');
    const token = decodeURIComponent(document.cookie.split('; ').find(c => c.startsWith('XSRF-TOKEN='))!.split('=')[1]);
    const intervals = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday']
      .flatMap(dayOfWeek => [{ dayOfWeek, start: '03:00', end: '04:00' }, { dayOfWeek, start: '09:00', end: '10:00' }]);
    const status = (await fetch('/api/admin/inspection-schedule', { method: 'PUT', headers: { 'Content-Type': 'application/json', RequestVerificationToken: token }, body: JSON.stringify({ version: schedule.version, intervals }) })).status;
    const before = await (await fetch('/api/inspection-slots?date=2099-10-24')).json();
    const after = await (await fetch('/api/inspection-slots?date=2099-10-25')).json();
    return { status, before: before.slots.map((slot: { startUtc: string }) => slot.startUtc), after: after.slots.map((slot: { startUtc: string }) => slot.startUtc) };
  });
  expect(setup.status).toBe(204);
  expect(setup.before.map((value: string) => new Date(value).toISOString())).toContain('2099-10-24T06:00:00.000Z');
  expect(setup.after.map((value: string) => new Date(value).toISOString())).toEqual(expect.arrayContaining([
    '2099-10-25T00:00:00.000Z', '2099-10-25T01:00:00.000Z', '2099-10-25T07:00:00.000Z',
  ]));
  const desktop = await register(browser, 'Перший клієнт', '0000000081');
  const mobile = await register(browser, 'Другий клієнт', '0000000082');
  try {
    await mobile.page.setViewportSize({ width: 320, height: 720 });
    await Promise.all([prepareBooking(desktop.page, 'Перший'), prepareBooking(mobile.page, 'Другий')]);
    const responses = [desktop.page, mobile.page].map(p => p.waitForResponse(r => r.url().endsWith('/api/appointments') && r.request().method() === 'POST'));
    await Promise.all([desktop.page, mobile.page].map(p => p.getByRole('button', { name: 'Підтвердити запис' }).click()));
    const results = await Promise.all(responses);
    expect(results.map(r => r.status()).sort()).toEqual([201, 409]);
    const winner = results[0].status() === 201 ? desktop.page : mobile.page;
    const loser = winner === desktop.page ? mobile.page : desktop.page;
    const id = (await results.find(r => r.status() === 201)!.json()).id as string;
    expect(id).toMatch(/^[0-9a-f-]{36}$/i);
    await expect(winner).toHaveURL(/\/account\/book\/success$/);
    await expect(winner.locator('main .profile-intro')).toContainText('25 жовтня 2099');
    await expect(winner.locator('main .profile-intro')).toContainText('09:00');
    await expect(loser.getByRole('alert').filter({ hasText: 'Цей час уже недоступний' })).toContainText('дані автомобіля та опис збережено');
    await loser.getByLabel('09:30', { exact: true }).check();
    await loser.getByRole('button', { name: 'Далі' }).click();
    await expect(loser.getByLabel('VIN')).toHaveValue('1HGCM82633A004352');
    await expect(loser.getByLabel('Марка')).toHaveValue(winner === desktop.page ? 'Другий' : 'Перший');
    await loser.getByRole('button', { name: 'Далі' }).click();
    await expect(loser.getByLabel('Що потрібно оглянути?')).toHaveValue('Пошкоджено бампер');
    const lists = await Promise.all([desktop.page, mobile.page].map(p => p.evaluate(async () => (await (await fetch('/api/appointments/my')).json()).length)));
    expect(lists.sort()).toEqual([0, 1]);
    const container = process.env['PROFILE_VERIFY_CONTAINER'] ?? '';
    if (!/^autobody-ui-verify-[0-9a-f]+$/.test(container)) throw new Error('Expected disposable E2E database');
    // Move only this synthetic order into the past to test the server's actual clock boundary.
    execFileSync('docker', ['exec', container, 'psql', '-U', 'auth_test', '-d', 'auth_test', '-v', 'ON_ERROR_STOP=1', '-c',
      `UPDATE "Appointments" SET "InspectionStartUtc" = now() - interval '1 minute' WHERE "Id" = '${id}'`], { stdio: 'ignore' });
    await winner.goto(`/account/appointments/${id}`);
    await expect(winner.getByLabel('Дії із записом')).toHaveCount(0);
    const cancellation = await winner.evaluate(async appointmentId => {
      await fetch('/api/auth/antiforgery');
      const token = decodeURIComponent(document.cookie.split('; ').find(c => c.startsWith('XSRF-TOKEN='))!.split('=')[1]);
      return (await fetch(`/api/appointments/${appointmentId}/cancel`, { method: 'POST', headers: { RequestVerificationToken: token } })).status;
    }, id);
    expect(cancellation).toBe(409);
    await expect(winner.locator('.status')).toContainText('Очікує огляду');
  } finally { await desktop.context.close(); await mobile.context.close(); }
});
