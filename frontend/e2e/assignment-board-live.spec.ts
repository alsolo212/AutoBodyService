import { expect, test } from '@playwright/test';

test('board copies an order to two workers, releases one and preserves customer snapshots', async ({ page, browser }) => {
  test.skip(process.env['E2E_LIVE_API'] !== '1', 'Requires an isolated API and PostgreSQL.');
  test.setTimeout(90_000);
  await page.setViewportSize({ width: 1440, height: 1200 });
  await page.goto('/login');
  await page.getByLabel('Номер телефону').fill(process.env['E2E_ADMIN_PHONE']!);
  await page.getByLabel('Пароль', { exact: true }).fill(process.env['E2E_ADMIN_PASSWORD']!);
  await page.getByRole('button', { name: 'Увійти' }).click(); await expect(page).toHaveURL(/\/admin$/);
  const setup = await page.evaluate(async () => {
    await fetch('/api/auth/antiforgery');
    const token = decodeURIComponent(document.cookie.split('; ').find(c => c.startsWith('XSRF-TOKEN='))!.split('=')[1]);
    const headers = { 'Content-Type': 'application/json', RequestVerificationToken: token };
    const checked = async (response: Response, status: number) => { if (response.status !== status) throw new Error(`${response.url}: ${response.status}`); return response; };
    const schedule = await (await checked(await fetch('/api/admin/inspection-schedule'), 200)).json();
    const intervals = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'].map(dayOfWeek => ({ dayOfWeek, start: '09:00', end: '11:00' }));
    const preview = await (await checked(await fetch('/api/admin/inspection-schedule/preview', { method: 'POST', headers, body: JSON.stringify({ version: schedule.version, intervals }) }), 200)).json();
    await checked(await fetch('/api/admin/inspection-schedule', { method: 'PUT', headers, body: JSON.stringify({ version: schedule.version, intervals, confirmationToken: preview.confirmationToken }) }), 204);
    const slots = (await (await checked(await fetch('/api/inspection-slots?date=2099-09-10'), 200)).json()).slots;
    const employees = [];
    for (const [fullName, specialization, isActive] of [['Майстер А', 'Маляр', true], ['Майстер Б', 'Рихтування', true], ['Неактивний', 'Майстер', false]]) {
      employees.push(await (await checked(await fetch('/api/admin/employees', { method: 'POST', headers, body: JSON.stringify({ fullName, specialization, isActive }) }), 201)).json());
    }
    const foreign = await (await checked(await fetch('/api/appointments', { method: 'POST', headers, body: JSON.stringify({ inspectionStartUtc: slots[2].startUtc, vin: '1HGCM82633A123456', make: 'Чужа', model: 'Авто', problemDescription: 'Пошкодження' }) }), 201)).json();
    return { employees, slots, foreign: foreign.id as string };
  });
  const customerContext = await browser.newContext({ ignoreHTTPSErrors: true });
  try {
    const customer = await customerContext.newPage(); await customer.goto('/register');
    await customer.getByLabel('Ім’я').fill('Тестовий клієнт'); await customer.getByLabel('Номер телефону').fill('0000000096');
    await customer.getByLabel('Пароль', { exact: true }).fill('Example-password-123!'); await customer.getByLabel('Повторіть пароль').fill('Example-password-123!');
    await customer.getByRole('button', { name: 'Зареєструватися' }).click(); await expect(customer).toHaveURL(/\/account\/appointments$/);
    const orders = await customer.evaluate(async slots => {
      await fetch('/api/auth/antiforgery');
      const token = decodeURIComponent(document.cookie.split('; ').find(c => c.startsWith('XSRF-TOKEN='))!.split('=')[1]);
      const headers = { 'Content-Type': 'application/json', RequestVerificationToken: token };
      const ids = [];
      for (let i = 0; i < 2; i++) {
        const response = await fetch('/api/appointments', { method: 'POST', headers, body: JSON.stringify({ inspectionStartUtc: slots[i].startUtc, vin: '1HGCM82633A123456', make: 'Тест', model: i === 0 ? 'У роботі' : 'Очікує', problemDescription: 'Опис' }) });
        if (response.status !== 201) throw new Error(`Booking: ${response.status}`);
        ids.push((await response.json()).id as string);
      }
      return ids;
    }, setup.slots);
    await page.evaluate(async id => {
      await fetch('/api/auth/antiforgery'); const token = decodeURIComponent(document.cookie.split('; ').find(c => c.startsWith('XSRF-TOKEN='))!.split('=')[1]);
      const headers = { 'Content-Type': 'application/json', RequestVerificationToken: token };
      const details = async () => (await (await fetch(`/api/appointments/${id}`)).json());
      let item = await details();
      const plan = await fetch(`/api/admin/appointments/${id}/repair-plan`, { method: 'PUT', headers, body: JSON.stringify({ version: item.version, price: 100, plannedStartUtc: '2099-09-11T07:00:00Z', estimatedFinishUtc: '2099-09-12T07:00:00Z', inspectionPerformed: true }) });
      if (plan.status !== 204) throw new Error(`Plan: ${plan.status}`);
      for (const target of ['Scheduled', 'InProgress']) {
        item = await details(); const response = await fetch(`/api/admin/appointments/${id}/transition`, { method: 'POST', headers, body: JSON.stringify({ version: item.version, target, inspectionPerformed: true }) });
        if (response.status !== 204) throw new Error(`Transition: ${response.status}`);
      }
    }, orders[0]);
    await page.goto('/admin/assignments');
    const source = page.locator('#assignment-source');
    const order = source.locator(`[data-order-id="${orders[0]}"]`);
    const workerOne = page.locator(`[data-employee-id="${setup.employees[0].id}"]`);
    const workerTwo = page.locator(`[data-employee-id="${setup.employees[1].id}"]`);
    await expect(order).toBeVisible(); await expect(source.locator(`[data-order-id="${orders[1]}"]`)).toHaveCount(0);
    await expect(page.getByText('Неактивний', { exact: true })).toHaveCount(0);
    const from = await order.locator('.drag-handle').boundingBox(); const to = await workerOne.locator('.worker-drop').boundingBox();
    if (!from || !to) throw new Error('Missing drag bounds');
    const firstAssignment = page.waitForResponse(r => r.url().endsWith('/api/admin/assignments') && r.request().method() === 'POST');
    await page.mouse.move(from.x + from.width / 2, from.y + from.height / 2); await page.mouse.down();
    await page.mouse.move(from.x + from.width / 2 + 10, from.y + from.height / 2, { steps: 3 });
    await page.mouse.move(to.x + to.width / 2, to.y + Math.min(40, to.height / 2), { steps: 15 }); await page.mouse.up();
    expect((await firstAssignment).status()).toBe(201);
    await expect(workerOne.locator('.assigned-card')).toContainText('Тест У роботі');
    await expect(order).toBeVisible();
    await workerTwo.locator('select').selectOption(orders[0]);
    const secondAssignment = page.waitForResponse(r => r.url().endsWith('/api/admin/assignments') && r.request().method() === 'POST');
    await workerTwo.getByRole('button', { name: /Призначити/ }).focus(); await page.keyboard.press('Enter');
    expect((await secondAssignment).status()).toBe(201);
    await expect(workerOne.locator('.assigned-card')).toHaveCount(1); await expect(workerTwo.locator('.assigned-card')).toHaveCount(1); await expect(order).toBeVisible();
    await page.screenshot({ path: '/tmp/autobody-assignment-board-after.png', fullPage: true });
    const released = page.waitForResponse(r => r.url().includes('/release') && r.request().method() === 'PATCH');
    await workerOne.getByRole('button', { name: /Зняти призначення/ }).click(); expect((await released).status()).toBe(204);
    await expect(workerOne.locator('.assigned-card')).toHaveCount(0); await expect(workerTwo.locator('.assigned-card')).toHaveCount(1); await expect(order).toBeVisible();
    // A separate API action assigns the same worker while the board remains stale.
    const concurrent = await page.evaluate(async ({ appointmentId, employeeId }) => {
      await fetch('/api/auth/antiforgery');
      const token = decodeURIComponent(document.cookie.split('; ').find(c => c.startsWith('XSRF-TOKEN='))!.split('=')[1]);
      const response = await fetch('/api/admin/assignments', { method: 'POST', headers: { 'Content-Type': 'application/json', RequestVerificationToken: token }, body: JSON.stringify({ appointmentId, employeeId }) });
      return response.status;
    }, { appointmentId: orders[0], employeeId: setup.employees[0].id });
    expect(concurrent).toBe(201);
    await expect(workerOne.locator('.assigned-card')).toHaveCount(0);
    await workerOne.locator('select').selectOption(orders[0]);
    const conflict = page.waitForResponse(r => r.url().endsWith('/api/admin/assignments') && r.request().method() === 'POST');
    await workerOne.getByRole('button', { name: /Призначити/ }).click();
    expect((await conflict).status()).toBe(409);
    await expect(page.getByRole('alert').filter({ hasText: 'Дані змінилися' })).toBeVisible();
    await expect(workerOne.locator('.assigned-card')).toHaveCount(1);
    await expect(workerTwo.locator('.assigned-card')).toHaveCount(1);
    await expect(order).toBeVisible();
    await page.goto('/admin/employees');
    const employeeOne = page.locator('.employee-card').filter({ hasText: 'Майстер А' });
    const employeeTwo = page.locator('.employee-card').filter({ hasText: 'Майстер Б' });
    await expect(employeeOne.getByRole('link', { name: new RegExp(orders[0]) })).toBeVisible();
    await expect(employeeTwo.getByRole('link', { name: new RegExp(orders[0]) })).toBeVisible();
    await page.goto('/admin/assignments');
    const secondRelease = page.waitForResponse(r => r.url().includes('/release') && r.request().method() === 'PATCH');
    await workerOne.getByRole('button', { name: /Зняти призначення/ }).click();
    expect((await secondRelease).status()).toBe(204);
    await expect(workerOne.locator('.assigned-card')).toHaveCount(0);
    await expect(workerTwo.locator('.assigned-card')).toHaveCount(1);
    await expect(order).toBeVisible();
    await page.evaluate(async employee => {
      await fetch('/api/auth/antiforgery'); const token = decodeURIComponent(document.cookie.split('; ').find(c => c.startsWith('XSRF-TOKEN='))!.split('=')[1]);
      const response = await fetch(`/api/admin/employees/${employee.id}`, { method: 'PATCH', headers: { 'Content-Type': 'application/json', RequestVerificationToken: token }, body: JSON.stringify({ version: employee.version, fullName: 'Змінене ім’я', specialization: 'Інша спеціалізація', isActive: false }) });
      if (response.status !== 200) throw new Error(`Deactivate: ${response.status}`);
    }, setup.employees[0]);
    await page.goto(`/admin/appointments/${orders[0]}`);
    const history = page.locator('section[aria-labelledby="assignments-heading"]');
    await expect(history).toContainText('Майстер А'); await expect(history).toContainText('Маляр');
    await expect(history).toContainText('Майстер Б'); await expect(history).toContainText('Активне');
    await expect(history).toContainText('Завершено'); await expect(history).toContainText('Призначив(ла): E2E test');
    await expect(history).toContainText('Завершив(ла): E2E test');
    await page.goto('/admin/assignments'); await expect(page.locator(`[data-employee-id="${setup.employees[0].id}"]`)).toHaveCount(0);
    await page.setViewportSize({ width: 320, height: 800 });
    await expect(workerTwo.getByRole('button', { name: /Зняти призначення/ })).toBeVisible();
    await expect(page.getByRole('heading', { name: 'Усі замовлення в роботі' })).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await customer.goto(`/account/appointments/${orders[0]}`);
    const participants = customer.locator('section').filter({ has: customer.getByRole('heading', { name: 'Працівники' }) });
    await expect(participants).toContainText('Майстер А'); await expect(participants).toContainText('Маляр'); await expect(participants).toContainText('Майстер Б');
    await expect(participants).not.toContainText('E2E test'); await expect(participants.locator('time')).toHaveCount(0);
    const security = await customer.evaluate(async ({ own, foreign }) => {
      const detail = await (await fetch(`/api/appointments/${own}`)).json();
      return { participants: detail.participants, foreign: (await fetch(`/api/appointments/${foreign}`)).status, assignments: (await fetch('/api/admin/assignments')).status };
    }, { own: orders[0], foreign: setup.foreign });
    expect(security.foreign).toBe(404); expect(security.assignments).toBe(403);
    expect(security.participants).toHaveLength(2);
    for (const participant of security.participants) expect(Object.keys(participant).sort()).toEqual(['name', 'specialization']);
    const finish = await page.evaluate(async id => {
      await fetch('/api/auth/antiforgery');
      const token = decodeURIComponent(document.cookie.split('; ').find(c => c.startsWith('XSRF-TOKEN='))!.split('=')[1]);
      const headers = { 'Content-Type': 'application/json', RequestVerificationToken: token };
      const results = [];
      for (const target of ['AwaitingPayment', 'Completed']) {
        const details = await (await fetch(`/api/appointments/${id}`)).json();
        const response = await fetch(`/api/admin/appointments/${id}/transition`, { method: 'POST', headers, body: JSON.stringify({ version: details.version, target, paymentReceived: true, vehicleHandedOver: true }) });
        results.push(response.status);
      }
      return results;
    }, orders[0]);
    expect(finish).toEqual([204, 204]);
    await customer.reload();
    await expect(customer.locator('.status')).toContainText('Завершено');
    await expect(participants).toContainText('Майстер А'); await expect(participants).toContainText('Маляр');
    await expect(participants).toContainText('Майстер Б'); await expect(participants.locator('time')).toHaveCount(0);
    await page.goto(`/admin/appointments/${orders[0]}`);
    const completedHistory = page.locator('section[aria-labelledby="assignments-heading"]');
    await expect(completedHistory).toContainText('Майстер А'); await expect(completedHistory).toContainText('Майстер Б');
    await expect(completedHistory).not.toContainText('Активне');
    await expect(completedHistory).toContainText('Завершив(ла): E2E test');
    await customer.goto('/admin/assignments'); await expect(customer).toHaveURL(/\/account\/appointments$/);
  } finally { await customerContext.close(); }
});
