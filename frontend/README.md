# AutoBodyService frontend

Install dependencies with `npm ci`. Follow the root README's local HTTPS setup before `npm start`; run `npm run build` for a production build. The pinned Node.js version is in the repository's `.nvmrc`.

The application uses Angular Router, HttpClient, RxJS and Reactive Forms. `src/app/core` holds application-wide services, `shared` reusable UI, `layout` application shells, and `features` feature-specific code.

For Playwright, install Chromium with `npx playwright install chromium`. The isolated browser accepts the test frontend's self-signed certificate; the Angular proxy still validates the API certificate. Run Angular unit tests with `npm test -- --watch=false`. A plain `npm run test:e2e` may skip live specs; use the isolated script described below for the full suite.

## UI foundation

Design tokens and reusable `.button`, `.field`, `.field__input`, `.field__hint` and `.field__error` styles are in `src/styles.scss`. Use a real label associated with each input, and connect validation text with `aria-describedby` and `aria-invalid` when needed. `UiState` provides loading, empty and error messages. `ConfirmDialog` uses a native modal dialog; the caller supplies a unique `dialogId`, title and description, opens it with `open()`, and handles `confirmed` only after the user confirms.

The public shell is available at `/`, with authentication pages at `/login`, `/register` and `/forgot-password`. The account area contains booking, appointments, notifications and profile; the admin area contains the operational boards, calendar, users and other management pages. Reports are reserved for SuperAdmin. Guards check the current server session for navigation; the server remains responsible for authorization. Temporary-password accounts must change their password before accessing protected routes.

The live authentication browser test uses `E2E_LIVE_AUTH=1` and creates an account; run it only against an isolated test database.

The live profile test uses `E2E_LIVE_PROFILE=1` and `PROFILE_VERIFY_CONTAINER`; it modifies a test account and must only run with the disposable test database container.

## Real authentication E2E

From the repository root, run `bash scripts/test-auth-e2e.sh`. For every spec, this starts a fresh disposable PostgreSQL 18 container, applies the application's migrations, creates a synthetic SuperAdmin, and launches the real API and Angular over HTTPS with a temporary certificate. Live tests use that API; the separate mocked UI tests retain their fixtures. Each run removes its own test container and temporary files and does not use the development database. Docker, OpenSSL, curl, Python 3, the project-local .NET SDK and `dotnet-ef`, installed frontend dependencies, and Playwright Chromium are required.

Live tests are skipped in the ordinary Playwright run unless their corresponding environment flags are set; use the script to run all of them against the isolated environment.

To run one live spec in the same isolated environment, set `E2E_SPEC`, for example: `E2E_SPEC=frontend/e2e/admin-appointment-live.spec.ts bash scripts/test-auth-e2e.sh`.
