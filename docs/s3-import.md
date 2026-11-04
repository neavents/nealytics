# Checkpointed S3 imports

E4 reads one S3 object into the existing NDJSON/checkpoint pipeline. Sources are optional startup
declarations inside the same executable. Existing track, beacon, Kafka and file-upload callers
retain their contracts. This adapter does not provide cloud hosting or require an AWS SDK/helper.

## Configure a source

Enable both NDJSON and import jobs, and declare a named source pinned to a project:

```json
{
  "TelemetryEngine": {
    "EnableNdjsonImport": true,
    "EnableImportJobs": true,
    "S3Sources": [
      {
        "Name": "source",
        "ProjectId": "example-project",
        "Endpoint": "https://s3.us-east-1.amazonaws.com",
        "Bucket": "example-input",
        "Region": "us-east-1",
        "Prefix": "events/",
        "PathStyle": true,
        "MaxObjectBytes": 1099511627776,
        "RequestTimeoutSeconds": 30
      }
    ]
  }
}
```

Supply AccessKey, SecretKey and an optional SessionToken through deployment secrets rather than
checking them into source control. For example, the matching environment settings are
TelemetryEngine__S3Sources__0__AccessKey and TelemetryEngine__S3Sources__0__SecretKey. Explicit
session tokens are supported; automatic credential discovery/refresh is not included. Rotation
requires restart. Credentials are excluded from snapshot identities, responses and job manifests.

Use an existing server ingestion key pinned to the same project. Public, unscoped and unpinned
keys cannot inspect or import. X-Project-Key takes precedence over k. Source names and project
matches are ordinal; inaccessible/unknown sources return 404. The existing project schema,
event-type restrictions, sanitizers and optional [source mapping](source-mapping.md) still apply.

The endpoint must be an absolute root URL without user information, query or fragment. Configure
the correct region/endpoint explicitly; redirects are refused. PathStyle false selects a bucket
subdomain and requires a DNS endpoint. Ordinary general-purpose S3 buckets and compatible endpoints
are supported; directory buckets, access points, Object Lambda, SSE-C and archive restoration are
outside this adapter. HTTPS is required unless AllowInsecureHttp is explicitly true for a local
compatible store. HTTPS certificate validation remains the platform default.

## Inspect and retain the snapshot

```sh
curl --fail-with-body -sS http://localhost:5000/api/v1/telemetry/import/s3/source/inspect \
  -H "X-Project-Key: $NEALYTICS_SERVER_KEY" \
  -H 'Content-Type: application/json' \
  --data-binary '{"key":"events/file.ndjson"}' > snapshot.json
```

An optional versionId requests an existing version. Otherwise inspection resolves the current
version when the store supplies one. Preserve the returned descriptor unchanged:

```json
{
  "key": "events/file.ndjson",
  "versionId": "store-provided-version",
  "etag": "\"store-provided-tag\"",
  "bytes": 768,
  "sourceId": "server-computed-64-character-hex-identity"
}
```

Values above are illustrative. sourceId is a hash of the configured resource and snapshot,
not a full-file content hash or authorization token. ETags are opaque strong representation
validators; do not interpret them as MD5. Retain versionId exactly, including a literal "null"
version if supplied. Versioned objects can survive a later overwrite; unversioned objects must
retain the matching ETag/length. Each fetch checks conditional metadata before admitting records.
These semantics follow [S3 GetObject](https://docs.aws.amazon.com/AmazonS3/latest/API/API_GetObject.html)
and [HeadObject](https://docs.aws.amazon.com/AmazonS3/latest/API/API_HeadObject.html).

The caller supplies only keys and descriptors, never URLs, buckets or credentials. Keys must
match the configured literal prefix and fit 1,024 UTF-8 bytes. Slashes, dot segments, percent
signs, spaces and Unicode are encoded without changing the S3 object name. Prefixes are S3 key
prefixes, not filesystem directories. Limit the backing S3 credentials to the permitted bucket/
prefix as part of deployment configuration. Source version reads need the corresponding store
permissions; archived/encrypted objects must already be readable by those credentials.

## Import one chunk at a time

Choose stable nonzero job/chunk UUIDs. Start at byte offset zero:

```sh
curl --fail-with-body -sS \
  "http://localhost:5000/api/v1/telemetry/import/s3/source/jobs/$JOB_ID/chunks/$CHUNK_ID?offset=0" \
  -H "X-Project-Key: $NEALYTICS_SERVER_KEY" \
  -H 'Content-Type: application/json' \
  --data-binary @snapshot.json
```

The response is the [D1 job/receipt contract](import-jobs.md). For ready jobs, submit the next
chunk with a new chunk UUID at exactly nextByteOffset, using the same snapshot. A complete job
has confirmed all source bytes. Outer counters are cumulative; the nested receipt is relative
to that chunk. Poll persisted status through GET /api/v1/telemetry/import/jobs/{jobId} using the
same project key.

Repeat the latest confirmed chunk with the original UUID, offset and snapshot to retrieve its
receipt without another WAL append. The adapter refetches the original stored chunk length,
including rejected input, so a later record-framing limit cannot reinterpret a confirmed retry.
Current byte budgets still apply; a stored chunk larger than a reduced cap returns contract_changed.
New chunks retain D1's ingestion-contract fence across schema/mapping/limit changes.

The source must remain fetchable for a verified adapter retry. If a version expires or an
unversioned representation changes, use the persisted D1 status to inspect confirmed progress;
the adapter cannot reconstruct deleted bytes. A changed snapshot conflicts with an existing job.
Do not switch to a new job UUID to bypass uncertainty: that can duplicate aggregates.

## Bounds, failures and ownership

| Resource | Bound/default |
| --- | --- |
| Source declarations | 0–32; an empty list disables routes/services |
| Concurrent S3 operations | One, no waiting queue |
| Downloaded range | min(MaxRequestBodyBytes, 1 MiB) |
| Control JSON | min(MaxRequestBodyBytes, 16 KiB), decoded UTF-8 |
| Per-source object size | MaxObjectBytes, default 1 TiB, positive signed 64-bit bound |
| Remote operation lifetime | RequestTimeoutSeconds, default 30, allowed 1–120 |
| Remote response headers | 16 KiB |

Source bodies are uncompressed UTF-8 NDJSON. There is no automatic decompression, format detection
or CSV/Parquet conversion. The adapter frames at LF and the NDJSON record cap. Final unterminated
lines are supported; nonfinal records must fit inside a chunk including LF. A partial tail is
refetched from the confirmed offset, not discarded. Empty objects are conditionally inspected and
can produce an empty complete job.

The source buffer and D1's bounded copy are cleared on return. Response/request ownership ends
on success, rejection, cancellation and failure. Existing global write/import admission also
applies. Normal track/beacon execution stays independent of the S3 operation gate.

Authentication uses 401/403; source lookup uses 404. Invalid control input uses 400/415/413.
Interrupted control-body reads use 400 control_body_unavailable before any source admission.
Busy admission returns 429. Missing objects return 404; changed validators/versions use 409.
Unsupported content encoding uses 415; oversized objects/records use 413. Remote protocol/access/
transport failures use fixed 502 diagnostics and deadlines use 504. Journal faults use D1's 503
latch. API errors contain no remote response body, credentials or signed request details.

A failed fetch creates no checkpoint. Once a chunk enters D1, its synchronized intent, WAL
handoff and receipt publication own durability. Missing/uncertain admissions become
needs_reconciliation and block replay. S3, WAL, manifests and ClickHouse do not share a transaction;
this is not event-level exactly-once deduplication. There is no automatic ambiguous retry.

Removing S3Sources removes the adapter while retaining D1 manifests and ordinary WAL recovery.
There is no DDL/WAL format change or backend-default switch. Bucket scans, notifications,
scheduling, other formats, GCS/URL sources, exports/sinks, retention and reconciliation resolution
remain separate packages. Benchmarks remain disabled.
