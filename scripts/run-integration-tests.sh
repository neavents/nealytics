#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/.."

# Isolatable for the same reason the benchmark is: scripts/aot-smoke.sh uses the default ports and
# the cleanup below is `down -v`, so running this while an AOT publish is in flight destroys it.
#   INTEGRATION_ISOLATED=1  ->  docker-compose.bench.yml on 9100/8223
COMPOSE_FILE="docker-compose.test.yml"
CH_CONTAINER="nealytics-clickhouse-test"
CH_PORT=9000

if [ "${INTEGRATION_ISOLATED:-0}" = "1" ]; then
  COMPOSE_FILE="docker-compose.bench.yml"
  CH_CONTAINER="nealytics-clickhouse-bench"
  CH_PORT=9100
fi

KEEP_UP="${KEEP_CLICKHOUSE:-0}"

cleanup() {
  if [ "$KEEP_UP" != "1" ]; then
    docker compose -f "$COMPOSE_FILE" down -v >/dev/null 2>&1 || true
  fi
}
trap cleanup EXIT

echo "Starting ClickHouse (docker compose -f $COMPOSE_FILE)..."
docker compose -f "$COMPOSE_FILE" up -d

echo "Waiting for ClickHouse to become healthy..."
for i in $(seq 1 60); do
  status="$(docker inspect -f '{{.State.Health.Status}}' "$CH_CONTAINER" 2>/dev/null || echo starting)"
  if [ "$status" = "healthy" ]; then
    echo "ClickHouse ready after ${i}s"
    break
  fi
  sleep 1
done

export TelemetryEngine__ClickHouseConnectionString="Host=127.0.0.1;Port=${CH_PORT};Database=nealytics_core;User=default;Password=;"
export TelemetryEngine__JwtSymmetricKey="local-integration-test-key-at-least-32-bytes!!"
export TelemetryEngine__AllowedProjectKeys="test-key-1,test-key-2"

echo "Running integration tests..."
dotnet test tests/Nealytics.Engine.Tests.Integration "$@"
