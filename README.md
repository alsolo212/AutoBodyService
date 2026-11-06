# AutoBodyService

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

The `read` syntax above is for zsh. Remove the bootstrap secrets from the secret store after successful initialization. This command does not expose an HTTP endpoint and refuses to run once any user exists. Registration, login, CSRF validation for future state-changing endpoints, and account-management flows are not implemented yet; they must enforce the configured lockout, persistent sign-in, blocked-account and password-change rules.

See [frontend/README.md](frontend/README.md) for frontend commands.
