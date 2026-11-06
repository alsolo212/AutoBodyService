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

Match the host port, database and user to `.env`. The API registers an empty EF Core context with the Npgsql provider; it does not connect at startup, create tables, or apply migrations. The existing API health endpoint reports API availability only, not database readiness.

See [frontend/README.md](frontend/README.md) for frontend commands.
