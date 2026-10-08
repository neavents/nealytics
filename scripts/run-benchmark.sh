#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/.."

# Usage: ./scripts/run-benchmark.sh [mode]
#   mode: noop | track | beacon | timeline | timeseries | active | top | all   (default: track)
#         read   seeds one tenant, then measures every read endpoint against it, including the
#                same breakdown answered from raw and from a rollup
#         set    seeds 1,000 tenants in one set among 3,000 others, then measures questions asked
#                over the whole set, from raw and from a tenant-attribute rollup
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

# The set mode answers one question over a tenant set: BENCH_SET_TENANTS tenants share the parent
# the token names, and BENCH_SET_NOISE_TENANTS more belong to other parents, so the measured query
# has to prune them out of the same table rather than read a table that only holds the set.
if [ "$MODE" = "set" ]; then
  export TelemetryEngine__SchemaFile="$(pwd)/bench/bench-set-schema.json"
  export TelemetryEngine__IngestionKeys__0__Key="bench-server-key"
  export TelemetryEngine__IngestionKeys__0__Scope="server"
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
if [ "$MODE" = "read" ] || [ "$MODE" = "set" ]; then
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
elif [ "$MODE" = "set" ]; then
  SET_TENANTS="${BENCH_SET_TENANTS:-1000}"
  NOISE_TENANTS="${BENCH_SET_NOISE_TENANTS:-3000}"
  EVENTS_PER_TENANT="${BENCH_SET_EVENTS_PER_TENANT:-400}"
  ALL_TENANTS=$((SET_TENANTS + NOISE_TENANTS))

  for i in $(seq 1 60); do
    curl -sf "http://127.0.0.1:${PORT}/ready" >/dev/null 2>&1 && break
    sleep 1
  done

  echo "== Declaring ${SET_TENANTS} tenants under parent org-bench and ${NOISE_TENANTS} under other parents =="
  python3 - "$SET_TENANTS" "$ALL_TENANTS" > /tmp/nealytics-bench-attributes.json <<'PY'
import json, sys
in_set, total = int(sys.argv[1]), int(sys.argv[2])
tenants = [{"tenantId": f"v{i}", "attributes": {"parent": "org-bench" if i < in_set else f"org-{i % 97}", "region": f"r{i % 8}"}} for i in range(total)]
print(json.dumps({"projectId": "bench", "tenants": tenants}))
PY
  curl -sf -X POST "http://127.0.0.1:${PORT}/api/v1/tenants/attributes" \
    -H "Content-Type: application/json" -H "X-Project-Key: bench-server-key" \
    --data-binary @/tmp/nealytics-bench-attributes.json
  echo

  echo "== Seeding $((ALL_TENANTS * EVENTS_PER_TENANT)) sale events straight into ClickHouse =="
  docker exec "$CH_CONTAINER" clickhouse-client -q "
    INSERT INTO nealytics_core.global_events
      (event_id, project_id, tenant_id, session_id, event_type, metadata_json, timestamp, currency, category_id, amount)
    SELECT generateUUIDv4(), 'bench', concat('v', toString(number % ${ALL_TENANTS})),
           concat('s', toString(intDiv(number, 3))), 'sale', '{}',
           toDateTime64('2026-03-01 00:00:00', 3, 'UTC') + toIntervalSecond(number % (180 * 86400)),
           if(number % 10 = 0, 'EUR', 'TRY'), concat('c', toString(number % 12)), toDecimal64(number % 500, 2) / 7
    FROM numbers($((ALL_TENANTS * EVENTS_PER_TENANT)))"
  docker exec "$CH_CONTAINER" clickhouse-client -q "OPTIMIZE TABLE nealytics_core.global_events FINAL" || true
  echo "Seeded rows: $(docker exec "$CH_CONTAINER" clickhouse-client -q 'SELECT count() FROM nealytics_core.global_events')"
  echo "Group rollup rows: $(docker exec "$CH_CONTAINER" clickhouse-client -q 'SELECT count() FROM nealytics_core.rollup_daily_by_org')"

  CONCURRENCY="${BENCH_READ_CONCURRENCY:-1,4,16}"
  DURATION="${BENCH_READ_DURATION:-10}"
  WARMUP=0

  for m in set-breakdown set-breakdown-group set-pivot-attribute set-timeseries; do
    run_mode "$m" --read-set "parent:org-bench"
  done
else
  run_mode "$MODE"
fi

echo "== Done. Results appended to bench/RESULTS.md =="
