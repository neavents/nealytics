# Nealytics Improvement Plan

## Critical (data integrity / correctness)

### 1. WAL truncation race — uncommitted events lost on crash
**File:** `WriteAheadLogger.cs:85-103`, `TelemetryBatchProcessor.cs:243-247`

`TruncateIfSafeAsync` truncates the entire WAL file after a ClickHouse commit succeeds. Between the commit and the truncation, ingestion endpoints may have appended new events to the WAL (line 61: `_fileStream.WriteAsync`). These events are also published to the channel, so they survive if the process stays alive. But if the process crashes before the next batch commits, those events exist only in volatile channel memory — gone from the WAL (truncated at line 95: `_fileStream.SetLength(0)`) and not yet in ClickHouse.

**Fix:** Replace full-file truncation with position-based truncation:
- Expose `_fileStream.Length` from `WriteAheadLogger` at the moment a batch is assembled.
- After ClickHouse commit, truncate only up to that captured offset.
- Events written after the capture point survive any crash.

### 2. `Convert.ToInt32` overflow on session `count()`
**File:** `GetSessionAnalyticsQuery.cs:82`

```csharp
int eventCount = Convert.ToInt32(reader.GetValue(3));
```

ClickHouse `count()` returns `UInt64`. A session with >2,147,483,647 events (possible for long-lived sessions in high-volume tenants) throws `OverflowException`, crashing the query.

**Fix:** Either change `SessionSummaryItem.EventCount` to `long` and use `reader.GetInt64(3)`, or handle the overflow explicitly (cap at `int.MaxValue` with a log warning).

---

## High (functional gaps)

### 3. No pagination on timeline endpoint
**File:** `GetProjectTimelineEndpoint.cs:17-49`, `GetProjectTimelineQuery.cs:19-24`

The timeline endpoint only supports `limit`. There is no `offset`, no cursor, no `before`/`after` parameter. For tenants with millions of events, there is no way to retrieve older events beyond the `limit` window.

The ClickHouse query uses `ORDER BY timestamp DESC LIMIT {limit:Int32}`. A cursor-based approach (e.g. `WHERE timestamp < {cursor}`) would be trivial to add since `timestamp` is already in the ORDER BY.

**Fix:** Add an optional `before` query parameter (ISO 8601 timestamp). When present, add `AND timestamp < {cursor:DateTime}` to the SQL WHERE clause. This gives infinite backward pagination with zero schema changes and optimal ClickHouse index usage (timestamp is in the ORDER BY key).

### 4. Session analytics C# aggregation should move to ClickHouse
**File:** `GetSessionAnalyticsQuery.cs:74-101`

The query groups by `session_id` with `count()` and `min/max(timestamp)`, then the C# code iterates all rows to sum `eventCount` and `durationSeconds` for page-level summary stats. For `limit=10000`, this is 10,000 row iterations in C# — trivial. But the semantics are actually correct: these are per-page aggregates, not global.

**Verdict:** This was a false positive. The summary stats (`TotalEventCount`, `AvgDurationSeconds`) are documented as "summary stats across the returned sessions" (README:155-170). Computing them in C# from already-returned rows is correct and avoids a second ClickHouse round-trip. **Removed from plan.**

### 5. No `from > to` validation on session analytics
**File:** `GetSessionAnalyticsEndpoint.cs:45-55`

If `from` is after `to`, the ClickHouse query runs and returns zero sessions silently. The client gets a 200 with empty data and no indication of the mistake.

**Fix:** After parsing `fromUtc` and `toUtc`, add:
```csharp
if (fromUtc > toUtc)
    return Results.BadRequest("'from' must be before 'to'");
```

---

## Medium (robustness)

### 6. `DateTime.TryParse` without explicit culture/style
**File:** `GetSessionAnalyticsEndpoint.cs:46-54`

```csharp
if (fromValues.Count > 0 && DateTime.TryParse(fromValues[0], out DateTime parsedFrom))
```

`DateTime.TryParse` with no format provider uses `CultureInfo.CurrentCulture`. On servers with non-en-US culture, ISO 8601 strings like `2024-06-15T10:30:00Z` may parse incorrectly or with `DateTimeKind.Local` instead of `Utc`. The parsed `DateTime` is passed directly to ClickHouse as a query parameter — a `Local` kind would shift by the server's timezone offset.

**Fix:**
```csharp
DateTime.TryParse(fromValues[0], CultureInfo.InvariantCulture,
    DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal, out DateTime parsedFrom)
```

### 7. WAL replay is synchronous (startup latency)
**File:** `WriteAheadLogger.cs:105-145`

`ReplayUncommitted` uses `StreamReader.ReadLine()` — a synchronous blocking call — from within the async `RecoverWalEntriesAsync`. Under normal operation the WAL is small (truncated after each batch). But after extended ClickHouse downtime (hours), the WAL accumulates all events. On restart, `ReplayUncommitted` reads the entire WAL synchronously, blocking the startup thread.

For a 100K-entry WAL this is ~50ms — negligible. For a WAL that grew for 8 hours at 10K events/sec, it's ~288M lines — potentially minutes of blocked startup.

**Fix:** Replace `StreamReader.ReadLine()` with `StreamReader.ReadLineAsync()` and make `ReplayUncommitted` an async method.

### 8. No cross-batch backoff when ClickHouse is down
**File:** `TelemetryBatchProcessor.cs:76-100,211-241`

When ClickHouse is unreachable, each batch goes through its internal retry loop (5 attempts, up to ~60 seconds total). After failure, the batch buffer is cleared (line 273 in finally), and the main loop immediately assembles and attempts the next batch with a fresh retry cycle. There is zero delay between failed batches.

This creates a tight retry loop: assemble → retry 5x over ~60s → fail → clear → assemble next → retry again immediately. The ingestion endpoints back up behind the full channel.

**Fix:** After a batch fails all retries, add an inter-batch delay (e.g., start at 1s, exponential backoff capped at 30s, reset on success). This gives ClickHouse breathing room to recover.

### 9. Shutdown drain uses `CancellationToken.None` — hangs on ClickHouse outage
**File:** `TelemetryBatchProcessor.cs:146-161`

```csharp
await PushBatchToClickHouseAsync(CancellationToken.None);
```

During graceful shutdown, the drain loop retries indefinitely if ClickHouse is down. Docker/K8s will eventually SIGKILL after the graceful shutdown timeout, causing data loss for whatever was in the drain buffer. The README acknowledges this ("No events are dropped on clean shutdown"), but the behavior on unclean shutdown (the typical case when ClickHouse is down) is effectively a hang-then-kill.

**Fix:** Pass a timeout-based `CancellationToken` to the drain loop (e.g., 30 seconds). Log a critical warning with the count of abandoned events if the timeout fires. This is better than an indefinite hang followed by SIGKILL with no logged reason.

### 10. Health check doesn't verify ClickHouse connectivity
**File:** `Program.cs:140`

```csharp
app.MapGet("/health", () => Results.Ok());
```

Returns 200 unconditionally. Orchestrators (K8s, Docker healthcheck) can't distinguish "API is up and ClickHouse is reachable" from "API is up but the database is down." The latter means read/write endpoints will fail.

**Fix:** Add a lightweight ClickHouse ping (e.g., `SELECT 1`) with a short timeout (2s). Return 503 if unreachable, 200 if healthy.

---

## Low (defensive / cosmetic)

### 11. No input length validation on JWT claim values
**Files:** `GetProjectTimelineEndpoint.cs:24-25`, `GetSessionAnalyticsEndpoint.cs:24-25`

`projectId` and `tenantId` from JWT claims are passed directly as ClickHouse query parameters without any length or character validation. While Octonica's parameterized queries prevent SQL injection, an excessively long claim value (e.g., from a misconfigured auth service) produces a bloated ClickHouse query and large log entries.

**Fix:** Validate that `projectId` and `tenantId` are non-empty and ≤ some reasonable max length (e.g., 256 chars). Return 400 if invalid.

### 12. Empty `AllowedProjectKeys` produces a silent reject-all
**File:** `ApiKeyValidator.cs:17-20`

```csharp
_validKeys = string.IsNullOrWhiteSpace(rawKeys)
    ? FrozenSet<string>.Empty
    : rawKeys.Split(...).ToFrozenSet(StringComparer.Ordinal);
```

When `AllowedProjectKeys` is empty (the default), `_validKeys` is an empty `FrozenSet` and every ingestion request gets 401. This is correct behavior (fail-closed), but there's no startup warning. An operator who forgets to configure keys gets no diagnostic — just silent auth failures.

**Fix:** Log a warning at startup: "No project keys configured. All ingestion requests will be rejected."

---

## Summary

| # | Severity | Area | Effort |
|---|---|---|---|
| 1 | Critical | WAL truncation race | Medium |
| 2 | Critical | `Convert.ToInt32` overflow | Small |
| 3 | High | No pagination on timeline | Small |
| 4 | High | No `from > to` validation | Trivial |
| 5 | Medium | DateTime culture-safety | Trivial |
| 6 | Medium | WAL replay is sync | Small |
| 7 | Medium | No cross-batch backoff | Small |
| 8 | Medium | Shutdown drain hangs | Small |
| 9 | Medium | Health check is blind | Small |
| 10 | Low | JWT claim length validation | Trivial |
| 11 | Low | Empty keys silent fail | Trivial |

**Excluded (false positives):**
- Docker compose example keys — not application code.
- Session aggregation in C# — correct per-page semantics, avoids second ClickHouse round-trip.
- Model mutability inconsistency (`{ get; set; }` vs `{ get; init; }`) — read-path models are only serialized, never deserialized; the difference has zero behavioral impact.
- Hardcoded SQL strings — adequate for current 2-query surface; premature abstraction.
