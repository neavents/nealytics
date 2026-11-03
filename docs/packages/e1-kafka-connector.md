# E1 Kafka connector

Selected by the owner on 2026-11-04. Branch: `feat/integrations-e1-kafka-connector`, based on
`feat/integrations` with the qualified A/B/C foundation through `91a6a57`. Main remains untouched.
Status: implementation in progress; no completion or performance claim.

## Delivery contract

An optional in-process Kafka consumer subscribes to one literal topic and consumer group.
Configuration references an existing server ingestion key pinned to one permitted project.
Resolve that project's immutable schema, ingest gate and sanitizers, then use the same WAL and
bounded delivery pipeline as track/beacon. Existing producers and HTTP contracts do not change.
The Kafka source must already exist; no helper daemon, hosted account or Schema Registry is required.

Each message value is one UTF-8 telemetry JSON object with an explicit nonzero eventId and an
explicit UTC-Z timestamp in the 1900–2299 storage calendar. The message key and headers are ignored.
An event must provide projectId/tenantId and satisfy its pinned key's event policy. Never derive
routing or credentials from a message. Unknown dimensions/measures follow existing sanitization;
logical measure normalization remains shared. Caller `_walRecordId` is never trusted.

Disable both automatic commits and automatic offset storage. Commit exactly the next offset of
that partition only after WAL persistence and live handoff both succeed. Use one sequential
consumer loop, read_committed isolation, no independent partition tasks or fire-and-forget commits.
A commit acknowledges engine durable ownership, not immediate ClickHouse visibility. The existing
WAL owns recovery after that point. Kafka retention and source availability remain operator duties.

This is at-least-once ingestion. A crash, commit failure or rebalance after persistence can cause
reprocessing and duplicate aggregates; a stable eventId does not deduplicate a fresh WAL append.
Do not enable the append-only rollup-planning assertion for this source without a separately
qualified deduplication strategy. Exactly-once ingestion is explicitly outside E1.

On malformed/oversized/tombstone input, authorization, persistence, dispatch or commit failure,
stop the connector and host through BackgroundService failure policy. Do not skip or commit the
failing message, advance to later records or automatically restart the connector. Logs identify a
fixed processing stage and source position without exception details, payloads or credentials.
Operator investigation and restart are required; correcting a poison source may require an
explicit operator-controlled group-offset/source repair. There is no silent dead-letter policy.
Shutdown cancellation closes/disposes the consumer without an implicit commit; uncommitted durable
records may replay. A rebalance can reject a late commit; stop and replay rather than claim success.

## Configuration and limits

`TelemetryEngine:Kafka` is absent/disabled by default. Enable with `Enabled`, `BootstrapServers`,
`Topic`, `GroupId`, and `ProjectKey`. `InitialOffset` defaults to `error`; explicitly choose
`earliest` or `latest` to define startup behavior when no valid committed offset exists. Latest
intentionally skips historical records and must be an operator decision. Topics are never auto-created.
Group members must share the same project/schema/policy and source interpretation. Reserve the
group for this connector; external consumers that commit offsets can weaken its guarantee.

`SecurityProtocol` defaults to `ssl`; optional `sasl_ssl` accepts `plain`, `scram-sha-256` or
`scram-sha-512`, username/password, and optional `SslCaLocation`. Certificate and hostname verification
remain enabled. Plaintext is an explicit local/trusted-network deployment choice. No SASL plaintext,
OAuth, arbitrary native config, live reload or remote secret-fetching service. Inject credentials
through deployment secrets/environment; never commit real secrets or print config objects.

`MaxMessageBytes`: 65,536 default, 1,024–1,048,576. `QueueBufferKiB`: 1,024 default, 1–65,536, at least
one maximum message. `MaxPollIntervalMs`: 300,000 default, 60,000–900,000. Native queue/fetch/receive
limits plus one application record constrain buffering. Native queue thresholds are not a strict
process-memory quota: broker batches/compression and client metadata have overhead. Configure
broker/topic batch limits consistently; oversized input fails before WAL admission. Backpressure
can exceed the poll interval and force a rebalance; safety is preserved through failed commits and
replay, with duplicates possible. No extra unbounded connector queue is introduced.

Confluent.Kafka/librdkafka.redist 2.15.1 is pinned. Its native assets include linux-x64/linux-arm64
and osx-arm64. The engine's single-file extraction includes native assets; published clean-host
qualification must demonstrate discovery and loading, not merely a managed test build. Retain
upstream licenses and report system compatibility. No separate native-library install is permitted.

## Acceptance

Test config bounds, default-off behavior, credentials/project/event scope, source JSON/identity/time,
sanitization, source-generated serialization, write/flush failure, backpressure/shutdown, commit
failure, consumer ownership and uncertain replay. Exercise a real isolated Kafka broker and
ClickHouse, committed offsets, restart and poison-prefix behavior. Verify the published artifact
on supported Linux architectures. Run focused tests during editing, milestone suites, changed-code
coverage and Complete mutation checks before declaring the package qualified. Preserve existing
mutation responsibilities. Benchmarks/profiling and backend-default changes remain stopped.

Upstream references: [consumer delivery and rebalance semantics](https://docs.confluent.io/kafka-clients/dotnet/current/overview.html),
[pinned package source](https://github.com/confluentinc/confluent-kafka-dotnet/tree/v2.15.1),
[native configuration](https://github.com/confluentinc/librdkafka/blob/v2.15.1/CONFIGURATION.md).
