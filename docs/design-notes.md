# Design notes

The engine's source carries no comments. This is where the reasoning behind its less obvious
choices lives instead, grouped by the part of the engine it explains. The [README](../README.md)
covers what each feature does; this file covers why it is built the way it is.

## Startup and configuration

**The schema file is one more configuration provider, not a second system.** A deployment with a
dozen dimensions and a dozen measures would need seventy-odd indexed environment variables, which
nobody can review. `TelemetryEngine:SchemaFile` adds a JSON file to the same `IConfiguration`, and
the environment provider is added again after it, so the precedence stays the expected one: the
file beats `appsettings.json` and an environment variable still beats the file.

**The registries are built eagerly, before anything takes traffic.** An invalid declaration throws
while they are built, and a refused boot is the only safe answer. Resolving them lazily would let
the service report healthy and then fail one request at a time.

**The engine ships with no dimensions.** `Dimensions` is empty in `appsettings.json` on purpose.
Everything a deployment collects comes from its own configuration, which is what keeps the engine
free of any one product's vocabulary.

**An unset `CorsAllowedOrigins` is logged at boot.** The default accepts any origin, which suits
getting started and does not suit a deployment, and an unset value looks identical to a deliberate
one from outside.

**Globalisation is invariant.** The engine holds no culture sensitive text handling and parses
with the invariant culture explicitly; casing rules for a particular language belong to the
producer. Turning invariant globalisation off would add about 30 MB of ICU data for behaviour
nothing in the engine asks for.

**Reflection based JSON is switched off.** Every serialised type goes through the source generated
`TelemetryAotContext`, and `JsonSerializerIsReflectionEnabledByDefault=false` keeps it that way and
keeps the trimmed size down.

## Ingestion

**One bad element never fails a beacon batch.** `navigator.sendBeacon` cannot retry and its caller
cannot react, so refusing the other events in the batch costs far more than it protects. Every
refused element is counted in `nealytics_events_rejected_total`, because a silent skip lets a client
lose every event of one shape while seeing only `204`s.

**Undeclared keys are dropped per field, never per event.** Losing a whole session because one key
was misspelled is far worse than losing one field. The drop is counted and logged by name, with at
most eight names per log line so a hostile payload cannot flood the log. `/track` callers are
server side code that can act on it, so they also get the dropped names in a response header, and
the event is still accepted without the field.

**Sanitising happens before the WAL append,** so a replay cannot reintroduce a key the registry
rejected: the log on disk and the declared schema stay one story.

**The names reported as dropped come from the removal itself.** `/validate` and the response
headers report what the sanitizer actually removed rather than recomputing it. Two implementations
of "is this declared" would be two things to keep in step, and the one a caller reads would not be
the one that decided what was stored.

**`/validate` runs every check `/track` runs,** including the project pin, or a dry run could
report "accepted" for a payload the real endpoint refuses. A refusal names the field that stopped
it and the whole required set, so wiring a producer up takes one round trip rather than one per
missing field.

**Rejection reasons travel in a header.** `X-Nealytics-Rejected` keeps a `400` a plain `400` to
anything parsing status codes while a person running curl still learns which check refused it. The
reason is a closed set, so as a metric tag it cannot become an unbounded cardinality dimension.

**The body limit is enforced per request, not only by Kestrel.** A chunked request sends no
`Content-Length`, so the up front check has nothing to read; before the limit was counted while
reading, a chunked request well over the limit was answered `202`. Kestrel's
`MaxRequestBodySize` is server configuration that another host ignores and a test without Kestrel
never sees, so `LengthLimitedStream` makes the bound a property of the endpoint. It throws rather
than returning a short read, because a truncated body would surface as malformed JSON and be
reported as the wrong bug. The resulting exception maps to `413`, never to a `5xx`.

**Only the far future is rejected as a timestamp.** A backfill is legitimate and a device hours
behind is ordinary, but a year 3000 row is a broken clock. It is rejected rather than clamped
because `timestamp` is the partition key: one such row creates a partition dated centuries out
that retention never expires. The comparison uses the same reading the insert uses (the raw value
taken as UTC, without conversion), so a row cannot pass the check and land in a partition the
check would have refused.

**The project pin is checked after the shape checks,** because "which project" is only a
meaningful question once there is a `projectId`. A pin binds a key to a project and never to a
tenant: one key legitimately writes many tenants when a single edge worker serves all of them.
Keys without a pin keep the permissive behaviour, so a deployment can adopt pins one key at a time
instead of all at once, and a pin for a key that is not accepted at all refuses the boot, since
dead security configuration reads as a control that is in force.

**Device, OS, browser and country are derived at the edge,** from the User-Agent and the edge's
request metadata, never sent by the client, so a caller cannot forge them and they cost the page
nothing.

**`seq` orders events within a session, never `timestamp`.** Client clocks are unreliable, badly so
on cheap devices; `seq` is monotonic per session from 0.

**Traffic is flagged, never dropped.** `traffic_class` is `normal`, `bot` or `internal`, and reads
count `normal` unless asked otherwise. Anything the edge did not classify is stored as `normal`: an
empty value would become its own group and fall outside the default filter. When someone disputes
a figure, `traffic=all` on the raw rows is the only way to answer.

## Batch writing

**The column list is written down once.** `TelemetryColumnLayout` holds core columns followed by
active dimensions and measures in registry order, which is configuration order and therefore
stable across restarts. The INSERT's column names and the value dictionary handed to the driver
are both derived from it. Built separately they could disagree, and a misaligned column does not
throw: it writes tenant ids into the browser column and reports success.

**Each dimension owns a typed, pooled buffer.** The engine cannot have a field per dimension
because it does not know their names, so each buffer knows its ClickHouse type and how to parse
the wire string. Reference typed arrays are cleared when returned to the pool, because a pooled
`string[]` handed to a later batch without clearing leaks one tenant's ids into another tenant's
rows. `TelemetryColumnBuffers` owns every array for one batch, so rent and return cannot drift
apart.

**Absent and empty become `NULL` for a nullable dimension,** since an empty string would become its
own group meaning "we did not receive this". `LowCardinality` columns store an empty string
instead, which keeps a `GROUP BY` total, the same choice the core device, OS, browser and country
columns make.

**A value that fails to parse is counted and written as `NULL`; the batch still commits.** One
unparseable cell must not cost the other events in the batch, and beacons cannot retry.

**The registry drives the buffers, never the payload's keys.** A WAL record written before a
dimension was retired simply is not read for that column, rather than widening the insert to a
column that may not exist.

**`ingested_at` is the server's clock,** stamped at insert rather than accepted from the caller, so
client clock skew is measurable against it.

## Connection pool

**A connection that failed a command is discarded, not returned.** When ClickHouse restarts or
goes read only, connections opened before keep reporting `Open` and only fail on the next command.
Returning them by state hands a dead connection to the next retry, so a blip that resolved itself
in seconds kept failing every retry until the process restarted. Throwing away a healthy
connection costs one reconnect; keeping a dead one costs every batch after it.
`PooledClickHouseConnection` is a struct holding its discard flag by reference, so a discarded copy
is the same discard the disposal sees.

**Idle connections are recycled by age.** ClickHouse closes a connection idle past
`idle_connection_timeout` (3600 seconds by default) without logging anything, and the client keeps
reporting `Open`. Discarding on failure alone costs one failed insert per dead connection: with a
pool of 16, five retries per batch and backoff between batches, that was fifteen consecutive
failures over four minutes, indistinguishable from an outage while `/health` and `/ready` stayed
green, because a `SELECT` takes a different path from the columnar writer. Age is checked first and
state second, since state cannot answer the question. The age is measured with
`Stopwatch.GetElapsedTime`: a `Stopwatch` tick is not a `TimeSpan` tick, and comparing the two
made every connection look a hundred times older, recycling the pool on every acquire.

## Schema reconciliation

**The reconciler exists because `clickhouse-init.sql` runs once.** It is mounted into
`docker-entrypoint-initdb.d`, so it runs against an empty data volume and never again. A deployment
that already has data never sees it, and it cannot know a deployment's dimensions anyway. The
reconciler adds core columns idempotently and creates declared columns at startup, before the batch
writer takes traffic, because a missing column rejects entire batches. The core column list in the
reconciler and in `clickhouse-init.sql` describe the same table and are kept in step;
`CoreColumnAgreementTests` checks that they agree.

**There is no lock.** `ADD COLUMN IF NOT EXISTS` is idempotent, so replicas racing converge, and
replicas of one deployment read the same configuration, so they cannot disagree about a type. Two
different configurations deployed at once is a deploy error, which the type check turns into a
refused boot.

**Two kinds of failure get two answers.** A declared type that contradicts the column, or a column
holding data nobody declared, is a disagreement between configuration and storage; restarting
will not change it, so the boot is refused. Failing to reach ClickHouse is transient, and refusing
to start would take analytics reads down because the write path could not be widened, so that is
logged loudly and the service comes up.

**An undeclared column holding data refuses the boot.** Without that rule, deleting one line of
configuration leaves the column and its data in place while new writes for it are dropped and the
query API stops offering it: a hole starts in the data at the moment the chart that would show it
goes blank. Retiring is an explicit act for the same reason. An undeclared column with no data is
left alone; the engine never drops a column.

**Types are compared against what ClickHouse reports.** Each configured type maps to the exact
string `system.columns.type` returns, read from a probe table rather than guessed, and whitespace is
normalised before comparing. A value that merely looks right would turn every boot into a spurious
refusal. The raw strings are what gets logged.

**Reserved names cannot be declared.** A dimension named after a core column would generate DDL
that fails obscurely or shadows the column, and a duplicate name would produce two buffers writing
one column. `metadata_json` is reserved while it exists, because `ADD COLUMN IF NOT EXISTS` on it
would silently do nothing.

**The object id index is added in place.** Adding the bloom filter index is metadata only and
idempotent, including on a nullable column. Existing parts stay unindexed until their next merge, so
it speeds up recent data first, which is what drilldowns mostly ask about. `object_id` is in no
part of the sort key, so without the index a single object report reads every granule in range.

**Event type names in retention rules are checked by shape.** Event types are never declared, so
there is no registry to check a name against, and the name is interpolated into DDL. The pattern
admits no quote, backslash, whitespace or semicolon, and is more permissive than the dimension
pattern because a deployment's event names may be camelCase or dotted.

**Retention is read back the way ClickHouse stores it.** `INTERVAL 30 DAY` comes back as
`toIntervalDay(30)` and the `DELETE` keyword is dropped as the default, so both spellings are
parsed, and only within the TTL clause so a column default elsewhere cannot be read as a rule. The
live rules are compared with the configuration as a set: clause order means nothing to ClickHouse,
and a reconciler that reports drift on every boot trains people to stop reading the log.

**A per event type retention longer than the base is refused.** ClickHouse evaluates every TTL
clause independently and any match deletes, so the shortest applicable rule wins wherever it is
written. A 365 day rule under a 90 day base deletes at 90 days while the configuration, the review
and the log all say a year; this was verified with a 100 day old row the longer rule did not save.

**Column names in the population census are interpolated** because they come from
`system.columns`, the database's own catalogue, never from a caller, and ClickHouse has no
parameter form for an identifier. They are quoted with backticks.

## Reading

**Caller strings never reach SQL as identifiers.** ClickHouse has no parameter form for an
identifier, so a `groupBy` column must be text in the statement. `QueryColumns` uses the caller's
string only as a dictionary key and returns the canonical instance it already held; a request such
as `groupBy=x; DROP TABLE` matches no key and becomes a `400`. Every value is a bound parameter.
Measure columns and aggregate functions are resolved the same way, from the registry and a closed
map. `event_id` and `metadata_json` are not groupable: one is unique per row and the other is what
typed columns exist to avoid. The traffic class parameter resolves to the stored instance for the
same reason, even though it is bound.

**The query allowlist, ingest and the reconciler share one registry,** so they cannot drift.
Retired dimensions are absent from the allowlist by construction.

**One filter grammar for every read.** `/breakdown`, `/timeseries`, `/active` and the others share
`FilterParser`, so a filter means one thing everywhere. Filters are repeatable and read with
`ToArray`, so a second filter narrows the result instead of overwriting the first and silently
widening it. At most sixteen are accepted, since each adds a clause and a parameter.

**Rows, totals and group counts come from one statement.** Issued separately, each would see a
different set of rows while ingestion continues, so `share` would not sum to what the totals imply
and `truncated` could be computed against a group count the rows never had. `share` is against the
total across all groups, so a capped response's shares add up to less than one, which is the
honest reading of a partial list. Limits are clamped rather than rejected, and the response says it
was capped. `/sessions` follows the same rule: its totals cover the whole range, not the page.

**Exact and approximate counting are both offered.** The table is a `ReplacingMergeTree` read
without `FINAL`, so a WAL replay can leave an event present twice until a merge collapses it;
`exact` counts distinct event ids instead of rows. `uniqExact` holds every distinct value in
memory, which is right for one tenant's week and fails a wide range over many tenants outright;
`mode=approx` uses `uniq` (HyperLogLog, about 1.6% error, fixed memory). Both are opt in so no
existing number moves, and `/breakdown` uses the same spelling as `/active`.

**Every response says how it was counted and where it came from.** `grain` is part of the metric's
definition, since two widgets that both say "visits" while counting at different grains disagree
forever. `source` is `raw` or `rollup:<name>`, because both paths return the same numbers by
construction and nothing else would reveal that a chart changed its data source.

**Endpoint arguments are passed by name.** The request factories take many consecutive optional
strings; passed positionally, `to` once landed in the `traffic` slot and every request setting `to`
got a `400`, while the unit tests, calling the factory directly, passed.

**`/schema` reports measured population separately from declared columns.** A declared dimension no
producer sends reads exactly like one whose tenant had no traffic. When `populationAvailable` is
false the counts are absent rather than zero, because a failed census and an empty column are
different facts. Rollups are reported too, since a client building itself from `/schema` has no
other way to learn that a range must land on a bucket boundary to benefit from one. Grain names are
built in one tested place because they are a wire contract.

## Rollups

**A rollup answers only aligned ranges.** A rollup row is one whole bucket, so answering 12:00 to
18:00 from a daily rollup returns the whole day, a number that is wrong and looks healthy. Both ends
must sit on a boundary, and the routed query then covers `[from, to)`. The rollup path keeps the
raw path's statement shape, totals and group counts, so `share` and `truncated` mean the same thing
on both.

**A session rollup is never a breakdown source.** It has no `bucket` column and does not key on
`event_type`, so the breakdown query against it would not be the same question. Percentiles stay on
raw because a rollup stores only sum, avg, min, max and count states.

**The user metric drops zero groups on the rollup path.** Raw counts users with
`user_id IS NOT NULL` in the `WHERE`, so a group with no known user has no rows and never appears; a
rollup keeps that group with a merged count of zero, which raw would never have returned.

**Rollup keys are normalised.** `AggregatingMergeTree` refuses a nullable sorting key, so a rollup
stores `ifNull(toString(x), '')`, which is exactly how a breakdown renders its key, so the two agree
by construction. Core columns such as `object_id` are groupable in a rollup, or a rollup could not
answer per object reports.

**Session rollups replace a scheduled sessionizer.** A job that sweeps sessions idle for thirty
minutes has to be rerun for late arrivals and still leaves a hole when one lands after the rerun.
A materialized view into an `AggregatingMergeTree` is always current: a late event is another
partial state that merges in, and "session ended" is `maxMerge(timestamp)`. A session rollup keys on
a plain `event_date`, because a partition key must be a function of the `ORDER BY` columns and
`min(timestamp)` is an aggregate; a session crossing midnight is stored as two rows and regrouped by
`session_id` at read time, which `minMerge`/`maxMerge` recombine (a 23:50 to 00:05 session reads back
as one 900 second session). It does not key on `event_type`, or a session touching five event types
would become five rows with durations per event type. Bounce is answered at read time from
`uniqExactMerge(event_types)` rather than frozen into a flag, so its definition can change without
rebuilding the table.

**`/sessions` reads a session rollup only when it is exact.** Both ends of the range must sit on
midnight, and the rollup must filter no event types, since one built over a subset holds only those
rows and its per session event count is not the session's. Without a matching rollup, raw is the
answer rather than a fallback.

## Telemetry about the engine

**Every discard is counted.** `nealytics_unknown_dimensions_dropped_total`,
`nealytics_dimension_values_rejected_total` and `nealytics_events_rejected_total` exist because
data that vanishes without a trace is the failure the declared schema exists to end: an edge that
answers `204` while discarding events leaves no evidence except a table that stops growing.

**Logs need their own exporter and the same service name.** Traces and metrics go through the
OpenTelemetry SDK; logs go through Serilog's OTLP sink, which builds its own resource. A service
name set in one pipeline and not the other files the logs under `unknown_service`, delivered and
attributed to nothing. Serilog owns the pipeline outright, so there is no second logger provider to
forward to.

**The OTLP protocol is resolved, not inherited.** The SDK's default is gRPC on `localhost:4317`,
which inside a container posts to nothing, silently, since the exporter retries in the background
and logs at debug. Serilog's sink reads `OTEL_EXPORTER_OTLP_PROTOCOL` only after its configuration
callback runs, so a deployment that sets the endpoint alone falls back to gRPC against an
HTTP/protobuf port. The port is the one fact that cannot disagree with the endpoint, so the log
exporter infers from it when no protocol is set. A `service.name` resource is set on every signal,
because observability backends group by it and a span without one cannot be attributed.

**Framework log levels are applied to Serilog.** `Logging:LogLevel` in `appsettings.json` does
nothing on its own when Serilog owns the pipeline. Before it was read explicitly, ASP.NET Core wrote
four Information lines per request; a benchmark at 20k requests a second produced 80k log lines a
second and a 7.7 GB file in minutes, all of it formatted on the hot path.

## Build and CI

**The binary is self contained, not compiled ahead of time.** The README's "Why it is not compiled
ahead of time" section has the history.

**The smoke test runs the real binary.** It is the only CI job that publishes, runs the result
against a real ClickHouse and asserts that a row carrying every declared column type was committed,
then calls every read endpoint. Releases are verified this way before they are packaged, and the
released tarball is that exact binary rather than a second build from the same inputs: three
releases once shipped a binary that answered `202`, passed `/health` and `/ready`, and stored
nothing. Every job carries a timeout so a hung job fails rather than spending the default six hours.

**ClickHouse is pinned, not `latest`.** CI, the test and benchmark compose files and the example
compose file run the version deployments run, so a ClickHouse release cannot turn the build red, or
break a fresh `docker compose up`, between two commits that changed nothing. That happened with
26.9, which defaults `network_compression_method` to ZSTD while the client library reads only LZ4.
Moving the pin forward is a deliberate change with the suites run against the new version.

**The benchmark's ClickHouse uses ports 9100 and 8223.** The integration suite and the smoke test
use the default ports and finish with `docker compose down -v`, so a benchmark sharing them would
destroy whatever else is running.

**The container image installs curl** for its `HEALTHCHECK`; the runtime base image ships no HTTP
client, and without one Docker reports no health status at all.

## Tenant sets, scoped keys and stitching

**A set is a subquery, not a list in the token.** The token names an attribute and a value; the
query filters `tenant_id IN (SELECT tenant_id FROM tenant_attributes FINAL ...)`. ClickHouse builds
that set before reading and uses it against the primary key, so a thousand-tenant set prunes like a
single tenant does, and the token stays the same size whether the set holds two tenants or ten
thousand. Membership is read at query time, so moving a tenant takes effect on the next query.

**Grouping by an attribute uses `transform` over one scalar subquery.** The attribute map is built
once per query as two aligned arrays and applied with `transform`, which hashes the keys. A join
would do the same work per block, and a `Map` lookup is a linear scan per row.

**Group rollups attribute at insert.** Their view joins each inserted block to `tenant_attributes`,
which is what makes them small. The price is that history keeps the attribution it was written
with; the README says so where the option is declared, because the raw path does not behave that way.

**New response fields are omitted when null.** The HTTP serializer does not inherit the source
generated context's `WhenWritingNull`, so each field added for a new feature carries its own
`JsonIgnore` condition. A deployment that uses none of the new features gets byte-identical responses.

**A public key's timestamp is replaced, not refused.** Refusing it loses the event, and a browser
whose clock is wrong is ordinary. Replacing it with the arrival time keeps the event and takes the
power to backdate away from anyone holding a key that was published in a page.

**Stitching reads links from the alias events at query time.** A table maintained by a view would
need rebuilding whenever a late alias arrived; the alias events already sort first by project,
tenant and event type, so reading them per query is a narrow range scan, and the latest link per
session wins without any bookkeeping.

**Sessionless server events store `''`.** Session metrics count it as one session. Filtering it out
would change the definition of every rollup view and so mark every existing rollup as drifted.
## Named metric execution

Named metric declarations compile at startup into a bounded immutable registry. The query slice
resolves only those definitions, uses existing authenticated scope and typed filter contracts,
and reads raw data. Its exclusive end boundary is explicit; legacy callers retain the previous
inclusive boundary. Numeric strings preserve large integer/decimal results, integer sums widen
before aggregation, and empty sums explicitly coalesce to zero after nullable widening.
Timestamp parameters explicitly use the driver's DateTime2 representation to retain fractional
seconds and the full supported calendar range. See [the operator guide](named-metrics.md),
[C1 scope](packages/c1-named-metrics.md) and [the boundary review](packages/c1-time-boundary-review.md).

## C2 event selection declarations

Event segments compile existing typed filters once at startup, with a bounded immutable catalog.
They select rows for C1 metrics through one optional query parameter; they neither change
ingestion nor persist a materialization. AND composition retains every authorization and metric
scope, and unit safety runs after composition. Tenant-attribute membership declarations are
excluded because they depend on mutable tenant state rather than event fields. Existing numeric
filter comparisons remain Float64; exact integer/decimal predicates require a separate contract.
Schema discovery exposes the original declarations, and the generated response records the
selected segment name. Live editing, OR trees and cross-event cohorts are separate capabilities.

Mutation qualification separates pure managed logic from storage execution. Schema-changing
fixtures run with one worker and early abort disabled: parallel add/drop and cleanup operations
on the shared test table can invalidate fixture cleanup. The two scopes retain the complete
original executable-mutant set and 100% thresholds; ordinary suites and artifact smoke remain
independent checks. Failed/partial reports retain their original source identity.

## C3 saved scalar reports

Reports resolve metric/segment references into an immutable bounded catalog at startup. A
request validates common scope once and checks every field's unit requirements before leasing
a connection. Report field aliases are response metadata; SQL reads values by ordinal and never
uses aliases as physical identifiers. Fixed shared segments combine with caller filters using
the existing AND contract. Tenant and project restrictions stay in the common WHERE clause.

Metrics can declare different event types. Applying the first metric's type to the common scope
would silently remove rows needed by later fields. Reports therefore clear that one-field scope
and bind per-field event-type conditions inside conditional aggregates. The shared expression
builder preserves C1's unconditioned expressions exactly. Count-of-measure uses an explicit
nonnull condition; nullable extrema/averages use OrNullIf and integer sums widen before SumIf.
ClickHouse documents the combinator semantics in its
[aggregate function reference](https://clickhouse.com/docs/reference/functions/aggregate-functions/combinators).

The 16-field ceiling bounds plan and response shape, not scan work. All fields share a raw event
source, one query and one read lease; separate segments, formulas and rollup planning would need
their own contracts. No speed claim follows from query shape. See the
[operator guide](saved-reports.md) for the supported configuration and numeric semantics.

## E1 Kafka durable source ownership

One optional sequential consumer reuses project-local validation and the existing dispatcher.
Automatic offset storage/commits are both disabled; the next partition offset is committed only
after WAL persistence and live handoff. Blocking commits deliberately establish a clear first
adapter contract. There is no performance or capacity claim. A later batching/parallelism change
must preserve contiguous per-partition durable prefixes, rebalance ownership and bounded buffers.

Kafka offsets and ClickHouse/WAL transactions are not atomic. A failed/uncertain commit can
re-read a durably accepted record into a new WAL identity. This is at-least-once ingestion and
cannot qualify C8's append-only/unique-source assertion, so that optimization combination fails
at startup. A supplied eventId cannot stand in for durable source deduplication.

The consumer observes both application shutdown and its worker token. Custom handoff can be
cancelled by application shutdown before BackgroundService.StopAsync cancels the worker token;
that ordering is a normal shutdown path. Close/dispose still own native cleanup. A completed
handoff can have an explicit commit race with shutdown; checking cancellation immediately before
a network commit cannot make the two atomic. Incomplete handoffs are never committed.

Poison records and dependency failures stop the host instead of inventing an implicit discard,
retry or quarantine policy. Failure diagnostics expose a fixed stage and source position; raw
upstream exceptions are not attached. Topic/group security, broker retention and manual source
repair remain explicit operator choices. Native redist and managed licenses are embedded in the
single-file artifact and retained verbatim in the repository's licenses directory.

## E2 bounded NDJSON framing and receipts

Complete lines parse directly from buffered input. Incomplete lines transfer to one bounded,
lazily rented byte buffer while their pipe fragments are consumed. Retaining incomplete segments
until a delimiter arrives can deadlock if a producer's pause threshold is below the permitted line
length. Pooled ownership is returned with clearing even if reading/completion fails or the
consumer exits early. Oversized input releases its pipe buffer before yielding the rejection.

The importer applies the pinned project's gate/sanitizers, then shares WAL normalization,
persistence and handoff. A caller token governs pre-admission and further reads; application
shutdown governs admitted persistence/handoff on both execution paths. Accepted-prefix offsets
count decoded UTF-8 bytes and line endings only after completed durable handoff. They are request
receipts, not durable job/checkpoint identities. Missing receipts require reconciliation rather
than automatic chunk retries or an exactly-once assumption.

One explicit import concurrency policy composes with global write/byte admission without changing
track/beacon endpoints. It has no queue. Import settings and route existence are frozen at startup;
no physical schema, persistent job state or optional service framework is added. Native backend
defaults and the owner's measurement stop remain unchanged.

## Compiled external source mapping

E3 shares a pure mapping feature between Kafka and NDJSON rather than adding a general plugin
runtime. Startup copies bounded field declarations into literal object paths and permitted targets.
Mapping directly constructs the existing envelope; no intermediate rewritten JSON document,
runtime code generation or new serialization dependency is needed. Materialized value UTF-8 bytes
share the adapter's existing line/message cap, including repeated values and defaults.

Identity/time remain explicit source data. Fixed envelope fields accept strings; custom dimensions
and measures additionally accept JSON scalar text before normal schema/policy validation. Missing
mapped project/tenant/event text stays empty for ordinary ingestion rejection. Unmapped source
fields cannot set internal delivery identity or select a different project mapping. Invalid Unicode
decoding produces a safe mapping error rather than an unstructured HTTP storage failure.

NDJSON chooses its declaration using the already authenticated server key's pinned project and
retains original source offsets. Kafka keeps its existing handoff/commit ownership. WAL records
hold the mapped envelope, so replay never applies a later mapping to acknowledged records; an
uncommitted Kafka replay can observe a new declaration after restart and requires operator review.
Durable source identities, retry/deduplication, import jobs and live mapping changes need separate
contracts. This package does not change persistent formats or backend defaults.

## D1 import checkpoint ownership

The caller supplies stable source/job/chunk identities and submits complete bounded decoded
chunks. Buffering one chunk before admission permits hashing its exact original bytes before
looking up a confirmed retry. It is opt-in and separately serialized; E2 streaming and ordinary
track/beacon stay independent. Existing WAL I/O selection remains internal and unchanged.

The manifest and WAL do not share a transaction. A synchronized processing marker precedes the
import callback; a synchronized atomic replacement records its confirmed receipt afterwards.
Unconfirmed markers recover as needs_reconciliation. This avoids treating a crash after a WAL
append as permission to append the same source again. It does not provide automatic resume of
an ambiguous prefix or general source deduplication. A metadata fault latches only the job service
until restart, because a failed directory synchronization can have published a newer manifest
than the in-memory snapshot. Recovery trusts only validated on-disk state.

Manifests contain no source bodies/credentials. Size, history count, versions, checksums, identities
and progress relations are validated before recovery; temporary replacements cannot manufacture
a receipt. File/directory synchronization and exclusive ownership reuse the existing WAL platform
boundary. A frozen ingestion fingerprint fences new chunks across mapping/schema/limit changes,
while confirmed receipts remain accessible. No eviction/deletion policy is implied: terminal
history consumes the configured job cap until a separately designed retention workflow exists.

D1 published qualification also restores a checksummed processing-marker fixture while the engine
is stopped, then verifies that actual Linux startup fences it from replay. This is a recovery
fixture, not evidence of an abrupt-crash timing window. File-level fault tests separately inject
prepare/final write, flush, rename and directory-sync failures around real manifest storage.

Mutation review strengthened checks for exact chunk/source boundaries, callback-reader completion,
native lease ownership independent of managed file sharing, lease flush and parent-directory
synchronization, read-only manifest recovery, maximum padded manifests and stable mapped contract
vectors. Endpoint metadata checks create their own host so startup mutation coverage belongs to
the assertion that inspects it. A negative source length is already rejected by the nonnegative
confirmed cursor and its upper source bound; the redundant explicit disjunct was removed without
changing accepted states. Manifest serialization now uses a named 8,127-byte payload bound with
ordinary bounded writer reservations, rather than unused expanded encoding scratch space. The
65-byte checksum header brings the complete file bound to 8,192 bytes.

## E4 bounded S3 source ownership

S3 is an optional named source adapter around D1's existing checkpoint protocol. Operator-owned
endpoint, bucket, key prefix and project declarations prevent a request from selecting a different
resource or credential. A snapshot identity binds the configured resource, key, version, opaque
ETag and length. It is a reproducible resource identifier, not an authorization token or a content
checksum; structural input validation and project authorization remain independent checks.

Exact S3 keys include literal percent signs, repeated slashes and dot segments. Strict UTF-8 byte
encoding and raw URI construction preserve those bytes through signing and HTTP transport. SigV4
uses sorted signed headers and a cleared pooled workspace. Independent signing vectors include
short secrets and session tokens. Redirects, cookies, automatic decompression and proxy discovery
are disabled so transport behavior cannot silently select a different representation or endpoint.

One queue-free source operation owns a bounded pooled response buffer through conditional range
validation and complete-record framing. D1 may make its existing second bounded copy before WAL
admission. Each layer retains disposal ownership on faults, cancellation and rejection; buffers
return cleared. Metadata failures cannot create a checkpoint. Once admission starts, D1 owns the
intent/receipt ambiguity contract. A confirmed retry refetches the original stored chunk length,
even after framing limits change, and D1 checks its byte hash before returning the old receipt.
An unavailable old object cannot be reconstructed from metadata alone.

Mutation review strengthened independently recomputed snapshot validation, exact EOF/range checks,
response disposal, short-secret signing and control-body I/O diagnostics. UTF-8 encoding uses
exception fallbacks without an unused BOM setting; URI construction delegates equivalent default
port normalization to Uri. These simplifications remove irrelevant decisions without reducing
source validation or excluding maintained code from the test scope.

Published qualification must wait for ClickHouse on its network address. The pinned image first
runs a localhost-only initialization server, which can answer a local table query before shutting
down for the real server. A local readiness query could start the engine during that gap and leave
startup schema reconciliation incomplete. The connector scripts now query the same network alias
used by the engine; a delayed-initialization reproduction checks refusal during the temporary
phase and success after network startup. No engine retry/readiness contract is changed by this
fixture correction.

## E5 CSV record and mapping ownership

CSV framing tracks quotes across pipe segments and records the physical comma positions outside
quoted fields. Decoding uses those validated boundaries directly; it does not run a second quote
scanner or build an intermediate JSON row. One bounded pooled raw-record buffer and at most 255
comma offsets are reused per request. The header's frozen ordinal index and current decoded cells
provide a narrow value reader to the existing compiled mapping. Constrained struct readers share
mapping, text-budget and payload construction rules without boxing or a per-row callback closure.

CSV's logical-record budget includes quote syntax, BOM and ending separators; quoted newlines
remain inside the record. The same import concurrency limiter is shared with NDJSON/job/S3 routes,
while ordinary track/beacon retains its independent admission. CSV can be enabled without NDJSON.
Headers validate every referenced column before telemetry admission, but a valid header consumes
source bytes even when no data row follows. Data receipt positions count records, not lines.

A mapping can name undeclared fields; normal dimension/measure sanitization still runs before WAL
serialization. Explicit event identity/time and project policy remain shared requirements. CSV
adds no persisted retry identity, so missing receipts remain ambiguous. Its multiline grammar
cannot be substituted into S3's NDJSON LF framing or D1 raw chunk contract without a separate
header/checkpoint design. No persisted format, mandatory dependency or backend default changes.

Initial mutation review exposed missing independent assertions for byte-level BOM preservation,
per-record parser state, declared-name boundaries, canceled-read progress, source/body ownership
and serialized sanitization. Tests now compare the UTF-8 bytes of preserved BOM content and use a
finite reader that rejects reads beyond completed input. Redundant preamble reset/fallthrough
work was removed: every successfully framed first record has already resolved its preamble state.
Field-boundary reuse also removes the duplicate scanner's redundant closing-quote bounds and its
mutated nonprogress loops. Preliminary/failed qualification attempts remain distinct from final
source evidence; no mutation threshold or maintained responsibility was reduced.
