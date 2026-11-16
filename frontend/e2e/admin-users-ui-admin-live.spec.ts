import { expect, test } from '@playwright/test';
import { execFileSync } from 'node:child_process';
import { changeTemporaryPassword, confirm, cookie, find, hub, login, register } from './admin-users-helpers';

test('Admin resets a User, cannot manage Admin and shows the temporary secret once', async ({ page, browser, baseURL, request }) => {
  test.setTimeout(90000);
  test.skip(process.env['E2E_LIVE_API'] !== '1', 'Requires isolated API and PostgreSQL');
  await login(page, process.env['E2E_ADMIN_PHONE']!, process.env['E2E_ADMIN_PASSWORD']!);
  await expect(page).toHaveURL(/\/admin$/);
  const operator = await register(browser, 'Оператор тест', '0000000061');
  const customer = await register(browser, 'Клієнт тест', '0000000062');
  try {
    const consoleMessages: string[] = [];
    operator.page.on('console', message => consoleMessages.push(message.text()));
    const assigned = await page.evaluate(async () => {
      const account = (await (await fetch('/api/admin/users?search=0000000061')).json())[0];
      await fetch('/api/auth/antiforgery');
      const token = decodeURIComponent(document.cookie.split('; ').find(c => c.startsWith('XSRF-TOKEN='))!.split('=')[1]);
      const response = await fetch(`/api/admin/users/${account.id}/roles`, { method: 'PUT', headers: { 'Content-Type': 'application/json', RequestVerificationToken: token }, body: JSON.stringify({ version: account.version, isAdmin: true }) });
      return { status: response.status, id: account.id, version: account.version };
    });
    expect(assigned.status).toBe(204);
    await login(operator.page, '0000000061', 'Example-password-123!');
    await expect(operator.page).toHaveURL(/\/admin$/);
    const forbidden = await operator.page.evaluate(async target => {
      await fetch('/api/auth/antiforgery');
      const token = decodeURIComponent(document.cookie.split('; ').find(c => c.startsWith('XSRF-TOKEN='))!.split('=')[1]);
      const headers = { 'Content-Type': 'application/json', RequestVerificationToken: token };
      const role = await fetch(`/api/admin/users/${target.id}/roles`, { method: 'PUT', headers, body: JSON.stringify({ version: target.version, isAdmin: false }) });
      const block = await fetch(`/api/admin/users/${target.id}/block`, { method: 'PATCH', headers, body: JSON.stringify({ version: target.version, isBlocked: true }) });
      const reset = await fetch(`/api/admin/users/${target.id}/reset-password`, { method: 'POST', headers, body: JSON.stringify({ version: target.version }) });
      return { role: role.status, block: block.status, reset: reset.status };
    }, assigned);
    expect(forbidden).toEqual({ role: 403, block: 403, reset: 404 });
    const target = await operator.page.evaluate(async () => (await (await fetch('/api/admin/users?search=0000000062')).json())[0] as { id: string; version: string });
    const userForbidden = await customer.page.evaluate(async account => {
      await fetch('/api/auth/antiforgery');
      const token = decodeURIComponent(document.cookie.split('; ').find(c => c.startsWith('XSRF-TOKEN='))!.split('=')[1]);
      const headers = { 'Content-Type': 'application/json', RequestVerificationToken: token };
      const reset = await fetch(`/api/admin/users/${account.id}/reset-password`, { method: 'POST', headers, body: JSON.stringify({ version: account.version }) });
      const role = await fetch(`/api/admin/users/${account.id}/roles`, { method: 'PUT', headers, body: JSON.stringify({ version: account.version, isAdmin: true }) });
      const block = await fetch(`/api/admin/users/${account.id}/block`, { method: 'PATCH', headers, body: JSON.stringify({ version: account.version, isBlocked: true }) });
      return [reset.status, role.status, block.status];
    }, target);
    expect(userForbidden).toEqual([403, 403, 403]);
    const container = process.env['PROFILE_VERIFY_CONTAINER'] ?? '';
    if (!/^autobody-ui-verify-[a-f0-9]+$/.test(container) || !/^[a-f0-9-]{36}$/i.test(target.id)) throw new Error('Expected disposable database and account ID');
    const label = execFileSync('docker', ['inspect', '-f', '{{ index .Config.Labels "autobody.auth-e2e.disposable" }}', container], { encoding: 'utf8' }).trim();
    if (label !== 'true') throw new Error('Database is not marked disposable');
    const unchanged = execFileSync('docker', ['exec', container, 'psql', '-U', 'auth_test', '-d', 'auth_test', '-v', 'ON_ERROR_STOP=1', '-At', '-c',
      `SELECT count(*) FROM "AspNetUsers" u WHERE u."Id" = '${target.id}' AND NOT u."IsBlocked" AND NOT u."MustChangePassword" AND (SELECT count(*) FROM "AspNetUserRoles" r WHERE r."UserId" = u."Id" AND r."RoleId" = 'role-user') = 1 AND (SELECT count(*) FROM "AspNetUserRoles" r WHERE r."UserId" = u."Id") = 1 AND NOT EXISTS (SELECT 1 FROM "AdminAuditEvents" a WHERE a."ObjectId" = u."Id")`], { encoding: 'utf8' }).trim();
    expect(unchanged).toBe('1');
    const oldCookie = await cookie(customer.context, baseURL!); const connection = await hub(customer.context, baseURL!);
    await operator.page.goto('/admin/users');
    await find(operator.page, '0000000062');
    const card = operator.page.locator('.user-card').filter({ hasText: 'Клієнт тест' });
    await expect(card).toBeVisible();
    await expect(card.getByRole('button')).toHaveCount(1);
    await expect(operator.page.getByText('Оператор тест', { exact: true })).toHaveCount(0);
    await card.getByRole('button', { name: 'Скинути пароль: Клієнт тест' }).click();
    const prompt = operator.page.locator('app-confirm-dialog dialog');
    await expect(prompt).toContainText('попередні сеанси');
    await prompt.getByRole('button', { name: 'Скасувати' }).click();
    await card.getByRole('button', { name: 'Скинути пароль: Клієнт тест' }).click(); await confirm(operator.page);
    const secretDialog = operator.page.locator('.secret-dialog');
    await expect(secretDialog).toBeVisible();
    const temporary = (await secretDialog.locator('.temporary-password').textContent())!.trim();
    expect(temporary.length).toBeGreaterThan(20);
    await expect(secretDialog).toContainText('вручну поза системою');
    expect(await operator.page.evaluate(value => JSON.stringify({ local: localStorage, session: sessionStorage, url: location.href }).includes(value), temporary)).toBe(false);
    expect(consoleMessages.some(message => message.includes(temporary))).toBe(false);
    await secretDialog.getByRole('button', { name: 'Зрозуміло, закрити' }).click();
    await expect(secretDialog).not.toBeVisible();
    await expect(operator.page.locator('.temporary-password')).toHaveCount(0);
    await operator.page.reload(); await expect(operator.page.locator('.temporary-password')).toHaveCount(0);
    await expect.poll(() => connection.state).not.toBe('Connected');
    expect((await request.get(`${baseURL}/api/auth/session`, { headers: { Cookie: oldCookie } })).status()).toBe(401);
    await login(customer.page, '0000000062', temporary);
    await expect(customer.page).toHaveURL(/\/account\/change-temporary-password$/);
    for (const path of ['/account/book', '/account/appointments', '/account/profile', '/account/notifications']) {
      await customer.page.goto(path);
      await expect(customer.page).toHaveURL(/\/account\/change-temporary-password$/);
    }
    const restricted = await customer.page.evaluate(async () => Promise.all(
      ['/api/profile', '/api/appointments/my', '/api/notifications'].map(async url => (await fetch(url)).status),
    ));
    expect(restricted).toEqual([403, 403, 403]);
    await changeTemporaryPassword(customer.page, temporary);
    await expect(customer.page).toHaveURL(/\/account\/appointments$/);
    const notices = await customer.page.evaluate(async () => (await fetch('/api/notifications')).json());
    expect(notices.some((n: { type: string }) => n.type === 'AccountPasswordReset')).toBe(true);
    expect(JSON.stringify(notices)).not.toContain(temporary);
    await connection.stop();
  } finally { await customer.context.close(); await operator.context.close(); }
});
