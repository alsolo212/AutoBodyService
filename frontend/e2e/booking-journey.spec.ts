import { expect, Page, test } from '@playwright/test';

const password = 'Example-password-123!';
async function setup(page: Page, options: { role?: 'User' | 'Admin'; signedIn?: boolean; temporary?: boolean; available?: boolean } = {}) {
  let signedIn = options.signedIn ?? false;
  let temporary = options.temporary ?? false;
  let createdBody: unknown = null;
  const requestedMonths: string[] = [];
  await page.clock.setFixedTime(new Date('2026-11-17T10:00:00Z'));
  await page.route('**/api/**', route => {
    const url = new URL(route.request().url());
    if (url.pathname === '/api/auth/session') return signedIn ? route.fulfill({ json: { name: 'Тест', phone: '+380000000000', roles: [options.role ?? 'User'], mustChangePassword: temporary } }) : route.fulfill({ status: 401 });
    if (url.pathname === '/api/auth/antiforgery') return route.fulfill({ status: 204 });
    if (url.pathname === '/api/auth/register') return route.fulfill({ status: 202 });
    if (url.pathname === '/api/auth/login') { signedIn = true; return route.fulfill({ status: 204 }); }
    if (url.pathname === '/api/auth/change-password') { temporary = false; return route.fulfill({ status: 204 }); }
    if (url.pathname === '/api/inspection-slots/month') {
      const month = `${url.searchParams.get('year')}-${url.searchParams.get('month')}`; requestedMonths.push(month);
      const count = new Date(Date.UTC(Number(url.searchParams.get('year')), Number(url.searchParams.get('month')), 0)).getUTCDate();
      return route.fulfill({ json: { days: Array.from({ length: count }, (_, i) => {
        const date = `${month}-${String(i + 1).padStart(2, '0')}`;
        return { date, available: options.available !== false && date === '2026-11-18' };
      }) } });
    }
    if (url.pathname === '/api/inspection-slots') return route.fulfill({ json: { slots: [{ startUtc: '2026-11-18T08:00:00Z', endUtc: '2026-11-18T08:30:00Z' }] } });
    if (url.pathname === '/api/public/contacts') return route.fulfill({ json: { phone: '+380000000000', address: 'Тестова адреса', workingHours: null } });
    if (url.pathname === '/api/admin/dashboard') return route.fulfill({ json: { counts: { awaitingInspection: 0, scheduled: 0, inProgress: 0, awaitingPayment: 0 }, upcomingInspections: [], newAppointments: [], cancellations: [], contactTasks: [] } });
    if (url.pathname === '/api/admin/appointments/guest' || url.pathname === '/api/appointments') {
      createdBody = route.request().postDataJSON();
      return route.fulfill({ status: 201, json: { id: '00000000-0000-0000-0000-000000000001', inspectionStartUtc: '2026-11-18T08:00:00Z', status: 'AwaitingInspection' } });
    }
    if (url.pathname === '/api/health') return route.fulfill({ json: { status: 'Healthy' } });
    return route.fulfill({ json: [] });
  });
  return { body: () => createdBody, requestedMonths };
}

test('login returns to booking after mandatory password change', async ({ page }) => {
  await setup(page, { temporary: true }); await page.goto('/account/book');
  await expect(page).toHaveURL(/\/login\?returnTo=/);
  await page.getByLabel('Номер телефону').fill('0000000000'); await page.getByLabel('Пароль', { exact: true }).fill(password);
  await page.getByRole('button', { name: 'Увійти' }).click();
  await expect(page).toHaveURL(/\/account\/change-temporary-password\?returnTo=/);
  await page.getByLabel('Тимчасовий пароль').fill(password);
  await page.getByLabel('Новий пароль', { exact: true }).fill('Another-password-123!');
  await page.getByLabel('Повторіть новий пароль').fill('Another-password-123!');
  await page.getByRole('button', { name: 'Змінити пароль' }).click();
  await expect(page).toHaveURL(/\/account\/book$/);
});

test('registration retains booking destination and rejects arbitrary return URLs', async ({ page }) => {
  await setup(page); await page.goto('/account/book');
  await page.getByRole('link', { name: 'Зареєструватися' }).click();
  await page.getByLabel('Ім’я').fill('Тест'); await page.getByLabel('Номер телефону').fill('0000000000');
  await page.getByLabel('Пароль', { exact: true }).fill(password); await page.getByLabel('Повторіть пароль').fill(password);
  await page.getByRole('button', { name: 'Зареєструватися' }).click();
  await expect(page).toHaveURL(/\/account\/book$/);
  await page.goto('/login?returnTo=https%3A%2F%2Fevil.example');
  await expect(page).toHaveURL(/\/account\/appointments$/);
});

test('registration fallback keeps booking destination for the subsequent login', async ({ page }) => {
  await setup(page); let fail = true;
  await page.route('**/api/auth/login', route => fail ? route.fulfill({ status: 401 }) : route.fallback());
  await page.goto('/account/book'); await page.getByRole('link', { name: 'Зареєструватися' }).click();
  await page.getByLabel('Ім’я').fill('Тест'); await page.getByLabel('Номер телефону').fill('0000000000');
  await page.getByLabel('Пароль', { exact: true }).fill(password); await page.getByLabel('Повторіть пароль').fill(password);
  await page.getByRole('button', { name: 'Зареєструватися' }).click();
  await page.getByRole('link', { name: /Перейти до входу/ }).click();
  await expect(page).toHaveURL(/\/login\?returnTo=/); fail = false;
  await page.getByLabel('Номер телефону').fill('0000000000'); await page.getByLabel('Пароль', { exact: true }).fill(password);
  await page.getByRole('button', { name: 'Увійти' }).click(); await expect(page).toHaveURL(/\/account\/book$/);
});

test('admin can book a guest without VIN and focus follows steps and validation', async ({ page }) => {
  const state = await setup(page, { signedIn: true, role: 'Admin' });
  await page.setViewportSize({ width: 320, height: 740 }); await page.goto('/admin');
  await page.getByRole('link', { name: 'Записати клієнта' }).click();
  await page.getByRole('button', { name: 'Далі' }).click();
  await expect(page.getByLabel('Ім’я клієнта')).toBeFocused();
  await page.getByLabel('Ім’я клієнта').fill('Клієнт'); await page.getByLabel('Телефон клієнта').fill('0000000000');
  await page.getByRole('button', { name: 'Далі' }).click(); await expect(page.locator('[data-slot-error]')).toBeFocused();
  await page.getByLabel('10:00', { exact: true }).check(); await page.getByRole('button', { name: 'Далі' }).click();
  await expect(page.getByRole('heading', { name: 'Дані автомобіля' })).toBeFocused();
  await page.getByRole('button', { name: 'Далі' }).click(); await expect(page.getByLabel('Марка')).toBeFocused();
  await page.getByLabel('Марка').fill('Марка'); await page.getByLabel('Модель').fill('Модель'); await page.getByRole('button', { name: 'Далі' }).click();
  await expect(page.getByRole('heading', { name: 'Опис проблеми' })).toBeFocused();
  await page.getByLabel('Що потрібно оглянути?').fill('Пошкоджено двері'); await page.getByRole('button', { name: 'Далі' }).click();
  await expect(page.getByRole('heading', { name: 'Перевірте дані' })).toBeFocused();
  await expect(page.locator('.summary dt', { hasText: 'VIN' })).toHaveCount(0);
  await page.getByRole('button', { name: 'Підтвердити запис' }).click();
  await expect(page.getByRole('heading', { name: 'Запис для клієнта створено' })).toBeFocused();
  expect(state.body()).toMatchObject({ customerName: 'Клієнт', customerPhone: '0000000000', vin: null });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
});

test('empty availability stays within current and next month and shows workshop contacts', async ({ page }) => {
  const state = await setup(page, { signedIn: true, available: false }); await page.goto('/account/book');
  await expect(page.getByText('До кінця наступного місяця вільних дат немає. Зв’яжіться з майстернею.')).toBeVisible();
  await expect(page.getByRole('link', { name: '+380000000000' })).toBeVisible();
  await expect(page.getByText('листопад 2026')).toBeVisible();
  expect(state.requestedMonths).toEqual(['2026-11', '2026-12', '2026-11']);
  await page.getByRole('button', { name: 'Наступний місяць' }).click();
  await expect(page.getByRole('button', { name: 'Наступний місяць' })).toBeDisabled();
});
