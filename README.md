# AutoBodyService

## .NET SDK

The repository selects .NET SDK 10.0.403 or a later stable patch in the same feature band. Before running any .NET command, activate the project-local SDK and runtimes in each new bash or zsh session (including a new Codex session). From the repository root:

```sh
source scripts/activate-dotnet.sh
dotnet --info
```

The activation checks the local SDK and both runtimes, sets `DOTNET_ROOT` and `PATH`, and clears conflicting architecture-specific `DOTNET_ROOT_*` values in the current shell. It fails if the local installation is missing or too old. Install SDK 10.0.403 in the ignored `.dotnet` directory using the [official .NET install script](https://learn.microsoft.com/dotnet/core/tools/dotnet-install-script) if needed. `global.json` selects an SDK only: without activation the system host may run an application using an older system runtime. The activation does not change system-wide installations or shell startup files.

## Local PostgreSQL

Docker Desktop (or another running Docker daemon) and Docker Compose are required. The Compose project `autobodyservice-dev` creates a dedicated PostgreSQL 18 container and named volume. The database is published only on `127.0.0.1:55432` by default.

Copy `.env.example` to `.env` and set `POSTGRES_PASSWORD` to a unique random value before starting. For example, generate a hexadecimal password with `openssl rand -hex 32`; restrict access to the file with `chmod 600 .env`. The file is ignored by Git. Do not use these development credentials in production.

```sh
docker compose up -d --wait
docker compose ps
```

The PostgreSQL image uses the database name, user and password only when initializing a new empty volume. Editing `.env` later does not update credentials in an existing database. `docker compose stop` stops the service and preserves its data; do not remove the volume unless its data may be deleted.

For an API running on the host, provide the connection string through an environment variable or another secret store; never commit it. Example format (replace the placeholder locally with the same password used to initialize PostgreSQL):

```sh
export ConnectionStrings__AutoBodyService='Host=127.0.0.1;Port=55432;Database=autobodyservice_dev;Username=autobodyservice_dev;Password=<local-password>'
dotnet run --project backend/src/AutoBodyService.Api
```

Match the host port, database and user to `.env`. Apply the migration only after verifying the target is the dedicated development database:

```sh
dotnet ef database update --project backend/src/AutoBodyService.Infrastructure --startup-project backend/src/AutoBodyService.Api
```

This command requires `dotnet-ef` compatible with EF Core 10. The API does not apply migrations automatically. Its health endpoint reports API availability only, not database readiness.

## First SuperAdmin

After applying the migration, an operator can initialize the first SuperAdmin through a one-time local command. It requires an empty users table, takes a database lock, creates the user and assigns the role in one transaction. The phone must already be in `+380` format followed by nine digits; the password must satisfy the configured Identity policy (at least 12 characters with upper- and lowercase letters, a digit and a non-alphanumeric character). Use a secret manager or environment variables; do not put credentials in source files, command arguments, logs, or shell history. For an interactive shell, the password can be entered without echo:

```sh
read -r 'InitialSuperAdmin__Name?Administrator name: '
read -r 'InitialSuperAdmin__Phone?Phone (+380...): '
read -rs 'InitialSuperAdmin__Password?Password: '
echo
export InitialSuperAdmin__Name InitialSuperAdmin__Phone InitialSuperAdmin__Password
dotnet run --project backend/src/AutoBodyService.Api -- --initialize-superadmin
unset InitialSuperAdmin__Name InitialSuperAdmin__Phone InitialSuperAdmin__Password
```

The `read` syntax above is for zsh. Remove the bootstrap secrets from the secret store after successful initialization. This command does not expose an HTTP endpoint and refuses to run once any user exists. Administrative account-management flows are not implemented yet.

See [frontend/README.md](frontend/README.md) for frontend commands.

## Local HTTPS frontend and API

The browser uses `https://localhost:4200` and same-origin `/api` URLs. Angular's development server forwards `/api/**` to `https://localhost:7097` and verifies the API certificate against the local certificate file. Production hosting must provide its own HTTPS reverse proxy for the same `/api` path; the Angular dev proxy is not a production server.

For local development, create a dedicated self-signed localhost certificate and private key (do not overwrite an existing pair). These files are Git-ignored; keep the directory and key private. This requires OpenSSL with `-addext` support:

```sh
mkdir -p frontend/.local-https
chmod 700 frontend/.local-https
test ! -e frontend/.local-https/localhost.pem && test ! -e frontend/.local-https/localhost.key && \
  (umask 077; openssl req -x509 -newkey rsa:2048 -sha256 -nodes \
    -keyout frontend/.local-https/localhost.key -out frontend/.local-https/localhost.pem \
    -days 365 -subj '/CN=localhost' -addext 'subjectAltName=DNS:localhost,IP:127.0.0.1')
```

The certificate is not automatically trusted by your browser. To use the UI without a certificate warning, explicitly trust this local certificate in your development machine's trust store, following your OS/browser instructions. Never distribute its private key or use it in production. The Playwright browser ignores the frontend certificate warning only in tests; the dev proxy itself validates the API certificate.

Start the verified development database with `docker compose up -d --wait`. In a shell at the repository root, set `ConnectionStrings__AutoBodyService` as described above, then start the API with the local certificate:

```sh
export Kestrel__Certificates__Default__Path="$PWD/frontend/.local-https/localhost.pem"
export Kestrel__Certificates__Default__KeyPath="$PWD/frontend/.local-https/localhost.key"
dotnet run --project backend/src/AutoBodyService.Api --launch-profile https
```

In a second shell, run `cd frontend && npm ci && npm start`. With all three services running, `curl --cacert frontend/.local-https/localhost.pem https://localhost:4200/api/health` checks the HTTPS proxy from the repository root. Set `E2E_LIVE_API=1` when running `npm run test:e2e` from `frontend` to include the real backend request. The health response confirms API availability only; it does not check PostgreSQL.

The API accepts same-origin browser requests; no cross-origin origins are permitted. Before registration or login, call `GET /api/auth/antiforgery`. Angular reads the resulting `XSRF-TOKEN` cookie and sends it in the `RequestVerificationToken` header on unsafe requests. Obtain a fresh token after login or logout because antiforgery tokens are bound to the current identity. `POST /api/auth/register` accepts name, phone and password and creates only the `User` role; it does not sign the user in. Registration and login accept Ukrainian numbers in `+380`, `380` or local `0` form, with optional spaces, parentheses and hyphens. `POST /api/auth/login` issues a persistent, sliding seven-day cookie; `GET /api/auth/session` returns the current account, and `POST /api/auth/logout` signs out. Valid registration requests return the same `202 Accepted` response for a newly created account and an already registered phone; sign in separately. Invalid registrations receive a generic error.

Unsafe `/api/auth` requests are limited to 10 per minute per remote IP on each application instance. Identity also locks an account temporarily after five failed password attempts. Configure any production reverse proxy and its trusted forwarded addresses explicitly before relying on IP-based limits; never trust arbitrary forwarded headers. A distributed deployment needs a shared rate limiter for a fleet-wide limit.

Identity validates the security stamp on each authenticated HTTP request. Changes to roles and `IsBlocked` made through `ApplicationDbContext` update the stamp with the same database save; Identity password resets update it as well. Future account-management operations must use those paths and handle concurrency failures; direct SQL or bulk updates bypass this mechanism. A `MustChangePassword` account can only access session, logout, antiforgery and `POST /api/auth/change-password`. Both ordinary and temporary password changes require the current password and a new password matching the Identity policy. On success, the current cookie is refreshed and other cookies are revoked; the API clears the mandatory-change flag with the password update. `GET /api/profile` returns only the current account name and phone, and `PATCH /api/profile` accepts only a name; changing a phone requires staff assistance. Existing SignalR connections will need their own revocation handling when the hub is implemented.

Production must persist and protect ASP.NET Core Data Protection keys across restarts and share them securely across instances. Without durable keys, restarting or moving between instances can invalidate cookies before seven days. HTTPS termination, same-origin routing and key storage must be configured for the deployment.


## Identity integration tests

Run `bash scripts/test-identity-postgres.sh` with Docker available to create an isolated temporary PostgreSQL 18 container, apply the real migrations and run the backend suite, including Identity HTTP integration tests against the application's configured services. The script removes only its own container and uses no development database. Without this script, PostgreSQL-specific tests are reported as skipped by the regular test run.

Logout stores a hash of the session identifier until the cookie's expiry plus a sliding window; expired revocations are cleaned up on a later logout. A database migration is required before starting this version. Cookies issued before session identifiers were introduced will require users to sign in again after deployment. Protect and persist Data Protection keys as described above.
