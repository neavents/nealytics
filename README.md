<p align="left">
  <img src="assets/nealytics_v2_poster.png" alt="Nealytics" width="full" />
</p>

# Nealytics

High throughput telemetry engine built on .NET 10 and ClickHouse. Ships as a single self contained binary. Source generated JSON, no reflection on the hot path, and no garbage collection pressure where it matters.

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

### Prebuilt binary (no SDK needed)

Grab the latest binary from the [Releases](../../releases) page. We publish builds for Linux x64 and Linux ARM64. No .NET runtime required, it is fully self contained.

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
GET http://localhost:5000/health   # liveness , process is up (no dependency check)
GET http://localhost:5000/ready    # readiness, dependencies reachable (SELECT 1 on ClickHouse)
```

No auth required. `/health` always returns `200` while the process is running, use it for a liveness probe so an orchestrator does not restart a healthy pod during a ClickHouse blip. `/ready` returns `200` when ClickHouse is reachable and `503` when it is not, use it for a readiness/traffic gate.

> **Note (v1.2.0):** `/health` was previously the dependency check. It is now liveness only; the dependency check moved to `/ready`. Update any alerting that treated `/health` as "database up".

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
    "objectId": "/home",
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

### What the engine refuses, and how you find out

Validation is deliberately small, an analytics pipeline that rejects events is an analytics
pipeline with holes in it, but it is not nothing, because two caller supplied fields are
structural:

| Rule | Why it is not merely tidiness |
|---|---|
| `projectId`, `tenantId`, `sessionId` required | without them a row belongs to nobody |
| `eventType` required, ≤ 128 chars | it leads the sort key after project and tenant, so an unbounded one widens the index for every row in the table |
| `projectId` / `tenantId` / `sessionId` / `objectId` / `userId` ≤ 256 chars | |
| `timestamp` no more than **24 h in the future** | it is the partition key. A year-2099 row creates a partition that TTL never reaches, and nothing surfaces it short of reading `system.parts` |

The past is accepted without limit. A backfill is legitimate and a phone whose clock is behind is
ordinary; only the future is impossible.

**How you learn about it** differs by endpoint, because the callers differ:

- `/track` is server side code that can react. A refusal returns `400` with
  **`X-Nealytics-Rejected: <reason>`**, so one status code does not stand for six different fixes.
  An accepted event whose undeclared keys were stripped returns `202` with
  **`X-Nealytics-Dropped: key1,key2`**, accepted deliberately, because refusing would turn one
  misspelled key into total loss for that event type.
- `/beacon` cannot be told anything: `sendBeacon` discards the response. So a bad element is
  skipped, the rest of the batch is kept, and the skip is counted on
  `nealytics_events_rejected_total{reason,transport}` and logged. It used to be a bare `continue`
  with no counter and no log, which is the same shape as a pipeline dropping everything while
  every signal says it is healthy.

Use `POST /api/v1/telemetry/validate` to see all of this without writing a row, including the
project pin below, so a dry run can never report `accepted` for something `/track` refuses.

### Pinning a key to its project (optional)

A valid project key can write **any** `projectId` it likes. One leaked or copy pasted key therefore
writes into a neighbour's data, and nothing objects. To close that:

```jsonc
"AllowedProjectKeys": "shop:key123,blog:key456",
"Projects": [ { "Key": "shop:key123", "ProjectId": "shop" } ]
```

- **Per key.** `blog:key456` above is unpinned and behaves exactly as before, so this can be adopted
  one service at a time rather than as a migration. With no `Projects` at all, nothing changes.
- **Project only, never tenant.** One edge worker legitimately serves every tenant through a single
  key, that is the architecture, so a tenant pin would be wrong by design.
- **A pin naming a key that isn't in `AllowedProjectKeys` refuses the boot.** Dead security config
  is worse than none: in a review it reads as a control that is in force.

Both endpoints are rate limited under the `"ingestion"` policy. You can tune the limits via [`RateLimitPermitCount`](src/Nealytics.Engine/Infrastructure/Configuration/TelemetryEngineOptions.cs), [`RateLimitPermitCount`](src/Nealytics.Engine/Infrastructure/Configuration/TelemetryEngineOptions.cs), and [`RateLimitPermitCount`](src/Nealytics.Engine/Infrastructure/Configuration/TelemetryEngineOptions.cs).

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
- `limit` (default 100, max set by [`RateLimitPermitCount`](src/Nealytics.Engine/Infrastructure/Configuration/TelemetryEngineOptions.cs))
- `before` (ISO 8601 timestamp), cursor for backward pagination; returns events strictly older than this value
- `eventType`, `sessionId`, `objectId`, optional exact match filters (each ≤ 256 chars), applied on top of the tenant scope
- `metaKey` + `metaValue`, optional metadata filter (both required together, each ≤ 256 chars). Matches events where `JSONExtractString(metadata_json, metaKey) = metaValue`. Note: `metadata_json` is unindexed, so this is a full scan over the time range, prefer narrowing with `before`/filters. Both are passed as parameters (never interpolated).

```bash
curl "http://localhost:5000/api/v1/telemetry/timeline?limit=50&eventType=purchase&sessionId=abc-123" \
  -H "Authorization: Bearer <your-jwt>"
```

### GET `/api/v1/analytics/timeseries`

Bucketed event counts over time, the primitive behind most charts. Grouped with `toStartOf{Minute,Hour,Day}` inside your tenant scope.

```bash
curl "http://localhost:5000/api/v1/analytics/timeseries?from=2024-06-01T00:00:00Z&to=2024-06-08T00:00:00Z&interval=day" \
  -H "Authorization: Bearer <your-jwt>"
```

Query params:
- `interval`, `minute`, `hour` (default), or `day`. Any other value returns `400`.
- `from` / `to` (ISO 8601, defaults to last 24 hours). `from` must be ≤ `to`.
- `eventType`, optional exact match filter
- `groupBy`, optional split into per series counts: any core column or declared dimension (validated against the same allowlist `/analytics/breakdown` uses; any other value returns `400`). When set, each point gains a `series` field and the query groups by `(bucket, series)`. `LIMIT` caps total `bucket×series` rows, so avoid very high cardinality dimensions (`session_id`); prefer `event_type`/`object_id`.
- `limit`, max number of buckets returned (defaults to `MaxQueryLimit`)

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

Session level aggregation. Groups events by `session_id` and returns per session metrics: first/last seen, duration, event count. Also includes summary stats across the returned sessions.

```bash
curl "http://localhost:5000/api/v1/analytics/sessions?from=2024-01-01T00:00:00Z&to=2024-12-31T23:59:59Z&limit=100" \
  -H "Authorization: Bearer <your-jwt>"
```

Query params:
- `from` / `to` (ISO 8601, defaults to last 24 hours, configurable via [`RateLimitPermitCount`](src/Nealytics.Engine/Infrastructure/Configuration/TelemetryEngineOptions.cs))
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

Active Users, distinct count series bucketed by day (DAU) or month (MAU), within your tenant scope.

```bash
curl "http://localhost:5000/api/v1/analytics/active?interval=day&by=user&mode=exact&from=2026-06-01T00:00:00Z&to=2026-06-08T00:00:00Z" \
  -H "Authorization: Bearer <your-jwt>"
```

Query params (all whitelisted; any other value returns `400`):
- `interval`, `day` (default, DAU) or `month` (MAU). Maps to `toStartOfDay`/`toStartOfMonth`.
- `by`, `user` (default → `user_id`) or `session` (→ `session_id`).
- `mode`, `exact` (default → `uniqExact`) or `approx` (→ `uniq`, HyperLogLog; cheaper/approximate for large ranges).
- `from` / `to` (ISO 8601, defaults to last 24 hours). `from` must be ≤ `to`.
- `limit`, max buckets returned (defaults to `MaxQueryLimit`).

Because `user_id` is nullable, distinct counts **skip anonymous (`NULL`) events**, so they never inflate the count. Note: a true rolling 30-day MAU is a single `uniq(user_id)` over `now()-30d`, do **not** sum daily buckets (users overlap).

```json
{
  "projectId": "my-app", "tenantId": "tenant-1",
  "interval": "day", "by": "user", "mode": "exact",
  "from": "2026-06-01T00:00:00Z", "to": "2026-06-08T00:00:00Z",
  "points": [ { "bucket": "2026-06-01T00:00:00Z", "activeCount": 512 } ]
}
```

### GET `/api/v1/schema`

What this deployment collects: core columns, declared dimensions, declared measures with their
allowed aggregations and bounds, and every valid `metric` value. Build a generic UI against this
instead of hardcoding column names.

It also reports what is **actually arriving**, which is a different question from what is declared:

- `eventTypes`, a census of the event types seen in the last 30 days, with counts.
- `populated` / `nonEmptyCount` on every dimension and measure, whether that column holds a
  non empty value on any row in the same window.

```json
{
  "dimensions": [
    { "name": "menu_id", "type": "String", "populated": true,  "nonEmptyCount": 4477 },
    { "name": "locale",  "type": "LowCardinality", "populated": false, "nonEmptyCount": 0 }
  ],
  "eventTypesAvailable": true,
  "populationAvailable": true
}
```

**Why a declared but empty column is worth an endpoint.** It looks healthy from every other angle:
the config is valid, the reconciler created the column, the query allowlist offers it, and a
breakdown over it returns `200` with no rows, exactly what a venue with no traffic returns. The
only component that can tell those apart is the one holding the data.

This is not hypothetical. Four dimensions in the Neavents deployment, `locale`,
`translation_present`, `query_id` and `has_photo`, were declared, reconciled and offered for the
whole retention window while sitting empty on every row, because the producer that fills them was
written but never deployed. Nothing anywhere reported a problem.

Note what `populated` does **not** claim. A column can be full of the wrong thing: `menu_id` carried
the packed payload's dense id for months, so a leaderboard on it drew two confident bars labelled
`"0"` and `"01MENU"`. Population is not correctness. Absence of population is a gap, and that half
is cheap to report.

`eventTypesAvailable` and `populationAvailable` are `false` when a census could not be taken. Read
them: a failed lookup and an empty column are different facts, and only one of them is a bug. Both
counts are cached for 5 minutes, and the population census is a single scan covering every declared
column rather than one query each.

### GET `/api/v1/analytics/breakdown`

Ranks any allowed column. `groupBy` accepts a core column or a declared dimension; `metric` accepts
`events`, `sessions`, `users`, or an aggregation over a declared measure such as `avg(dwell_ms)` or
`p95(dwell_ms)`. The response echoes `grain` (`event` / `session` / `user`) and `truncated`.

`groupBy` and every `filter` name are validated against an allowlist and never interpolated, a
caller supplied string reaches the SQL builder nowhere. An unknown name is a `400` naming it.

Two more parameters worth knowing:

- `traffic`, `normal` (default), `bot`, `internal` or `all`. Reads exclude everything but normal
  traffic unless told otherwise. Nothing is dropped at ingest, so `traffic=all` always gets it back.
- `exact=true`, counts `uniqExact(event_id)` instead of rows. The table is a `ReplacingMergeTree`
  and no query uses `FINAL`, so a WAL replay after a restart can leave an event present twice until
  a background merge collapses it. Slower and exact, versus fast and eventually right.

The response reports `grain` (`event` / `session` / `user`), `source` (`raw` or `rollup:<name>`) and
`truncated`, so a number always says what it counted and where it came from.

### GET `/api/v1/analytics/top`

Top N events or items by count, descending, within your tenant scope.

```bash
curl "http://localhost:5000/api/v1/analytics/top?dimension=event_type&from=2026-06-01T00:00:00Z&to=2026-06-08T00:00:00Z&limit=20" \
  -H "Authorization: Bearer <your-jwt>"
```

Query params:
- `dimension`, `event_type` (default), any other core column, or any declared dimension (validated against the same allowlist `/analytics/breakdown` uses; any other value returns `400`). Nullable columns **exclude `NULL` keys**, so the leaderboard ranks values rather than absence.
- `from` / `to` (ISO 8601, defaults to last 24 hours). `from` must be ≤ `to`.
- `limit`, number of rows (default 20, clamped to `MaxQueryLimit`).
- `traffic`, `normal` (default), `bot`, `internal`, or `all`.
- `exact=true`, counts distinct `event_id` instead of rows. Slower and correct; see below.
- `mode=approx`, counts distinct **sessions and users** with HyperLogLog instead of exactly.

```json
{
  "projectId": "my-app", "tenantId": "tenant-1", "dimension": "event_type",
  "from": "2026-06-01T00:00:00Z", "to": "2026-06-08T00:00:00Z",
  "items": [ { "key": "view", "count": 1240 }, { "key": "purchase", "count": 318 } ]
}
```

---

### GET `/api/v1/analytics/funnel`

Ordered conversion through a sequence of events, within a time window per session or per user.

```bash
curl "http://localhost:5000/api/v1/analytics/funnel\
?step=app_open&step=item_view&step=add_to_cart&step=checkout\
&grain=sessions&windowSeconds=1800&breakdownBy=locale\
&from=2026-06-01T00:00:00Z&to=2026-06-08T00:00:00Z" \
  -H "Authorization: Bearer <your-jwt>"
```

Query params:
- `step`, repeated, **2 to 10** of them, in order. Each is an event type, optionally with one
  filter: `step=item_view:section_id=01J...`. One step is a count, not a funnel, so one is refused.
- `grain`, `sessions` (default) or `users`. Echoed back, so two widgets cannot disagree about what
  they counted.
- `windowSeconds`, how long the whole sequence may take, default `1800`, max `86400`.
- `breakdownBy`, a declared dimension or groupable core column. **This is where the value is.**
- `from` / `to`, `traffic`, `tz` as elsewhere.

**Ordered by `seq`, never by `timestamp`.** Client clocks on cheap Android are hours out, so a
funnel ordered by the clock reports a sequence the user never performed, and it looks entirely
plausible, which is worse than an obviously broken one.

**The aggregate number is close to useless; the segment differences are the finding.** A funnel
converting at 12% in Turkish and 3% in English is a translation quality problem you can now prove
rather than suspect. That is what `breakdownBy` is for, and why it takes a declared dimension.

---

## Authentication

The write path and read path use completely different auth mechanisms. This is by design.

**Write path (ingestion):** API keys. Comma separated list in [`RateLimitPermitCount`](src/Nealytics.Engine/Infrastructure/Configuration/TelemetryEngineOptions.cs). Validated against a `FrozenSet<string>` for O(1) exact match lookups. No substring matching, no wildcards. Pass the key via `X-Project-Key` header or `?k=` query param.

**Read path (queries):** JWT Bearer tokens. The engine validates the signature using the symmetric key in [`RateLimitPermitCount`](src/Nealytics.Engine/Infrastructure/Configuration/TelemetryEngineOptions.cs) and extracts `project_id` and `tenant_id` from the token claims. These claims become mandatory WHERE filters on every query. There's no way to read another project's data even if you have a valid token.

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

Full source: [`RateLimitPermitCount`](src/Nealytics.Engine/Infrastructure/Configuration/TelemetryEngineOptions.cs)

### Declared schema

The engine ships knowing no column names beyond the core ones. A deployment declares its own.

| Variable | Default | What it does |
|---|---|---|
| `Dimensions__N__Name` | _(empty)_ | snake_case column name, `[a-z][a-z0-9_]{0,62}`. A dimension is a GROUP BY key. |
| `Dimensions__N__Type` | `String` | `String` \| `LowCardinality` \| `UInt64` \| `Int64` \| `DateTime` |
| `Dimensions__N__Retired` | `false` | Stops collection, keeps the column and every row. |
| `Measures__N__Name` | _(empty)_ | snake_case column name. A measure is a quantity to aggregate, never grouped by. |
| `Measures__N__Type` | _(required)_ | `UInt8` \| `UInt16` \| `UInt32` \| `UInt64` \| `Int16` \| `Int32` \| `Int64` \| `Float32` \| `Float64` \| `Decimal` |
| `Measures__N__Aggregations` | `sum,avg,min,max,count` | Subset of `sum,avg,min,max,count,p50,p75,p90,p95,p99`. Anything not declared is a 400. |
| `Measures__N__Minimum` / `Maximum` | _(none)_ | Inclusive bounds. Out of range values are rejected and counted, never clamped. |
| `Measures__N__Retired` | `false` | Same contract as a retired dimension. |
| `MaxDimensions` / `MaxMeasures` | `64` | Ceilings. The registry issues DDL, so a config bug must not add columns without limit. |
| `RetentionDays` | `90` | Applied to the table's TTL at startup when it differs. |
| `EventTypeRetention__N__EventType` / `Days` | _(none)_ | Keeps one event type for **less** time than the base. See below. |
| `Projects__N__Key` / `ProjectId` | _(none)_ | Pins a key to one project. Empty ⇒ any valid key may write any project, as today. |

#### Per event type retention

Every analytics deployment has one event type an order of magnitude larger than the rest, worth
nothing individually once it has been rolled up:

```json
"RetentionDays": 90,
"EventTypeRetention": [ { "EventType": "item_impression", "Days": 30 } ]
```

becomes a compound `TTL` on `global_events`. **It can only ever be shorter than `RetentionDays`,
and the engine refuses to boot otherwise.** That is not a style rule, it was measured. ClickHouse
evaluates every TTL clause independently and any match deletes, so:

- **Clause order is irrelevant.** A 45-day old impression was removed by a 30 day rule written
  *after* the 90 day catch all.
- **The shortest applicable rule wins.** A 100-day old row with a 365 day rule was deleted anyway
  by a 30 day base. Allowing that would mean the config file, the code review and the startup log
  all say a year while the rows are gone in a month.

To keep something *longer*, raise `RetentionDays`.

Five core columns arrived with this: `seq` (monotonic per session, order within a session by this,
never by `timestamp`, because client clocks lie), `ingested_at` (server clock, so skew is measurable),
`traffic_class`, `page_path` and `referrer`.

**`traffic_class` changes what every read returns.** Values are `normal`, `bot` and `internal`, and
reads now count `normal` only unless you pass `?traffic=all|normal|bot|internal`. Bots were labelled
at the edge from the day `device_class` existed and nothing ever filtered on the label, so every
number this engine has served included preview crawlers and link unfurlers. Numbers dropping when
you upgrade is the fix working. Nothing is dropped at ingest, `traffic=all` always gets it back,
which is the only way to answer someone disputing a figure.
| `Rollups__N__Name` | _(empty)_ | Preaggregate table suffix. Creates `rollup_<name>` plus its materialized view. |
| `Rollups__N__Grain` | `day` | `hour` \| `day` \| `session` |
| `Rollups__N__EventTypes` | _(all)_ | Comma separated. Empty means every event type. |
| `Rollups__N__Dimensions` | _(none)_ | Comma separated declared dimensions to group by. |
| `Rollups__N__Measures` | _(none)_ | Comma separated `measure:aggregation`, e.g. `dwell_ms:sum,dwell_ms:avg`. Percentiles are refused, they stay on the raw table. |

#### Session rollups

`Grain: session` gives **one row per session** instead of per time bucket, and `/api/v1/analytics/sessions`
reads it instead of scanning raw:

```jsonc
{ "Name": "sessions", "Grain": "session", "EventTypes": "",
  "Dimensions": "menu_id,locale", "Measures": "dwell_ms:sum,dwell_ms:max" }
```

This is what replaces a scheduled sessionizer. A job that sweeps sessions idle for thirty minutes
has to be rerun for late arrivals and *still* leaves a hole when one lands after the rerun, so
"how long was that session" gets an answer that depends on when you asked. An `AggregatingMergeTree`
fed by a materialized view cannot have that hole: a late event is simply another partial state that
merges in. "Session ended" needs no idle rule at all, it is `maxMerge(timestamp)`.

Three properties worth knowing before you declare one, all measured on 26.7.1:

- **A session crossing midnight is stored as two rows and regrouped into one at read time.** The
  partition key has to be a function of the ORDER BY columns and `min(timestamp)` is an aggregate,
  so the key is a plain `event_date`. A 23:50–00:05 session reads back as a single 900-second
  session. Any daily bucketing has this property; it is honest, not a bug.
- **It does not key on `event_type`,** unlike every bucketed rollup. A session touching five event
  types would otherwise become five rows whose duration is measured per event type, a number that
  looks entirely reasonable and answers a question nobody asked.
- **Bounce is computed at read time,** from `uniqExactMerge(event_types)`, not frozen into a flag at
  write time. The definition can change without rebuilding the table.

`/sessions` reads the rollup only when **both ends of the range sit on midnight** and the rollup
**filters no event types**, one declared over `app_open,menu_view` holds only those rows, so its
per session event count is not the session's event count. Otherwise it scans raw. The response says
which, in `source`.

A session rollup is never used by `/breakdown` or `/timeseries`: it has no `bucket` column and no
`event_type` key, so the question is not the same one.

```bash
TelemetryEngine__Rollups__0__Name=daily_by_product
TelemetryEngine__Rollups__0__Grain=day
TelemetryEngine__Rollups__0__EventTypes=view,impression
TelemetryEngine__Rollups__0__Dimensions=product_id,campaign_id
TelemetryEngine__Rollups__0__Measures=cart_value:sum,cart_value:avg
```

Each rollup stores `events`, `sessions` and `users` plus the declared measure states, and normalises
its grouping keys the same way `/analytics/breakdown` does, so a rollup and the raw table answer the
same question with the same keys.

`/analytics/breakdown` routes to a rollup automatically and **says which store answered** in the
response's `source` field, `raw`, or `rollup:<name>`. A chart that silently changed data source is
one nobody can debug, so this is reported for the same reason `truncated` is.

A rollup answers a request only when all of these hold, and falls back to raw otherwise:

- **The range is aligned to the bucket.** A rollup row is one whole day (or hour). Answering
  12:00–18:00 from a daily rollup would return the whole day and look entirely healthy, so both ends
  must sit on a boundary; the routed query then covers `[from, to)`.
- The `groupBy` column and every `filter` column are stored in that rollup.
- The request names an event type the rollup aggregated. A rollup restricted to some event types
  will never answer a request that spans all of them, because it would undercount.
- For a measure metric, that exact `measure:aggregation` pair is stored. Percentiles never route.

When several rollups match, the one with the fewest grouping columns wins. A rollup **aggregates from the moment it is created**, existing
rows are not backfilled, which the startup log says explicitly. Changing a rollup's shape is not
done by editing it: the engine leaves an existing rollup alone and warns, because recreating the
view would start aggregating a new shape while every stored row kept the old one.

Past a handful of entries, declare them in a file instead, `TelemetryEngine__SchemaFile=/etc/nealytics/schema.json`,
whose root is a `TelemetryEngine` section. It is the same configuration system, one more provider,
so an environment variable still overrides anything in it. See
[from, to)`.
- The `groupBy` column and every `filter` column are stored in that rollup.
- The request names an event type the rollup aggregated. A rollup restricted to some event types
  will never answer a request that spans all of them, because it would undercount.
- For a measure metric, that exact `measure:aggregation` pair is stored. Percentiles never route.

When several rollups match, the one with the fewest grouping columns wins. A rollup **aggregates from the moment it is created** — existing
rows are not backfilled, which the startup log says explicitly. Changing a rollup's shape is not
done by editing it: the engine leaves an existing rollup alone and warns, because recreating the
view would start aggregating a new shape while every stored row kept the old one.

Past a handful of entries, declare them in a file instead — `TelemetryEngine__SchemaFile=/etc/nealytics/schema.json`,
whose root is a `TelemetryEngine` section. It is the same configuration system, one more provider,
so an environment variable still overrides anything in it. See
[`neavents-schema.example.json`](neavents-schema.example.json).

```bash
TelemetryEngine__Dimensions__0__Name=product_id
TelemetryEngine__Dimensions__0__Type=String
TelemetryEngine__Dimensions__1__Name=campaign_id
TelemetryEngine__Dimensions__1__Type=LowCardinality
TelemetryEngine__Measures__0__Name=cart_value
TelemetryEngine__Measures__0__Type=Decimal
TelemetryEngine__Measures__0__Aggregations=sum,avg,p95
```

Each becomes a real typed ClickHouse column, created at startup. **Removing a declaration is refused
at boot if the column holds data**, set `Retired: true` instead. Deletion must not be expressible by
absence, because absence is what a mistake looks like.

Check a declaration without writing anything:

```bash
curl -XPOST localhost:5000/api/v1/telemetry/validate -H 'X-Project-Key: myapp:mykey123' \
  -d '{"projectId":"myapp","tenantId":"t1","sessionId":"s1","eventType":"view",
       "dimensions":{"product_id":"SKU-1","typo":"x"},"measures":{"cart_value":"49.90"}}'
# -> { "accepted": false, "droppedDimensions": ["typo"], ... }
```

### Database & Storage

| Variable | Default | What it does |
|---|---|---|
| `ClickHouseConnectionString` | `Host=127.0.0.1;Port=9000;Database=nealytics_core;` | ClickHouse native protocol connection string |
| `WriteAheadLogDirectory` | `/var/log/nealytics_engine/` | Directory for the WAL file. Must be writable. |
| `WalFileBufferBytes` | `65536` | Size of the buffered WAL `FileStream`. Larger buffers let group commit coalesce more appends per flush. |
| `ConnectionPoolSize` | `16` | Max concurrent ClickHouse connections. Bounds acquisition (acquire side semaphore) and idle retention. `0` = unbounded, retain nothing. |
| `EnableWireCompression` | `true` | Enables LZ4 compression on the ClickHouse native protocol (inserts and query results). |

### Batch Processing

| Variable | Default | What it does |
|---|---|---|
| `MemoryChannelCapacity` | `100000` | Bounded channel size. When full, ingestion endpoints block (backpressure). |
| `DatabaseBatchCommitSize` | `10000` | Events per ClickHouse batch insert |
| `ForceFlushIntervalSeconds` | `3` | Max time to wait before flushing a partial batch |
| `MaxInsertRetries` | `5` | Retry attempts on ClickHouse insert failure |
| `RetryBackoffCeilingMs` | `30000` | Max delay between retries (exponential backoff caps here) |
| `WalReplayRetryDelayMs` | `10000` | Delay before retrying a WAL replay batch when ClickHouse is unavailable at startup |
| `EnableAsyncInsert` | `true` | Appends `SETTINGS async_insert=1, wait_for_async_insert=1` to the batch INSERT. Server side coalescing reduces part churn at pod scale; `wait_for_async_insert=1` keeps the WAL ack honest (durable before commit). |

### Ingestion

| Variable | Default | What it does |
|---|---|---|
| `AllowedProjectKeys` | _(empty)_ | Comma separated API keys. Empty = all requests rejected. |
| `MaxRequestBodyBytes` | `1048576` | Max request body size (1 MB). Enforced at Kestrel level and per endpoint. |
| `MaxConcurrentConnections` | `20000` | Kestrel concurrent connection safety valve. `0` = unbounded. |
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
| `DefaultSessionQueryRangeHours` | `24` | Default time range when `from`/`to` are not specified on sessions, active users, top N, and time series endpoints |
| `EnablePrometheusScrape` | `false` | Expose the engine metrics at `GET /metrics` for Prometheus scraping (unauthenticated, keep on an internal network). |

---

## Write Ahead Log (WAL)

Source: [`WriteAheadLogger.cs`](src/Nealytics.Engine/Infrastructure/Storage/WriteAheadLogger.cs)

Every event that hits an ingestion endpoint is serialized to the WAL file before being published to the in memory channel. The WAL is a simple newline delimited JSON file (`telemetry_wal.log`). Durability uses **group commit**: appends stage their serialized bytes into an in process channel, a single background writer coalesces all pending appends into one vectored write followed by one `Flush(flushToDisk: true)` (fsync), and each waiting request is acked only after its group's flush completes. This amortizes the per event syscall + device round trip over the whole group while keeping the durability barrier, the `202` still means "on disk". Under low load a lone append flushes immediately (a group of one); under load the group grows and self tunes, so no timer is needed.

### Why we need this

The in memory channel is fast but volatile. If the process crashes, everything in the channel is gone. The WAL gives us a recovery point. On startup, the batch processor calls `ReplayUncommitted()`, reads every line from the WAL, deserializes it, and pushes it through the normal batch insert pipeline. Only after ALL recovered events are successfully committed to ClickHouse does the WAL get truncated.

### How it avoids allocations

Serialization uses a `[ThreadStatic]` `ArrayBufferWriter<byte>`. Each thread gets its own reusable buffer. The JSON is written directly to the buffer via `Utf8JsonWriter`, a newline is appended, and the raw bytes are written to the file. No string intermediaries, no `byte[]` allocations per event.

Each append serializes into a `[ThreadStatic]` buffer and copies the framed bytes into a pooled (`ArrayPool<byte>`) segment that the background writer returns to the pool after its group flushes. Only the single background writer touches the `FileStream`, so file writes never contend across request threads; the per file `SemaphoreSlim` now only serializes the writer's flush against truncation/acknowledge.

### Truncation safety

The WAL only deletes a record once its event is durably in ClickHouse. Two mechanisms guarantee that:

- **Uncommitted record counter.** The background group commit writer increments a counter by the group size once the group is durably flushed (before releasing its waiters, so a returned `AppendAsync` always sees itself counted); `AcknowledgeCommitAsync(n)` decrements it by the size of a committed batch. The active log is truncated **only when the counter reaches zero**, i.e. when every record still on disk is known to be committed. This closes the race where an event that was appended (but not yet published to the channel) could be dropped by a truncation triggered by an unrelated batch.
- **Startup segment rotation.** On construction, any preexisting `telemetry_wal.log` is sealed to `telemetry_wal.replay` and a fresh active log is opened. Recovery replays the *sealed* segment while new ingestion writes to the *active* segment, so events arriving during recovery can never be destroyed by the recovery cleanup. The sealed segment is deleted only after every recovered event commits (retried every `WalReplayRetryDelayMs` if ClickHouse is down).

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
    object_id Nullable(String),
    device_class LowCardinality(String) DEFAULT '',
    os LowCardinality(String) DEFAULT '',
    browser LowCardinality(String) DEFAULT '',
    country LowCardinality(String) DEFAULT '',
    metadata_json String CODEC(ZSTD(1)),
    timestamp DateTime64(3, 'UTC') CODEC(Delta, ZSTD(1))
)
ENGINE = ReplacingMergeTree()
PARTITION BY toYYYYMM(timestamp)
ORDER BY (project_id, tenant_id, event_type, timestamp, event_id)
TTL toDateTime(timestamp) + INTERVAL 90 DAY DELETE
SETTINGS index_granularity = 8192, ttl_only_drop_parts = 1;
```

Design decisions:

- **`LowCardinality`** on `project_id` and `event_type` because these have few distinct values across millions of rows. ClickHouse stores them as dictionary encoded integers internally.
- **`ZSTD(1)`** compression on `metadata_json` because JSON strings are highly compressible and this column can be large.
- **ORDER BY** is `(project_id, tenant_id, event_type, timestamp, event_id)`. All queries filter by project and tenant first (from JWT claims), so these are the primary sort keys. Event type and timestamp come next for the most common aggregation patterns. `event_id` is the final key so that duplicate rows for the same event collapse under `ReplacingMergeTree`.
- **`user_id Nullable(String)`** is an optional cross session identifier that powers Active Users / DAU / MAU (`/api/v1/analytics/active`). It is deliberately kept **out of the `ORDER BY` sort key**, it is high cardinality and would bloat the primary index. Anonymous events store `NULL`; the distinct count aggregates (`uniqExact`/`uniq`) skip `NULL`, so anonymous traffic never inflates the counts.
- **`DateTime64(3, 'UTC')`** gives millisecond precision in UTC. Good enough for analytics, avoids timezone headaches.
- **`ReplacingMergeTree`** engine. Crash recovery replays the Write Ahead Log at least once, so a batch that committed to ClickHouse but was not yet acknowledged in the WAL can be reinserted after a restart. Because `event_id` is part of the sorting key, these duplicates collapse to a single row on merge, giving idempotent recovery. Deduplication is eventual (it happens on background merges); use `FINAL` in a query when you need exact once results immediately.
- **`TTL toDateTime(timestamp) + INTERVAL 90 DAY DELETE`** with `ttl_only_drop_parts = 1` evicts events older than 90 days by dropping whole parts, keeping the store bounded without expensive per row deletes.

### Running more than one ClickHouse node

One node took **318,448 events/s with zero loss** on a 12-core laptop (see
[`bench/RESULTS.md`](bench/RESULTS.md)), so the first answer is usually *you do not need to yet* , 
and the second is *add a rollup before you add a node*, because the rollup is 8–22× on the read path
and a shard is not.

When you do need one, the engine does not have to change. It issues plain SQL against
`nealytics_core.global_events`, so a **`Distributed`** table by that name works exactly like the
local one:

```sql
-- On every node: the local shard, which is what clickhouse-init.sql already creates.
CREATE TABLE nealytics_core.global_events_local ON CLUSTER analytics AS nealytics_core.global_events;

-- The name the engine talks to. sipHash64(tenant_id) keeps a tenant on one shard, so every query
-- this engine issues -- all of which filter project_id + tenant_id first -- hits exactly one.
CREATE TABLE nealytics_core.global_events ON CLUSTER analytics
AS nealytics_core.global_events_local
ENGINE = Distributed(analytics, nealytics_core, global_events_local, sipHash64(tenant_id));
```

Three things to know before you do it:

- **Shard on `tenant_id`, not randomly.** Every query builder puts `project_id` and `tenant_id` in
  the `WHERE` unconditionally, so a tenant keyed shard turns each read into a single shard query.
  Shard at random and every read fans out to every node and merges.
- **`uniqExact` does not distribute cheaply.** Exact distinct counting ships the whole hash set
  between nodes. Use `mode=approx` on `/breakdown` and `/active` for anything estate wide, that is
  what the switch is for.
- **The schema reconciler is not cluster aware.** It issues `ALTER TABLE` without `ON CLUSTER`, so
  it widens the node it connects to and no other. Until that changes, run declared schema changes
  against each node, or point the engine at a node and replicate the DDL yourself.

The engine itself scales out already: each instance owns its own WAL and they all write to the same
ClickHouse, so replicas are additive with no coordination. That path is architecturally clean and
**untested**, nothing in this repo runs two instances.

### Partitioning an existing table

`clickhouse-init.sql` only runs on an empty data volume, so a deployment that already has data never
gets the partition key from it, and unlike a column, a partition key cannot be `ALTER`ed in. The
table has to be rebuilt:

```bash
./scripts/repartition.sh
```

It reads the live definition rather than a file (declared dimensions and measures are columns no
file knows about), copies, swaps with `EXCHANGE TABLES`, atomic on the default Atomic engine , 
copies anything that arrived during the copy, and leaves the old table in place for you to drop
deliberately. Ingest keeps running throughout. Running it on an already partitioned table is a
noop.

This is a performance change, not a correctness fix: retention works without it. What it buys is
time pruning for queries that do not pin an `eventType`, the sort key leads with `event_type`, so
those cannot prune by time through the primary index at all, and retention becoming a
metadata only partition drop instead of a part rewrite.

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

And the four that exist so that nothing the engine discards is invisible. Every one of them is
tagged, and a nonzero reading on any of them means data you sent is not data you can query:

| Metric | Tags | What it means |
|---|---|---|
| `nealytics_unknown_dimensions_dropped_total` | `dimension`, `project_id` | a key you send is not declared, so the value is gone |
| `nealytics_unknown_measures_dropped_total` | `measure`, `project_id` | the same, for measures |
| `nealytics_dimension_values_rejected_total` | `dimension` | declared, but the value did not parse as the declared type; the cell is `NULL` and the batch still commits |
| `nealytics_measure_values_rejected_total` | `measure` | the same, including a value outside a declared `Minimum`/`Maximum` |
| `nealytics_events_rejected_total` | `reason`, `transport` | a whole event refused. `transport` is `track` or `beacon`; `reason` comes from a closed set, never from anything the caller sent |

The first two are worth an alert on any nonzero value: they mean a producer and this deployment's
declaration disagree, and the disagreement is silent everywhere else.

These export over OTLP by default. Set `EnablePrometheusScrape=true` to additionally expose them at `GET /metrics` in Prometheus text format. That endpoint is **unauthenticated** by convention, keep it on an internal network / behind your ingress.

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

## Docker and the binary

Ships as one self contained binary. Nothing to install on the host, no .NET runtime, no shared
framework.

The [`Dockerfile`](Dockerfile) uses a multi stage build:
1. **Build stage** (`dotnet/sdk:10.0`): restores and publishes self contained for `linux-x64`.
2. **Runtime stage** (`dotnet/runtime-deps:10.0`): a minimal base with just libc and OpenSSL.

The container runs as a non root `nealytics` user. The WAL directory (`/app/logs/`) is pre created
with the right ownership.

### Why it is not compiled ahead of time

Short version: it was, and it silently threw away every event.

The ClickHouse client builds its column writers reflectively, roughly
`Activator.CreateInstance(typeof(Dispatcher<>).MakeGenericType(columnType))`. An ahead of time
build cannot see that, so those writers did not exist at runtime. What you got was a binary that
built cleanly, started, answered `/health` and `/ready`, returned `202` to every ingest, and wrote
nothing at all. Three published releases went out like that.

It was possible to keep, by naming every closed generic instantiation in an `rd.xml` file, but that
is a list somebody has to remember to extend every time a column type is added, and forgetting
means silent data loss rather than a build error. It also pushed a publish from 8 seconds to nearly
three hours.

So the engine publishes self contained instead. You give up about 20 MB and a little startup time.
You get a build you can run in CI and a failure mode that does not exist.

The one thing that catches this class of problem is [`scripts/smoke-test.sh`](scripts/smoke-test.sh),
which publishes, runs the real binary against a real ClickHouse, and checks a row was **committed**.
Not that ingest returned `202`. Ingest returned `202` the whole time it was broken. It runs in CI
and gates the release workflow, so a binary that cannot store a row cannot be published.

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
      TelemetryColumnLayout.cs            # The one ordered column list: core + dimensions + measures
      TelemetryColumnBuffers.cs           # Payload -> pooled column arrays
      DimensionColumnBuffer.cs            # One declared dimension's column, typed
      MeasureColumnBuffer.cs              # One declared measure's column, typed + bounds-checked
      TelemetryInsertMath.cs              # Backoff + timestamp math (pure, testable)
    GetFunnel/
      GetFunnelEndpoint.cs                # GET /api/v1/analytics/funnel
      GetFunnelQuery.cs                   # windowFunnel over toDateTime(timestamp), ordered by seq
      FunnelRequestFactory.cs             # Step/grain/window parsing (pure, testable)
    GetSchema/
      GetSchemaEndpoint.cs                # GET /api/v1/schema, what THIS deployment collects
      GetEventTypesQuery.cs               # Cached event-type census, degrades to unavailable
    ValidateTelemetry/
      ValidateTelemetryEndpoint.cs        # POST /api/v1/telemetry/validate, dry run, writes nothing
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
      TopDimensionRules.cs                # null-exclusion rule for the ranked column
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
      GlobalTelemetryPayload.cs           # Payload contract + source generated JSON context
    Storage/
      ClickHouseConnectionFactory.cs      # Connection pool
      WriteAheadLogger.cs                 # WAL with crash recovery
```

Vertical Slice Architecture. Each feature is self contained in its own folder. Infrastructure is shared across slices but has no business logic.

---

## Benchmarks

`bench/RESULTS.md` carries measured numbers, not estimates: **318,448 events/s ingested with zero
loss** on one 12-core box, and the read path answered from a rollup **8–22× faster** than from raw
with the rows proven identical.

```bash
./scripts/run-benchmark.sh all                    # noop, track, beacon
./scripts/run-benchmark.sh read                   # every read endpoint, raw vs rollup
BENCH_ISOLATED=1 ./scripts/run-benchmark.sh all   # on its own ClickHouse, ports 9100/8223
```

Use `BENCH_ISOLATED=1` (and `INTEGRATION_ISOLATED=1` for the integration suite) whenever anything
else might be using ClickHouse. Both scripts finish with `docker compose down -v`, and
`scripts/smoke-test.sh` uses the same default ports, so a benchmark started while one of those is
running will destroy it.

**Put the WAL on real storage before believing a write number.** `BENCH_WAL_DIR` defaults to a
`/tmp` path, and if `/tmp` is tmpfs the WAL is in RAM, `fsync` is a memcpy, and the benchmark
measures durable ingest with its durability switched off. It reported 6× too high that way.

See [`bench/README.md`](bench/README.md) for the layer definitions and how to produce a before/after
pair on one host.

## Testing

```bash
dotnet test tests/Nealytics.Engine.Tests.Unit          # fast, no dependencies
./scripts/run-integration-tests.sh                      # spins up ClickHouse, runs the integration suite
```

The unit suite covers all pure logic, the batch processor orchestration (via a fake writer), and the endpoint validation paths (booted in memory). The integration suite runs against a real ClickHouse and is fully decoupled from any container name or port, it reads `TelemetryEngine__ClickHouseConnectionString` (default `Host=127.0.0.1;Port=9000;...`). See [CONTRIBUTING.md](CONTRIBUTING.md) for details and code style rules.

---

## License

MIT
