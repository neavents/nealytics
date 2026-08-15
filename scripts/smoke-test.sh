#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/.."

CH_HTTP="${CLICKHOUSE_HTTP:-http://127.0.0.1:8123}"
PORT="${SMOKE_PORT:-5095}"
OUT="${SMOKE_OUT:-$(mktemp -d)/publish}"
KEY="smoke-test-symmetric-key-at-least-32-bytes!!"
PROJECT_KEY="probe:key123"
PROJECT="smoketest"
TENANT="t1"

ch() { curl -sS --data-binary "$1" "$CH_HTTP/" ; }

echo "Publishing the release binary (${SMOKE_RID:-linux-x64})..."
# Not silenced on failure. Anything that interrupts the publish leaves `dotnet publish` returning
# without a binary, and the script then ran the missing file and reported "engine exited during
# startup", which sends you looking at the engine instead of at the build that never finished.
if ! dotnet publish src/Nealytics.Engine/Nealytics.Engine.csproj -c Release -r "${SMOKE_RID:-linux-x64}" -o "$OUT" >"$OUT.publish.log" 2>&1; then
  echo "FAIL: dotnet publish did not succeed. Last 30 lines:"
  tail -30 "$OUT.publish.log"
  exit 1
fi

if [ ! -x "$OUT/Nealytics.Engine" ]; then
  echo "FAIL: publish reported success but produced no binary at $OUT/Nealytics.Engine."
  echo "Last 30 lines:"
  tail -30 "$OUT.publish.log"
  exit 1
fi

WAL="$(mktemp -d)/wal"
mkdir -p "$WAL"
LOG="$(mktemp)"

ASPNETCORE_URLS="http://127.0.0.1:$PORT" \
TelemetryEngine__ClickHouseConnectionString="Host=127.0.0.1;Port=9000;Database=nealytics_core;User=default;Password=;" \
TelemetryEngine__WriteAheadLogDirectory="$WAL/" \
TelemetryEngine__JwtSymmetricKey="$KEY" \
TelemetryEngine__AllowedProjectKeys="$PROJECT_KEY" \
TelemetryEngine__ForceFlushIntervalSeconds=1 \
TelemetryEngine__MaxInsertRetries=1 \
TelemetryEngine__Dimensions__0__Name=d_string \
TelemetryEngine__Dimensions__0__Type=String \
TelemetryEngine__Dimensions__1__Name=d_lowcard \
TelemetryEngine__Dimensions__1__Type=LowCardinality \
TelemetryEngine__Dimensions__2__Name=d_uint \
TelemetryEngine__Dimensions__2__Type=UInt64 \
TelemetryEngine__Dimensions__3__Name=d_int \
TelemetryEngine__Dimensions__3__Type=Int64 \
TelemetryEngine__Dimensions__4__Name=d_datetime \
TelemetryEngine__Dimensions__4__Type=DateTime \
TelemetryEngine__Measures__0__Name=m_uint8 \
TelemetryEngine__Measures__0__Type=UInt8 \
TelemetryEngine__Measures__1__Name=m_uint16 \
TelemetryEngine__Measures__1__Type=UInt16 \
TelemetryEngine__Measures__2__Name=m_uint32 \
TelemetryEngine__Measures__2__Type=UInt32 \
TelemetryEngine__Measures__3__Name=m_uint64 \
TelemetryEngine__Measures__3__Type=UInt64 \
TelemetryEngine__Measures__4__Name=m_int16 \
TelemetryEngine__Measures__4__Type=Int16 \
TelemetryEngine__Measures__5__Name=m_int32 \
TelemetryEngine__Measures__5__Type=Int32 \
TelemetryEngine__Measures__6__Name=m_int64 \
TelemetryEngine__Measures__6__Type=Int64 \
TelemetryEngine__Measures__7__Name=m_float32 \
TelemetryEngine__Measures__7__Type=Float32 \
TelemetryEngine__Measures__8__Name=m_float64 \
TelemetryEngine__Measures__8__Type=Float64 \
TelemetryEngine__Measures__8__Aggregations=sum,avg,min,max,count,p95 \
TelemetryEngine__Measures__9__Name=m_decimal \
TelemetryEngine__Measures__9__Type=Decimal \
TelemetryEngine__Rollups__0__Name=smoke \
TelemetryEngine__Rollups__0__Grain=day \
TelemetryEngine__Rollups__0__EventTypes=smoke \
TelemetryEngine__Rollups__0__Dimensions=d_lowcard,d_string \
TelemetryEngine__Rollups__0__Measures=m_uint32:sum,m_decimal:sum \
OTEL_SDK_DISABLED=true \
"$OUT/Nealytics.Engine" > "$LOG" 2>&1 &
ENGINE_PID=$!

finish() {
  local code=$?
  kill "$ENGINE_PID" 2>/dev/null || true
  if [ "$code" != "0" ]; then
    echo "--- engine log ---"
    tail -40 "$LOG"
  fi
  ch "TRUNCATE TABLE IF EXISTS nealytics_core.global_events" >/dev/null 2>&1 || true
  ch "DROP TABLE IF EXISTS nealytics_core.rollup_smoke_mv" >/dev/null 2>&1 || true
  ch "DROP TABLE IF EXISTS nealytics_core.rollup_smoke" >/dev/null 2>&1 || true
  exit "$code"
}
trap finish EXIT

for i in $(seq 1 40); do
  if [ "$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:$PORT/ready" || true)" = "200" ]; then
    echo "engine ready after ${i}s"
    break
  fi
  if ! kill -0 "$ENGINE_PID" 2>/dev/null; then
    echo "FAIL: engine exited during startup"
    exit 1
  fi
  sleep 1
done

echo "Dry-running the payload through /validate..."
validated="$(curl -sS -XPOST "http://127.0.0.1:$PORT/api/v1/telemetry/validate" \
  -H 'Content-Type: application/json' -H "X-Project-Key: $PROJECT_KEY" \
  -d "{\"projectId\":\"$PROJECT\",\"tenantId\":\"$TENANT\",\"sessionId\":\"s0\",
       \"eventType\":\"smoke\",\"dimensions\":{\"d_string\":\"abc\",\"nope\":\"x\"},
       \"measures\":{\"m_uint32\":\"1\"}}")"
echo "validate: $validated"
case "$validated" in
  *nope*) echo "PASS: /validate named the undeclared key" ;;
  *) echo "FAIL: /validate did not report the undeclared dimension"; exit 1 ;;
esac

echo "Ingesting one event carrying every declared column type..."
curl -sS -o /dev/null -XPOST "http://127.0.0.1:$PORT/api/v1/telemetry/track" \
  -H 'Content-Type: application/json' \
  -H "X-Project-Key: $PROJECT_KEY" \
  -d "{\"projectId\":\"$PROJECT\",\"tenantId\":\"$TENANT\",\"sessionId\":\"s1\",\"userId\":\"u1\",
       \"eventType\":\"smoke\",\"objectId\":\"o1\",\"seq\":7,\"trafficClass\":\"normal\",
       \"pagePath\":\"/m/x\",\"referrer\":\"https://example.test\",
       \"deviceClass\":\"phone\",\"os\":\"iOS\",
       \"browser\":\"Safari\",\"country\":\"TR\",\"metadataJson\":\"{}\",
       \"dimensions\":{\"d_string\":\"abc\",\"d_lowcard\":\"x\",\"d_uint\":\"42\",
                       \"d_int\":\"-7\",\"d_datetime\":\"2026-08-13T12:00:00Z\"},
       \"measures\":{\"m_uint8\":\"7\",\"m_uint16\":\"300\",\"m_uint32\":\"11200\",
                     \"m_uint64\":\"9000000000\",\"m_int16\":\"-5\",\"m_int32\":\"-70000\",
                     \"m_int64\":\"-9000000000\",\"m_float32\":\"1.5\",\"m_float64\":\"0.02\",
                     \"m_decimal\":\"185.50\"}}"

for i in $(seq 1 30); do
  rows="$(ch "SELECT count() FROM nealytics_core.global_events WHERE project_id='$PROJECT'" | tr -d '[:space:]')"
  [ "$rows" = "1" ] && break
  sleep 1
done

if [ "${rows:-0}" != "1" ]; then
  echo "FAIL: the binary accepted the event and never committed it (rows=${rows:-0})."
  echo "A missing generic instantiation looks exactly like this: 202 on ingest, nothing in storage."
  exit 1
fi
echo "PASS: write path committed the row"

stored="$(ch "SELECT d_string, d_lowcard, d_uint, d_int, toString(d_datetime) FROM nealytics_core.global_events WHERE project_id='$PROJECT'")"
echo "stored: $stored"
case "$stored" in
  *abc*x*42*-7*) : ;;
  *) echo "FAIL: declared dimension values did not survive the round trip"; exit 1 ;;
esac
echo "PASS: every declared dimension type round-tripped"

measured="$(ch "SELECT m_uint8, m_uint16, m_uint32, m_uint64, m_int16, m_int32, m_int64, m_float32, m_float64, m_decimal FROM nealytics_core.global_events WHERE project_id='$PROJECT'")"
echo "measures: $measured"
case "$measured" in
  *7*300*11200*9000000000*-5*-70000*-9000000000*1.5*0.02*185.5*) : ;;
  *) echo "FAIL: declared measure values did not survive the round trip"; exit 1 ;;
esac
echo "PASS: every declared measure type round-tripped"

core="$(ch "SELECT seq, traffic_class, page_path, referrer, ingested_at > toDateTime64('2026-01-01 00:00:00', 3) FROM nealytics_core.global_events WHERE project_id='$PROJECT'")"
echo "core: $core"
case "$core" in
  *7*normal*/m/x*example.test*1*) : ;;
  *) echo "FAIL: the core columns added in this release did not round-trip"; exit 1 ;;
esac
echo "PASS: seq, traffic_class, page_path, referrer and ingested_at round-tripped"

rollup_tables="$(ch "SELECT count() FROM system.tables WHERE database='nealytics_core' AND name IN ('rollup_smoke','rollup_smoke_mv')" | tr -d '[:space:]')"
if [ "$rollup_tables" != "2" ]; then
  echo "FAIL: the reconciler did not create the rollup table and its materialized view (found $rollup_tables of 2)."
  exit 1
fi
echo "PASS: rollup table and materialized view created"

b64url() { openssl base64 -A | tr '+/' '-_' | tr -d '='; }
HDR="$(printf '%s' '{"alg":"HS256","typ":"JWT"}' | b64url)"
PLD="$(printf '%s' "{\"project_id\":\"$PROJECT\",\"tenant_id\":\"$TENANT\",\"exp\":$(( $(date +%s) + 3600 ))}" | b64url)"
SIG="$(printf '%s' "$HDR.$PLD" | openssl dgst -sha256 -hmac "$KEY" -binary | b64url)"
TOKEN="$HDR.$PLD.$SIG"

FROM="$(date -u -d '1 hour ago' +%Y-%m-%dT%H:%M:%SZ)"
TO="$(date -u -d '1 hour' +%Y-%m-%dT%H:%M:%SZ)"

check() {
  local name="$1" path="$2"
  local code
  code="$(curl -s -o /tmp/smoke-read.out -w '%{http_code}' -H "Authorization: Bearer $TOKEN" "http://127.0.0.1:$PORT$path")"
  if [ "$code" != "200" ]; then
    echo "FAIL: $name returned $code"
    cat /tmp/smoke-read.out
    exit 1
  fi
  echo "PASS: $name"
}

check "timeline"   "/api/v1/telemetry/timeline?limit=10"
check "sessions"   "/api/v1/analytics/sessions?from=$FROM&to=$TO&limit=10"
check "timeseries" "/api/v1/analytics/timeseries?from=$FROM&to=$TO&interval=hour"
check "active"     "/api/v1/analytics/active?from=$FROM&to=$TO&interval=day"
check "top"        "/api/v1/analytics/top?from=$FROM&to=$TO&dimension=event_type&limit=10"
check "breakdown/event_type" "/api/v1/analytics/breakdown?from=$FROM&to=$TO&groupBy=event_type&metric=events"
check "breakdown/traffic-all" "/api/v1/analytics/breakdown?from=$FROM&to=$TO&groupBy=event_type&metric=events&traffic=all"
check "breakdown/d_uint"     "/api/v1/analytics/breakdown?from=$FROM&to=$TO&groupBy=d_uint&metric=sessions"
check "breakdown/d_datetime" "/api/v1/analytics/breakdown?from=$FROM&to=$TO&groupBy=d_datetime&metric=events"
check "schema"               "/api/v1/schema"
check "funnel"               "/api/v1/analytics/funnel?from=$FROM&to=$TO&step=smoke&step=smoke"
check "funnel/segmented"     "/api/v1/analytics/funnel?from=$FROM&to=$TO&step=smoke&step=smoke&breakdownBy=d_lowcard"
check "breakdown/avg-measure"  "/api/v1/analytics/breakdown?from=$FROM&to=$TO&groupBy=event_type&metric=avg(m_uint32)"
check "breakdown/p95-measure"  "/api/v1/analytics/breakdown?from=$FROM&to=$TO&groupBy=event_type&metric=p95(m_float64)"
check "breakdown/sum-decimal"  "/api/v1/analytics/breakdown?from=$FROM&to=$TO&groupBy=event_type&metric=sum(m_decimal)"

echo
echo "Smoke test passed: every column type the engine can declare survives a real publish."
