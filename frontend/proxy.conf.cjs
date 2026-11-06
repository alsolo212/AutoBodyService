// Development only: validate the API's localhost certificate against the local dev certificate.
const { readFileSync } = require('node:fs');
const { Agent } = require('node:https');
const { join } = require('node:path');

module.exports = {
  '/api/**': {
    target: 'https://localhost:7097',
    changeOrigin: true,
    secure: true,
    agent: new Agent({ ca: readFileSync(join(__dirname, '.local-https/localhost.pem')) }),
  },
};
