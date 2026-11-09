import { defineConfig, devices } from '@playwright/test';

const port = process.env['E2E_FRONTEND_PORT'] ?? '4173';
if (!/^\d{1,5}$/.test(port) || Number(port) < 1 || Number(port) > 65535) {
  throw new Error('E2E_FRONTEND_PORT must be a valid port number.');
}
const baseURL = `https://localhost:${port}`;
const customCertificate = process.env['E2E_TLS_CERT'] && process.env['E2E_TLS_KEY'];

export default defineConfig({
  testDir: './e2e',
  fullyParallel: true,
  forbidOnly: !!process.env['CI'],
  retries: process.env['CI'] ? 2 : 0,
  reporter: 'list',
  use: {
    baseURL,
    // The test browser has an isolated profile and does not inherit OS trust settings.
    ignoreHTTPSErrors: true,
    trace: 'retain-on-failure',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: {
    command: `npm start -- --host localhost --port ${port}` + (customCertificate ? ' --ssl-cert "$E2E_TLS_CERT" --ssl-key "$E2E_TLS_KEY"' : ''),
    url: baseURL,
    ignoreHTTPSErrors: true,
    reuseExistingServer: false,
    timeout: 120_000,
  },
});
