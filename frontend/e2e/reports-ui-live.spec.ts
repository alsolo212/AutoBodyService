import { execFileSync } from 'node:child_process';
import { expect, test } from '@playwright/test';
import { login, register } from './admin-users-helpers';

function seedDisposableDatabase() {
  const container = process.env['PROFILE_VERIFY_CONTAINER'] ?? '';
  if (!/^autobody-ui-verify-[0-9a-f]+$/.test(container)) throw new Error('Missing isolated test database');
  const label = execFileSync('docker', ['inspect', '--format', '{{index .Config.Labels "autobody.auth-e2e.disposable"}}', container], { encoding: 'utf8' }).trim();
  if (label !== 'true') throw new Error('Database is not the disposable test container');
  const sql = `BEGIN;
    INSERT INTO "Appointments" ("Id", "UserId", "InspectionStartUtc", "Vin", "Make", "Model", "ProblemDescription", "Status", "CreatedAtUtc")
    SELECT v.id::uuid, u."Id", v.slot::timestamptz, '1HGCM82633A123456', v.make, 'Тест', 'Тестова проблема', v.status, '2019-01-01T00:00:00Z'::timestamptz
    FROM "AspNetUsers" u CROSS JOIN (VALUES
      ('11111111-1111-4111-8111-111111111111','2020-09-01T09:00:00Z','Вересневе',4),
      ('22222222-2222-4222-8222-222222222222','2020-09-01T10:00:00Z','Жовтневе',4),
      ('33333333-3333-4333-8333-333333333333','2020-09-01T11:00:00Z','Інше жовтневе',4),
      ('44444444-4444-4444-8444-444444444444','2020-09-01T12:00:00Z','Очікує оплати',3)
    ) AS v(id,slot,make,status) WHERE u."PhoneNormalized" = '+380000000099';
    INSERT INTO "RepairPlan" ("AppointmentId", "AgreedPrice", "Currency", "PlannedStartUtc", "EstimatedFinishUtc", "UpdatedAtUtc")
    SELECT v.id::uuid, v.price::numeric, 'UAH', '2020-09-01T09:00:00Z', '2020-09-02T09:00:00Z', '2020-09-01T08:00:00Z'
    FROM (VALUES ('11111111-1111-4111-8111-111111111111','10.10'),('22222222-2222-4222-8222-222222222222','25.25'),('33333333-3333-4333-8333-333333333333','5.50'),('44444444-4444-4444-8444-444444444444','999')) AS v(id,price);
    INSERT INTO "AppointmentStatusEvents" ("Id", "AppointmentId", "FromStatus", "ToStatus", "ActorId", "ChangedAtUtc", "Reason")
    SELECT gen_random_uuid(), v.id::uuid, 3, 4, u."Id", v.finished::timestamptz, NULL
    FROM "AspNetUsers" u CROSS JOIN (VALUES
      ('11111111-1111-4111-8111-111111111111','2020-09-30T20:59:59Z'),
      ('22222222-2222-4222-8222-222222222222','2020-09-30T21:00:00Z'),
      ('33333333-3333-4333-8333-333333333333','2020-10-15T08:00:00Z')
    ) AS v(id,finished) WHERE u."PhoneNormalized" = '+380000000099';
    INSERT INTO "PaymentHandoverConfirmations" ("AppointmentId", "ActorId", "PaidAtUtc", "HandedOverAtUtc")
    SELECT v.id::uuid, u."Id", v.finished::timestamptz, v.finished::timestamptz
    FROM "AspNetUsers" u CROSS JOIN (VALUES
      ('11111111-1111-4111-8111-111111111111','2020-09-30T20:59:59Z'),
      ('22222222-2222-4222-8222-222222222222','2020-09-30T21:00:00Z'),
      ('33333333-3333-4333-8333-333333333333','2020-10-15T08:00:00Z')
    ) AS v(id,finished) WHERE u."PhoneNormalized" = '+380000000099';
    INSERT INTO "MonthlyExpenses" ("Id", "Amount", "Description", "CreatedById", "RecordedAtUtc", "RecordedYear", "RecordedMonth", "UpdatedAtUtc")
    SELECT v.id::uuid, v.amount::numeric, v.description, u."Id", v.recorded::timestamptz, 2020, 9, NULL
    FROM "AspNetUsers" u CROSS JOIN (VALUES
      ('aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaa1','12.34','Старі матеріали','2020-09-15T08:00:00Z'),
      ('aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaa2','4.20','Стара оренда','2020-09-16T08:00:00Z')
    ) AS v(id,amount,description,recorded) WHERE u."PhoneNormalized" = '+380000000099';
    COMMIT;`;
  execFileSync('docker', ['exec', '-i', container, 'psql', '-v', 'ON_ERROR_STOP=1', '-U', 'auth_test', '-d', 'auth_test'], { input: sql, stdio: ['pipe', 'pipe', 'pipe'] });
}

test('finance tab respects Kyiv months, historical corrections and SuperAdmin access', async ({ page, browser }) => {
  test.setTimeout(120000);
  test.skip(process.env['E2E_LIVE_API'] !== '1', 'Requires isolated API and PostgreSQL');
  seedDisposableDatabase();
  await login(page, process.env['E2E_ADMIN_PHONE']!, process.env['E2E_ADMIN_PASSWORD']!);
  await expect(page).toHaveURL(/\/admin$/);
  await expect(page.getByRole('link', { name: 'Контроль та звітність' })).toBeVisible();
  await expect(page.getByText('Виручка', { exact: true })).toHaveCount(0);
  await page.getByRole('link', { name: 'Контроль та звітність' }).click();
  await expect(page).toHaveURL(/\/admin\/reports$/);
  await expect(page.getByRole('tab', { name: 'Фінансова статистика' })).toHaveAttribute('aria-selected', 'true');
  async function period(month: string, year = '2020') {
    await page.getByLabel('Місяць', { exact: true }).selectOption({ label: month });
    await page.getByLabel('Рік', { exact: true }).fill(year);
    await page.getByRole('button', { name: 'Показати' }).click();
    await expect(page.getByText('Завантажуємо фінансові дані…')).toHaveCount(0);
  }
  await period('Вересень');
  const summary = page.getByLabel('Показники місяця');
  await expect(summary).toContainText('10,10 ₴'); await expect(summary).toContainText('16,54 ₴');
  await expect(summary).toContainText('−6,44 ₴');
  const orders = page.getByRole('region', { name: 'Таблиця завершених замовлень' });
  await expect(orders).toContainText('Вересневе'); await expect(orders).not.toContainText('Жовтневе');
  const expenses = page.getByRole('region', { name: 'Таблиця витрат' });
  await expect(expenses).toContainText('Старі матеріали');
  await page.getByLabel('Сортувати витрати за').selectOption('amount');
  await page.getByLabel('Порядок витрат').selectOption('asc'); await page.getByRole('button', { name: 'Сортувати', exact: true }).click();
  await expect(page.locator('#expenses-title')).toBeVisible();
  await expect(page.getByRole('region', { name: 'Таблиця витрат' }).locator('tbody tr').first()).toContainText('Стара оренда');
  const oldRow = page.getByRole('row').filter({ hasText: 'Старі матеріали' });
  await oldRow.getByRole('button', { name: /Виправити витрату/ }).click();
  await expect(page.getByText(/залишиться за вересень 2020/)).toBeVisible();
  await page.getByLabel('Сума, ₴', { exact: true }).fill('15,67');
  await page.getByRole('button', { name: 'Зберегти виправлення' }).click();
  await expect(page.getByRole('status').filter({ hasText: 'залишається за вересень 2020' })).toBeVisible();
  await expect(summary).toContainText('19,87 ₴');
  await expect(page.getByText(/сервер запише в поточний місяць/)).toBeVisible();
  await page.getByLabel('Сума, ₴', { exact: true }).fill('7,45');
  await page.getByLabel('Опис (необов’язково)').fill('Нова поточна витрата');
  const createdResponse = page.waitForResponse(response => response.url().includes('/api/admin/reports/expenses') && response.request().method() === 'POST');
  await page.getByRole('button', { name: 'Додати витрату', exact: true }).click();
  const response = await createdResponse; expect(response.status()).toBe(201); const created = await response.json();
  expect([created.recordedYear, created.recordedMonth]).not.toEqual([2020, 9]);
  const kyivParts = new Intl.DateTimeFormat('en-GB', { timeZone: 'Europe/Kyiv', year: 'numeric', month: 'numeric' }).formatToParts(new Date(created.recordedAtUtc));
  expect(created.recordedYear).toBe(Number(kyivParts.find(part => part.type === 'year')!.value));
  expect(created.recordedMonth).toBe(Number(kyivParts.find(part => part.type === 'month')!.value));
  await expect(page.getByRole('status').filter({ hasText: 'за часом сервера' })).toBeVisible();
  await expect(page.getByRole('region', { name: 'Таблиця витрат' })).not.toContainText('Нова поточна витрата');
  await period('Жовтень');
  await expect(summary).toContainText('30,75 ₴');
  await expect(page.getByRole('region', { name: 'Таблиця завершених замовлень' })).toContainText('Жовтневе');
  await expect(page.getByText('За цей місяць витрат немає.')).toBeVisible();
  await page.getByLabel('Сортувати за', { exact: true }).selectOption('amount');
  await page.getByLabel('Порядок', { exact: true }).selectOption('asc');
  await page.getByRole('button', { name: 'Застосувати фільтри' }).click();
  await expect(page.getByRole('region', { name: 'Таблиця завершених замовлень' }).locator('tbody tr').first()).toContainText('Інше жовтневе');
  await page.getByLabel('Сума від, ₴').fill('20'); await page.getByRole('button', { name: 'Застосувати фільтри' }).click();
  await expect(page.getByRole('region', { name: 'Таблиця завершених замовлень' }).locator('tbody tr')).toHaveCount(1);
  await expect(summary).toContainText('30,75 ₴');
  const cardLink = page.getByRole('link', { name: 'Жовтневе Тест', exact: true });
  await cardLink.click(); await expect(page).toHaveURL(/\/admin\/appointments\/22222222-2222-4222-8222-222222222222$/);
  await page.goBack(); await expect(page).toHaveURL(/\/admin\/reports$/);
  await period('Січень', '2018');
  await expect(summary).toContainText('0,00 ₴'); await expect(page.getByText('За цей місяць витрат немає.')).toBeVisible();
  await page.getByLabel('Місяць', { exact: true }).selectOption({ index: created.recordedMonth - 1 });
  await page.getByLabel('Рік', { exact: true }).fill(String(created.recordedYear)); await page.getByRole('button', { name: 'Показати' }).click();
  await expect(page.getByRole('region', { name: 'Таблиця витрат' })).toContainText('Нова поточна витрата');
  await page.setViewportSize({ width: 375, height: 812 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  const mobileRow = page.getByRole('row').filter({ hasText: 'Нова поточна витрата' });
  await expect(mobileRow).toContainText('7,45 ₴');
  await mobileRow.getByRole('button', { name: /Виправити витрату/ }).focus(); await page.keyboard.press('Enter');
  await expect(page.getByLabel('Сума, ₴', { exact: true })).toBeFocused();
  await page.screenshot({ path: '/tmp/autobody-reports-mobile.png', fullPage: true });
  const admin = await register(browser, 'Тестовий адміністратор', '0000000066');
  const user = await register(browser, 'Тестовий клієнт', '0000000067');
  try {
    const status = await page.evaluate(async () => {
      const account = (await (await fetch('/api/admin/users?search=0000000066')).json())[0];
      await fetch('/api/auth/antiforgery');
      const token = decodeURIComponent(document.cookie.split('; ').find(c => c.startsWith('XSRF-TOKEN='))!.split('=')[1]);
      return (await fetch(`/api/admin/users/${account.id}/roles`, { method: 'PUT', headers: { 'Content-Type': 'application/json', RequestVerificationToken: token }, body: JSON.stringify({ version: account.version, isAdmin: true }) })).status;
    });
    expect(status).toBe(204);
    await login(admin.page, '0000000066', 'Example-password-123!'); await expect(admin.page).toHaveURL(/\/admin$/);
    await expect(admin.page.getByRole('link', { name: 'Контроль та звітність' })).toHaveCount(0);
    await expect(admin.page.getByText('Виручка', { exact: true })).toHaveCount(0);
    await expect(admin.page.getByText('Фінансовий результат', { exact: true })).toHaveCount(0);
    expect(await admin.page.evaluate(async () => Promise.all(['/api/admin/reports/finance?year=2020&month=9', '/api/admin/reports/expenses?year=2020&month=9'].map(async url => (await fetch(url)).status)))).toEqual([403, 403]);
    await admin.page.goto('/admin/reports'); await expect(admin.page).toHaveURL(/\/admin$/);
    expect(await user.page.evaluate(async () => Promise.all(['/api/admin/reports/finance?year=2020&month=9', '/api/admin/reports/expenses?year=2020&month=9'].map(async url => (await fetch(url)).status)))).toEqual([403, 403]);
    await user.page.goto('/admin/reports'); await expect(user.page).toHaveURL(/\/account\/appointments$/);
  } finally { await admin.context.close(); await user.context.close(); }
});
