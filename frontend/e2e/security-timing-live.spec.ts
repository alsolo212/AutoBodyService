import { expect, test } from '@playwright/test';

test('authentication responses hide account existence while registration followed by login remains observable', async ({ page }) => {
  test.skip(process.env['E2E_LIVE_API'] !== '1', 'Requires isolated API and PostgreSQL.');
  await page.goto('/register');
  const result = await page.evaluate(async () => {
    await fetch('/api/auth/antiforgery');
    const token = decodeURIComponent(document.cookie.split('; ').find(c => c.startsWith('XSRF-TOKEN='))!.split('=')[1]);
    const headers = { 'Content-Type': 'application/json', RequestVerificationToken: token };
    const send = async (url: string, body: object) => {
      const start = performance.now();
      const response = await fetch(url, { method: 'POST', headers, body: JSON.stringify(body) });
      const elapsed = performance.now() - start;
      const text = await response.text();
      return { status: response.status, body: text ? JSON.parse(text) : null, elapsed };
    };
    const known = '0000000073'; const created = '0000000072';
    const initial = await send('/api/auth/register', { name: 'Тест', phone: known, password: 'Example-password-123!' });
    const registration = (phone: string) => send('/api/auth/register', { name: 'Тест', phone, password: 'Another-password-123!' });
    const duplicate = [await registration(known)];
    const fresh = [await registration(created), await registration('0000000070')];
    duplicate.push(await registration(known));
    const wrong = []; const missing = [];
    for (let i = 0; i < 2; i++) {
      const a = () => send('/api/auth/login', { phone: known, password: 'Another-password-123!' });
      const b = () => send('/api/auth/login', { phone: '0000000071', password: 'Another-password-123!' });
      if (i === 0) { wrong.push(await a()); missing.push(await b()); }
      else { missing.push(await b()); wrong.push(await a()); }
    }
    const freshLogin = await send('/api/auth/login', { phone: created, password: 'Another-password-123!' });
    return { initial, duplicate, fresh, wrong, missing, freshLogin };
  });
  expect([result.initial.status, ...result.duplicate.map(r => r.status), ...result.fresh.map(r => r.status)]).toEqual([202, 202, 202, 202, 202]);
  for (const response of result.duplicate) expect(response.body).toEqual(result.fresh[0].body);
  for (const response of [...result.wrong, ...result.missing]) {
    expect(response.status).toBe(401);
    expect(response.body.title).toBe(result.wrong[0].body.title);
    expect(Object.keys(response.body).sort()).toEqual(Object.keys(result.wrong[0].body).sort());
  }
  expect(result.freshLogin.status).toBe(204);
  console.log(`Timing sample (ms): existing wrong ${result.wrong.map(r => r.elapsed.toFixed(1)).join(', ')}; unknown ${result.missing.map(r => r.elapsed.toFixed(1)).join(', ')}; registration duplicate ${result.duplicate.map(r => r.elapsed.toFixed(1)).join(', ')}, new ${result.fresh.map(r => r.elapsed.toFixed(1)).join(', ')}`);
});
