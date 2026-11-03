# E3 declarative source mapping

Selected on the owner's continuation after the integration-gap review, 2026-11-04.
Branch: `feat/integrations-e3-source-mapping`, based on qualified category commit `0f092b6`.
Status: implementation in progress. Main and the original checkout's unfinished rebase stay untouched.

## Outcome and compatibility

Optional startup declarations map nested JSON object fields into the existing telemetry envelope.
Kafka has one mapping per configured source; NDJSON mappings are selected by the authenticated
key's pinned project, never by an untrusted input selector. Unconfigured adapters preserve their
existing parsing behavior. Track/beacon do not use mappings.

Mappings explicitly name eventId, timestamp, projectId, tenantId and eventType. Identity and time
must come from the source, with a nonzero GUID and ISO UTC-Z timestamp in 1900–2299. Other string
fields may have bounded string defaults for missing/null values. Dimensions/measures additionally
accept JSON numbers and booleans converted to their JSON scalar text. Normal project authorization,
schema validation, sanitization, measure normalization and durable handoff follow mapping.

## Bounds and exclusions

Compile and snapshot declarations at startup: at most 64 field bindings per mapping, 16 object
path segments per binding, 128 UTF-16 characters per segment, and 4,096 per default. NDJSON accepts
at most 128 explicitly keyed project mappings. Sum UTF-8 bytes of materialized mapped text against
the existing source line/message byte cap. No JSON rewriting, additional serialization buffer,
arbitrary scripts, array navigation, wildcard paths, coercion of object/array values, custom
timestamp formats, metadata-object conversion, seq mapping, jobs, retry, deduplication or routing
inference. Unknown source fields are ignored; only declared targets are materialized.

NDJSON keeps original decoded source byte receipts and stops at the first mapping error. Kafka
retains its existing failure/offset behavior. Mapping does not establish exactly-once aggregates.
Changing a mapping requires restart and explicit consideration of uncommitted Kafka replay.
WAL replay uses the already mapped envelope, never reapplies a new source mapping.

## Acceptance and rollout

Exercise startup bounds and immutable snapshots, nested/case-sensitive paths, defaults, scalar
conversion, escaped property names, malformed data, required identity/time, output budget,
authorization, per-project isolation, original byte receipts and persistence/query visibility
on managed/custom paths. Cover changed lines/branches and Complete Stryker mutations; consolidate
unit, real-dependency, published and native-platform qualification at the milestone. No benchmarks.

Disable/remove a mapping to restore the adapter's canonical input contract; already persisted
envelopes retain their existing WAL/schema compatibility. No persistent format or DDL changes.
