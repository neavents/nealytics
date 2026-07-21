<p align="left">
  <img src="assets/Nealytics.png" alt="Nealytics" width="200" />
</p>

# Nealytics

High throughput telemetry engine built on .NET 10 Native AOT and ClickHouse. Ships as a single self contained binary. No reflection, no runtime code generation, no garbage collection pressure on the hot path.

Built this because I wanna use collected data to show my tenants their analytics, I looked up some other tools but none of them offered what I want. the most closest one is TinyBird, good project but I don't wanna pay anything while I can make similar myself. And this an open source project so anyone can benefit from this. 

This service is so fast and very easy to configure. Nealytics is one binary, one config file, one ClickHouse instance.

---

## Get it running

### Docker (fastest way)

```bash
git clone https://github.com/neavents/nealytics.git
cd nealytics
```

Open [`docker-compose.yml`](docker-compose.yml) and change the JWT key to something real:

```
TelemetryEngine__JwtSymmetricKey=replace_this_please
```

Then:

```bash
docker compose up -d
```

That's it. ClickHouse starts, the schema gets created automatically from [`clickhouse-init.sql`](clickhouse-init.sql), and the API is live on port 5000.

### Pre-built binary (no SDK needed)

Grab the latest binary from the [Releases](../../releases) page. We publish Native AOT binaries for Linux x64 and Linux ARM64. No .NET runtime required it's fully self contained.

```bash
tar -xzf nealytics-engine-linux-x64.tar.gz
chmod +x Nealytics.Engine

export TelemetryEngine__ClickHouseConnectionString="Host=127.0.0.1;Port=9000;Database=nealytics_core;"
export TelemetryEngine__JwtSymmetricKey="replace_this_please"
export TelemetryEngine__AllowedProjectKeys="myapp:mykey123"

./Nealytics.Engine
```

You still need a ClickHouse instance running somewhere the binary is just the API server. Run the [`clickhouse-init.sql`](clickhouse-init.sql) against your ClickHouse to create the schema, or use the Docker Compose file just for the database:

```bash
docker compose up -d telemetry-db
```

### Build from source

You need .NET 10 SDK and a running ClickHouse instance.

```bash
cd src/Nealytics.Engine
dotnet run
```

Configuration goes in [`appsettings.json`](src/Nealytics.Engine/appsettings.json) or environment variables. At minimum you need to set `TelemetryEngine__JwtSymmetricKey` and `TelemetryEngine__AllowedProjectKeys`.

### Health check

```
GET http://localhost:5000/health   # liveness  — process is up (no dependency check)
GET http://localhost:5000/ready    # readiness — dependencies reachable (SELECT 1 on ClickHouse)
```

No auth required. `/health` always returns `200` while the process is running — use it for a liveness probe so an orchestrator does not restart a healthy pod during a ClickHouse blip. `/ready` returns `200` when ClickHouse is reachable and `503` when it is not — use it for a readiness/traffic gate.

> **Note (v1.2.0):** `/health` was previously the dependency check. It is now liveness-only; the dependency check moved to `/ready`. Update any alerting that treated `/health` as "database up".

---

## Send your first event

```bash
curl -X POST http://localhost:5000/api/v1/telemetry/track \
  -H "Content-Type: application/json" \
  -H "X-Project-Key: neavents:projkey123" \
  -d '{
    "projectId": "my-app",
    "tenantId": "tenant-1",
    "sessionId": "abc-123",
    "eventType": "page_view",
    "itemId": "/home",
    "metadataJson": "{\"referrer\": \"google.com\"}"
  }'
```

You should get a `202 Accepted`. The event is now in the WAL and queued for batch insert into ClickHouse.

---

## How ingestion works

There are two separate endpoints for sending events. This is intentional.

### POST `/api/v1/telemetry/track`

Standard HTTP POST. One event per request. Your server side code, mobile app, or any HTTP client uses this. The request body is parsed using `PipeReader` and `Utf8JsonReader` directly on the raw bytes, no intermediate string allocation.

Auth: `X-Project-Key` header or `?k=` query parameter.

### POST `/api/v1/telemetry/beacon`

This exists specifically for [`navigator.sendBeacon()`](https://developer.mozilla.org/en-US/docs/Web/API/Navigator/sendBeacon). Browsers use sendBeacon to fire telemetry on page unload. The problem is sendBeacon sends the request and the browser doesn't wait for a response, it might also batch multiple events into one payload.

So the beacon endpoint accepts a JSON array of events and deserializes them as a stream using `DeserializeAsyncEnumerable` over the `PipeReader`. Each event is validated, WAL'd, and published individually as it arrives. No need to buffer the entire array in memory.

Auth: `?k=` query parameter only (sendBeacon doesn't let you set custom headers).

Both endpoints are rate limited under the `"ingestion"` policy. You can tune the limits via [`RateLimitPermitCount`](src/Nealytics.Engine/Infrastructure/Configuration/TelemetryEngineOptions.cs), [`RateLimitWindowSeconds`](src/Nealytics.Engine/Infrastructure/Configuration/TelemetryEngineOptions.cs), and [`RateLimitQueueSize`](src/Nealytics.Engine/Infrastructure/Configuration/TelemetryEngineOptions.cs).

---

## Reading data back

Read endpoints require a JWT token with `project_id` and `tenant_id` claims. The engine doesn't issue tokens, your auth service does. Nealytics just validates the signature and extracts the claims. This keeps the engine stateless and out of the identity business.

### GET `/api/v1/telemetry/timeline`

Returns raw events for your project, newest first. The workhorse query endpoint. Most analytics questions can be answered by filtering/aggregating timeline data on the client side.

```bash
curl http://localhost:5000/api/v1/telemetry/timeline?limit=50 \
  -H "Authorization: Bearer <your-jwt>"
```

Query params:
- `limit` (default 100, max set by [`MaxQueryLimit`](src/Nealytics.Engine/Infrastructure/Configuration/TelemetryEngineOptions.cs))
- `before` (ISO 8601 timestamp) — cursor for backward pagination; returns events strictly older than this value
- `eventType`, `sessionId`, `itemId` — optional exact-match filters (each ≤ 256 chars), applied on top of the tenant scope
- `metaKey` + `metaValue` — optional metadata filter (both required together, each ≤ 256 chars). Matches events where `JSONExtractString(metadata_json, metaKey) = metaValue`. Note: `metadata_json` is unindexed, so this is a full scan over the time range — prefer narrowing with `before`/filters. Both are passed as parameters (never interpolated).

```bash
curl "http://localhost:5000/api/v1/telemetry/timeline?limit=50&eventType=purchase&sessionId=abc-123" \
  -H "Authorization: Bearer <your-jwt>"
```

### GET `/api/v1/analytics/timeseries`

Bucketed event counts over time — the primitive behind most charts. Grouped with `toStartOf{Minute,Hour,Day}` inside your tenant scope.

```bash
curl "http://localhost:5000/api/v1/analytics/timeseries?from=2024-06-01T00:00:00Z&to=2024-06-08T00:00:00Z&interval=day" \
  -H "Authorization: Bearer <your-jwt>"
```

Query params:
- `interval` — `minute`, `hour` (default), or `day`. Any other value returns `400`.
- `from` / `to` (ISO 8601, defaults to last 24 hours). `from` must be ≤ `to`.
- `eventType` — optional exact-match filter
- `groupBy` — optional split into per-series counts: `event_type`, `item_id`, or `session_id` (whitelisted; any other value returns `400`). When set, each point gains a `series` field and the query groups by `(bucket, series)`. `LIMIT` caps total `bucket×series` rows, so avoid very high-cardinality dimensions (`session_id`); prefer `event_type`/`item_id`.
- `limit` — max number of buckets returned (defaults to `MaxQueryLimit`)

Response (ungrouped `series` is omitted):

```json
{
  "projectId": "my-app",
  "tenantId": "tenant-1",
  "interval": "day",
  "from": "2024-06-01T00:00:00Z",
  "to": "2024-06-08T00:00:00Z",
  "totalCount": 4210,
  "points": [
    { "bucket": "2024-06-01T00:00:00Z", "series": "purchase", "count": 512 },
    { "bucket": "2024-06-01T00:00:00Z", "series": "view", "count": 631 }
  ]
}
```

### GET `/api/v1/analytics/sessions`

Session level aggregation. Groups events by `session_id` and returns per-session metrics: first/last seen, duration, event count. Also includes summary stats across the returned sessions.

```bash
curl "http://localhost:5000/api/v1/analytics/sessions?from=2024-01-01T00:00:00Z&to=2024-12-31T23:59:59Z&limit=100" \
  -H "Authorization: Bearer <your-jwt>"
```

Query params:
- `from` / `to` (ISO 8601, defaults to last 24 hours, configurable via [`DefaultSessionQueryRangeHours`](src/Nealytics.Engine/Infrastructure/Configuration/TelemetryEngineOptions.cs))
- `limit` (default 100)

Response:

```json
{
  "projectId": "my-app",
  "tenantId": "tenant-1",
  "uniqueSessionCount": 42,
  "totalEventCount": 1234,
  "avgDurationSeconds": 234.5,
  "sessions": [
    {
      "sessionId": "abc-123",
      "firstSeen": "2024-06-15T10:30:00Z",
      "lastSeen": "2024-06-15T10:45:30Z",
      "durationSeconds": 930.0,
      "eventCount": 28
    }
  ]
}
```

### GET `/api/v1/analytics/active`

Active Users — distinct-count series bucketed by day (DAU) or month (MAU), within your tenant scope.

```bash
curl "http://localhost:5000/api/v1/analytics/active?interval=day&by=user&mode=exact&from=2026-06-01T00:00:00Z&to=2026-06-08T00:00:00Z" \
  -H "Authorization: Bearer <your-jwt>"
```

Query params (all whitelisted; any other value returns `400`):
- `interval` — `day` (default, DAU) or `month` (MAU). Maps to `toStartOfDay`/`toStartOfMonth`.
- `by` — `user` (default → `user_id`) or `session` (→ `session_id`).
- `mode` — `exact` (default → `uniqExact`) or `approx` (→ `uniq`, HyperLogLog; cheaper/approximate for large ranges).
- `from` / `to` (ISO 8601, defaults to last 24 hours). `from` must be ≤ `to`.
- `limit` — max buckets returned (defaults to `MaxQueryLimit`).

Because `user_id` is nullable, distinct counts **skip anonymous (`NULL`) events**, so they never inflate the count. Note: a true rolling 30-day MAU is a single `uniq(user_id)` over `now()-30d` — do **not** sum daily buckets (users overlap).

```json
{
  "projectId": "my-app", "tenantId": "tenant-1",
  "interval": "day", "by": "user", "mode": "exact",
  "from": "2026-06-01T00:00:00Z", "to": "2026-06-08T00:00:00Z",
  "points": [ { "bucket": "2026-06-01T00:00:00Z", "activeCount": 512 } ]
}
```

### GET `/api/v1/analytics/top`

Top-N events or items by count, descending, within your tenant scope.

```bash
curl "http://localhost:5000/api/v1/analytics/top?dimension=event_type&from=2026-06-01T00:00:00Z&to=2026-06-08T00:00:00Z&limit=20" \
  -H "Authorization: Bearer <your-jwt>"
```

Query params:
- `dimension` — `event_type` (default) or `item_id` (whitelisted; any other value returns `400`). `item_id` results **exclude `NULL` keys**.
- `from` / `to` (ISO 8601, defaults to last 24 hours). `from` must be ≤ `to`.
- `limit` — number of rows (default 20, clamped to `MaxQueryLimit`).

```json
{
  "projectId": "my-app", "tenantId": "tenant-1", "dimension": "event_type",
  "from": "2026-06-01T00:00:00Z", "to": "2026-06-08T00:00:00Z",
  "items": [ { "key": "view", "count": 1240 }, { "key": "purchase", "count": 318 } ]
}
```

---

## Authentication

The write path and read path use completely different auth mechanisms. This is by design.

**Write path (ingestion):** API keys. Comma separated list in [`AllowedProjectKeys`](src/Nealytics.Engine/Infrastructure/Configuration/TelemetryEngineOptions.cs). Validated against a `FrozenSet<string>` for O(1) exact match lookups. No substring matching, no wildcards. Pass the key via `X-Project-Key` header or `?k=` query param.

**Read path (queries):** JWT Bearer tokens. The engine validates the signature using the symmetric key in [`JwtSymmetricKey`](src/Nealytics.Engine/Infrastructure/Configuration/TelemetryEngineOptions.cs) and extracts `project_id` and `tenant_id` from the token claims. These claims become mandatory WHERE filters on every query. There's no way to read another project's data even if you have a valid token.

The engine does not have a login endpoint, a user database, or any identity management. You bring your own auth service, mint JWTs with the right claims, and hand them to your frontend. Nealytics stays focused on analytics.

JWT example payload:

```json
{
  "project_id": "my-app",
  "tenant_id": "tenant-1",
  "exp": 1750000000
}
```

---

# Technical Documentation

Everything below is the deep dive. How things actually work under the hood.

---

## Configuration Reference

Every setting is an environment variable prefixed with `TelemetryEngine__`. You can also set them in [`appsettings.json`](src/Nealytics.Engine/appsettings.json) under the `TelemetryEngine` section. Environment variables take precedence.

Full source: [`TelemetryEngineOptions.cs`](src/Nealytics.Engine/Infrastructure/Configuration/TelemetryEngineOptions.cs)

### Database & Storage

| Variable | Default | What it does |
|---|---|---|
| `ClickHouseConnectionString` | `Host=127.0.0.1;Port=9000;Database=nealytics_core;` | ClickHouse native protocol connection string |
| `WriteAheadLogDirectory` | `/var/log/nealytics_engine/` | Directory for the WAL file. Must be writable. |
| `WalFileBufferBytes` | `65536` | Size of the buffered WAL `FileStream`. Larger buffers let group-commit coalesce more appends per flush. |
| `ConnectionPoolSize` | `16` | Max concurrent ClickHouse connections. Bounds acquisition (acquire-side semaphore) and idle retention. `0` = unbounded, retain nothing. |
| `EnableWireCompression` | `true` | Enables LZ4 compression on the ClickHouse native protocol (inserts and query results). |

### Batch Processing

| Variable | Default | What it does |
|---|---|---|
| `MemoryChannelCapacity` | `100000` | Bounded channel size. When full, ingestion endpoints block (backpressure). |
| `DatabaseBatchCommitSize` | `10000` | Events per ClickHouse batch insert |
| `ForceFlushIntervalSeconds` | `3` | Max time to wait before flushing a partial batch |
| `MaxInsertRetries` | `5` | Retry attempts on ClickHouse insert failure |
| `RetryBackoffCeilingMs` | `30000` | Max delay between retries (exponential backoff caps here) |
| `WalReplayRetryDelayMs` | `10000` | Delay before retrying a WAL-replay batch when ClickHouse is unavailable at startup |
| `EnableAsyncInsert` | `true` | Appends `SETTINGS async_insert=1, wait_for_async_insert=1` to the batch INSERT. Server-side coalescing reduces part churn at pod scale; `wait_for_async_insert=1` keeps the WAL ack honest (durable before commit). |

### Ingestion

| Variable | Default | What it does |
|---|---|---|
| `AllowedProjectKeys` | _(empty)_ | Comma separated API keys. Empty = all requests rejected. |
| `MaxRequestBodyBytes` | `1048576` | Max request body size (1 MB). Enforced at Kestrel level and per endpoint. |
| `MaxConcurrentConnections` | `20000` | Kestrel concurrent-connection safety valve. `0` = unbounded. |
| `EnableRequestDecompression` | `true` | Accept `Content-Encoding: gzip`/`deflate`/`br` on ingestion endpoints (`/track`, `/beacon`). |
| `RateLimitPermitCount` | `1000` | Requests allowed per rate limit window |
| `RateLimitWindowSeconds` | `10` | Rate limit window duration |
| `RateLimitQueueSize` | `500` | Requests queued when rate limit is hit (before 429) |
| `CorsAllowedOrigins` | _(empty)_ | Comma separated allowed origins. Empty = allow any origin. |

### Authentication

| Variable | Default | What it does |
|---|---|---|
| `JwtSymmetricKey` | _(empty)_ | HS256 signing key. Must be at least 32 bytes. App won't start without it. |
| `JwtClockSkewSeconds` | `30` | Clock drift tolerance for JWT expiration checks |

### Query Endpoints

| Variable | Default | What it does |
|---|---|---|
| `MaxQueryLimit` | `10000` | Max `limit` parameter value for read endpoints |
| `DefaultSessionQueryRangeHours` | `24` | Default time range when `from`/`to` are not specified on sessions, active-users, top-N, and time-series endpoints |
| `EnablePrometheusScrape` | `false` | Expose the engine metrics at `GET /metrics` for Prometheus scraping (unauthenticated — keep on an internal network). |

---

## Write-Ahead Log (WAL)

Source: [`WriteAheadLogger.cs`](src/Nealytics.Engine/Infrastructure/Storage/WriteAheadLogger.cs)

Every event that hits an ingestion endpoint is serialized to the WAL file before being published to the in memory channel. The WAL is a simple newline delimited JSON file (`telemetry_wal.log`). Durability uses **group commit**: appends stage their serialized bytes into an in-process channel, a single background writer coalesces all pending appends into one vectored write followed by one `Flush(flushToDisk: true)` (fsync), and each waiting request is acked only after its group's flush completes. This amortizes the per-event syscall + device round-trip over the whole group while keeping the durability barrier — the `202` still means "on disk". Under low load a lone append flushes immediately (a group of one); under load the group grows and self-tunes, so no timer is needed.

### Why we need this

The in memory channel is fast but volatile. If the process crashes, everything in the channel is gone. The WAL gives us a recovery point. On startup, the batch processor calls `ReplayUncommitted()`, reads every line from the WAL, deserializes it, and pushes it through the normal batch insert pipeline. Only after ALL recovered events are successfully committed to ClickHouse does the WAL get truncated.

### How it avoids allocations

Serialization uses a `[ThreadStatic]` `ArrayBufferWriter<byte>`. Each thread gets its own reusable buffer. The JSON is written directly to the buffer via `Utf8JsonWriter`, a newline is appended, and the raw bytes are written to the file. No string intermediaries, no `byte[]` allocations per event.

Each append serializes into a `[ThreadStatic]` buffer and copies the framed bytes into a pooled (`ArrayPool<byte>`) segment that the background writer returns to the pool after its group flushes. Only the single background writer touches the `FileStream`, so file writes never contend across request threads; the per-file `SemaphoreSlim` now only serializes the writer's flush against truncation/acknowledge.

### Truncation safety

The WAL only deletes a record once its event is durably in ClickHouse. Two mechanisms guarantee that:

- **Uncommitted-record counter.** The background group-commit writer increments a counter by the group size once the group is durably flushed (before releasing its waiters, so a returned `AppendAsync` always sees itself counted); `AcknowledgeCommitAsync(n)` decrements it by the size of a committed batch. The active log is truncated **only when the counter reaches zero** — i.e. when every record still on disk is known to be committed. This closes the race where an event that was appended (but not yet published to the channel) could be dropped by a truncation triggered by an unrelated batch.
- **Startup segment rotation.** On construction, any pre-existing `telemetry_wal.log` is sealed to `telemetry_wal.replay` and a fresh active log is opened. Recovery replays the *sealed* segment while new ingestion writes to the *active* segment, so events arriving during recovery can never be destroyed by the recovery cleanup. The sealed segment is deleted only after every recovered event commits (retried every `WalReplayRetryDelayMs` if ClickHouse is down).

---

## Connection Pooling

Source: [`ClickHouseConnectionFactory.cs`](src/Nealytics.Engine/Infrastructure/Storage/ClickHouseConnectionFactory.cs)

Octonica's ClickHouse client has no built in connection pooling. Every `new ClickHouseConnection()` opens a fresh TCP socket. At high throughput, that's a lot of unnecessary handshakes.

We built a simple pool on top of `ConcurrentQueue<ClickHouseConnection>`:

- **Acquire:** dequeue an idle connection, check that it's still `Open`. If it's stale (ClickHouse restarted, network blip), dispose it and try the next one. If the pool is empty, open a new connection.
- **Return:** when a `PooledClickHouseConnection` is disposed via `await using`, the connection goes back to the pool if it's healthy and the pool isn't over capacity. Otherwise it gets disposed for real.
- **Shutdown:** `DisposeAsync` drains the pool and closes all connections.

The pool is self healing. If ClickHouse restarts and all pooled connections go stale, the acquire loop silently discards them and creates fresh ones. The batch processor's retry logic handles any transient failures during the reconnection window.

`PooledClickHouseConnection` is a readonly struct. Zero heap allocation for the wrapper itself.

---

## Batch Processor

Source: [`TelemetryBatchProcessor.cs`](src/Nealytics.Engine/Features/BatchProcessor/TelemetryBatchProcessor.cs)

This is a `BackgroundService` that reads from the bounded channel and inserts into ClickHouse in batches. The flow:

1. **WAL replay** (on startup): reads uncommitted events from the WAL, inserts them in batches, retries indefinitely until ClickHouse accepts everything, then truncates the WAL. The service does not process new events until recovery is complete.

2. **Main loop**: reads events from the channel until either the batch size is reached (`DatabaseBatchCommitSize`) or the flush timer fires (`ForceFlushIntervalSeconds`). Whichever comes first triggers a batch insert.

3. **Batch insert**: events are decomposed into columnar arrays (one array per column) rented from `ArrayPool<T>.Shared`. The arrays are passed to `ClickHouseColumnWriter.WriteTableAsync` for a single bulk columnar insert. This is significantly faster than row by row inserts.

4. **Retry**: if the ClickHouse insert fails, it retries with exponential backoff (1s, 2s, 4s, 8s, up to `RetryBackoffCeilingMs`). After `MaxInsertRetries` failures, it logs critical and moves on. The data is safe in the WAL and will be replayed on next restart.

5. **Graceful shutdown**: when the host signals shutdown, the main loop exits and a drain loop reads all remaining events from the channel, batches them, and flushes to ClickHouse with `CancellationToken.None`. No events are dropped on clean shutdown.

---

## ClickHouse Schema

Source: [`clickhouse-init.sql`](clickhouse-init.sql)

```sql
CREATE TABLE nealytics_core.global_events
(
    event_id UUID,
    project_id LowCardinality(String),
    tenant_id String,
    session_id String,
    user_id Nullable(String),
    event_type LowCardinality(String),
    item_id Nullable(String),
    metadata_json String CODEC(ZSTD(1)),
    timestamp DateTime64(3, 'UTC')
)
ENGINE = ReplacingMergeTree()
ORDER BY (project_id, tenant_id, event_type, timestamp, event_id)
TTL toDateTime(timestamp) + INTERVAL 90 DAY DELETE
SETTINGS index_granularity = 8192, ttl_only_drop_parts = 1;
```

Design decisions:

- **`LowCardinality`** on `project_id` and `event_type` because these have few distinct values across millions of rows. ClickHouse stores them as dictionary encoded integers internally.
- **`ZSTD(1)`** compression on `metadata_json` because JSON strings are highly compressible and this column can be large.
- **ORDER BY** is `(project_id, tenant_id, event_type, timestamp, event_id)`. All queries filter by project and tenant first (from JWT claims), so these are the primary sort keys. Event type and timestamp come next for the most common aggregation patterns. `event_id` is the final key so that duplicate rows for the same event collapse under `ReplacingMergeTree`.
- **`user_id Nullable(String)`** is an optional cross-session identifier that powers Active Users / DAU / MAU (`/api/v1/analytics/active`). It is deliberately kept **out of the `ORDER BY` sort key** — it is high-cardinality and would bloat the primary index. Anonymous events store `NULL`; the distinct-count aggregates (`uniqExact`/`uniq`) skip `NULL`, so anonymous traffic never inflates the counts.
- **`DateTime64(3, 'UTC')`** gives millisecond precision in UTC. Good enough for analytics, avoids timezone headaches.
- **`ReplacingMergeTree`** engine. Crash recovery replays the Write-Ahead Log at least once, so a batch that committed to ClickHouse but was not yet acknowledged in the WAL can be re-inserted after a restart. Because `event_id` is part of the sorting key, these duplicates collapse to a single row on merge, giving idempotent recovery. Deduplication is eventual (it happens on background merges); use `FINAL` in a query when you need exact-once results immediately.
- **`TTL toDateTime(timestamp) + INTERVAL 90 DAY DELETE`** with `ttl_only_drop_parts = 1` evicts events older than 90 days by dropping whole parts, keeping the store bounded without expensive per-row deletes.

### Migration (existing deployments)

`user_id` was added in v1.2.0. Fresh installs get it from `clickhouse-init.sql`; existing clusters must apply it manually (the init script only runs on an empty data volume):

```sql
ALTER TABLE nealytics_core.global_events ADD COLUMN IF NOT EXISTS user_id Nullable(String) AFTER session_id;
```

The column is additive and nullable, so old rows and old clients keep working unchanged.

---

## Observability

Source: [`TelemetryDiagnostics.cs`](src/Nealytics.Engine/Infrastructure/Diagnostics/TelemetryDiagnostics.cs)

### Metrics (OpenTelemetry)

| Metric | Type | What it measures |
|---|---|---|
| `nealytics_events_ingested_total` | Counter | Events accepted by ingestion endpoints |
| `nealytics_batches_committed_total` | Counter | Successful ClickHouse batch inserts |
| `nealytics_read_queries_total` | Counter | Query endpoint executions |
| `nealytics_storage_write_duration_seconds` | Histogram | Time spent per batch insert (including retries) |
| `nealytics_query_read_duration_seconds` | Histogram | Time spent per read query |
| `nealytics_queue_depth_current` | Gauge | Current number of events in the in memory channel |

These export over OTLP by default. Set `EnablePrometheusScrape=true` to additionally expose them at `GET /metrics` in Prometheus text format. That endpoint is **unauthenticated** by convention — keep it on an internal network / behind your ingress.

### Tracing

Activity spans are created for:
- `IngestHttpRequest` (track endpoint)
- `BeaconIngest` (beacon endpoint)
- `BatchProcessor.Flush` (batch insert)
- `GetProjectTimelineQuery.Execute`
- `GetSessionAnalyticsQuery.Execute`

Traces and metrics are exported via OTLP. Set `OTEL_EXPORTER_OTLP_ENDPOINT` to point at your collector (Jaeger, Grafana Tempo, etc.).

### Logging

Structured JSON logging via Serilog. All log messages use the `LoggerMessage` source generator for zero allocation logging on the hot path. Log level is controllable via the standard `Logging__LogLevel__Default` environment variable.

---

## Security Headers

Every response includes:

```
X-Content-Type-Options: nosniff
X-Frame-Options: DENY
X-XSS-Protection: 0
Referrer-Policy: strict-origin-when-cross-origin
```

This is hardcoded in the middleware pipeline. See [`Program.cs`](src/Nealytics.Engine/Program.cs).

---

## Native AOT & Docker

The project compiles to a self contained native binary. No .NET runtime needed on the host.

The [`Dockerfile`](Dockerfile) uses a multi stage build:
1. **Build stage** (`dotnet/sdk:10.0-preview`): restores, publishes with `-r linux-x64` for Native AOT compilation.
2. **Runtime stage** (`dotnet/runtime-deps:10.0-preview`): minimal base image with just the native dependencies (libc, OpenSSL). No .NET runtime installed.

The container runs as a non root `nealytics` user. The WAL directory (`/app/logs/`) is pre created with correct ownership.

---

## Project Structure

```
src/Nealytics.Engine/
  Program.cs                              # Composition root
  Features/
    IngestTelemetry/
      IngestTelemetryEndpoint.cs          # POST /api/v1/telemetry/track
      BeaconTelemetryEndpoint.cs          # POST /api/v1/telemetry/beacon
      TelemetryChannelBroker.cs           # Bounded channel with backpressure
    IngestTelemetry/
      IngestValidation.cs                 # Key resolution + payload validation (pure, testable)
    BatchProcessor/
      TelemetryBatchProcessor.cs          # BackgroundService: WAL replay, retry, backoff, drain
      ITelemetryBatchWriter.cs            # Insert abstraction (fault-injectable in tests)
      ClickHouseBatchWriter.cs            # Zero-alloc columnar insert
      TelemetryColumnMapper.cs            # Payload -> column arrays
      TelemetryInsertMath.cs              # Backoff + timestamp math (pure, testable)
    GetProjectTimeline/
      GetProjectTimelineEndpoint.cs       # GET /api/v1/telemetry/timeline
      GetProjectTimelineQuery.cs          # ClickHouse query + testable SQL builder
      TimelineRequestFactory.cs           # Request parsing/validation (pure, testable)
      GlobalTimelineItem.cs               # Response model
      ProjectTimelineResponse.cs          # Response model
    GetSessionAnalytics/
      GetSessionAnalyticsEndpoint.cs      # GET /api/v1/analytics/sessions
      GetSessionAnalyticsQuery.cs         # ClickHouse query with GROUP BY
      SessionAnalyticsRequestFactory.cs   # Request parsing/validation (pure, testable)
      SessionSummaryItem.cs               # Response model
      SessionAnalyticsResponse.cs         # Response model
    GetEventTimeSeries/
      GetEventTimeSeriesEndpoint.cs       # GET /api/v1/analytics/timeseries
      GetEventTimeSeriesQuery.cs          # Bucketed count query + testable SQL builder
      EventTimeSeriesRequestFactory.cs    # Request parsing/validation (pure, testable)
      TimeSeriesInterval.cs               # Interval enum + parser
      TimeSeriesGroupBy.cs                # groupBy whitelist enum + parser
      EventTimeSeriesPoint.cs             # Response model
      EventTimeSeriesResponse.cs          # Response model
    GetActiveUsers/
      GetActiveUsersEndpoint.cs           # GET /api/v1/analytics/active (DAU/MAU)
      GetActiveUsersQuery.cs              # Distinct-count query + testable SQL builder
      ActiveUsersRequestFactory.cs        # Request parsing/validation (pure, testable)
      ActiveUsersInterval.cs              # day/month enum + parser
      ActiveDimension.cs                  # user/session whitelist enum + parser
      ActiveCountMode.cs                  # exact/approx whitelist enum + parser
      ActiveUsersPoint.cs                 # Response model
      ActiveUsersResponse.cs              # Response model
    GetTopEvents/
      GetTopEventsEndpoint.cs             # GET /api/v1/analytics/top
      GetTopEventsQuery.cs                # Top-N count query + testable SQL builder
      TopEventsRequestFactory.cs          # Request parsing/validation (pure, testable)
      TopDimension.cs                     # event_type/item_id whitelist enum + parser
      TopEventItem.cs                     # Response model
      TopEventsResponse.cs                # Response model
  Infrastructure/
    Configuration/
      TelemetryEngineOptions.cs           # All settings, env configurable
    Diagnostics/
      TelemetryDiagnostics.cs             # Metrics and tracing
    Security/
      ApiKeyValidator.cs                  # FrozenSet backed key validation
    Serialization/
      GlobalTelemetryPayload.cs           # Payload contract + AOT JSON context
    Storage/
      ClickHouseConnectionFactory.cs      # Connection pool
      WriteAheadLogger.cs                 # WAL with crash recovery
```

Vertical Slice Architecture. Each feature is self contained in its own folder. Infrastructure is shared across slices but has no business logic.

---

## Testing

```bash
dotnet test tests/Nealytics.Engine.Tests.Unit          # fast, no dependencies
./scripts/run-integration-tests.sh                      # spins up ClickHouse, runs the integration suite
```

The unit suite covers all pure logic, the batch-processor orchestration (via a fake writer), and the endpoint validation paths (booted in-memory). The integration suite runs against a real ClickHouse and is fully decoupled from any container name or port — it reads `TelemetryEngine__ClickHouseConnectionString` (default `Host=127.0.0.1;Port=9000;...`). See [CONTRIBUTING.md](CONTRIBUTING.md) for details and code-style rules.

---

## License

MIT
