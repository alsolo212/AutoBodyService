#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
source scripts/activate-dotnet.sh
container="autobody-identity-test-$(openssl rand -hex 8)"
password="$(openssl rand -hex 24)"
container_id=""
cleanup() { if [[ -n "$container_id" ]]; then docker rm -f "$container_id" >/dev/null 2>&1 || true; fi; }
trap cleanup EXIT

container_id=$(docker run -d --rm --name "$container" --label autobody.identity.disposable=true \
  -e POSTGRES_USER=autobody_identity_test -e POSTGRES_DB=autobody_identity_test \
  -e POSTGRES_PASSWORD="$password" -p 127.0.0.1::5432 postgres:18.6)
for _ in {1..60}; do
  if docker exec "$container" pg_isready -U autobody_identity_test -d autobody_identity_test >/dev/null 2>&1; then break; fi
  sleep 1
done
port=$(docker port "$container" 5432/tcp | awk -F: 'NR == 1 {print $NF}')
export AUTOBODY_IDENTITY_CONTAINER="$container"
export AUTOBODY_IDENTITY_POSTGRES="Host=127.0.0.1;Port=$port;Database=autobody_identity_test;Username=autobody_identity_test;Password=$password"
unset AUTH_TEST_POSTGRES PROFILE_TEST_POSTGRES
dotnet test backend/AutoBodyService.slnx
