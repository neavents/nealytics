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

**The benchmark's ClickHouse uses ports 9100 and 8223.** The integration suite and the smoke test
use the default ports and finish with `docker compose down -v`, so a benchmark sharing them would
destroy whatever else is running.

**The container image installs curl** for its `HEALTHCHECK`; the runtime base image ships no HTTP
client, and without one Docker reports no health status at all.
