#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/.."

# Usage: ./scripts/run-benchmark.sh [mode]
#   mode: noop | track | beacon | timeline | timeseries | active | top | all   (default: track)
#         read   seeds one tenant, then measures every read endpoint against it, including the
#                same breakdown answered from raw and from a rollup
# Env knobs:
#   OVERDRIVE=1            crank concurrency + batch to find the limit
#   BENCH_DURATION=15      seconds per level (0 => use BENCH_REQUESTS)
#   BENCH_CONCURRENCY=...  comma list (overrides tier default)
#   BENCH_REQUESTS=40000   requests/level when BENCH_DURATION=0
#   BENCH_BEACON_BATCH=50  events per beacon request
#   BENCH_WAL_DIR=/path    point at tmpfs vs SSD vs PVC to sweep WAL storage
#   BENCH_RATE_LIMIT=...   ingestion permits (default effectively unlimited)
#   KEEP_CLICKHOUSE=1      leave ClickHouse up afterwards
#   BENCH_SEED_SECONDS=30  seconds of seeding before the read modes run

MODE="${1:-track}"
SEED_TENANT="bench-read"
SEED_SECONDS="${BENCH_SEED_SECONDS:-30}"

# The read modes need a schema, because a rollup only exists if a deployment declares one. This is
# also the only run that exercises the declared-column write path end to end: three dimensions and
# a measure go in through /beacon, the materialized view aggregates them, and the breakdown reads
# them back out of the AggregatingMergeTree.
if [ "$MODE" = "read" ]; then
  export TelemetryEngine__SchemaFile="$(pwd)/bench/bench-schema.json"
fi

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
# Overridable so a benchmark can run on its own ClickHouse rather than fighting the integration
# suite or scripts/smoke-test.sh for the default ports — both of those tear their container down
# with `down -v`, which is a bad way to find out something else was using it.
#   BENCH_ISOLATED=1  ->  docker-compose.bench.yml on 9100/8223
COMPOSE_FILE="${BENCH_COMPOSE_FILE:-docker-compose.test.yml}"
CH_CONTAINER="${BENCH_CH_CONTAINER:-nealytics-clickhouse-test}"
CH_PORT="${BENCH_CH_PORT:-9000}"

if [ "${BENCH_ISOLATED:-0}" = "1" ]; then
  COMPOSE_FILE="docker-compose.bench.yml"
  CH_CONTAINER="nealytics-clickhouse-bench"
  CH_PORT=9100
fi
KEEP_UP="${KEEP_CLICKHOUSE:-0}"
PORT="${BENCH_PORT:-5199}"

export TelemetryEngine__ClickHouseConnectionString="Host=127.0.0.1;Port=${CH_PORT};Database=nealytics_core;User=default;Password=;"
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

# A read run insists on an empty volume. The schema reconciler refuses to boot when the table holds
# a column no declaration mentions and that column has rows in it -- which is exactly what another
# script leaves behind on this same container. Without this the benchmark dies at startup with a
# message about someone else's columns, and the cause is three scripts away.
if [ "$MODE" = "read" ]; then
  echo "== Resetting ClickHouse (a read run needs a table only bench-schema.json describes) =="
  docker compose -f "$COMPOSE_FILE" down -v >/dev/null 2>&1 || true
fi

echo "== Starting ClickHouse =="
docker compose -f "$COMPOSE_FILE" up -d
for i in $(seq 1 60); do
  status="$(docker inspect -f '{{.State.Health.Status}}' "$CH_CONTAINER" 2>/dev/null || echo starting)"
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
  shift
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
    --out "bench/RESULTS.md" \
    "$@"
}

if [ "$MODE" = "all" ]; then
  for m in noop track beacon; do
    run_mode "$m"
  done
elif [ "$MODE" = "read" ]; then
  # Loss verification is off for the seed: every level writes to one tenant, so the stored count is
  # cumulative and a per-level comparison would report a false surplus. The seed is not the
  # measurement -- the read modes below are.
  echo "== Seeding tenant '${SEED_TENANT}' for ${SEED_SECONDS}s =="
  BENCH_SEED_CONCURRENCY="${BENCH_SEED_CONCURRENCY:-64}"
  dotnet run -c Release --project bench/Nealytics.Engine.Bench/Nealytics.Engine.Bench.csproj -- \
    --url "http://127.0.0.1:${PORT}" \
    --mode beacon \
    --key "test-key-1" \
    --concurrency "$BENCH_SEED_CONCURRENCY" \
    --duration "$SEED_SECONDS" \
    --warmup 0 \
    --beacon-batch 200 \
    --declared true \
    --seed-tenant "$SEED_TENANT" \
    --verify-loss false \
    --out /dev/null

  echo "== Waiting for the batch writer to drain =="
  for i in $(seq 1 60); do
    seeded="$(docker exec "$CH_CONTAINER" clickhouse-client -q \
      "SELECT count() FROM nealytics_core.global_events WHERE tenant_id='${SEED_TENANT}'" 2>/dev/null | tr -d '[:space:]')"
    sleep 1
    again="$(docker exec "$CH_CONTAINER" clickhouse-client -q \
      "SELECT count() FROM nealytics_core.global_events WHERE tenant_id='${SEED_TENANT}'" 2>/dev/null | tr -d '[:space:]')"
    [ -n "$seeded" ] && [ "$seeded" = "$again" ] && [ "$seeded" != "0" ] && break
  done
  echo "Seeded rows: ${seeded:-unknown}"

  echo "== Rollup rows (the materialized view's own output) =="
  docker exec "$CH_CONTAINER" clickhouse-client -q \
    "SELECT count() FROM nealytics_core.rollup_daily_by_product WHERE tenant_id='${SEED_TENANT}'" || true

  # Read concurrency is deliberately lower. These are multi-second analytical scans, not 200us
  # writes, and driving 512 of them at once measures ClickHouse's queue rather than the query.
  CONCURRENCY="${BENCH_READ_CONCURRENCY:-1,4,16,64}"
  DURATION="${BENCH_READ_DURATION:-10}"
  WARMUP=0

  for m in breakdown breakdown-rollup breakdown-measure breakdown-measure-rollup timeseries top active timeline; do
    run_mode "$m" --read-tenant "$SEED_TENANT"
  done
else
  run_mode "$MODE"
fi

echo "== Done. Results appended to bench/RESULTS.md =="
