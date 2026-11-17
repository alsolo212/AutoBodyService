import { expect, Page, test } from '@playwright/test';

const appointmentId = '00000000-0000-0000-0000-000000000001';

async function setup(page: Page, role: 'User' | 'Admin' | 'SuperAdmin' = 'SuperAdmin', mustChangePassword = false) {
  let signedIn = true;
  let currentRole = role;
  let logoutAttempts = 0;
  let failLogout = false;
  await page.route('**/api/**', route => {
    const url = new URL(route.request().url());
    if (url.pathname === '/api/auth/session') return signedIn
      ? route.fulfill({ json: { name: 'Тест', phone: '+380000000000', roles: [currentRole], mustChangePassword } })
      : route.fulfill({ status: 401 });
    if (url.pathname === '/api/auth/antiforgery') return route.fulfill({ status: 204 });
    if (url.pathname === '/api/auth/logout') {
      logoutAttempts++;
      if (failLogout) return route.fulfill({ status: 500 });
      signedIn = false;
      return route.fulfill({ status: 204 });
    }
    if (url.pathname === '/api/health') return route.fulfill({ json: { status: 'Healthy' } });
    if (url.pathname === '/api/profile') return route.fulfill({ json: { name: 'Тест', phone: '+380000000000' } });
    if (['/api/notifications', '/api/appointments/my', '/api/admin/employees', '/api/admin/prices', '/api/admin/users', '/api/admin/assignments', '/api/prices'].includes(url.pathname)) return route.fulfill({ json: [] });
    return route.fulfill({ status: 503 });
  });
  return { attempts: () => logoutAttempts, fail: (value: boolean) => { failLogout = value; }, role: (value: typeof currentRole) => { currentRole = value; } };
}

test('admin pages retain navigation and highlight the current section', async ({ page }) => {
  await setup(page);
  const pages = [
    ['/admin', 'Огляд'], ['/admin/appointments', 'Замовлення'],
    [`/admin/appointments/${appointmentId}`, 'Замовлення'], ['/admin/assignments', 'Розподіл робіт'],
    ['/admin/inspection-calendar', 'Календар оглядів'], ['/admin/employees', 'Працівники'],
    ['/admin/prices', 'Послуги та ціни'], ['/admin/users', 'Користувачі'],
    ['/admin/reports', 'Контроль та звітність'], ['/admin/notifications', 'Сповіщення'], ['/admin/profile', 'Мій профіль'],
  ];
  for (const [path, label] of pages) {
    await page.goto(path);
    const nav = page.locator('aside.sidebar').getByRole('navigation', { name: 'Навігація панелі керування' });
    await expect(nav).toBeVisible();
    await expect(nav.getByRole('link', { name: label, exact: true })).toHaveAttribute('aria-current', 'page');
    await expect(nav.locator('[aria-current="page"]')).toHaveCount(1);
    await expect(nav.getByRole('link', { name: 'Замовлення' })).toBeVisible();
    await expect(nav.getByRole('link', { name: 'Календар оглядів' })).toBeVisible();
    if (path.endsWith(appointmentId)) {
      const breadcrumbs = page.getByRole('navigation', { name: 'Шлях сторінки' });
      await expect(breadcrumbs.getByRole('link', { name: 'Замовлення' })).toHaveAttribute('href', '/admin/appointments');
      await expect(breadcrumbs.getByText('Деталі замовлення')).toBeVisible();
    }
  }
});

test('admin navigation respects report permissions', async ({ page }) => {
  await setup(page, 'Admin');
  await page.goto('/admin/employees');
  const nav = page.locator('aside.sidebar');
  await expect(nav.getByRole('link', { name: 'Працівники' })).toHaveAttribute('aria-current', 'page');
  await expect(nav.getByRole('link', { name: 'Контроль та звітність' })).toHaveCount(0);
  await page.goto('/admin/reports');
  await expect(page).toHaveURL(/\/admin$/);
});

test('reports disappear when the current session loses SuperAdmin access', async ({ page }) => {
  const state = await setup(page);
  await page.goto('/admin/reports');
  await expect(page.getByRole('heading', { name: 'Контроль та звітність' })).toBeVisible();
  await expect(page.locator('aside.sidebar').getByRole('link', { name: 'Контроль та звітність' })).toBeVisible();
  state.role('Admin');
  await page.evaluate(() => window.dispatchEvent(new Event('online')));
  await expect(page.getByRole('alert')).toContainText('Доступ до звітності обмежено.');
  await expect(page.getByRole('heading', { name: 'Контроль та звітність' })).toHaveCount(0);
  await expect(page.locator('aside.sidebar').getByRole('link', { name: 'Контроль та звітність' })).toHaveCount(0);
});

test('mobile menus open, close with Escape, and navigate to a selected section', async ({ page }) => {
  await setup(page, 'Admin');
  await page.setViewportSize({ width: 320, height: 740 });
  await page.goto('/admin');
  const open = page.getByRole('button', { name: 'Відкрити меню' });
  await open.click();
  let menu = page.getByRole('dialog', { name: 'Меню' });
  await expect(menu).toBeVisible(); await expect(open).toHaveAttribute('aria-expanded', 'true');
  await page.keyboard.press('Escape'); await expect(menu).not.toBeVisible(); await expect(open).toBeFocused();
  await open.click();
  await menu.getByRole('link', { name: 'Розподіл робіт' }).click();
  await expect(page).toHaveURL(/\/admin\/assignments$/);
  await expect(page.getByRole('dialog', { name: 'Меню' })).not.toBeVisible();
  await page.getByRole('button', { name: 'Відкрити меню' }).click();
  menu = page.getByRole('dialog', { name: 'Меню' });
  await expect(menu.getByRole('link', { name: 'Розподіл робіт' })).toHaveAttribute('aria-current', 'page');
  await menu.getByRole('link', { name: 'Особистий кабінет' }).click();
  await expect(page).toHaveURL(/\/account\/appointments$/);
  await page.getByRole('button', { name: 'Відкрити меню' }).click();
  menu = page.getByRole('dialog', { name: 'Меню' });
  await expect(menu.getByRole('link', { name: 'Мої записи' })).toHaveAttribute('aria-current', 'page');
  await menu.getByRole('link', { name: 'Запис на огляд' }).click();
  await expect(page).toHaveURL(/\/account\/book$/);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
});

test('mobile logout confirmation can be dismissed without closing the menu or ending the session', async ({ page }) => {
  const state = await setup(page, 'User');
  await page.setViewportSize({ width: 320, height: 740 });
  await page.goto('/account/appointments');
  await page.getByRole('button', { name: 'Відкрити меню' }).click();
  const menu = page.getByRole('dialog', { name: 'Меню' });
  await menu.getByRole('button', { name: 'Вийти', exact: true }).click();
  const confirm = page.getByRole('dialog', { name: 'Вийти з облікового запису?' });
  await expect(confirm).toBeVisible();
  await confirm.getByRole('button', { name: 'Залишитися' }).click();
  await expect(menu).toBeVisible();
  await expect(menu.getByRole('button', { name: 'Вийти', exact: true })).toBeFocused();
  expect(state.attempts()).toBe(0);
  await menu.getByRole('link', { name: 'Запис на огляд' }).click();
  await expect(page).toHaveURL(/\/account\/book$/);
  await page.getByRole('button', { name: 'Відкрити меню' }).click();
  await page.getByRole('dialog', { name: 'Меню' }).getByRole('button', { name: 'Вийти', exact: true }).click();
  await page.getByRole('dialog', { name: 'Вийти з облікового запису?' }).getByRole('button', { name: 'Вийти', exact: true }).click();
  await expect(page).toHaveURL(/\/login$/);
  expect(state.attempts()).toBe(1);
});

test('an open mobile menu remains dismissible after resizing to desktop', async ({ page }) => {
  await setup(page, 'Admin');
  await page.setViewportSize({ width: 320, height: 740 });
  await page.goto('/admin');
  await page.getByRole('button', { name: 'Відкрити меню' }).click();
  await page.setViewportSize({ width: 1024, height: 740 });
  const menu = page.getByRole('dialog', { name: 'Меню' });
  await expect(menu).toBeVisible();
  await menu.getByRole('button', { name: 'Закрити меню' }).click();
  await expect(menu).not.toBeVisible();
  await page.locator('aside.sidebar').getByRole('link', { name: 'Працівники' }).click();
  await expect(page).toHaveURL(/\/admin\/employees$/);
});

test('public mobile menu closes after following a page section link', async ({ page }) => {
  await setup(page, 'User');
  await page.setViewportSize({ width: 320, height: 740 });
  await page.goto('/');
  await page.getByRole('button', { name: 'Відкрити меню' }).click();
  const menu = page.getByRole('dialog', { name: 'Меню' });
  await menu.getByRole('link', { name: 'Контакти' }).click();
  await expect(menu).not.toBeVisible();
  await expect(page).toHaveURL(/#contacts$/);
  await expect(page.locator('#contacts')).toBeInViewport();
});

test('skip links focus current page content without leaving the route', async ({ page }) => {
  await setup(page);
  const cases = [
    ['/', '#main'], ['/account/appointments', '#account-main'], [`/account/appointments/${appointmentId}`, '#account-main'],
    ['/account/notifications', '#account-main'], ['/account/book', '#account-main'],
    ['/account/book/success', '#account-main'], ['/account/profile', '#account-main'], ['/admin', '#workspace-main'],
    ['/admin/appointments?status=active', '#workspace-main'], ['/admin/inspection-calendar', '#workspace-main'],
    [`/admin/appointments/${appointmentId}`, '#workspace-main'], ['/admin/profile', '#workspace-main'],
  ];
  for (const [path, target] of cases) {
    await page.goto(path);
    await expect(page.locator(target)).toBeAttached();
    await page.keyboard.press('Tab');
    await expect(page.getByRole('link', { name: 'Перейти до вмісту' })).toBeFocused();
    await page.keyboard.press('Enter');
    await expect(page.locator(target)).toBeFocused();
    const expected = new URL(path, page.url());
    expect(new URL(page.url()).pathname).toBe(expected.pathname);
    expect(new URL(page.url()).search).toBe(expected.search);
  }
});

test('cancelling logout keeps an unsaved profile edit on the current page', async ({ page }) => {
  const state = await setup(page, 'User');
  await page.goto('/account/profile');
  const name = page.getByLabel('Ім’я');
  await expect(name).toHaveValue('Тест');
  await name.fill('Нове ім’я');
  await page.getByRole('button', { name: 'Вийти', exact: true }).click();
  await page.getByRole('dialog', { name: 'Вийти з облікового запису?' }).getByRole('button', { name: 'Залишитися' }).click();
  await expect(name).toHaveValue('Нове ім’я');
  await expect(page).toHaveURL(/\/account\/profile$/);
  expect(state.attempts()).toBe(0);
});

test('the temporary password page also confirms logout', async ({ page }) => {
  const state = await setup(page, 'User', true);
  await page.goto('/account/change-temporary-password');
  await page.getByRole('button', { name: 'Вийти', exact: true }).click();
  const confirm = page.getByRole('dialog', { name: 'Вийти з облікового запису?' });
  await confirm.getByRole('button', { name: 'Вийти', exact: true }).click();
  await expect(page).toHaveURL(/\/login$/);
  expect(state.attempts()).toBe(1);
});

test('logout requires confirmation and keeps the session when cancelled or the request fails', async ({ page }) => {
  const state = await setup(page, 'User');
  await page.goto('/account/appointments');
  await page.getByRole('button', { name: 'Вийти', exact: true }).click();
  let dialog = page.getByRole('dialog', { name: 'Вийти з облікового запису?' });
  await expect(dialog.getByRole('button', { name: 'Залишитися' })).toBeFocused();
  await dialog.getByRole('button', { name: 'Залишитися' }).click();
  expect(state.attempts()).toBe(0); await expect(page).toHaveURL(/\/account\/appointments$/);
  await page.getByRole('button', { name: 'Вийти', exact: true }).click();
  await page.keyboard.press('Escape'); expect(state.attempts()).toBe(0);
  state.fail(true);
  await page.getByRole('button', { name: 'Вийти', exact: true }).click();
  dialog = page.getByRole('dialog', { name: 'Вийти з облікового запису?' });
  await dialog.getByRole('button', { name: 'Вийти', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('Не вдалося вийти');
  expect(state.attempts()).toBe(1); await expect(page).toHaveURL(/\/account\/appointments$/);
  state.fail(false);
  await page.getByRole('button', { name: 'Вийти', exact: true }).click();
  await page.getByRole('dialog', { name: 'Вийти з облікового запису?' }).getByRole('button', { name: 'Вийти', exact: true }).click();
  await expect(page).toHaveURL(/\/login$/); expect(state.attempts()).toBe(2);
});
