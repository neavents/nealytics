# Checkpointed named HTTP imports

Implemented and locally qualified on feat/integrations-e6-http-import. Actual ARM64 checks pass;
remote CI and actual x64 checks are deferred by the owner on 2026-11-06. See the
[qualification ledger](packages/e6-http-import-verification.md) for exact evidence and limits.

## Configure the source

HTTP sources are optional and empty by default. Enable NDJSON imports and jobs, then configure
one named source per export URL. The URL and headers belong to the operator configuration:

```json
{
  "TelemetryEngine": {
    "EnableNdjsonImport": true,
    "EnableImportJobs": true,
    "HttpSources": [
      {
        "Name": "export",
        "ProjectId": "example-project",
        "Url": "https://data.example/export.ndjson",
        "Headers": { "Accept": "application/x-ndjson" },
        "MaxObjectBytes": 1099511627776,
        "RequestTimeoutSeconds": 30
      }
    ]
  }
}
```

Every source also requires IdentityKey: exactly 32 secret bytes encoded as 44-character Base64.
Generate a random key, keep it with deployment secrets and supply
TelemetryEngine__HttpSources__0__IdentityKey. Supply private selection headers through secrets,
for example TelemetryEngine__HttpSources__0__Headers__Authorization. There is no default secret,
ambient credential discovery, refresh or signed-URL renewal. Activating configuration changes
requires restart. The engine adds no new SDK, helper service or persistent identity-key file.

Snapshot identity uses a keyed digest of the named project/resource configuration, canonical URL
including query, normalized/sorted request headers, ETag and length. It exposes no raw URL/header
secrets or public checksum suitable for guessing those secrets. Keep configuration/key stable to
retain descriptors across restart. URL/query/header/key changes fence old descriptors. This also
means credential or signed-URL rotation invalidates the old adapter snapshot; it is not a promise
of transparent credential refresh. Persisted D1 progress remains available. Do not bypass a fence
with a new job ID and assume the accepted prefix will be deduplicated.

A source name is an ordinal ASCII letter/digit/dash/underscore token up to 64 characters and is
unique within HTTP sources. A server ingestion key must be pinned to the same project. Public,
unpinned or inaccessible keys cannot select the source. X-Project-Key takes precedence over k.
Unknown, differently cased and foreign-project source names return 404. SourceId is an integrity
identifier, not an authorization token; key/project policy still applies independently.

## Inspect and import

Inspection is a POST with no body. Callers cannot supply a URL or replace selection headers:

```sh
curl --fail-with-body -sS http://localhost:5000/api/v1/telemetry/import/http/export/inspect \
  -H "X-Project-Key: $NEALYTICS_SERVER_KEY" -X POST > snapshot.json
```

Retain the returned descriptor exactly:

```json
{ "etag": "\"opaque-strong-validator\"", "bytes": 768, "sourceId": "64-character-uppercase-hex-value" }
```

Values are illustrative. Inspection requires a canonical strong ETag and known nonnegative length
within MaxObjectBytes. Unlike the S3 descriptor, HTTP descriptors have no caller-supplied key or
version. Import with stable nonzero job/chunk UUIDs and the retained snapshot:

```sh
curl --fail-with-body -sS \
  "http://localhost:5000/api/v1/telemetry/import/http/export/jobs/$JOB_ID/chunks/$CHUNK_ID?offset=0" \
  -H "X-Project-Key: $NEALYTICS_SERVER_KEY" \
  -H 'Content-Type: application/json; charset=utf-8' --data-binary @snapshot.json
```

The response uses the [D1 job/receipt contract](import-jobs.md). Continue ready jobs with a new chunk
UUID at exactly nextByteOffset. Matching latest-chunk retries refetch the stored original byte
length, including rejected input, and D1 verifies the raw chunk hash before returning a confirmed
receipt. Reduced current byte budgets still apply; new chunks retain D1 schema/mapping/limit
fences. Use the existing project-scoped GET /api/v1/telemetry/import/jobs/{jobId} for status.

A source must remain readable under its old validator. This adapter does not archive representations.
Changed/unavailable data cannot be reconstructed from a receipt. A current strong ETag is an opaque
representation validator, not a content MD5/full-file checksum. Providers must support the required
conditional HEAD and single-range GET behavior. Ignoring a range or returning inconsistent metadata
fails before ingestion; no full-body or weak-validator resume fallback is used.

## Transport, budgets and failures

Only absolute HTTP/HTTPS URLs are accepted, with no user information or fragment. HTTPS is required
unless AllowInsecureHttp is explicitly true for a local compatible source. Platform certificate
validation stays enabled. Operators may deliberately name private endpoints; requests cannot select
other URLs. Configured static headers may include authentication, but cannot override Host, Range,
If-*, Accept-Encoding, Content-*, Proxy-* or hop/framing headers. Header values are printable ASCII
including spaces, with no control characters. The engine requests identity encoding, disables
redirects, ambient proxies, cookie storage and automatic decompression, and requires uncompressed
UTF-8 NDJSON. CSV, Parquet and provider-specific object-version behavior are separate adapters.

| Resource | Bound |
| --- | --- |
| Named HTTP declarations | 0–32 |
| Canonical URL | 16,384 characters |
| Headers | At most 32; names 1–128 ASCII token characters, values 0–4,096 printable ASCII characters |
| Aggregate header names/values | 16,384 bytes |
| Identity key | Exactly 32 bytes, Base64 encoded |
| Object size | Positive MaxObjectBytes, default 1 TiB |
| Operation deadline | RequestTimeoutSeconds 1–120, default 30; includes remote body |
| Response headers | 16 KiB |
| Download range | min(MaxRequestBodyBytes, 1 MiB) |
| Snapshot control JSON | min(MaxRequestBodyBytes, 16 KiB) |
| Active HTTP source operations | One, no waiting queue, plus global/import/D1 admission |

Each request checks exact Content-Length/Content-Range and validator metadata before reading a
bounded range. Complete LF records are framed within the NDJSON record cap; a final unterminated
line is supported and a nonfinal record must fit the chunk including LF. Partial tails are refetched
from the confirmed offset. Empty objects still use a conditional HEAD. Cleared pooled source bytes
remain owned through validation, D1's bounded copy and disposal; ordinary ingestion is independent
of the source-operation gate. Keys and temporary serialized configuration buffers have explicit
cleanup ownership. No per-event JSON rewriting or extra remote request on track/beacon is added.

Remote errors use fixed safe diagnostics without provider bodies, URLs, query strings, headers or
credentials. Missing objects use 404, changed representations 409, source/body budgets 413,
unsupported encoding/media 415, busy admission 429, transport/protocol/access errors 502 and source
deadlines 504. Invalid control metadata uses 400; journal failures use D1's 503 latch. Inspection
rejects a nonempty body with unexpected_control_body. Interrupted body reads use
control_body_unavailable. Standard authentication uses 401/403.

Failed fetches create no checkpoint. Once admitted, synchronized intent/receipt publication and
WAL ownership retain D1's ambiguity contract. Source, WAL, manifests and ClickHouse are not one
transaction. Unconfirmed admissions require reconciliation; neither eventId nor a new job ID
provides exactly-once aggregation. Remove HttpSources to disable the adapter while retaining D1
history and ordinary WAL recovery. There is no DDL/WAL/checkpoint-format or backend-default change.
Benchmarks remain stopped.
