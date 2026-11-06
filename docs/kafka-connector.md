# Kafka ingestion

E1 adds an optional Kafka source inside the existing engine executable. It uses the same project
schemas, validation, sanitization, WAL and bounded delivery pipeline as track/beacon. Existing
callers keep sending their current requests. This adapter requires your Kafka broker; it adds no
hosted service, Schema Registry or helper daemon. It is disabled by default.

## Configure one source

Use a dedicated consumer group and an existing server ingestion key pinned to one project.
Every member of that group must use the same project schema and ingestion policy. The topic must
already exist. Do not share the group with consumers that advance its offsets independently.

```json
{
  "TelemetryEngine": {
    "IngestionKeys": [
      {
        "Key": "replace-with-deployment-secret",
        "Scope": "server",
        "ProjectId": "example-project",
        "EventTypes": "action"
      }
    ],
    "Kafka": {
      "Enabled": true,
      "BootstrapServers": "broker.example:9093",
      "Topic": "events",
      "GroupId": "nealytics-example-project",
      "ProjectKey": "replace-with-deployment-secret",
      "InitialOffset": "earliest",
      "SecurityProtocol": "ssl",
      "MaxMessageBytes": 65536,
      "QueueBufferKiB": 1024,
      "MaxPollIntervalMs": 300000
    }
  }
}
```

Keep actual credentials in deployment secrets, for example environment overrides for
`TelemetryEngine__IngestionKeys__0__Key` and `TelemetryEngine__Kafka__ProjectKey`.
Keys, project pinning and project schemas retain their existing meaning. If project catalogs
are enabled, the key must resolve to an allowed catalog context. Each record must supply its
matching projectId and its own tenantId; the adapter never fills or infers either field.

`InitialOffset` defaults to `error`: missing/invalid committed offsets stop the connector.
Choose `earliest` explicitly to start at the earliest retained record, or `latest` to intentionally
skip existing history. Kafka retention can remove unprocessed input; configure it for your
recovery window. Do not use reset-to-earliest/latest as an unnoticed data-loss recovery policy.

TLS is the default and retains certificate/hostname verification. Set `SslCaLocation` when a
private CA is required. For `sasl_ssl`, also set `SaslUsername`, `SaslPassword` and `SaslMechanism`
(`plain`, `scram-sha-256` or `scram-sha-512`). Plaintext requires the explicit value `plaintext`
and is intended for local or explicitly trusted deployments. SASL without TLS, OAuth and
arbitrary native-client overrides are not exposed by E1. Configuration changes require restart.

## Message contract

The value is one UTF-8 telemetry JSON object. Kafka keys and headers do not affect routing.
An optional startup `Kafka:SourceMapping` accepts an external JSON envelope through
[declarative source mapping](source-mapping.md); the policy, size, delivery and offset rules below
still apply after translation. Without a mapping the canonical contract below is unchanged.
An explicit nonzero eventId and UTC timestamp ending in Z are required; the storage calendar is
1900–2299. Event types, identities, sizes and logical schema rules follow existing server
injection policy. Unknown dimension/measure names follow existing sanitization.

```json
{
  "eventId": "11111111-1111-4111-8111-111111111111",
  "timestamp": "2026-11-04T00:00:00Z",
  "projectId": "example-project",
  "tenantId": "example-tenant",
  "eventType": "action"
}
```

Arrays, NDJSON batches, tombstones, malformed/oversized messages, implicit identities and implicit
timestamps are rejected. A supplied `_walRecordId` cannot control internal delivery identity.
Existing server ingestion semantics permit an omitted sessionId; public-key ingestion is not
supported by this connector. Historical input must still satisfy the configured storage/retention
contract; a successful Kafka commit does not extend ClickHouse retention.

## Ownership, failures and replay

Both automatic offset storage and automatic commits are disabled. The connector processes one
record at a time, persists it in the WAL, hands it to the live delivery pipeline and commits only
that partition's next offset. A committed offset establishes durable engine ownership, not
immediate query visibility. The existing WAL/delivery journal handles subsequent database retries.

Delivery is at least once. A crash, rebalance or uncertain commit after persistence can re-read
the same source record into a new WAL append. Reusing eventId does not provide exactly-once
aggregation. Startup rejects the combination of enabled Kafka, `EnableAnalyticsRollupPlanning`
and `AnalyticsRollupSourceIsAppendOnly`; E1 cannot establish the uniqueness assertion required
for that optimization. Raw analytics remain available. A deduplication extension needs separate
qualification before this guard can be relaxed.

Invalid input and consume/persistence/dispatch/commit failures stop the connector and host.
The connector does not skip, automatically retry past a poison record, or send it to a dead-letter
sink. The exception identifies a fixed stage, topic, partition and offset without raw payload or
upstream exception details. For validation/persistence/commit failures this is the current record.
A consume failure reports the last observed position, which may already be committed; -1 means
no position has been observed. Check the group's committed offsets before any manual repair.
Investigate the input/credentials/storage and restart after repair. Any manual offset reset must be an explicit operator decision.
Health checks/orchestration should detect a stopped process; repeated automatic process restarts
can repeatedly encounter the same poison record.

Shutdown closes/disposes the consumer. A record whose durable handoff did not complete cannot
be committed; a record with completed handoff can have an in-flight commit finish during shutdown.
Uncommitted records remain eligible for Kafka replay, including a possibly durable prefix.
A replay can duplicate events already held or delivered by the WAL.

## Resource and packaging boundaries

| Setting | Default | Allowed |
| --- | --- | --- |
| MaxMessageBytes | 65,536 | 1,024–1,048,576 |
| QueueBufferKiB | 1,024 | 1–65,536, at least one maximum message |
| MaxPollIntervalMs | 300,000 | 60,000–900,000 |

The application holds one current record; native fetch/receive/queue settings constrain
prefetching. Native thresholds are not a strict process-memory quota: broker batches, compression,
metadata and allocator overhead also consume memory. Configure broker/topic batch limits
consistently. Existing WAL/channel/backlog limits continue to apply. Prolonged backpressure can
exceed the poll interval, lose assignment and force safe replay with possible duplicates.

Confluent.Kafka and librdkafka.redist are pinned to 2.15.1. Native libraries and notices are bundled
into the self-contained executable; native assets are extracted into the engine's normal bundle
location. No customer-installed librdkafka is required. The published checks run on actual Linux
x64 and ARM64 with io_uring/custom ingestion. The feature leaves existing backend defaults intact.

The original E1 fixtures use plaintext Kafka 4.1.1 and ClickHouse 25.3.14.14. E8 adds real
TLS/SASL and actual ARM64 security qualification below; neither set certifies every broker vendor,
security deployment, multi-broker topology or network failure. See the
[E1 evidence ledger](packages/e1-kafka-verification.md) for its historical scope/source revisions.

## Encrypted broker qualification

E8 locally qualifies the existing ssl and sasl_ssl settings against real brokers and the
published actual Linux ARM64 executable. Its scope covers private-CA trust, hostname verification, PLAIN, SCRAM-SHA-256
and SCRAM-SHA-512, together with mapping, committed-offset restart, poison-prefix stops and
credential/payload redaction. All 25 new integration cases and thirteen published ARM64 scenarios
pass. See the [E8 evidence](packages/e8-kafka-security-verification.md). Remote CI/actual x64 remain
owner-deferred; the E1 plaintext evidence above remains a historical source-specific record.

The test-only secured broker fixture creates fresh signing keys, one-day leaf certificates and
fixture credentials on each run. Its plaintext administration listener belongs only to the
isolated setup/offset-inspection path. Engine reads use encrypted listeners with verification
enabled. Fixture credentials and certificate generation are not engine deployment requirements.
Source health, operator-controlled poison recovery, vendor-specific authentication, ACL management
and multi-broker failover remain separate qualification/workflow boundaries.
