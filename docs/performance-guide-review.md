# Technical assessment of the performance architecture guide

Status: Discussion draft 0.3

Date: 2026-10-28

Scope: Review and roadmap integration; no runtime implementation or benchmark execution.

This assessment covers the supplied `performance_architecture_guide.md` and the related
[repository guide](linux-kestrel-wal-clickhouse-performance-guide.md). They are technical proposals,
not verified performance results or execution instructions. The repository version includes more
detail in places, notably a non-replicated deduplication window. Both originals remain unchanged.
The [product roadmap](product-strategy-and-roadmap.md) incorporates the decisions below as
workstreams H1-H3, W1, C1-C5, and T1-T2.

For execution, use the [categorized work packages](work-packages.md). Proposal IDs in this review
are design references, not single implementation tasks. Native backend extraction, implementation,
integration, custom ingestion, and release qualification are separate packages.

Owner decision for this revision: implement native Linux `io_uring` WAL I/O and a dedicated custom
ingestion execution path as early deliverables. Existing callers must send the same requests
without knowing which backend handles them. This supersedes the previous recommendation to
defer native I/O. Technical caveats below are implementation requirements and validation work,
not a recommendation to omit the requested architecture. "Custom injections" is interpreted as
custom ingestion, not dynamically injected user plugins.

## Overall assessment

The strongest proposals target real code paths: sequential beacon WAL waits, shared database
connections, large exact-distinct states, group-rollup joins, and duplicate inserts affecting
materialized aggregates. These deserve implementation work or focused experiments.

The guide is too certain about causation, speedups, universal compatibility, and exactly-once
delivery. Its alternatives also mix different abstraction layers: reducing application middleware
is much smaller than replacing HTTP processing. The selected design keeps the existing caller
contract while introducing custom application processing and completion-driven native WAL I/O.
Lower p95/p99 is the explicit objective; the magnitude remains to be measured.

No new speedup figures in this review are measured. Hypotheses below require the workload and
acceptance gates in the roadmap.

## Proposal-by-proposal decisions

| Guide topic | Assessment | Roadmap treatment |
| --- | --- | --- |
| The approximately 32k req/s result is a Kestrel ceiling | Not established; client, host, protocol, and application effects are confounded | Profile and compare independent clients/minimal servers in P0 |
| Batch beacon WAL appends | Strong proposal; preserve bounds and partial acceptance | H1, P1 |
| Dedicated custom ingestion path | Owner-selected implementation direction; preserve the HTTP contract and required policies | H2, P1 |
| Partition the rate limiter | Useful isolation; not proof that a lock is the bottleneck | P1 authenticated project/key budgets with a global ceiling |
| Replace stream wrappers with `BodyReader` | Worth measuring; cumulative decoded-byte limits must survive | H3, conditional |
| Replace child spans with server-span tags | Reasonable experiment, dependent on listeners and sampling | H2, measured separately |
| Tune sockets, HTTP/2, and Linux networking | Environment-dependent; some suggested options are already defaults | T1 and deployment-specific experiments |
| Add raw TCP or a mandatory streaming protocol | Would require caller changes | Outside the transparent-compatibility requirement |
| Vectored managed WAL writes | Compatible fallback and comparison backend | W1, shared WAL I/O contract |
| Native `io_uring` WAL and fixed-file support | Owner-selected early deliverable; preserve completion and durability semantics | T2, P1, with native/managed parity and latency qualification |
| SQPOLL, registered buffers, and worker tuning | Optional modes within the native implementation | Qualify CPU, deployment, and tail-latency tradeoffs separately |
| Separate database read/write capacity | Strong protection against client-pool starvation | C1, P1 |
| Replace all exact distinct states with sketches | Changes answers and storage types | C3, explicit opt-in with migration |
| Replace tenant-attribute joins with a dictionary | Potential benefit with a significant freshness tradeoff | C4, conditional |
| Add a batch token for replay-safe rollups | Valid direction, insufficient guarantee as proposed | C2, correctness work starts in P0 |
| Disable async inserts, grow batches, pre-sort | Three independent experiments, not universal defaults | C5, measure separately |
| Guaranteed speedups, no downsides, all-network/all-Linux claims | Unsupported | Exclude from release claims and acceptance criteria |

## 1. HTTP and beacon ingestion

### The batching opportunity is real, but not exactly 200 physical flushes in every run

[`BeaconTelemetryEndpoint`](../src/Nealytics.Engine/Features/IngestTelemetry/BeaconTelemetryEndpoint.cs)
awaits `wal.AppendAsync` and broker publication for one event before parsing the next.
At low concurrency, each valid event can therefore wait for a separate durability cycle.
Concurrent requests can share group commits; there is no requirement that they arrive at the
same microsecond. Awaiting also does not occupy a dedicated Kestrel thread for the whole wait.

Add bounded WAL batch submission, then compare beacon sizes and concurrency levels. The current
request-byte limit does not make decoded lists negligible: object overhead, pooled serialized
buffers, and many simultaneous requests multiply memory use. Prefer byte/record-limited
microbatches with a bounded number of admitted requests.

Preserve existing per-element rejection and accepted-prefix behavior when later JSON fails.
Specify what happens after persistence if the request is cancelled before broker publication.
Complete the batch only after the disk barrier and maintain the WAL's uncommitted-record counter.
Keep JSON parsing and sanitization outside the WAL's file lock. No 10-50x speedup is assumed.

### Middleware changes need an endpoint inventory

The current pipeline includes a storage error mapper, authentication/authorization, CORS,
decompression, tracing, and ingestion rate limiting. There is optimization scope, but a route
prefix is not a complete classification: authenticated timeline reads are under `/telemetry`,
schema is outside `/analytics`, and validation and tenant-attribute writes have their own needs.
Keep header and query-string credential compatibility, including query-only beacon keys.

Authentication middleware does not necessarily validate a JWT when no bearer token is present.
Likewise, an activity may not be created when the source is not listened to or sampling declines
it. Measure the actual configured path. CORS preflight and compressed ingestion must still work.
Removing a span changes diagnostic behavior even if response bodies remain identical.

`/health` has no ingestion limiter policy. The guide's limiter-lock explanation therefore does
not establish the cause of that endpoint's observed throughput. Partitioning can improve isolation,
but raw attacker-controlled keys must not create unbounded limiter partitions. Resolve credentials
first and keep a whole-process budget; all traffic on one legitimate key can still contend.

### Direct body access must preserve decoded-byte accounting

Current endpoints construct a `PipeReader` over
[`LengthLimitedStream`](../src/Nealytics.Engine/Features/IngestTelemetry/LengthLimitedStream.cs).
The extra abstraction is worth profiling, but `buffer.Length > limit` is not a general streaming
body limit: consumed buffers disappear, and retained bytes may be observed repeatedly. Track the
cumulative newly received decoded bytes, not just the current window.

Read from the body after decompression middleware has applied its wrapper. Cover chunked requests,
missing or misleading lengths, compressed expansion, malformed compression, JSON spanning reads,
disconnects, and non-Kestrel hosting in parity tests. Framework decompression is lazy; wrapping a
body earlier in middleware does not itself prove unauthorized bodies are eagerly decompressed.
[Microsoft request decompression documentation](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/middleware/request-decompression?view=aspnetcore-10.0).

### Custom ingestion must retain the existing HTTP contract

A raw `ConnectionHandler` receives a connection and its byte stream. It is not a drop-in HTTP
router preserving HTTP/2 parsing, request contexts, or application protections. A custom
`IHttpApplication` is a different integration point: Kestrel can retain HTTP processing, but the
application assumes more request-handling responsibility. The guide groups these approaches too
closely. [Microsoft connection abstraction](https://source.dot.net/Microsoft.AspNetCore.Connections.Abstractions/ConnectionHandler.cs.html).

The implementation will introduce a dedicated ingestion dispatcher behind the existing HTTP
methods, paths, payloads, credentials, and response contracts. Existing HTTP/1.1 callers and browser
beacons must work unchanged. Kestrel retains HTTP/TLS handling initially; the WAL backend performs
native `io_uring` disk I/O. If socket processing changes later, caller compatibility remains a
requirement. A new TCP port or mandatory client-side streaming protocol cannot satisfy it.

`WaitForDataBeforeAllocatingBuffer` is already `true` in .NET 10. Setting it explicitly is not a
new optimization. More I/O queues are not inherently faster than the runtime default. Inspect
defaults and benchmark before exposing transport knobs.
[Versioned .NET 10 transport source](https://raw.githubusercontent.com/dotnet/aspnetcore/v10.0.0/src/Servers/Kestrel/Transport.Sockets/src/SocketTransportOptions.cs).

HTTP/2 is a useful experiment, but proxies can buffer streams, enforce idle/request limits, or
use a different backend protocol. HTTP/3 uses QUIC over UDP and is not simply the TCP/epoll path
in the diagram. Test supported client/proxy/runtime combinations. Do not change host networking
settings or add raw ports by default based only on throughput forecasts.

The proposed security controls are useful requirements, not universal defenses. API-key checks
occur after connection/TLS handling and cannot stop all resource-exhaustion traffic. TLS session
resumption still has CPU cost; buffering policies do not eliminate idle connection memory.
Advertise the specific limits actually implemented and tested, not complete attack protection.

## 2. WAL I/O and memory ownership

[`WriteAheadLogger`](../src/Nealytics.Engine/Infrastructure/Storage/WriteAheadLogger.cs) uses
buffered `FileStream` writes, a shared flush loop, and a disk flush before completing waiters.
One managed write per event is not necessarily one syscall per event. Compare the existing
coalescing behavior with the multi-buffer `RandomAccess.WriteAsync` API before replacing it.
The API accepts buffers and an explicit offset; a universal single-syscall guarantee is stronger
than its contract. [Microsoft API documentation](https://learn.microsoft.com/en-us/dotnet/api/system.io.randomaccess.writeasync?view=net-10.0).

The Unix runtime may use workers for file operations; the explicit `_fileStream.Flush(true)` is
synchronous on the code path invoking it, not magically detached from the flush loop. Measure
blocked time and scheduling rather than infer the benefit of a different backend.
[Versioned Unix runtime implementation](https://raw.githubusercontent.com/dotnet/runtime/v10.0.0/src/libraries/System.Private.CoreLib/src/System/IO/RandomAccess.Unix.cs).

Before increasing producer throughput, bound the currently unbounded pending-append channel and
flush-group size. Stream sealed-log replay; today `ReplayUncommittedAsync` returns a list holding
all recovered events. Include admission before serialization, buffer ownership through I/O
completion, durable dispatch after disconnect, truncation/rotation synchronization, offset reset,
shutdown, and disk-fault recovery in the design. Torn-tail recovery must not silently classify
arbitrary corruption in acknowledged records as an ordinary incomplete final record.

Native `io_uring` can reduce managed-thread waiting; it does not remove storage work or establish
a net CPU gain. Pinned buffers, completion errors/short writes, cancellation, handle lifetime,
recovery metadata, and fallback behavior become implementation obligations. A linked write/flush
pair must be checked for successful completion before acknowledgment.

These obligations now belong to the planned P1 implementation. Extract an internal WAL I/O
contract and retain shared serialization, record accounting, truncation, replay, and dispatch
ownership. Implement a bounded Linux submission/completion loop and package its native assets
for supported Linux x64/ARM64 deployments. Keep the existing log format readable. Backend
selection and capability checks are operator concerns; producers must not negotiate a backend.

Proposed selection modes are `auto`, `io_uring`, and `managed`: use native mode automatically on
qualified supported hosts, choose managed mode at startup otherwise, and fail clearly when an
operator explicitly requires unsupported native mode. Report the selected backend and reason.
Never change backend under uncertain in-flight writes without a completed drain or recovery.

SQPOLL uses a kernel polling thread and may need a wakeup syscall after sleeping. It trades
CPU for submission overhead; increasing worker limits is not automatically helpful to a serialized
WAL. Fixed-file registration and memlock requirements depend on the selected features and host.
Do not require unlimited memlock, loosen container restrictions, or change host limits by default.
[Linux SQPOLL documentation](https://man7.org/linux/man-pages/man7/io_uring_sqpoll.7.html).

Evaluate latency with legacy/managed, custom/managed, legacy/native, and custom/native paths at
the same offered load and durability. Include p95/p99 success latency, visibility latency, errors,
CPU, and memory, plus cancellation and crash parity. Gains must come from less work or waiting,
not earlier acknowledgments, additional rejection, or an unbounded queue. Start without mandatory
SQPOLL; tuning it is separate from delivering the native backend itself.

## 3. ClickHouse throughput and analytical correctness

### Reserve connections without promising database isolation

The same
[`ClickHouseConnectionFactory`](../src/Nealytics.Engine/Infrastructure/Storage/ClickHouseConnectionFactory.cs)
serves reads and inserts. Reserving capacity can prevent slow reads from taking every client lease.
Reuse its eviction/disposal logic across roles and bound overall connections, acquisition waits,
and readiness probe time. Current readiness uses uncancelled acquisition and execution, so give it
an explicit bounded policy. The current batch processor has one insert in flight; size its initial
reservation from that fact rather than blindly allocate four write connections.

Read/write pools still share database CPU, memory, disk, and merge capacity. They do not guarantee
that reads can never delay ingestion. Coordinate this change with query and maintenance budgets.

### Replay can permanently change rollup counts and sums

Incremental materialized views aggregate inserted blocks. Later replacement merges in the raw
table do not undo already-added count/sum states. This makes the guide's identified failure mode
more important than its speculative network gains. Exact distinct-actor states behave differently
from additive counts; test both rather than treating every aggregate as equally affected.

The repository guide improves on the attachment by adding a non-replicated deduplication window,
but a window of 1,000 blocks is still finite. It must cover the actual retry/recovery workload;
expired entries cannot suppress an old replay.
[ClickHouse 25.3 settings](https://raw.githubusercontent.com/ClickHouse/ClickHouse/v25.3.1.2703-lts/docs/en/operations/settings/merge-tree-settings.md).

A hash of whichever events are in the current batch is insufficient when replay uses different
batch boundaries or event order. Persist insert identity and membership before its first attempt,
then preserve it across retries and restarts. Include `ingested_at`, currently regenerated during
column-buffer filling, in the design of stable row representation. Test driver block splitting,
partial source/MV failure, schema changes, and retries beyond the deduplication horizon.

Current ClickHouse documentation describes deduplication behavior changed in 26.2, including async
inserts and dependent views. Nealytics pins 25.3; validate its settings and behavior on that version
instead of applying current defaults. Tokens are one part of a bounded delivery protocol, not a
standalone exactly-once guarantee. Upgrading the database also requires resolving the documented
client compression compatibility constraint.
[ClickHouse deduplication documentation](https://clickhouse.com/docs/guides/developer/deduplicating-inserts-on-retries).

### Approximate rollups must be explicit and migrated

`uniqCombined64` is an approximate cardinality estimator that changes representation as cardinality
grows. Its default dense HLL representation occupies about 96 KiB per state, not universally a
few kilobytes. A bounded state can still be expensive across many groups. Neither a fixed accuracy
percentage for every dataset nor a universal 50x saving follows from choosing this function.
[ClickHouse function documentation](https://clickhouse.com/docs/sql-reference/aggregate-functions/reference/uniqcombined64).

Add an opt-in estimator/precision policy. Match state and merge function types, create versioned
rollups, backfill, and switch only after validating results. Preserve exact requests and existing
defaults. Document measured error and total memory/storage/write amplification at several
cardinalities. Do not silently convert existing exact states or compare exact and approximate
queries as if their guarantees were identical.

### Dictionary lookups change the freshness contract

The current group-rollup query scans and groups tenant attributes with `FINAL`. A dictionary may
reduce insert-time work, but its layout determines memory behavior and its refresh policy controls
visibility. Use a composite project/tenant/attribute key and test deletion, missing values, reload
failure, and per-node consistency.
[ClickHouse dictionary definition](https://clickhouse.com/docs/sql-reference/dictionaries).

If an event uses a stale attribute, its materialized historical attribution remains stale after
the next refresh. Issuing reload commands is not proof that every insert node has atomically
adopted the same revision. Keep the join until lag is an accepted contract or a tested revision
barrier preserves the required behavior. Benchmark reload cost as well as lookup speed.

### Async inserts, batch sizing, and sorting are separate experiments

`async_insert=1` can coalesce work across engine instances; waiting for that flush can also increase
visibility latency. Verify how the binary driver and pinned server execute this path, inspect
actual timeout settings, and compare single- and multi-instance runs. There is no universal
200 ms penalty established for this deployment. Preserve `wait_for_async_insert=1` when relying
on server completion to retire WAL records.

Measure row, byte, and age limits together. Bigger batches can improve throughput while worsening
freshness, retained memory, and retry work. Pre-sorting costs engine CPU and may duplicate database
sorting; the full physical key also includes `event_id`. Match ClickHouse's type ordering rather
than assume .NET comparisons are identical. The proposed 0.5 ms sort cost and compression gains
are hypotheses, not measurements.

## Recommended sequence

This is dependency guidance; the [work-package index](work-packages.md) splits it into independently
reviewable changes and keeps later categories out of the initial native-ingestion track.

1. Establish mixed-load and independent-client baselines, then reproduce replay/rollup failures.
2. Extract shared WAL I/O/accounting, bound pending/recovery memory, and add beacon microbatching.
3. Implement native Linux `io_uring` WAL and the custom ingestion dispatcher behind unchanged HTTP
   routes; qualify native/managed parity and existing-client compatibility.
4. Validate the chosen retry/replay protocol on pinned ClickHouse and document its guarantee limits.
5. Reserve bounded database write/probe capacity, add authenticated admission, and continue the
   named-metric customization work after the ingestion foundation.
6. Add opt-in approximate rollups and migration tooling; evaluate dictionaries and insert tuning.
7. Evaluate optional streaming and native tuning without requiring producer changes. Keep native
   WAL I/O in the main delivery sequence rather than treating it as a deferred experiment.

This sequence prioritizes features customers can use, honest correctness guarantees, and measurable
efficiency while retaining a simple self-hosted deployment. See the main roadmap for completion
gates and open product decisions.
