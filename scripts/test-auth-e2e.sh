#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
source scripts/activate-dotnet.sh

container="autobody-ui-verify-$(openssl rand -hex 8)"
password="$(openssl rand -hex 24)"
test_dir="$(mktemp -d "${TMPDIR:-/tmp}/autobody-auth-e2e.XXXXXXXX")"
container_id=""
api_pid=""
cleanup() {
  if [[ -n "$api_pid" ]]; then kill "$api_pid" 2>/dev/null || true; wait "$api_pid" 2>/dev/null || true; fi
  if [[ -n "$container_id" ]]; then docker rm -f "$container_id" >/dev/null 2>&1 || true; fi
  rm -rf -- "$test_dir"
}
trap cleanup EXIT

# Both the database and certificate are created exclusively for this test run.
container_id=$(docker run -d --rm --name "$container" --label autobody.auth-e2e.disposable=true \
  -e POSTGRES_USER=auth_test -e POSTGRES_DB=auth_test \
  -e POSTGRES_PASSWORD="$password" -p 127.0.0.1::5432 postgres:18.6)
for _ in {1..60}; do
  if docker exec "$container_id" pg_isready -U auth_test -d auth_test >/dev/null 2>&1; then break; fi
  sleep 1
done
docker exec "$container_id" pg_isready -U auth_test -d auth_test >/dev/null
db_port=$(docker port "$container_id" 5432/tcp | awk -F: 'NR == 1 {print $NF}')
export ConnectionStrings__AutoBodyService="Host=127.0.0.1;Port=$db_port;Database=auth_test;Username=auth_test;Password=$password"
export ASPNETCORE_ENVIRONMENT=Development DOTNET_ENVIRONMENT=Development
# Apply migrations only to the disposable container created above.
dotnet ef database update --project backend/src/AutoBodyService.Infrastructure --startup-project backend/src/AutoBodyService.Api

# Bootstrap a synthetic administrator only in this newly created test database.
export E2E_ADMIN_PHONE="+380000000099"
export E2E_ADMIN_PASSWORD="Example-password-123!"
(cd backend/src/AutoBodyService.Api && \
  InitialSuperAdmin__Name="E2E test" InitialSuperAdmin__Phone="$E2E_ADMIN_PHONE" \
  InitialSuperAdmin__Password="$E2E_ADMIN_PASSWORD" \
  "$DOTNET_ROOT/dotnet" bin/Debug/net10.0/AutoBodyService.Api.dll --initialize-superadmin)

(umask 077; openssl req -x509 -newkey rsa:2048 -sha256 -nodes \
  -keyout "$test_dir/localhost.key" -out "$test_dir/localhost.pem" \
  -days 1 -subj '/CN=localhost' -addext 'subjectAltName=DNS:localhost,IP:127.0.0.1' >/dev/null 2>&1)
free_port() { python3 -c 'import socket; s=socket.socket(); s.bind(("127.0.0.1",0)); print(s.getsockname()[1]); s.close()'; }
export E2E_TLS_CERT="$test_dir/localhost.pem"
export E2E_TLS_KEY="$test_dir/localhost.key"
export E2E_API_CERT="$E2E_TLS_CERT"
export Kestrel__Certificates__Default__Path="$E2E_TLS_CERT"
export Kestrel__Certificates__Default__KeyPath="$E2E_TLS_KEY"
export ASPNETCORE_URLS="https://127.0.0.1:0"
# Synthetic contact used exclusively by the isolated E2E deployment.
export E2E_TEST_CONTACT_PHONE="+380000000000"
export PublicContacts__Phone="$E2E_TEST_CONTACT_PHONE"
# Every spec gets a fresh API process and its own in-memory rate limiter. The
# disposable database is shared only within this run; generated users are unique.
api_connection="$ConnectionStrings__AutoBodyService"
api_certificate="$Kestrel__Certificates__Default__Path"
api_key="$Kestrel__Certificates__Default__KeyPath"
unset ConnectionStrings__AutoBodyService Kestrel__Certificates__Default__Path Kestrel__Certificates__Default__KeyPath ASPNETCORE_URLS
export PROFILE_VERIFY_CONTAINER="$container"
export E2E_REAL_AUTH=1 E2E_LIVE_AUTH=1 E2E_LIVE_PROFILE=1 E2E_LIVE_API=1

specs=(frontend/e2e/*.spec.ts)
if [[ -n "${E2E_SPEC:-}" ]]; then
  if [[ ! "$E2E_SPEC" =~ ^frontend/e2e/[a-zA-Z0-9._-]+\.spec\.ts$ ]] || [[ ! -f "$E2E_SPEC" ]]; then
    echo 'E2E_SPEC must name an existing spec under frontend/e2e.' >&2
    exit 1
  fi
  specs=("$E2E_SPEC")
fi

for spec in "${specs[@]}"; do
  : >"$test_dir/api.log"
  (cd backend/src/AutoBodyService.Api && \
    ConnectionStrings__AutoBodyService="$api_connection" \
    Kestrel__Certificates__Default__Path="$api_certificate" \
    Kestrel__Certificates__Default__KeyPath="$api_key" \
    ASPNETCORE_URLS="https://127.0.0.1:0" \
    exec "$DOTNET_ROOT/dotnet" bin/Debug/net10.0/AutoBodyService.Api.dll >"$test_dir/api.log" 2>&1) &
  api_pid=$!
  api_port=""
  for _ in {1..60}; do
    if ! kill -0 "$api_pid" 2>/dev/null; then echo 'The isolated API failed to start.' >&2; exit 1; fi
    api_port=$(sed -nE 's/.*Now listening on: https:\/\/127\.0\.0\.1:([0-9]+).*/\1/p' "$test_dir/api.log" | head -n 1)
    if [[ "$api_port" =~ ^[0-9]+$ ]] && curl --silent --fail --cacert "$E2E_TLS_CERT" "https://127.0.0.1:$api_port/api/health" >/dev/null 2>&1; then break; fi
    sleep 1
  done
  if [[ ! "$api_port" =~ ^[0-9]+$ ]] || ! curl --silent --fail --cacert "$E2E_TLS_CERT" "https://127.0.0.1:$api_port/api/health" >/dev/null; then echo 'The isolated API is not ready.' >&2; exit 1; fi
  export E2E_API_TARGET="https://127.0.0.1:$api_port"
  export E2E_FRONTEND_PORT="$(free_port)"
  while [[ "$E2E_FRONTEND_PORT" == "$api_port" ]]; do E2E_FRONTEND_PORT="$(free_port)"; done
  echo "Running $spec against a fresh isolated API instance"
  (cd frontend && npm run test:e2e -- "${spec#frontend/}" --workers=1 --retries=0)
  kill "$api_pid"
  wait "$api_pid" 2>/dev/null || true
  api_pid=""
done
