# Nealytics product strategy and implementation roadmap

Status: Discussion draft 0.4

Date: 2026-10-28

Scope: Self-hosted Nealytics; preserve the option of managed hosting later.

This document proposes a direction, implementation sequence, and acceptance criteria. Proposed
features, interfaces, and targets are not promises or descriptions of existing functionality.
The phases describe dependencies, not delivery dates. We should revise this document as we
discuss priorities and measure the engine.

**Start with the [delivery categories and work packages](work-packages.md).** Revision 0.4 separates
correctness, ingestion performance, analytics customization, operator tools, optional integrations,
and future hosting. This document retains the technical design; execution uses small packages with
explicit dependencies, compatibility checks, and rollout boundaries rather than whole phases.

Revision 0.3 makes native Linux `io_uring` WAL I/O and a dedicated custom ingestion pipeline
planned early deliverables, following the owner's explicit direction. Existing callers must
continue sending the same HTTP requests. Revision 0.2 added the guide's HTTP/WAL/ClickHouse work
and prioritized replay correctness; its recommendation to defer native I/O is superseded.
See the [technical assessment](performance-guide-review.md) for the reasoning and
the [existing performance guide](linux-kestrel-wal-clickhouse-performance-guide.md) for the proposals
being assessed. The supplied guide and engine code have not been changed by this revision.

Terminology: "custom injections" is interpreted here as a custom ingestion execution path behind
the existing API, rather than arbitrary user-loaded code or dependency-injection plugins.

## 1. Product direction

**Make Nealytics a highly customizable, resource-efficient analytics engine for multi-tenant
applications, running on infrastructure the customer controls.**

The product promise is: define your analytics once; Nealytics handles tenant access and efficient
execution through reusable APIs.

Our target customer is a developer embedding analytics into a SaaS or other multi-tenant product.
They need their own metrics and event vocabulary, but should not need to implement every query,
authorization rule, and optimization themselves.

Tinybird provides a broader managed platform for ingestion, SQL pipelines, and analytical APIs.
Nealytics can compete through ready-made analytics behavior, declarative customization,
predictable resource use, and deployment control. We should evaluate features against that focus,
rather than attempt to reproduce every part of Tinybird's platform.

### Principles

- Self-hosting is the complete product experience today. Hosted services are not a prerequisite.
- Customization belongs in typed, validated definitions wherever possible.
- Compile definitions when a configuration revision is activated; minimize repeated request work.
- An optimization must preserve authorization, metric meaning, and declared accuracy.
- Treat latency, freshness, correctness, and resource consumption as performance requirements.
- Keep the engine independent of any deployment's business vocabulary.
- Preserve the single engine binary/container plus ClickHouse deployment as the default.
- Keep administrative work outside the ingestion and query execution paths where practical.
- Implement native Linux `io_uring` WAL I/O and a dedicated ingestion fast path; keep managed I/O
  as a compatible fallback and comparison baseline.
- Make the ingestion implementation change invisible to existing producers: no mandatory SDK,
  protocol, payload, credential, endpoint, or acknowledgment-contract migration.

We have not established that Nealytics is faster than Tinybird. Our objective is to demonstrate
an advantage on defined workloads with comparable durability, result semantics, and resources.

## 2. Starting point: what already exists

The repository already provides:

- HTTP event tracking, browser beacon batches, a disk-backed WAL, and batched ClickHouse writes.
- Configurable typed dimensions, measures, bounds, allowed aggregations, and retention.
- Timeline, sessions, active users, time series, top items, breakdowns, funnels, pivots,
  distributions, retention, and identity stitching.
- JWT-scoped reads, tenant sets, public/server ingestion keys, and endpoint read scopes.
- Declared rollups with automatic routing for supported requests.
- Schema discovery, startup schema reconciliation, observability, and benchmark tooling.

Important constraints observed in the current implementation:

| Area | Current constraint | Implication |
| --- | --- | --- |
| Definitions | Registries are built from deployment-wide configuration at startup. | Project-specific schemas and live revisions need an explicit design. |
| Rollup routing | Breakdown and pivot routing require aligned hour/day boundaries. | An otherwise similar unaligned request can scan raw events. |
| Time ranges | Raw scope uses an inclusive upper bound; rollup scope uses an exclusive upper bound. | Hybrid plans must resolve this contract before combining sources. |
| Tenant attributes | Raw queries use current membership; group rollups retain attribution from insertion. | These sources cannot always answer the same question interchangeably. |
| Admission | The configured ingestion limiter is shared; equivalent tenant query budgets are not present there. | Expensive tenants can compete for shared resources. |
| Durability | WAL replay is at least once; storage deduplication is eventual. | Event counts, sums, and materialized aggregates need explicit replay semantics. |
| Beacon batches | Each valid element awaits its own WAL append before the next is read. | At low concurrency a request can repeatedly wait on disk; batch admission should be explicit. |
| WAL resources | The pending-append channel is unbounded; sealed-log replay loads all recovered events into a list. | The bounded broker alone does not bound ingestion or recovery memory. |
| Database access | Reads, batch writes, attribute writes, and readiness use the same connection factory. | Pool reservation and role-specific timeouts can protect ingestion from slow reads. |
| Scaling | Multiple engine instances are described but explicitly untested in the README. | Validate scale-out before presenting it as a verified deployment mode. |

The recorded peak of about 318,000 events/second came from a short batched ingestion run whose
p95 HTTP latency was about 1.84 seconds. It is useful evidence of capacity, not proof of sustained
low latency or superiority to Tinybird. Existing results and harness behavior must be reviewed
before setting release targets.

Repository references: [README](../README.md), [design notes](design-notes.md),
[configuration](../src/Nealytics.Engine/Infrastructure/Configuration/TelemetryEngineOptions.cs),
[scope construction](../src/Nealytics.Engine/Infrastructure/Query/ScopeClause.cs),
[rollup planner](../src/Nealytics.Engine/Features/GetBreakdown/RollupPlanner.cs),
[benchmarks](../bench/README.md), and [recorded results](../bench/RESULTS.md).

## 3. Customization: define analytics once

### 3.1 Named metrics, segments, and reports

Start by extending the existing schema file rather than introducing a separate configuration
system. Add versioned declarations for:

- **Metrics:** event counts, distinct actors, measure aggregations, and bounded formulas over them.
- **Segments:** reusable typed predicates over events and declared dimensions.
- **Reports:** a named query specification with declared parameters, metrics, grouping, and limits.
- **Computed dimensions:** a later addition for bounded classification and transformation rules.

For example, a deployment could define a revenue metric restricted to completed-payment events,
with a currency unit, then reference it in several reports. These business names belong in the
deployment's declarations and examples, not in engine implementation branches.

Initial implementation:

1. Define a small typed expression model: constants, field references, supported comparisons,
   aggregate references, and arithmetic. Do not accept expression strings as SQL fragments.
2. Resolve fields and functions through the existing dimension, measure, and query allowlists.
3. Validate types, units, permitted aggregations, maximum expression depth, dependency cycles,
   and definition count before activating a revision.
4. Compile declarations into immutable semantic plans: canonical field references, aggregate
   requirements, predicates, parameters, and result types. This does not require runtime code generation.
5. Finalize formulas after their component aggregates are combined. Define zero-denominator,
   null, missing-field, numeric precision, and unit behavior explicitly.
6. Expose definitions and capabilities through `/schema`. Add a small report execution feature
   that calls shared planning code; retain existing endpoints and response contracts.

Begin with event-level segments. User- or session-level segments such as "has purchased before"
need separate semantics and query planning; they are not equivalent to filtering purchase events.
Defer those until their meaning and cost are specified.

**Acceptance:** a deployment defines a metric, segment, and parameterized report without engine
changes; invalid definitions fail before activation; responses match equivalent reference queries;
legacy endpoints continue to work with no new declarations.

### 3.2 Project-specific definitions

Add logical project schemas after the first named-definition release. Resolve an immutable
project context once per request from authenticated scope or the validated ingestion key.
Reuse that context for ingestion, validation, discovery, authorization, and planning.

Initially keep one physical event table and a bounded union of physical columns. Projects may
expose different logical subsets, but a shared physical column name must have a compatible type.
Reject conflicts before applying DDL. Do not create a table per tenant or permit arbitrary
tenant-driven schema creation.

This is a meaningful tradeoff: one table stays simple, but unrelated projects can widen the
physical schema. Measure column count, DDL cost, insert overhead, and storage amplification;
consider separate storage namespaces only when actual workloads justify them.

For new project definitions, pin ingestion credentials to the project. Keep any legacy permissive
key behavior behind an explicit compatibility path. Missing projects or retired fields must fail
or sanitize according to a documented policy, never fall through to another project's registry.

**Acceptance:** two projects can expose distinct definitions without discovery or query leakage;
conflicting types are rejected; many-project load has bounded registry memory and lookup cost.

### 3.3 Computed dimensions and extension points

Introduce a small set of deterministic transforms: mapping, bucketing, normalization, and bounded
string operations. Benchmark their per-event CPU and allocation costs before enabling them on ingest.
Version stored transforms because changing a rule does not rewrite historical data automatically.

Defer arbitrary in-process scripts or plugins. Expensive enrichment should use optional external
workers with explicit retry and delivery semantics. Later custom SQL endpoints should be
operator-defined, parameterized, read-only, resource-limited, and structurally tenant-scoped.
A text check for a tenant filter is not an authorization boundary.

## 4. Query planning: make customization efficient

### 4.1 Establish semantic contracts first

Represent metric grain, accuracy mode, interval boundaries, timezone, traffic filtering, tenant
membership semantics, units, and identity resolution in the semantic plan.

Choose a consistent range contract for new APIs, preferably `[from, to)`. Preserve legacy behavior
through explicit adapters or a versioned API change; do not silently change existing endpoint totals.
Compare raw and optimized results only when the contracts match.

Start with one report shape or breakdown execution path. Promote reusable planning contracts into
infrastructure only as another feature needs them. Avoid rewriting every endpoint at once.

### 4.2 Combine rollups with raw boundaries

An unaligned range can be divided into a raw prefix, whole summarized buckets, and a raw suffix.
Generate disjoint ranges and combine aggregate states in ClickHouse before finalization.

Implement counts and sums first, then averages using sum/count state. Add distinct counts and
percentiles only when raw and rollup branches emit compatible mergeable states. Never add distinct
counts, average averages, or average percentiles. Global ordering, totals, and limits apply after
the branches have been combined.

Route only to a rollup that covers every filter, dimension, event type, metric, and semantic mode.
Require an explicit backfill-complete state for its covered history. Missing coverage falls back
to raw; it must not return a partial answer as complete.

Group rollups need particular care: insertion-time attribution cannot be combined with raw
current-membership attribution. Initially exclude these from hybrid plans unless the request
explicitly selects matching historical semantics. Exact replay-deduplicated requests must also
avoid aggregate states that have already counted duplicate inserts.

**Acceptance:** optimized and raw results agree on boundary, late-arrival, null, duplicate-replay,
and tenant-membership fixtures; supported unaligned queries scan fewer bytes with a measured latency
benefit; unsupported requests use raw with a recorded reason.

### 4.3 Explain, combine, and improve plans

Next add:

- Operator-visible plan explanations with source choice and rollup rejection reasons.
- Execution measurements for rows/bytes read, duration, and memory where ClickHouse exposes them.
- Dashboard request batching that combines compatible metrics into fewer scans. Bound the number
  of widgets and preserve independent authorization and result semantics.
- Plan ranking using measured or estimated work, rather than only the number of rollup dimensions.
- Rollup recommendations from recurring expensive query shapes. Show estimated read savings
  alongside added storage, backfill work, and ingestion cost.

Automatic rollup creation and result caching come later. Caching needs authorization-aware keys,
definition and membership revisions, explicit freshness limits, and defined late-event behavior.
Canonical query shape alone is not a safe result cache key. Detailed plans are administrative
information; tenant responses must not expose other projects or physical storage details.

## 5. Workload isolation and predictable resource use

Introduce shared admission infrastructure with separate read and ingest policies:

1. Resolve authenticated project/tenant identity before selecting a budget. Public ingestion
   tenant IDs are caller-controlled; use project/key budgets first so invented tenant IDs cannot
   bypass limits or allocate unlimited limiter state.
2. Add global ceilings plus bounded per-project and authorized per-tenant counters and queues.
3. Bound query concurrency, queued requests, execution time, memory, rows/bytes read, and result size.
   Push supported limits into ClickHouse; propagate request cancellation to database work.
4. Reserve or limit capacity for backfills and maintenance so interactive work can continue.
5. Report overload, invalid requests, cancellation, and storage failures separately. Return
   documented retry guidance when rejection is temporary.

Start with explicit configured budgets. Adaptive limits can follow once measurements show how
to adjust them safely. Process-local quotas are sufficient for the first single-instance release;
document that replicas multiply them until a shared quota mechanism is designed.

Account for all buffering layers: active decoded requests, serialized pending WAL bytes, flush
groups, the broker, column buffers, and replay. Bound pending WAL work and each flush group by
bytes and records; acquire capacity before building large serialized batches. Stream recovery
through bounded batches instead of materializing the whole sealed log. A bounded broker is not
an end-to-end memory limit when earlier queues can grow without bound.

Disk exhaustion needs admission rules too. Set a WAL disk budget and reject new work before
acknowledgment if it cannot be persisted safely. Never discard already acknowledged records to
make room. Recovery and replay must continue respecting the accepted event's project context.

**Acceptance:** an expensive tenant cannot consume every interactive slot; limiter state stays
bounded under random identities; cancelled queries stop consuming resources; saturation tests
preserve accepted events and recover after pressure subsides.

### 5.1 Technical performance workstreams from the supplied guide

The workstreams below add implementation detail to the product roadmap. Their order is governed
by correctness and measurement, not the guide's predicted speedups. Detailed qualifications and
primary references are in the [technical assessment](performance-guide-review.md).

| ID | Proposal | Decision | Main dependency |
| --- | --- | --- | --- |
| H1 | Batch `/beacon` WAL submissions | Prioritize bounded microbatches | Explicit partial-request and cancellation semantics |
| H2 | Dedicated custom ingestion execution path and cheaper tracing | Planned alongside native WAL I/O | Existing HTTP contract and endpoint-by-endpoint behavior parity |
| H3 | Reduce request-body wrapping | Experiment without removing cumulative decoded-byte limits | Compressed/chunked/non-Kestrel regression fixtures |
| W1 | Managed WAL backend abstraction and vectored fallback | Support native/managed backend parity and comparison | Durable completion, offset/reset, and fault tests |
| C1 | Reserve write and probe connections | Prioritize workload-specific capacity | Global connection budget and cancellation |
| C2 | Stable retry/replay identity and rollup correctness | Highest-priority correctness investigation | Persisted insert grouping and pinned-version integration tests |
| C3 | Approximate distinct rollups | Add as an explicit option, preserve exact semantics | Versioned states and measured accuracy/resource tradeoff |
| C4 | Dictionary-backed tenant attributes | Conditional optimization | Defined refresh, failure, and attribution behavior |
| C5 | Async inserts, batch size, and pre-sorting | Controlled experiments, not blanket defaults | Mixed-load and multi-instance measurements |
| T1 | HTTP/2 and optional persistent ingestion | Evaluate after H1 and profiling | SDK acknowledgments, reconnect semantics, and proxy tests |
| T2 | Native Linux `io_uring` WAL backend | Planned early deliverable, not deferred | Completion-driven durability, bounded resources, packaging, and fault/latency qualification |

**H1: Bounded batch durability.** Extend the existing WAL with a batch submission operation that
queues the valid records as one logical append unit; all records must be submitted before awaiting
its durability barrier. Reuse the group-commit loop so concurrent requests can still share flushes.
Prefer bounded microbatches over buffering an arbitrarily large decoded object graph. Preserve
per-element validation, sanitization, rejection diagnostics, ordering, and the documented behavior
when a later element is malformed. After disk persistence, dispatch ownership must survive caller
cancellation; a disconnected request must not strand durable events until restart. Preserve record
accounting and WAL truncation safety. A successful response follows persistence and the defined
dispatch handoff, never just enqueueing to an in-memory pending channel.

**H2/H3: Build the custom ingestion path behind the existing HTTP API.** Capture allocations,
CPU profiles, limiter waits, and sampled/unsampled tracing costs as its baseline. Keep Kestrel's
HTTP processing while replacing the application-level ingest execution path. Scope expensive behavior using
resolved endpoint requirements rather than only `/analytics/*`: timeline, schema, validation,
tenant attributes, probes, and beacon preflight have different requirements. A direct `BodyReader`
implementation must count new bytes cumulatively, preserve decompression, enforce limits without
`Content-Length`, and correctly advance/release buffers. Retain the current wrapper until that
parity is demonstrated. Evaluate enriching the server span instead of adding an ingestion child
span; keep that change separately measurable and document the observability difference.

**W1: Shared WAL contract and managed fallback.** Separate WAL record/accounting logic from the
backend performing writes and flushes. Compare explicit-offset multi-buffer writes and a disk
flush with the existing buffered writer and native backend at several group sizes. Measure syscalls,
copies, CPU, allocation, throughput, and p99 durability latency. Existing per-record `WriteAsync`
calls do not imply one disk syscall each because `FileStream` is buffered. Keep buffers owned until
I/O completion; serialize offset updates with truncation/rotation, reset offsets after truncation,
and stop or recover safely after write/flush failures. Neither backend may acknowledge before the
flush completes. Include legacy WAL recovery and interruption tests before changing the default.

**C1: Database capacity by workload.** Reuse the connection lifecycle logic with explicit read,
ingestion-write, and bounded probe/maintenance roles. Size reservations from measured concurrency;
the current processor has one batch insert in flight, so four reserved write slots are not an
automatic requirement. Bound total connections, acquisition time, and probe execution time.
Ensure eviction targets the affected pool. Dedicated connections prevent pool starvation, not
ClickHouse CPU, memory, disk, or merge contention; server-side workload budgets remain necessary.

**C2: Retry and replay correctness.** Reproduce inserts that commit before the client receives
success, then retry and restart with dependent count/sum rollups enabled. Persist insert-unit
identity and its event membership before the first attempt so recovery does not regroup events
into unrelated batches. Account for driver block splitting, changed batch sizes, schema revisions,
and regenerated fields such as `ingested_at`. Evaluate deduplication tokens only with the actual
table engines and settings on pinned ClickHouse 25.3. Include materialized-view failures and retries
outside the deduplication window. A fixed window or hash of the current batch is not an unlimited
exactly-once guarantee; document remaining bounds and reconciliation/rebuild procedures.

**C3/C4: Smaller states and cheaper attribution.** Add an opt-in approximate distinct-state type
with compatible state/merge functions, versioned destination tables, backfill, and accuracy metadata.
Exact requests cannot use approximate-only rollups. Test high-cardinality groups and many sparse
groups because bounded memory per state still multiplies by the number of groups. For dictionaries,
key by project and tenant/attribute, preserve deletion and missing-value behavior, measure reload
cost, and specify per-node refresh visibility. A stale lookup can permanently place an inserted
event in the wrong historical group; later refresh does not repair that rollup. Keep the existing
join unless the customer accepts the proposed consistency behavior or a verified alternative.

**C5/T1: Experiments with explicit adoption gates.** Sweep synchronous/async inserts, byte/row/age
batch limits, and optional pre-sorting independently. Include the complete physical sort key and
ClickHouse-compatible comparison rules when testing sorting; verify whether it saves database
work rather than merely relocating CPU. Do not assume a fixed 200 ms async-insert penalty. Test
HTTP/2 through the actual TLS/proxy route before considering a bounded streaming protocol. Retain
HTTP beacon support. Persistent streams remain optional; the new ingestion path and native WAL
must benefit callers still using ordinary HTTP/1.1 requests. Host networking changes and a new
HTTP parser are separate decisions, not prerequisites for implementing `io_uring`.

**Completion gates:** H1 reduces low-concurrency beacon latency without unbounded request memory;
H2/H3 preserve all endpoint behaviors; W1 passes the same crash/disk-fault suite as the existing WAL;
C1 permits bounded write/probe progress under read saturation; C2 makes duplicate effects and their
remaining guarantees explicit; C3/C4 preserve declared accuracy and attribution; C5/T1 adoption
requires reproducible workload benefits. H2/T2 must pass the transparent-compatibility and native
backend gates below; backend selection is a product requirement, and measured latency determines
qualification for default use on a supported environment.

### 5.2 Native I/O and custom ingestion with transparent compatibility

The priority is lower producer-observed p95/p99 acknowledgment latency, including ordinary
single-event requests and low-concurrency beacon batches. `io_uring` is selected for implementation;
the size of the gain must be established with measurements. Completion-driven I/O can reduce
managed-thread waiting and scheduling overhead, but cannot eliminate the disk flush itself.

#### Caller contract: unchanged

Existing producers must continue using the same host/listener and HTTP methods, including:

- `POST /api/v1/telemetry/track` with its existing single-event JSON and header/query credentials.
- `POST /api/v1/telemetry/beacon` with its existing JSON array, query credential, and browser
  `navigator.sendBeacon()` behavior.
- Existing response status/body/header behavior, validation and rejection reasons, public/server
  key rules, CORS/preflight, compressed bodies, request limits, and accepted-prefix semantics.
- The same durability promise: success cannot be returned merely because work entered a queue
  or the kernel page cache. No requirement to adopt HTTP/2, gRPC, WebSockets, TCP framing, a new
  content type, a new port, or a new client library.

Existing analytics, timeline, schema, validation, tenant-attribute, and probe behavior also stays
compatible. Operator configuration and backend diagnostics may grow; those are separate from
the producer contract.

#### Internal architecture

```text
Existing HTTP producer (unchanged)
    -> Kestrel HTTP/TLS handling and existing route contract
    -> custom ingestion dispatcher: credential resolution, bounded admission, decode/validate
    -> bounded WAL microbatch submission
    -> shared WAL ownership/accounting and backend interface
         -> Linux io_uring write + durability completion
         -> managed backend on unsupported hosts or operator selection
    -> durable dispatch handoff -> bounded broker -> ClickHouse writer
    -> existing success response after the defined persistence and dispatch barrier
```

The first native backend handles WAL disk I/O. Socket networking can still use Kestrel's existing
transport; a native WAL does not imply native network I/O. Any later socket-transport replacement
must retain Kestrel's HTTP semantics or prove equivalent protocol behavior, with the same external
listener. Raw custom ports or an externally visible ingestion protocol are not substitutes.

#### Native backend implementation

1. Extract a small internal WAL I/O interface while keeping serialization, record ownership,
   counters, dispatch, replay format, and truncation rules shared. Keep current WAL files readable.
2. Create an isolated Linux interop module and package its required native assets for supported
   Linux x64/ARM64 releases. Probe required operations and host/container restrictions at startup.
   Prefer a maintained `liburing` interface; decide packaging/linking explicitly.
3. Give the WAL owner a bounded submission/completion loop with fixed-file registration where
   supported. Use stable pooled/pinned buffer ownership until every relevant completion arrives;
   no thread-per-flush scheduling and no unbounded tasks, ring overflow, or pending-byte growth.
4. Submit vectored writes and an ordered durability operation. Track completion IDs, expected
   byte counts, negative results, short writes, and cancelled linked operations. Recover or fail
   incomplete writes before a successful durability acknowledgment; never acknowledge merely
   because submission succeeded or because one completion in the chain succeeded.
5. Start with a flush barrier equivalent to the current disk-flush contract. Use data-only sync
   only after file creation, rename, truncation, and recovery metadata persistence are accounted
   for. Keep offsets, file-handle registration, rotation, and truncation under the same owner.
6. On disconnect after submission, retain buffer and WAL ownership until completion and durable
   dispatch. On disk/completion failure, stop admitting affected writes and expose a recoverable
   storage failure without losing already acknowledged records.
7. Start with completion-driven operation without mandatory busy polling. Qualify SQPOLL,
   registered buffers, affinity, and worker limits separately against CPU and tail-latency goals.
   They are tuning modes, not reasons to postpone the base native backend.

Provide operator-only backend selection, with proposed modes `auto`, `io_uring`, and `managed`.
On supported, qualified Linux hosts, `auto` selects native I/O; otherwise it selects managed I/O
at startup and reports why. Explicit native selection fails clearly if unsupported. Never switch
backends underneath in-flight writes after an uncertain completion; drain or recover first.
Fallback must preserve the exact same caller contract and durability barrier. Do not silently
require elevated privileges or relax container policy to make native mode start.

#### Custom ingestion implementation and acceptance

Build a dedicated application-level dispatcher using the existing parsing, credential-policy,
sanitization, and diagnostics components. Resolve its dependencies once where appropriate;
avoid unrelated read work and repeated per-event scheduling. Keep cumulative decoded-byte
limits and shared policy enforcement even when routing bypasses general application middleware.
If a lower-level `IHttpApplication` becomes part of the implementation, it still uses Kestrel's
HTTP parsing and must pass the complete contract suite before replacing the current handlers.

Run identical captured HTTP requests against legacy and new paths with equivalent configuration.
Compare status, relevant headers, body, stored fields, rejection behavior, ordering, partial
batches, and restart outcomes. Use real Kestrel for HTTP/1.1, compression, disconnect, and browser
preflight cases; in-memory endpoint tests alone cannot establish transport compatibility.

Measure legacy/managed, custom/managed, legacy/native, and custom/native combinations to separate
the contribution of each change. Use matched offered load, durability, payloads, hardware, and
tracing settings. Report p95/p99 acknowledgment and visibility latency alongside sustained
throughput, errors, CPU, memory, and disk pressure. Include low traffic, normal traffic, and
near-saturation load. Select numerical targets from the baseline; do not claim gains by lowering
accepted load, weakening fsync, rejecting more work, or moving latency beyond the success response.

## 6. Performance evidence and release gates

Extend the existing benchmark harness rather than replacing it. Separate durable acknowledgment
latency, batch commit latency, and end-to-end event visibility. An acknowledgment is not proof
that an event is already queryable.

Use a run manifest recording commit, configuration, runtime and ClickHouse versions, hardware,
disk, dataset, cardinality, concurrency, network placement, warm-up, and measurement duration.
Record accuracy mode, indexes, rollups, compression, and durability settings.

Profile HTTP and WAL layers before attributing a bottleneck. Repeat the no-op test with an
independent load generator, a minimal Kestrel application, and the real middleware pipeline;
measure client and server CPU separately. `/health` does not carry the ingestion limiter policy,
so its measured throughput does not establish that limiter contention causes the observed ceiling.
Measure actual syscall counts before inferring them from managed method calls. Record TLS,
HTTP version, tracing/sampling/export settings, connection-acquisition waits, and ClickHouse
part/merge pressure. Diagnostic tracing runs and headline performance runs should be separate
when instrumentation materially changes their results.

| Workload | Measurements |
| --- | --- |
| Track and beacon, including sustained load | Accepted/stored counts, events/s, p50/p95/p99 acknowledgment and visibility latency, WAL growth |
| Concurrent ingestion and dashboards | Query percentiles, errors, freshness, backlog growth, CPU, memory, disk |
| Small and large tenants together | Fairness, rejection rate, and unaffected-tenant latency |
| Raw, rollup, and hybrid queries | Reference-result agreement, bytes/rows read, memory, storage and write amplification |
| Wide schemas and high cardinality | Registry/plan memory, insert cost, distinct-state growth, query limits |
| Restart, disk pressure, and database interruption | Recovery time, acknowledged-event accounting, duplicate effects, and draining behavior |

Use longer steady-state and soak runs; record offered load as well as completed work. Include
an arrival-rate-controlled mode so a client waiting for responses does not hide overload latency.
Probe newly accepted event IDs to measure visibility; wall-clock event timestamps alone are unreliable.

Use direct ClickHouse on identical hardware as an engineering baseline, including equivalent
schema and queries. Its missing API, authorization, and WAL work must be disclosed. Compare with
Tinybird separately using documented provisioned resources, equivalent answers, and disclosed
network differences. Do not compare a local microbenchmark to a remote end-to-end request.

Before a performance claim or release gate, select named datasets, target hardware, and latency
and freshness objectives. Establish variance with repeated runs, then agree regression budgets.
Run fast semantic checks in normal CI and longer performance suites on a stable dedicated host.
Keep correctness failures blocking even if throughput improves.

**Acceptance:** a result is reproducible from its manifest; no acknowledged events are unaccounted
for in the tested failure model; a performance improvement includes resource and tail-latency evidence.
Single-disk WAL durability does not imply survival of loss of that disk or machine.

## 7. Development workflow and self-hosting experience

### Definitions and deployment

Provide an operator workflow for validating definitions and previewing schema changes before
starting a migration. Command names and packaging remain open; first reuse the engine's validation
logic without starting the HTTP server or writing to storage.

Initially activate definitions on restart, as today. Assign a content revision, validate the
complete candidate, and report the active revision through administrative status. Separate
definition validation from storage reconciliation and report both outcomes clearly.

Later migrations should use additive changes, resumable backfills, coverage status, validation,
then activation. Keep the previous compatible definition until activation succeeds. Rolling back
a definition does not reverse destructive DDL or restore expired data; such operations need a
separate explicit operator workflow.

Do not add live reload until ingestion, queued events, WAL replay, and requests in flight have
defined revision behavior. Retain the schema version or sufficient interpretation information
needed to replay already accepted records. Reject incompatible revisions rather than silently
reinterpreting them. Replicas must not activate conflicting storage definitions.

### SDKs and an optional interface

Start with generated TypeScript and .NET query clients from the declared schema. Add a small
browser tracking client with batching and lifecycle handling; retries must respect event identity
and the actual deduplication guarantees. Provide optional report/chart examples, followed by a
lightweight administration playground driven by `/schema`.

Keep these clients and the UI optional. The engine remains usable over HTTP without a frontend
framework or a new mandatory service. Add bulk import or connectors only in response to concrete
customer workflows; start with one bounded, resumable adapter before a connector ecosystem.

### Operating the engine

Document and test installation, upgrade, backup, restore, WAL volume placement, retention, and
ClickHouse compatibility. Separate process liveness, dependency readiness, and schema/backfill
readiness. Test probe behavior during an outage rather than treating every failure as a restart.

Test two engine instances against shared ClickHouse with independent persistent WALs, restarts,
replay, configuration agreement, and controlled shutdown. Shared ClickHouse alone does not provide
replicated engine WALs or validate failover. Keep the first supported topology small and explicit.

**Acceptance:** a new operator can install, upgrade, restore a backup, and recover from the documented
failure cases using the guide; the documented multi-instance topology passes integration tests.

## 8. Architecture and implementation sequence

Preserve vertical feature slices. Endpoints handle transport and authentication, request factories
validate input, and shared infrastructure owns reusable definition, scope, planning, and admission
contracts. Infrastructure must not depend on feature implementations. Continue source-generated
serialization, explicit types, and the existing engine source conventions.

The intended flow is:

```text
Versioned definitions -> validation -> immutable project/metric registries
                                          |
Authenticated request -> typed request -> semantic plan -> admission -> SQL execution -> result
                                          |
                               compatible raw/rollup sources
```

Definitions and plans are bounded in memory. Bind authorization values per request; never put a
request's tenant data into an unscoped shared plan. Source selection must account for current
coverage and policy rather than trusting a cached plan after a schema or membership change.

The phases below are broad milestones. They are not individual tasks or simultaneous workstreams;
the [work-package index](work-packages.md) is the source for selecting implementation scope.

| Phase | Deliverable | Completion gate |
| --- | --- | --- |
| P0: Evidence and contracts | Sustained/mixed-load baseline; HTTP/WAL profiling; C2 replay/rollup failure fixtures; documented time, membership, accuracy, and freshness semantics | Reproducible reference queries and manifests; delivery/duplicate failure model specified before throughput tuning |
| P1: Resource protection and native ingestion | H1 microbatches; H2 custom ingestion path; W1 shared WAL interface; T2 Linux io_uring backend; C1 reserved database capacity; bounded memory/admission | Unmodified callers pass contract tests; native/managed backends pass crash tests; matched-load p95/p99 results and backend qualification recorded |
| P2: Definition MVP | Named metrics, event segments, saved reports, schema discovery; restart activation | One complete customer analytics report works through declarations and a typed API |
| P3: Planner and storage efficiency | Explain output, coverage checks, hybrid aggregate states; C3 explicit approximate rollups; measured H2/H3/W1/C5 improvements | Reference parity, declared accuracy, and demonstrated mixed-load benefit; no speculative default changes |
| P4: Project customization and tooling | Project contexts, compatible physical schema union, validation/migration preview, generated clients | Cross-project isolation and schema-evolution tests; documented adoption workflow |
| P5: Operational maturity | Backup/restore, upgrade guidance, verified multi-instance topology, optional playground | Operators complete recovery exercises and the supported topology passes integration tests |

Operational fixes that affect durability belong in P0/P1 even though broader operational work is
listed in P5. Existing startup reconciliation is reused throughout; P4 adds the richer preview
and lifecycle tooling. Establish versioned-definition semantics in P2 before expanding them to
project-specific physical schema changes in P4.

C2's tested delivery protocol belongs before relying on optimized rollup totals; the initial
failure fixtures are P0, and the chosen fix must pass before the affected release ships. C4
dictionaries and T1 streaming are optional follow-ons when their consistency and deployment
contracts are agreed. T2 native WAL I/O and H2 custom ingestion are in P1. Protocol-changing
transports are outside this plan because producers must keep sending existing requests.

### First implementation track

Use the bounded A/B packages in the [delivery index](work-packages.md): latency baseline and
contract fixtures, managed WAL I/O extraction, bounded WAL admission, standalone native backend,
native integration, separate custom ingestion dispatcher, and final qualification/default enablement.
Each package has its own completion evidence. Reserved database capacity, streaming recovery,
and replay correctness fixes remain separate changes instead of expanding a native-I/O patch.

Native `io_uring` and custom ingestion remain required early deliverables, with existing callers
unchanged. Named metrics, management APIs, and adapters stay organized as subsequent categories;
their presence in this roadmap does not authorize building all of them as one large subsystem.

## 9. Keep a future managed service possible

Preserve the same engine and APIs for self-hosted and any future hosted deployments. Add useful
boundaries now: project identity, explicit policies, configuration revisions, operational status,
secret injection, and bounded local usage measurements.

A future control service could provision deployments, manage organizations and credentials,
coordinate upgrades, collect usage, and handle billing. Hosting should sit around the engine;
ingestion and queries should not need a remote billing or provisioning call to complete.
Usage data should stay local unless an operator explicitly configures export.

Do not build billing, a cloud scheduler, or cross-region orchestration now. Revisit hosting when
customer demand for managed operations and the cost of supporting self-hosted deployments justify
it. A hosted launch also requires verified isolation, restore/failover, metering accuracy, and
operational staffing; it is not just deploying the current container to a server.

## 10. Decisions to refine together

| Decision | Recommended starting position | What could change it |
| --- | --- | --- |
| First customer workflow | Embedded tenant dashboards with custom metrics | A stronger repeated need from current users |
| Customization entry point | Declarative metrics, segments, and reports | Multiple real workflows blocked by the bounded expression model |
| Definition activation | Validated restart activation | Evidence that redeployment materially disrupts customers |
| Physical project isolation | Shared table, compatible typed-column union | Schema width, stronger isolation needs, or measured contention |
| New time-range contract | Half-open ranges with explicit legacy compatibility | Client migration requirements |
| Attribution | Preserve current and historical membership as distinct semantics | A clearly specified historical reattribution feature |
| Accuracy and replay | Explicit per-metric guarantees; no silent downgrade | A tested stronger ingestion/deduplication design |
| Approximate rollups | Opt-in versioned states; preserve exact defaults | Measured cardinality, error, and storage requirements |
| Attribute lookup optimization | Keep join semantics until dictionary freshness is specified | Accepted attribution lag and tested per-node visibility |
| WAL I/O backend | Required Linux io_uring implementation with managed fallback | Supported-host qualification and measured latency determine default selection |
| Ingestion implementation | Required custom ingestion dispatcher behind unchanged HTTP API | Contract parity and p95/p99 results guide internal integration depth |
| Caller compatibility | Preserve existing requests, credentials, responses, and durable acknowledgment | Only an explicitly approved future API version can require client migration |
| Performance objective | Name workload, hardware, sustained rate, p99, and freshness together | Baseline results and customer requirements |
| UI priority | Generated clients and examples before a full dashboard builder | Adoption evidence that a visual workflow is the main blocker |
| Cloud timing | Keep the option open; no cloud delivery commitment | Customer demand and operational readiness |

The first discussion should choose one customer workflow and its performance envelope. That gives
the definition MVP, planner work, and benchmark suite the same concrete target.

## References

- [Delivery categories and bounded work packages](work-packages.md).
- [Technical assessment of the supplied performance guide](performance-guide-review.md).
- [Existing Kestrel, WAL, Linux I/O, and ClickHouse proposal](linux-kestrel-wal-clickhouse-performance-guide.md).
- [Nealytics contributor and architecture conventions](../CONTRIBUTING.md).
- [Example deployment schema](../example-schema.json).
- [Scope and time-boundary construction](../src/Nealytics.Engine/Infrastructure/Query/ScopeClause.cs).
- [Pivot rollup selection](../src/Nealytics.Engine/Features/GetPivot/PivotRollupPlanner.cs).
- [Schema discovery response](../src/Nealytics.Engine/Features/GetSchema/SchemaResponse.cs).
- [Tinybird core concepts](https://www.tinybird.co/docs/forward/core-concepts).
- [Tinybird materialized views](https://www.tinybird.co/docs/forward/core-concepts/materialized-views).
- [Tinybird resource limits](https://www.tinybird.co/docs/forward/pricing/limits).

External references are context for this draft, reviewed on 2026-10-28. They do not replace
reproducible comparisons of the products.
