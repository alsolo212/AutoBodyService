# AutoBodyService frontend

Install dependencies with `npm ci`, then run `npm start` for the development server or `npm run build` for a production build. The pinned Node.js version is in the repository's `.nvmrc`.

The application uses Angular Router, HttpClient, RxJS and the Angular Reactive Forms package. Add forms when their features are implemented. `src/app/core` holds application-wide services, `shared` reusable UI, `layout` application shells, and `features` feature-specific code.

For the Playwright smoke test, install its Chromium browser with `npx playwright install chromium`, then run `npm run test:e2e`. Playwright starts its own Angular development server. The smoke test intercepts the API health request, so it does not require a running backend. Run the existing Angular unit tests with `npm test`.
