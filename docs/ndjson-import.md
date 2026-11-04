# Bounded NDJSON import

For optional persisted chunk progress and confirmed retries, see [checkpointed imports](import-jobs.md).
The standalone E2 route described below retains its request-relative receipt contract.

E2 adds an opt-in raw-body file import to the same executable and ingestion pipeline used by
track/beacon. Existing callers keep their current contracts. There is no URL fetcher, helper
service, upload-storage service or background import job.

## Enable and send a chunk

```json
{
  "TelemetryEngine": {
    "EnableNdjsonImport": true,
    "ImportMaxLineBytes": 65536,
    "ImportMaxRecords": 1000,
    "ImportMaxConcurrency": 2,
    "IngestionKeys": [
      {
        "Key": "replace-with-deployment-secret",
        "Scope": "server",
        "ProjectId": "example-project"
      }
    ]
  }
}
```

Keep the actual key in deployment secrets. Use an existing configured server key pinned to one
permitted project. Public, legacy/unscoped and unpinned keys cannot import. The X-Project-Key
header takes precedence over the existing k query parameter. The pinned project's logical
schema and event-type restrictions apply to every record.

```sh
curl -i http://localhost:5000/api/v1/telemetry/import/ndjson \
  -H "X-Project-Key: $NEALYTICS_SERVER_KEY" \
  -H 'Content-Type: application/x-ndjson' \
  --data-binary @events.ndjson
```

An optional project-pinned [source mapping](source-mapping.md) can translate each source object
before normal validation. Receipts always count the original decoded source bytes, and an
incompatible mapped object returns `invalid_mapping`. Without a mapping the canonical contract
below is unchanged.

Send raw UTF-8 NDJSON, not multipart/form-data. Content-Type must be application/x-ndjson;
an optional charset must be UTF-8. Each line contains one telemetry object with an explicit
nonzero eventId, explicit UTC-Z timestamp in the 1900–2299 calendar, matching projectId, tenantId
and permitted eventType. Existing server-key semantics apply, including an optional sessionId.
Caller `_walRecordId` is ignored. Unknown dimensions/measures follow existing sanitization and
logical measure normalization. The server never invents an import event identity or timestamp.

```json
{"eventId":"11111111-1111-4111-8111-111111111111","timestamp":"2026-11-04T00:00:00Z","projectId":"example-project","tenantId":"example-tenant","eventType":"action"}
{"eventId":"22222222-2222-4222-8222-222222222222","timestamp":"2026-11-04T00:00:01Z","projectId":"example-project","tenantId":"example-tenant","eventType":"action"}
```

LF, CRLF and a final line without a newline are supported. Empty lines, arrays, malformed JSON,
UTF-8 BOM prefixes, implicit identities/times and out-of-calendar timestamps reject. Empty files
return an empty successful receipt. Gzip/Brotli/deflate follow the engine's existing request
decompression setting and decoded-body budget; offsets always refer to decoded input bytes.

## Understand the receipt

The engine processes one bounded record at a time and stops at the first rejection. A receipt
counts only records whose WAL persistence and live handoff completed. This acknowledges durable
engine ownership; ClickHouse query visibility can follow later.

```json
{
  "acceptedRecords": 2,
  "nextByteOffset": 384,
  "error": "invalid_json",
  "nextLine": 3,
  "complete": false
}
```

The illustrative 384 is the byte length of the accepted prefix in that request. nextLine is the
one-based next record line; nextByteOffset includes line endings of accepted records. Complete
receipts omit error and use HTTP 202. Invalid input/record caps use 400, line/body limits use 413,
unsupported media uses 415, and admission saturation uses 429. Authentication/authorization
retain 401/403. Storage failures do not manufacture a successful receipt.

Offsets are request-relative, not server-side resume tokens. Retain the confirmed receipt and
original decoded source bytes; add its offset to your own chunk's base position when preparing
another request. Every new request starts its own receipt at zero. Send bounded chunks and stop
on validation errors instead of automatically retrying the same file.

A missing receipt, interrupted connection, storage failure or uncertain response can leave an
accepted prefix you cannot infer from the client error. Reconcile before retrying. Supplied
stable event IDs support identifying source records but do not create HTTP deduplication or
exactly-once aggregates. There is no durable import checkpoint, idempotency key, automatic retry,
job polling, file content hash or persisted source snapshot in E2.

## Limits and ownership

| Setting | Default | Allowed |
| --- | --- | --- |
| ImportMaxLineBytes | 65,536 | 1–1,048,576 and no larger than MaxRequestBodyBytes |
| ImportMaxRecords | 1,000 | 1–10,000 |
| ImportMaxConcurrency | 2 | 1–64 |

The line limit counts UTF-8 bytes including a trailing CR and excluding LF. It is independent
of the total decoded-body limit. The import limiter has no waiting queue and composes with
existing global write/request/byte admission. Choose its cap below global request capacity when
reserving room for ordinary track/beacon traffic. No unbounded connector queue is introduced.

Complete buffered lines parse directly. An incomplete line transfers into one lazily rented
buffer bounded by the configured line limit (up to pool bucket rounding, at most 1 MiB per
admitted import). Consuming those pipe fragments prevents producer/consumer backpressure from
deadlocking long lines. The buffer is returned with clearing on completion, rejection, early exit,
cancellation and reader failure. It is retained only for the current request after first use.

Caller cancellation before admission cannot persist the next record. Once a validated record
enters persistence, the engine owns its handoff even if the caller disconnects; application
shutdown can still interrupt work, leaving durable records for normal WAL recovery. A receipt
cannot promise client delivery after disconnect. Import limits are compiled at startup and
changes require restart.

Set EnableNdjsonImport to false to remove the route and feature services. Accepted WAL records
retain ordinary replay/schema compatibility requirements; disabling the route does not discard
acknowledged data. No physical schema or WAL format migration is introduced by E2.
