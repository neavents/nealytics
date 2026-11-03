# Declarative JSON source mapping

Optional source mappings adapt existing JSON envelopes to Nealytics without changing track/beacon.
Kafka uses `TelemetryEngine:Kafka:SourceMapping`; NDJSON uses
`TelemetryEngine:NdjsonSourceMappings:<pinned-project>`. The key's authenticated project selects the
NDJSON mapping. A source document cannot select another mapping or change the key's permissions.

```json
{
  "Fields": {
    "eventId": { "Path": ["source", "id"] },
    "timestamp": { "Path": ["source", "time"] },
    "projectId": { "Default": "example-project" },
    "tenantId": { "Path": ["source", "tenant"] },
    "eventType": { "Default": "action" },
    "dimensions.tag": { "Path": ["attributes", "label"], "Default": "unknown" },
    "measures.amount": { "Path": ["attributes", "amount"] }
  }
}
```

Put that object under either configuration location above. It accepts a source record such as:

```json
{
  "source": {
    "id": "11111111-1111-4111-8111-111111111111",
    "time": "2026-11-04T00:00:00Z",
    "tenant": "example-tenant"
  },
  "attributes": { "label": "example", "amount": 7 }
}
```

Declare `tag` and `amount` in the project's normal dimensions/measures schema. Mapping does not
create physical columns, grant event permissions, or bypass logical measure bounds. A mapped
project must still match the pinned server key. Existing sanitizer rules discard undeclared
dimension/measure targets. A boolean converted to text can still fail a numeric measure's rules.

## Targets and values

All five targets `eventId`, `timestamp`, `projectId`, `tenantId` and `eventType` must be declared.
Identity and time require source paths and forbid defaults. IDs must be nonzero GUIDs. Timestamps
use `yyyy-MM-ddTHH:mm:ss[.fffffff]Z`, with up to seven fractional digits and years 1900–2299.
Offsets, local times, implicit IDs/times and custom date formats are unsupported.

Optional envelope targets are `sessionId`, `userId`, `objectId`, `trafficClass`, `pagePath`,
`referrer`, `deviceClass`, `os`, `browser`, `country` and `metadataJson`. They accept strings;
metadataJson is already encoded JSON text, not an arbitrary object. Dimensions and measures
use `dimensions.<name>` and `measures.<name>` and additionally accept numbers and booleans as
their original JSON scalar text. Containers are rejected rather than stringified. `seq`,
`_walRecordId` and unknown targets are unsupported.

Paths are arrays of literal, case-sensitive object keys. Dots, slashes and brackets inside a key
have no selector meaning. Array indexing, wildcards and expressions are unsupported. A missing
key or null value uses Default when present, otherwise leaves that field absent. An empty string
is a real value. Crossing a non-object intermediate value rejects; a default cannot hide that
shape error. Omit Path for a constant string default. Only declared targets are materialized;
canonical fields in the source are not automatically copied through.

## Limits and delivery

Each mapping has 5–64 fields, paths of at most 16 keys, keys of 1–128 UTF-16 characters, target
names up to 128 characters, and defaults up to 4,096 characters. NDJSON accepts at most 128
project mappings with project identifiers up to 256 characters. Sum of mapped value UTF-8 bytes,
including defaults and repeated source references, must fit the source's existing message/line
byte cap. Input limits still count the original source bytes. Processing remains one bounded
record at a time with no extra serialized-envelope buffer or new dependency.

Invalid configuration fails startup for an enabled adapter. Declarations are copied at startup;
configuration changes require restart. Unconfigured sources keep canonical parsing. NDJSON
mapping failures return `invalid_mapping` with the prior durable prefix and original decoded
source byte offsets. Kafka retains its existing host-stop and offset policy. Neither path adds
automatic retries, durable import jobs, deduplication or exactly-once aggregation.

Already acknowledged WAL records contain the mapped envelope; recovery does not remap them.
Changing a Kafka mapping while uncommitted source records remain can produce different output
on replay. Review that change explicitly. Removing the mapping restores canonical source input;
it does not alter already acknowledged data or migrate storage.
