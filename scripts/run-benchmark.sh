#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/.."

# Usage: ./scripts/run-benchmark.sh [mode]
#   mode: noop | track | beacon | timeline | timeseries | active | top | all   (default: track)
# Env knobs:
#   OVERDRIVE=1            crank concurrency + batch to find the limit
#   BENCH_DURATION=15      seconds per level (0 => use BENCH_REQUESTS)
#   BENCH_CONCURRENCY=...  comma list (overrides tier default)
#   BENCH_REQUESTS=40000   requests/level when BENCH_DURATION=0
#   BENCH_BEACON_BATCH=50  events per beacon request
#   BENCH_WAL_DIR=/path    point at tmpfs vs SSD vs PVC to sweep WAL storage
#   BENCH_RATE_LIMIT=...   ingestion permits (default effectively unlimited)
#   KEEP_CLICKHOUSE=1      leave ClickHouse up afterwards

MODE="${1:-track}"

if [ "${OVERDRIVE:-0}" = "1" ]; then
  CONCURRENCY="${BENCH_CONCURRENCY:-16,64,256,512,1024,2048,4096}"
  DURATION="${BENCH_DURATION:-15}"
  BEACON_BATCH="${BENCH_BEACON_BATCH:-200}"
else
  CONCURRENCY="${BENCH_CONCURRENCY:-1,8,32,64,128,256,512}"
  DURATION="${BENCH_DURATION:-10}"
  BEACON_BATCH="${BENCH_BEACON_BATCH:-50}"
fi
REQUESTS="${BENCH_REQUESTS:-40000}"
WARMUP="${BENCH_WARMUP:-3000}"
WAL_DIR="${BENCH_WAL_DIR:-$(mktemp -d)/wal}"
COMPOSE_FILE="docker-compose.test.yml"
KEEP_UP="${KEEP_CLICKHOUSE:-0}"
PORT="${BENCH_PORT:-5199}"

export TelemetryEngine__ClickHouseConnectionString="Host=127.0.0.1;Port=9000;Database=nealytics_core;User=default;Password=;"
export TelemetryEngine__JwtSymmetricKey="local-integration-test-key-at-least-32-bytes!!"
export TelemetryEngine__AllowedProjectKeys="test-key-1,test-key-2"
export TelemetryEngine__WriteAheadLogDirectory="$WAL_DIR"
export TelemetryEngine__RateLimitPermitCount="${BENCH_RATE_LIMIT:-1000000000}"
export TelemetryEngine__RateLimitWindowSeconds=1
export TelemetryEngine__RateLimitQueueSize=10000000
export TelemetryEngine__MemoryChannelCapacity=2000000
export TelemetryEngine__DatabaseBatchCommitSize=20000
export TelemetryEngine__ForceFlushIntervalSeconds=1
export TelemetryEngine__MaxConcurrentConnections=100000
export ASPNETCORE_URLS="http://127.0.0.1:${PORT}"
export ASPNETCORE_ENVIRONMENT="Production"
export DOTNET_gcServer=1

API_PID=""
cleanup() {
  if [ -n "$API_PID" ]; then
    kill "$API_PID" >/dev/null 2>&1 || true
    wait "$API_PID" 2>/dev/null || true
  fi
  if [ "$KEEP_UP" != "1" ]; then
    docker compose -f "$COMPOSE_FILE" down -v >/dev/null 2>&1 || true
  fi
}
trap cleanup EXIT

echo "== Starting ClickHouse =="
docker compose -f "$COMPOSE_FILE" up -d
for i in $(seq 1 60); do
  status="$(docker inspect -f '{{.State.Health.Status}}' nealytics-clickhouse-test 2>/dev/null || echo starting)"
  [ "$status" = "healthy" ] && break
  sleep 1
done

echo "== Starting Nealytics API (port $PORT, WAL: $WAL_DIR, ServerGC) =="
mkdir -p "$WAL_DIR"
dotnet run -c Release --no-launch-profile --project src/Nealytics.Engine/Nealytics.Engine.csproj >/tmp/nealytics-bench-api.log 2>&1 &
API_PID=$!

GIT_SHA="$(git rev-parse --short HEAD 2>/dev/null || echo unknown)"
[ -n "$(git status --porcelain 2>/dev/null)" ] && GIT_SHA="${GIT_SHA}+dirty"
export GIT_SHA

run_mode() {
  local m="$1"
  echo "== Benchmark: mode=$m concurrency=$CONCURRENCY duration=${DURATION}s/level =="
  dotnet run -c Release --project bench/Nealytics.Engine.Bench/Nealytics.Engine.Bench.csproj -- \
    --url "http://127.0.0.1:${PORT}" \
    --mode "$m" \
    --key "test-key-1" \
    --concurrency "$CONCURRENCY" \
    --duration "$DURATION" \
    --requests "$REQUESTS" \
    --warmup "$WARMUP" \
    --beacon-batch "$BEACON_BATCH" \
    --out "bench/RESULTS.md"
}

if [ "$MODE" = "all" ]; then
  for m in noop track beacon; do
    run_mode "$m"
  done
else
  run_mode "$MODE"
fi

echo "== Done. Results appended to bench/RESULTS.md =="
