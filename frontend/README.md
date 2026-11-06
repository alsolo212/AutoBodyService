# AutoBodyService frontend

Install dependencies with `npm ci`, then run `npm start` for the development server or `npm run build` for a production build. The pinned Node.js version is in the repository's `.nvmrc`.

The application uses Angular Router, HttpClient, RxJS and the Angular Reactive Forms package. Add forms when their features are implemented. `src/app/core` holds application-wide services, `shared` reusable UI, `layout` application shells, and `features` feature-specific code.

For the Playwright smoke test, install its Chromium browser with `npx playwright install chromium`, then run `npm run test:e2e`. Playwright starts its own Angular development server; the shell smoke test does not require a running backend. Run the Angular unit tests with `npm test`.

## UI foundation

Design tokens and reusable `.button`, `.field`, `.field__input`, `.field__hint` and `.field__error` styles are in `src/styles.scss`. Use a real label associated with each input, and connect validation text with `aria-describedby` and `aria-invalid` when needed. `UiState` provides loading, empty and error messages. `ConfirmDialog` uses a native modal dialog; the caller supplies a unique `dialogId`, title and description, opens it with `open()`, and handles `confirmed` only after the user confirms.

The public shell is available at `/`; `/account/appointments` and `/admin` show the planned account and administration shells with no business data. Their navigation labels reflect the project route map, but unfinished sections are not links. These shells are not authentication or authorization: future protected pages and API calls must enforce access on the server. Remaining routes, forms and features are added when implemented.
