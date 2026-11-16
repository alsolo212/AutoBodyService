# AutoBodyService

AutoBodyService runs as one Angular SPA, one ASP.NET Core API and one PostgreSQL database. The supported local setup uses the existing Compose service for PostgreSQL; the API and Angular run on the host. Compose does **not** start or order the host processes. Wait for PostgreSQL to be healthy, apply migrations and initialize the first administrator before starting the API, then start Angular.

Commands below assume a Unix-like host, a bash or zsh shell and the repository root unless stated otherwise.

## Prerequisites

- .NET SDK **10.0.403 or a later stable patch in the 10.0.4xx feature band**, plus .NET and ASP.NET Core runtimes **10.0.14 or later within 10.0**. `global.json` selects the SDK. The repository's activation script requires the SDK and runtimes in the ignored `.dotnet` directory. Install them locally with the [official .NET install script](https://learn.microsoft.com/dotnet/core/tools/dotnet-install-script), without changing the system installation.
- Node.js **24.21.0** as pinned in `.nvmrc`, npm, and the lockfile-installed Angular 22 dependencies. The installed Angular build package accepts Node `^22.22.3`, `^24.15.0` or `>=26.0.0`; the pinned Node version is the tested choice.
- Docker Engine with Compose, PostgreSQL image **18.6**, OpenSSL with `-addext`, curl, Python 3 and a Playwright Chromium installation for the live browser tests. Docker must be running.
- `dotnet-ef` 10.0.x on `PATH`, preferably the patch matching the EF Core packages (10.0.14). For an isolated local tool installation, from the repository root:

```sh
source scripts/activate-dotnet.sh
dotnet tool install --tool-path .dotnet/tools dotnet-ef --version 10.0.14
export PATH="$PWD/.dotnet/tools:$PATH"
dotnet ef --version
```

Activate the SDK and add the tool directory to `PATH` in each new shell. The local tool installation is ignored by Git. Existing `dotnet-ef` 10.0.0 also runs the migrations but emits a version warning against the 10.0.14 runtime.

## Prepare the development database

Create `.env` from `.env.example` **only if it does not already exist**. Edit it locally: set a unique random `POSTGRES_PASSWORD` from a secure generator, and choose a database/user/port if the example values are unsuitable. Restrict the file to its owner and never commit or reuse it in production. Keep these values as simple one-line Compose environment values.

```sh
test ! -e .env && cp .env.example .env
chmod 600 .env
# Edit .env locally and set POSTGRES_PASSWORD before continuing.
docker compose up -d --wait
docker compose ps
```

The existing Compose project `autobodyservice-dev` publishes PostgreSQL only on `127.0.0.1` (default port `55432`) and uses a named volume. Its `pg_isready` healthcheck proves the server accepts connections, not that application migrations or data are present. `--wait` waits for that healthcheck. The image reads `POSTGRES_DB`, `POSTGRES_USER` and `POSTGRES_PASSWORD` only when initializing an empty volume: editing `.env` later does not change an existing database or its password. `docker compose stop` preserves the database. Do not use `down -v` or remove a volume containing data that must be kept.

Before any migration, verify the host, port, database and user point to the **dedicated development database**, not an existing production or unrelated database. In a new shell at the repository root, activate the SDK, then enter the database password without echo. The example assumes the unmodified database/user/port from `.env.example`; change those non-secret values to match your own `.env`:

```sh
source scripts/activate-dotnet.sh
export PATH="$PWD/.dotnet/tools:$PATH"
printf 'Development database password: '
read -r -s DEV_DB_PASSWORD
printf '\n'
export ConnectionStrings__AutoBodyService="Host=127.0.0.1;Port=55432;Database=autobodyservice_dev;Username=autobodyservice_dev;Password=$DEV_DB_PASSWORD"
unset DEV_DB_PASSWORD
```

Use a trusted environment or secret manager for configuration; do not put a real connection string in a tracked file, command argument, shell history or log. `ConnectionStrings__AutoBodyService` must be available to the EF command, the bootstrap command and the API process. It is inherited by child processes of this shell; unset it when no longer needed.

## Migrations and first SuperAdmin

The API does **not** apply migrations on startup. EF Core applies the checked-in migrations in order, using `__EFMigrationsHistory` to determine what is pending. On the verified development database, from the configured shell:

```sh
dotnet ef migrations list --project backend/src/AutoBodyService.Infrastructure --startup-project backend/src/AutoBodyService.Api
dotnet ef database update --project backend/src/AutoBodyService.Infrastructure --startup-project backend/src/AutoBodyService.Api
```

The latest checked-in migration is `20261115123315_MonthlyExpense`; check the repository again when upgrading. Back up an existing database and verify the target and migration plan before updating it. Do not create a new initial migration or run an automatic migration against an unknown database.

Only on a newly migrated database with an empty users table, initialize the first SuperAdmin once. The command takes a database lock and creates the account and role in one transaction; it refuses to run if any user exists. Supply a name, a phone in `+380` plus nine digits and a unique password of at least 12 characters with upper- and lowercase letters, a digit and a non-alphanumeric character. Enter the password without echo and never place it in Git or an argument:

```sh
printf 'Administrator name: '; read -r InitialSuperAdmin__Name
printf 'Phone (+380...): '; read -r InitialSuperAdmin__Phone
printf 'Password: '; read -r -s InitialSuperAdmin__Password; printf '\n'
export InitialSuperAdmin__Name InitialSuperAdmin__Phone InitialSuperAdmin__Password
dotnet run --project backend/src/AutoBodyService.Api -- --initialize-superadmin
unset InitialSuperAdmin__Name InitialSuperAdmin__Phone InitialSuperAdmin__Password
```

The bootstrap is a local command, not a public endpoint. Transfer account credentials through an appropriate protected channel. Existing installations use the application's Admin/SuperAdmin account-management flows; do not rerun bootstrap to reset an account.

## Local HTTPS and start

Angular serves `https://localhost:4200` and forwards same-origin `/api/**` and `/hubs/**` to the API at `https://localhost:7097`. Its development proxy validates the API certificate and supports the SignalR WebSocket path. It is not a production server. Create a dedicated local certificate **only if neither file already exists**:

```sh
mkdir -p frontend/.local-https
chmod 700 frontend/.local-https
test ! -e frontend/.local-https/localhost.pem && test ! -e frontend/.local-https/localhost.key && \
  (umask 077; openssl req -x509 -newkey rsa:2048 -sha256 -nodes \
    -keyout frontend/.local-https/localhost.key -out frontend/.local-https/localhost.pem \
    -days 365 -subj '/CN=localhost' -addext 'subjectAltName=DNS:localhost,IP:127.0.0.1')
```

The private key is local and ignored by Git. The browser must trust the development certificate through the OS/browser trust store; do not disable TLS validation to work around a certificate problem. Remove that trust when it is no longer needed. Neither the certificate nor key is suitable for production.

In the shell with the verified connection string, start the API **after** PostgreSQL, migrations and bootstrap:

```sh
export Kestrel__Certificates__Default__Path="$PWD/frontend/.local-https/localhost.pem"
export Kestrel__Certificates__Default__KeyPath="$PWD/frontend/.local-https/localhost.key"
# Optional public service contact, configured locally; no real contact is stored in Git.
# export PublicContacts__Phone='+380...'  # use an actual configured service number
# PublicContacts__Address and PublicContacts__WorkingHours are optional public text.
dotnet run --project backend/src/AutoBodyService.Api --launch-profile https
```

Configure a valid `PublicContacts__Phone` for a usable contact and access-recovery link; an empty or invalid value leaves the phone unavailable in the UI. The address and working hours are optional public text.

In a second shell, use the pinned Node version and start the SPA:

```sh
cd frontend
npm ci
npm start
```

Open `https://localhost:4200`. From the repository root, these check certificate validation, the Angular-to-API proxy and a database-backed public read:

```sh
curl --fail --cacert frontend/.local-https/localhost.pem https://localhost:4200/api/health
curl --fail --cacert frontend/.local-https/localhost.pem https://localhost:4200/api/prices
```

`/api/health` checks API availability, **not** PostgreSQL connectivity. A real sign-in and booking against the isolated test database provide a stronger end-to-end check than the `/api/prices` read. The `https` launch profile also listens on HTTP port 5260 and redirects to HTTPS; when the API has no usable HTTPS redirect target, plaintext requests are rejected.

## Tests and diagnostics

From the repository root, with the SDK activated and frontend dependencies/Chromium installed:

```sh
dotnet build backend/AutoBodyService.slnx
bash scripts/test-identity-postgres.sh
(cd frontend && npm run build && npm test -- --watch=false)
bash scripts/test-auth-e2e.sh
```

`bash scripts/test-identity-postgres.sh` starts its own disposable PostgreSQL 18 container, runs the complete xUnit suite with PostgreSQL integration tests and removes only that container. A plain `dotnet test backend/AutoBodyService.slnx` without the test database reports PostgreSQL-specific tests as skipped. Install Chromium once with `(cd frontend && npx playwright install chromium)`.

The Playwright script creates a fresh isolated PostgreSQL database per spec, applies real migrations, bootstraps a synthetic SuperAdmin, and runs API and Angular over HTTPS. It cleans up its own containers and temporary files; it does not use the development database. Run one spec with, for example:

```sh
E2E_SPEC=frontend/e2e/booking-live.spec.ts bash scripts/test-auth-e2e.sh
```

The test browser accepts the ephemeral frontend certificate; the Angular proxy still validates the API certificate. Ordinary `npm run test:e2e` can skip live specs without their environment flags and is not a substitute for the isolated script.

For diagnosis, check `docker compose ps` and `docker compose logs postgres`; inspect API and Angular console output without logging credentials or request bodies. Verify the configured host port and target database, API health, migration history, certificate SAN/trust and proxy target. A 401 means sign-in is required, 403 can indicate insufficient permissions, and 409 means stale or conflicting state that should be reloaded. Restarting the API is not a migration. `docker compose stop` retains the volume.

## Backup and test restore

Backups contain personal and business data. Store them outside the repository, restrict access, encrypt them according to the deployment's policy and protect encryption keys separately. Choose a new absolute path in a protected directory before running the following; never overwrite an existing backup:

```sh
printf 'New backup file (absolute path): '; read -r BACKUP_FILE
(
  set -eu
  case "$BACKUP_FILE" in /*) ;; *) echo 'Use an absolute path'; exit 1;; esac
  umask 077
  set -C # Refuse to overwrite an existing file.
  docker compose exec -T postgres sh -c 'exec pg_dump -U "$POSTGRES_USER" -d "$POSTGRES_DB" -Fc' > "$BACKUP_FILE"
  docker compose exec -T postgres pg_restore --list < "$BACKUP_FILE" > /dev/null
)
```

The archive listing checks readability, not recoverability. Test restoration into a **separate empty PostgreSQL 18 instance**, never the source database. The following creates a uniquely named disposable container backed by tmpfs, restores without deleting or replacing existing databases, and prints counts for comparison with the source. Use only a synthetic test backup for this exercise; a real backup needs the same security controls as the source data.

```sh
RESTORE_CONTAINER="autobody-restore-$(openssl rand -hex 8)"
RESTORE_PASSWORD="$(openssl rand -hex 32)"
POSTGRES_PASSWORD="$RESTORE_PASSWORD" docker run -d --name "$RESTORE_CONTAINER" --label autobody.restore=disposable \
  --tmpfs /var/lib/postgresql -e POSTGRES_USER=restore_test -e POSTGRES_DB=restore_test \
  -e POSTGRES_PASSWORD postgres:18.6
unset RESTORE_PASSWORD
until docker exec "$RESTORE_CONTAINER" pg_isready -U restore_test -d restore_test; do sleep 1; done
docker exec -i "$RESTORE_CONTAINER" pg_restore -U restore_test -d restore_test --exit-on-error --no-owner --no-acl < "$BACKUP_FILE"
docker exec "$RESTORE_CONTAINER" psql -U restore_test -d restore_test -c \
  'SELECT (SELECT count(*) FROM "__EFMigrationsHistory") AS migrations, (SELECT max("MigrationId") FROM "__EFMigrationsHistory") AS latest_migration, (SELECT count(*) FROM "Appointments") AS appointments;'
docker compose exec -T postgres sh -c 'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -c '\''SELECT (SELECT count(*) FROM "__EFMigrationsHistory") AS migrations, (SELECT max("MigrationId") FROM "__EFMigrationsHistory") AS latest_migration, (SELECT count(*) FROM "Appointments") AS appointments;'\'''
```

Compare counts and application-specific records on a consistent, quiesced source or a single snapshot: the source may change after the dump. Also verify the latest migration and, for real recovery, validate representative records and application access in an isolated environment. When finished, verify the container name and disposable label, then remove **only that test container** with `docker rm -f "$RESTORE_CONTAINER"`; unset `RESTORE_CONTAINER` and `BACKUP_FILE`. Never use this test cleanup for the development container or volume. Data Protection keys and other external secrets require their own protected backup and recovery procedure.

## Production and data boundaries

Public HTTPS deployment is **not supplied or verified** by this repository. The dev proxy, self-signed development certificate, `.env`, local SDK, synthetic accounts and test scripts are not production infrastructure. Use HTTPS for the public origin and preserve same-origin routing for `/api/**` and `/hubs/**`, including WebSockets and the public Host. The current API does not trust forwarded headers by default and does not configure `UseForwardedHeaders`: terminate TLS at the API or use a trusted proxy-to-API HTTPS connection. An HTTP proxy-to-API hop requires explicit application configuration for known proxies/networks and forwarding before HTTPS/security middleware; do not trust arbitrary forwarded headers. Set `AllowedHosts` and the proxy host allowlist to actual hosts.

The Identity cookie is `HttpOnly`, `Secure`, `SameSite=Lax`, persistent for seven days with sliding expiration. The antiforgery cookie is `HttpOnly`, `Secure`, `SameSite=Strict`; the readable `XSRF-TOKEN` is used by Angular as the `RequestVerificationToken` header on unsafe requests. Obtain a fresh token after a change of identity. CORS grants no cross-origin browser access. Configure and protect persistent ASP.NET Core Data Protection keys across restarts and instances; their loss invalidates existing cookies. SignalR events are local to one API process, so multiple API instances need a separately designed secure cross-instance delivery mechanism. Rate limits are per process and require correctly trusted client addresses.

Keep production database credentials, bootstrap credentials, TLS keys and Data Protection keys in a protected secret store, separate from development and tests. `PublicContacts` contains intentionally public contact information only. Neither secrets, customer data, backups, nor the five local instruction/learning Markdown files belong in Git, images or a build context. The current Compose file has **no image build context**; before introducing a Docker build, explicitly exclude those files and local artifacts from its context.

For an update: review changes and migrations, test on an isolated restored copy, take and verify a backup, stop or coordinate application writers, apply the reviewed migrations to the verified target, deploy compatible API and SPA versions, and run HTTPS, authorization, database and SignalR smoke checks. Decide rollback and restore procedures before deployment; rolling back code alone does not reverse database migrations. Do not claim production readiness without validating the actual proxy, certificates, secrets, backup and recovery in that environment.
