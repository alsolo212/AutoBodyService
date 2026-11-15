import { expect, test } from '@playwright/test';
import { login, register } from './admin-users-helpers';

test('SuperAdmin finance API records and corrects general expenses while other roles are denied', async ({ page, browser }) => {
  test.setTimeout(90000);
  test.skip(process.env['E2E_LIVE_API'] !== '1', 'Requires isolated API and PostgreSQL');
  await login(page, process.env['E2E_ADMIN_PHONE']!, process.env['E2E_ADMIN_PASSWORD']!);
  await expect(page).toHaveURL(/\/admin$/);
  const admin = await register(browser, 'Тестовий адміністратор', '0000000064');
  const user = await register(browser, 'Тестовий клієнт', '0000000065');
  try {
    const promoted = await page.evaluate(async () => {
      const account = (await (await fetch('/api/admin/users?search=0000000064')).json())[0];
      await fetch('/api/auth/antiforgery');
      const token = decodeURIComponent(document.cookie.split('; ').find(c => c.startsWith('XSRF-TOKEN='))!.split('=')[1]);
      return (await fetch(`/api/admin/users/${account.id}/roles`, { method: 'PUT', headers: { 'Content-Type': 'application/json', RequestVerificationToken: token }, body: JSON.stringify({ version: account.version, isAdmin: true }) })).status;
    });
    expect(promoted).toBe(204);
    await login(admin.page, '0000000064', 'Example-password-123!'); await expect(admin.page).toHaveURL(/\/admin$/);
    const result = await page.evaluate(async () => {
      await fetch('/api/auth/antiforgery');
      const token = decodeURIComponent(document.cookie.split('; ').find(c => c.startsWith('XSRF-TOKEN='))!.split('=')[1]);
      const headers = { 'Content-Type': 'application/json', RequestVerificationToken: token };
      const noCsrf = await fetch('/api/admin/reports/expenses', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ amount: 1 }) });
      const forged = await fetch('/api/admin/reports/expenses', { method: 'POST', headers, body: JSON.stringify({ amount: 1, recordedMonth: 1 }) });
      const first = await fetch('/api/admin/reports/expenses', { method: 'POST', headers, body: JSON.stringify({ amount: 0.1, description: 'Тестова витрата' }) });
      const firstItem = await first.json();
      const second = await fetch('/api/admin/reports/expenses', { method: 'POST', headers, body: JSON.stringify({ amount: 0.2 }) });
      const changed = await fetch(`/api/admin/reports/expenses/${firstItem.id}`, { method: 'PATCH', headers, body: JSON.stringify({ version: firstItem.version, amount: 0.3 }) });
      const updated = await changed.json();
      const stale = await fetch(`/api/admin/reports/expenses/${firstItem.id}`, { method: 'PATCH', headers, body: JSON.stringify({ version: firstItem.version, amount: 1 }) });
      const query = `year=${updated.recordedYear}&month=${updated.recordedMonth}`;
      const report = await (await fetch(`/api/admin/reports/finance?${query}`)).json();
      const expenses = await (await fetch(`/api/admin/reports/expenses?${query}&sort=amount&direction=asc`)).json();
      return { noCsrf: noCsrf.status, forged: forged.status, first: first.status, second: second.status, changed: changed.status, stale: stale.status, report, expenses };
    });
    expect(result).toMatchObject({ noCsrf: 400, forged: 400, first: 201, second: 201, changed: 200, stale: 409 });
    expect(result.report).toMatchObject({ completedCount: 0, revenue: 0, expenses: 0.5, result: -0.5, currency: 'UAH' });
    expect(result.report.expensesExact).toBe('0.5');
    expect(result.expenses.items.map((item: { amount: number }) => item.amount)).toEqual([0.2, 0.3]);
    for (const other of [admin.page, user.page]) {
      const denied = await other.evaluate(async () => {
        await fetch('/api/auth/antiforgery');
        const token = decodeURIComponent(document.cookie.split('; ').find(c => c.startsWith('XSRF-TOKEN='))!.split('=')[1]);
        const get = await fetch('/api/admin/reports/finance?year=2026&month=10');
        const list = await fetch('/api/admin/reports/expenses?year=2026&month=10');
        const post = await fetch('/api/admin/reports/expenses', { method: 'POST', headers: { 'Content-Type': 'application/json', RequestVerificationToken: token }, body: JSON.stringify({ amount: 1 }) });
        return [get.status, list.status, post.status];
      });
      expect(denied).toEqual([403, 403, 403]);
    }
    const dashboard = await admin.page.evaluate(async () => (await (await fetch('/api/admin/dashboard')).json()));
    expect(JSON.stringify(dashboard)).not.toMatch(/revenue|expenses|profit/i);
  } finally { await admin.context.close(); await user.context.close(); }
});
