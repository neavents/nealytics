#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/.."

COMPOSE_FILE="docker-compose.test.yml"
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
  status="$(docker inspect -f '{{.State.Health.Status}}' nealytics-clickhouse-test 2>/dev/null || echo starting)"
  if [ "$status" = "healthy" ]; then
    echo "ClickHouse ready after ${i}s"
    break
  fi
  sleep 1
done

export TelemetryEngine__ClickHouseConnectionString="Host=127.0.0.1;Port=9000;Database=nealytics_core;User=default;Password=;"
export TelemetryEngine__JwtSymmetricKey="local-integration-test-key-at-least-32-bytes!!"
export TelemetryEngine__AllowedProjectKeys="test-key-1,test-key-2"

echo "Running integration tests..."
dotnet test tests/Nealytics.Engine.Tests.Integration "$@"
