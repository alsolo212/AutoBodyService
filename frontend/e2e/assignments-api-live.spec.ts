import { expect, test } from '@playwright/test';

test('live assignment lifecycle is reflected in employee directory and repair history', async ({ page }) => {
  test.skip(process.env['E2E_LIVE_API'] !== '1', 'Requires an isolated API and PostgreSQL.');
  await page.goto('/login');
  await page.getByLabel('Номер телефону').fill(process.env['E2E_ADMIN_PHONE']!);
  await page.getByLabel('Пароль', { exact: true }).fill(process.env['E2E_ADMIN_PASSWORD']!);
  await page.getByRole('button', { name: 'Увійти' }).click();
  await expect(page).toHaveURL(/\/admin$/);
  const data = await page.evaluate(async () => {
    await fetch('/api/auth/antiforgery');
    const token = decodeURIComponent(document.cookie.split('; ').find(c => c.startsWith('XSRF-TOKEN='))!.split('=')[1]);
    const headers = { 'Content-Type': 'application/json', RequestVerificationToken: token };
    const checked = async (response: Response, status: number) => { if (response.status !== status) throw new Error(`${response.url}: ${response.status}`); return response; };
    const schedule = await (await checked(await fetch('/api/admin/inspection-schedule'), 200)).json();
    const intervals = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'].map(dayOfWeek => ({ dayOfWeek, start: '09:00', end: '10:00' }));
    const preview = await (await checked(await fetch('/api/admin/inspection-schedule/preview', { method: 'POST', headers, body: JSON.stringify({ version: schedule.version, intervals }) }), 200)).json();
    await checked(await fetch('/api/admin/inspection-schedule', { method: 'PUT', headers, body: JSON.stringify({ version: schedule.version, intervals, confirmationToken: preview.confirmationToken }) }), 204);
    const slot = (await (await checked(await fetch('/api/inspection-slots?date=2099-09-10'), 200)).json()).slots[0];
    const order = await (await checked(await fetch('/api/appointments', { method: 'POST', headers, body: JSON.stringify({ inspectionStartUtc: slot.startUtc, vin: '1HGCM82633A123456', make: 'Test', model: 'Car', problemDescription: 'Проблема' }) }), 201)).json();
    const details = async () => (await (await checked(await fetch(`/api/appointments/${order.id}`), 200)).json());
    let item = await details();
    await checked(await fetch(`/api/admin/appointments/${order.id}/repair-plan`, { method: 'PUT', headers, body: JSON.stringify({ version: item.version, price: 100, plannedStartUtc: '2099-09-11T07:00:00Z', estimatedFinishUtc: '2099-09-12T07:00:00Z', inspectionPerformed: true }) }), 204);
    for (const target of ['Scheduled', 'InProgress']) {
      item = await details(); await checked(await fetch(`/api/admin/appointments/${order.id}/transition`, { method: 'POST', headers, body: JSON.stringify({ version: item.version, target, inspectionPerformed: true }) }), 204);
    }
    const employee = await (await checked(await fetch('/api/admin/employees', { method: 'POST', headers, body: JSON.stringify({ fullName: 'Тестовий майстер', specialization: 'Маляр', isActive: true }) }), 201)).json();
    const assignment = await (await checked(await fetch('/api/admin/assignments', { method: 'POST', headers, body: JSON.stringify({ appointmentId: order.id, employeeId: employee.id }) }), 201)).json();
    return { order: order.id as string, employee: employee.id as string, assignment: assignment.id as string };
  });
  const redundant: string[] = [];
  page.on('request', request => { if (request.method() === 'GET' && request.url().includes('/api/admin/assignments?active=true')) redundant.push(request.url()); });
  const directoryResponse = page.waitForResponse(response => response.url().endsWith('/api/admin/employees') && response.request().method() === 'GET');
  await page.goto('/admin/employees');
  const directory = await (await directoryResponse).json();
  const employeeRow = directory.find((row: { id: string }) => row.id === data.employee);
  expect(employeeRow.currentAssignments).toEqual([{ id: data.assignment, appointmentId: data.order }]);
  console.log('Employee API sample:', JSON.stringify(employeeRow));
  const card = page.locator('.employee-card').filter({ hasText: 'Тестовий майстер' });
  await expect(card.getByRole('link', { name: new RegExp(data.order) })).toHaveAttribute('href', `/admin/appointments/${data.order}`);
  expect(redundant).toHaveLength(0);
  await card.getByRole('button', { name: /Деактивувати/ }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Деактивувати' }).click();
  await expect(card).toContainText('Неактивний');
  await expect(card.getByRole('link', { name: new RegExp(data.order) })).toBeVisible();
  const result = await page.evaluate(async ({ order, assignment }) => {
    await fetch('/api/auth/antiforgery');
    const token = decodeURIComponent(document.cookie.split('; ').find(c => c.startsWith('XSRF-TOKEN='))!.split('=')[1]);
    const details = await (await fetch(`/api/appointments/${order}`)).json();
    const response = await fetch(`/api/admin/appointments/${order}/transition`, { method: 'POST', headers: { 'Content-Type': 'application/json', RequestVerificationToken: token }, body: JSON.stringify({ version: details.version, target: 'AwaitingPayment' }) });
    const history = await (await fetch(`/api/admin/assignments/${assignment}`)).json();
    return { status: response.status, history };
  }, data);
  expect(result.status).toBe(204);
  expect(result.history.releasedAtUtc).toBeTruthy();
  expect(result.history.name).toBe('Тестовий майстер');
  await page.reload();
  await expect(card).toContainText('Активних призначень немає.');
  await expect(card.getByRole('link', { name: new RegExp(data.order) })).toHaveCount(0);
});
