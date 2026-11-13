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

Identity validates the security stamp on each authenticated HTTP request. Changes to roles and `IsBlocked` made through `ApplicationDbContext` update the stamp with the same database save; Identity password resets update it as well. Future account-management operations must use those paths and handle concurrency failures; direct SQL or bulk updates bypass this mechanism. A `MustChangePassword` account can access session, logout, antiforgery, `POST /api/auth/change-password`, and the public read-only contacts endpoint. Both ordinary and temporary password changes require the current password and a new password matching the Identity policy. On success, the current cookie is refreshed and other cookies are revoked; the API clears the mandatory-change flag with the password update. `GET /api/profile` returns only the current account name and phone, and `PATCH /api/profile` accepts only a name; changing a phone requires staff assistance. Existing SignalR connections will need their own revocation handling when the hub is implemented.

Production must persist and protect ASP.NET Core Data Protection keys across restarts and share them securely across instances. Without durable keys, restarting or moving between instances can invalidate cookies before seven days. HTTPS termination, same-origin routing and key storage must be configured for the deployment.


## Identity integration tests

Run `bash scripts/test-identity-postgres.sh` with Docker available to create an isolated temporary PostgreSQL 18 container, apply the real migrations and run the backend suite, including Identity HTTP integration tests against the application's configured services. The script removes only its own container and uses no development database. Without this script, PostgreSQL-specific tests are reported as skipped by the regular test run.

Logout stores a hash of the session identifier until the cookie's expiry plus a sliding window; expired revocations are cleaned up on a later logout. A database migration is required before starting this version. Cookies issued before session identifiers were introduced will require users to sign in again after deployment. Protect and persist Data Protection keys as described above.

## Public service contacts

Configure `PublicContacts__Phone` with the service's real Ukrainian phone number (international `+380` and nine digits). This is required to show a usable phone link for access recovery; no real number is included in the repository. `PublicContacts__Address` and `PublicContacts__WorkingHours` are optional public text. Set these environment variables in the API deployment or the equivalent `PublicContacts` configuration section. Only these three fields are exposed by `GET /api/public/contacts`; do not put private information in them. Empty or invalid phone values are returned as null and the interface explicitly indicates that the phone has not been configured.

The admin weekly inspection schedule is available at `GET/PUT /api/admin/inspection-schedule` to Admin and SuperAdmin. GET returns the current `version`, `timeZone` (`Europe/Kyiv`), and local `intervals`. PUT replaces the entire list and requires the version returned by GET and the usual antiforgery token. An empty list closes all days; no default business hours are seeded. Each interval must start and end on the same local day, use whole minutes, have positive duration divisible by 30 minutes, and not overlap another interval on that day; adjacent intervals are allowed. A stale version returns 409 and requires the client to reload. Schedule changes do not modify appointments. Booking availability and warnings about affected bookings belong to later functionality.


`GET /api/admin/inspection-exceptions?date=YYYY-MM-DD` returns the shared schedule version, date exception, and effective `templateSlots` (local start and UTC instant), before filtering bookings or past times. `PUT` replaces the rule for one date; `exception: null` removes it. `Weekly` uses the weekly template, `Closed` closes the entire day, and `Replace` uses the supplied date intervals. Individual `Open` or `Closed` half-hour slot rules override overlapping template slots. Intervals and slot rules cannot overlap within their respective lists, and times are local to Europe/Kyiv. Nonexistent local slots are omitted; repeated local times can yield two UTC instants. Slots crossing a daylight-saving transition are omitted. Date rules share the weekly schedule's version, so stale changes return 409.

**Booking integration is not implemented:** the API returns `bookingConflictsChecked: false` and a warning. It cannot identify affected appointments or provide client contacts; do not treat `templateSlots` as bookable availability.

`GET /api/inspection-slots?date=YYYY-MM-DD` requires an authenticated user. It returns the Kyiv calendar date, time-zone identifier, and future 30-minute slots as UTC start/end instants, excluding all non-cancelled appointments that overlap a slot. This is an advisory read, not a reservation: creating an appointment must revalidate availability transactionally and handle database conflicts. The appointment table has a partial unique index for identical non-cancelled start instants; the future booking workflow must also coordinate with schedule updates and prevent overlapping slots after schedule changes. Repair-plan persistence and appointment creation are not implemented yet.

`POST /api/appointments` creates an inspection for the signed-in account. Submit a future UTC `inspectionStartUtc` returned by the slots API, together with `vin`, `make`, `model`, and `problemDescription`; refresh the antiforgery token first. An unavailable slot returns 409. PostgreSQL serializes creation against the schedule row and enforces unique active start times. The appointment, initial history, per-recipient creation notifications, and applicable future reminders are committed together. Notification history delivery, SignalR, reminder processing, and the appointment detail GET referenced by the `Location` header are not implemented yet.

### Notification integration boundaries

`GET /api/notifications` returns the signed-in recipient's persisted history, newest first; the optional `unread=true` or `unread=false` filter selects unread or read items. `PATCH /api/notifications/{id}/read` and `POST /api/notifications/read-all` require the application's antiforgery token. Links are returned only when the recipient currently has access to the referenced appointment. Reading does not record a manual customer contact. SignalR delivery is a later integration: commit the notification first, then send to the authenticated recipient, without treating delivery as storage.

Administrative password reset, role changes and blocking are not implemented. In task 47/K8, their actual command handlers must stage recipient-specific, secret-free security notifications in the same database transaction as the Identity change and audit, using a stable operation identifier in each unique `EventKey`; deliver online only after commit. Do not emit events for a failed or rolled-back operation.
