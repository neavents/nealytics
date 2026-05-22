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

Grab the latest binary from the [Releases](../../releases) page. We publish Native AOT binaries for Linux x64, Linux ARM64, macOS ARM64, and Windows x64. No .NET runtime required it's fully self contained.

```bash
# Linux / macOS
tar -xzf nealytics-engine-linux-x64.tar.gz
chmod +x Nealytics.Engine

export TelemetryEngine__ClickHouseConnectionString="Host=127.0.0.1;Port=9000;Database=nealytics_core;"
export TelemetryEngine__JwtSymmetricKey="replace_this_please"
export TelemetryEngine__AllowedProjectKeys="myapp:mykey123"

./Nealytics.Engine
```

```powershell
# Windows
Expand-Archive nealytics-engine-win-x64.zip -DestinationPath .

$env:TelemetryEngine__ClickHouseConnectionString="Host=127.0.0.1;Port=9000;Database=nealytics_core;"
$env:TelemetryEngine__JwtSymmetricKey="replace_this_please"
$env:TelemetryEngine__AllowedProjectKeys="myapp:mykey123"

.\Nealytics.Engine.exe
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
GET http://localhost:5000/health
```

Returns 200 if the service is up. No auth required.

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
| `ConnectionPoolSize` | `16` | Max idle connections kept in the ClickHouse connection pool |

### Batch Processing

| Variable | Default | What it does |
|---|---|---|
| `MemoryChannelCapacity` | `100000` | Bounded channel size. When full, ingestion endpoints block (backpressure). |
| `DatabaseBatchCommitSize` | `10000` | Events per ClickHouse batch insert |
| `ForceFlushIntervalSeconds` | `3` | Max time to wait before flushing a partial batch |
| `MaxInsertRetries` | `5` | Retry attempts on ClickHouse insert failure |
| `RetryBackoffCeilingMs` | `30000` | Max delay between retries (exponential backoff caps here) |

### Ingestion

| Variable | Default | What it does |
|---|---|---|
| `AllowedProjectKeys` | _(empty)_ | Comma separated API keys. Empty = all requests rejected. |
| `MaxRequestBodyBytes` | `1048576` | Max request body size (1 MB). Enforced at Kestrel level and per endpoint. |
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
| `DefaultSessionQueryRangeHours` | `24` | Default time range when `from`/`to` are not specified on sessions endpoint |

---

## Write-Ahead Log (WAL)

Source: [`WriteAheadLogger.cs`](src/Nealytics.Engine/Infrastructure/Storage/WriteAheadLogger.cs)

Every event that hits an ingestion endpoint is serialized to the WAL file before being published to the in memory channel. The WAL is a simple newline delimited JSON file (`telemetry_wal.log`) written with `FileOptions.WriteThrough` for durability, meaning writes go directly to disk, they don't sit in an OS buffer.

### Why we need this

The in memory channel is fast but volatile. If the process crashes, everything in the channel is gone. The WAL gives us a recovery point. On startup, the batch processor calls `ReplayUncommitted()`, reads every line from the WAL, deserializes it, and pushes it through the normal batch insert pipeline. Only after ALL recovered events are successfully committed to ClickHouse does the WAL get truncated.

### How it avoids allocations

Serialization uses a `[ThreadStatic]` `ArrayBufferWriter<byte>`. Each thread gets its own reusable buffer. The JSON is written directly to the buffer via `Utf8JsonWriter`, a newline is appended, and the raw bytes are written to the file. No string intermediaries, no `byte[]` allocations per event.

The buffer fill and file write both happen under a `SemaphoreSlim` lock. This prevents a subtle race condition where async continuations could hop threads and two concurrent `AppendAsync` calls could end up sharing the same thread static buffer.

### Truncation safety

There are two truncation methods:

- **`TruncateIfSafeAsync`** only truncates if new bytes were appended since the last truncation. This prevents accidental truncation during WAL replay (where events are read but not appended).
- **`TruncateAsync`** unconditionally truncates. Used after WAL replay completes successfully.

During normal operation, the batch processor calls `TruncateIfSafeAsync` after each successful ClickHouse insert. During WAL replay, truncation is deferred until all recovered events are committed, preventing data loss if ClickHouse is temporarily unavailable at startup.

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
    event_type LowCardinality(String),
    item_id Nullable(String),
    metadata_json String CODEC(ZSTD(1)),
    timestamp DateTime64(3, 'UTC')
)
ENGINE = MergeTree()
ORDER BY (project_id, tenant_id, event_type, timestamp)
SETTINGS index_granularity = 8192;
```

Design decisions:

- **`LowCardinality`** on `project_id` and `event_type` because these have few distinct values across millions of rows. ClickHouse stores them as dictionary encoded integers internally.
- **`ZSTD(1)`** compression on `metadata_json` because JSON strings are highly compressible and this column can be large.
- **ORDER BY** is `(project_id, tenant_id, event_type, timestamp)`. All queries filter by project and tenant first (from JWT claims), so these are the primary sort keys. Event type and timestamp come next for the most common aggregation patterns.
- **`DateTime64(3, 'UTC')`** gives millisecond precision in UTC. Good enough for analytics, avoids timezone headaches.
- **`MergeTree`** engine. No deduplication (that would be `ReplacingMergeTree`). If you need exactly once semantics, use `event_id` for dedup in your queries.

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
    BatchProcessor/
      TelemetryBatchProcessor.cs          # BackgroundService, WAL replay, batch insert
    GetProjectTimeline/
      GetProjectTimelineEndpoint.cs       # GET /api/v1/telemetry/timeline
      GetProjectTimelineQuery.cs          # ClickHouse query
      GlobalTimelineItem.cs               # Response model
      ProjectTimelineResponse.cs          # Response model
    GetSessionAnalytics/
      GetSessionAnalyticsEndpoint.cs      # GET /api/v1/analytics/sessions
      GetSessionAnalyticsQuery.cs         # ClickHouse query with GROUP BY
      SessionSummaryItem.cs               # Response model
      SessionAnalyticsResponse.cs         # Response model
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

## License

MIT
