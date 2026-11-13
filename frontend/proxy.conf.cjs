// Development only: validate the API's localhost certificate against the local dev certificate.
const { readFileSync } = require('node:fs');
const { Agent } = require('node:https');
const { join } = require('node:path');

const api = process.env['E2E_API_TARGET'] || 'https://localhost:7097';
const agent = new Agent({ ca: readFileSync(process.env['E2E_API_CERT'] || join(__dirname, '.local-https/localhost.pem')) });

module.exports = {
  '/api/**': {
    target: api,
    changeOrigin: true,
    secure: true,
    agent,
  },
  '/hubs/**': {
    target: api,
    changeOrigin: false,
    secure: true,
    ws: true,
    agent,
  },
};
