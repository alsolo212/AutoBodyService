# AutoBodyService frontend

Install dependencies with `npm ci`. Follow the root README's local HTTPS setup before `npm start`; run `npm run build` for a production build. The pinned Node.js version is in the repository's `.nvmrc`.

The application uses Angular Router, HttpClient, RxJS and Reactive Forms. `src/app/core` holds application-wide services, `shared` reusable UI, `layout` application shells, and `features` feature-specific code.

For Playwright, install its Chromium browser with `npx playwright install chromium`, then run `npm run test:e2e`. Playwright starts its own HTTPS Angular server. The live API test is skipped unless the API is running and `E2E_LIVE_API=1` is set. The isolated Playwright browser accepts the local self-signed certificate; the Angular proxy still validates the API certificate. Run the Angular unit tests with `npm test`.

## UI foundation

Design tokens and reusable `.button`, `.field`, `.field__input`, `.field__hint` and `.field__error` styles are in `src/styles.scss`. Use a real label associated with each input, and connect validation text with `aria-describedby` and `aria-invalid` when needed. `UiState` provides loading, empty and error messages. `ConfirmDialog` uses a native modal dialog; the caller supplies a unique `dialogId`, title and description, opens it with `open()`, and handles `confirmed` only after the user confirms.

The public shell is available at `/`, with authentication pages at `/login`, `/register` and `/forgot-password`. `/account/appointments` and `/admin` are guarded shells with no business data yet. Guards check the current server session for navigation; the server remains responsible for authorization. Profile pages at `/account/profile` and `/admin/profile` share the same implementation. Temporary-password accounts are directed to `/account/change-temporary-password` and must change their password before accessing protected routes.

The live authentication browser test uses `E2E_LIVE_AUTH=1` and creates an account; run it only against an isolated test database.

The live profile test uses `E2E_LIVE_PROFILE=1` and `PROFILE_VERIFY_CONTAINER`; it modifies a test account and must only run with the disposable test database container.
