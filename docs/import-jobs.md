# Checkpointed NDJSON imports

D1 adds an optional caller-driven chunk protocol to the existing executable. It persists confirmed
progress and the latest receipt so callers can repeat a confirmed chunk after a lost response or
restart. It reuses NDJSON parsing, source mapping, policy, WAL and durable handoff. Existing track,
beacon and standalone NDJSON contracts stay compatible.

## Enable

```json
{
  "TelemetryEngine": {
    "EnableNdjsonImport": true,
    "EnableImportJobs": true,
    "ImportJobMaxJobs": 256
  }
}
```

Use an existing server ingestion key pinned to one permitted project, following the
[NDJSON guide](ndjson-import.md). X-Project-Key takes precedence over k. Public, legacy/unscoped
and unpinned keys cannot use either route. Another project's job returns 404 for reads and writes.

## Submit and inspect

Choose stable nonzero job/chunk UUIDs. Start at decoded byte offset zero. Retain the original bytes,
identities and receipts. Send UTF-8 application/x-ndjson:

```sh
curl -i "http://localhost:5000/api/v1/telemetry/import/jobs/$JOB_ID/chunks/$CHUNK_ID?offset=0" \
  -H "X-Project-Key: $NEALYTICS_SERVER_KEY" \
  -H "X-Import-Source: $SOURCE_SHA256" \
  -H "X-Import-Source-Bytes: $DECODED_FILE_BYTES" \
  -H 'Content-Type: application/x-ndjson' \
  --data-binary @chunk.ndjson

curl "http://localhost:5000/api/v1/telemetry/import/jobs/$JOB_ID" \
  -H "X-Project-Key: $NEALYTICS_SERVER_KEY"
```

X-Import-Source is a caller-asserted immutable source identity of exactly 64 ASCII hex digits.
Using a full-file SHA-256 is recommended; the server cannot verify that claim without the whole
file. It hashes each bounded decoded chunk before admitting records. X-Import-Source-Bytes is
the total decoded source length, a nonnegative signed 64-bit integer. Compression follows the
existing decompression setting. Lengths, offsets and hashes use decoded bytes.

Nonfinal chunks must end at LF, including CRLF. Final chunks may omit their last newline. Empty
bodies are accepted only for empty sources. Respect NDJSON's line and per-request record limits
when splitting files. A record rejection stops processing, persists the accepted prefix and makes
the job terminal `rejected`.

```json
{
  "jobId": "11111111-1111-4111-8111-111111111111",
  "sourceId": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
  "sourceBytes": 768,
  "state": "ready",
  "nextByteOffset": 384,
  "acceptedRecords": 2,
  "lastChunkId": "22222222-2222-4222-8222-222222222222",
  "receipt": {
    "acceptedRecords": 2,
    "nextByteOffset": 384,
    "nextLine": 3,
    "complete": true
  }
}
```

Byte lengths above are illustrative. Outer counters are cumulative; the receipt is chunk-relative.
`ready` permits a new chunk UUID at exactly nextByteOffset. `complete` confirms the declared source
length; query visibility can follow durable engine ownership. `lastChunkId` is the latest attempted
chunk. Processing/uncertain states return a null receipt.

Repeat the latest confirmed chunk with identical UUIDs, offset, source identity/length and decoded
bytes to receive the same HTTP status and stored response without another WAL append. Terminal
rejection receipts also support this retry. Older chunks are not a replay ledger. Changed bytes,
identities, offsets, terminal jobs and out-of-order chunks reject with 409. This is not general
event deduplication or an exactly-once ingestion guarantee.

## Recovery and failures

A synchronized processing marker precedes admission. An atomic, synchronized receipt follows WAL
persistence and handoff. A storage fault latches job operations unavailable until restart, returning
503 `job_storage_unavailable`; ordinary ingestion remains independent of that latch.

A crash, disconnect, cancellation or checkpoint failure can leave durable but unconfirmed records.
Such a marker recovers as `needs_reconciliation`, preserving only previously confirmed counters.
Retries return 409 rather than replaying an ambiguous chunk. Preserve the source and reconcile
operationally. D1 has no reconciliation-resolution API. Deleting manifests or changing job UUIDs
to bypass uncertainty can duplicate aggregates.

Confirmed `ready` progress survives restart. A fingerprint binds new chunks to startup schema,
mapping, relevant ingestion settings and import limits. Known receipts remain readable/repeatable
after contract changes; new chunks return `contract_changed`. Restore the original contract or
reconcile and plan another import instead of silently reinterpreting the source.

## Bounds and operations

| Resource | Bound |
| --- | --- |
| Retained history | ImportJobMaxJobs, default 256, allowed 1–4096, including terminal jobs |
| Concurrent chunks | One across jobs, no waiting queue |
| Decoded chunk | min(MaxRequestBodyBytes, 1 MiB) |
| Manifest | 8 KiB per job plus one temporary replacement |

Existing global write and NDJSON admission also apply. Busy jobs return 429; input/media/size
failures return 400/415/413 before job record admission. Confirmed record errors retain NDJSON's
400/413 status. Authentication retains 401/403. Buffers are bounded and cleared on return.

Metadata lives under WriteAheadLogDirectory/import-jobs with checksums, an exclusive process lease
and synchronized replacements. It holds source identities/counters and contract fingerprints,
never credentials or payload bodies. Back it up with durable deployment state. Corrupt, oversized,
incompatible or excessive history fails startup. Terminal history is not evicted; capacity requires
an explicit future retention policy, not deleting receipts to reuse identifiers.

Disable EnableImportJobs to remove routes/services and retain manifests. Older binaries ignore
the folder and recover ordinary WAL records but cannot serve this protocol. No WAL format, DDL,
backend default or mandatory service changes. Scheduling, staging, source fetching, S3/GCS,
retention/deletion and reconciliation tools remain separate packages. Benchmarks remain disabled.
