# Generation-pinned GCS NDJSON imports

Locally qualified on feat/integrations-e7-gcs-import; see the
[adapter evidence](packages/e7-gcs-import-adapter-verification.md).
This guide specifies the implemented contract, not a live Google-service certification.
Remote CI/x64 remain deferred by the owner's 2026-11-06 instruction. Benchmarks remain stopped.

## Configuration

Enable NDJSON imports and jobs, then declare a named source. Empty GcsSources is the default:

```json
{
  "TelemetryEngine": {
    "EnableNdjsonImport": true,
    "EnableImportJobs": true,
    "GcsSources": [
      {
        "Name": "archive",
        "ProjectId": "example-project",
        "Bucket": "example-bucket",
        "Prefix": "exports/",
        "MaxObjectBytes": 1099511627776,
        "RequestTimeoutSeconds": 30
      }
    ]
  }
}
```

Supply GcsSources__0__AccessKey and GcsSources__0__SecretKey through deployment secrets, using
Google XML HMAC interoperability credentials. Supply GcsSources__0__IdentityKey as an independent
random 32-byte secret encoded in Base64; it is required and has no default. Prefix these settings
with TelemetryEngine__ when using environment variables. The endpoint defaults to
https://storage.googleapis.com and must be an operator-selected HTTP(S) origin without credentials,
path, query or fragment. HTTPS/certificate validation is the default; AllowInsecureHttp is an
explicit local-compatible-server choice. No request-supplied endpoint or bucket is accepted.

The concrete signer uses the documented AWS4-HMAC-SHA256 interoperability form and region auto.
This bounded adapter does not discover ADC/workload credentials, exchange OAuth tokens, refresh
keys, create buckets, list objects or run schedules. Grant object read permissions to the chosen
service account outside the engine. Requester Pays configuration and customer-supplied encryption
keys are separate capabilities. See Google's
[HMAC guide](https://docs.cloud.google.com/storage/docs/authentication/hmackeys) and
[interoperability guide](https://docs.cloud.google.com/storage/docs/aws-simple-migration).

## Inspect and import

A server ingestion key must be pinned to the source's project. Public/unpinned keys cannot select
a source. Header X-Project-Key takes precedence over query k. Source names are ordinal and
project scoped. Inspection takes a literal full object name within the configured prefix:

```http
POST /api/v1/telemetry/import/gcs/archive/inspect
X-Project-Key: <project-pinned-server-key>
Content-Type: application/json

{"key":"exports/events.ndjson"}
```

Optionally include a positive decimal generation to inspect a retained version. Generation and
metageneration are returned as decimal strings, preserving all Int64 bits for JavaScript clients.
Integer JSON values are also accepted on input. Retain the returned descriptor without rewriting
or rounding it. It includes key, generation, metageneration, etag, bytes and sourceId. It contains
no endpoint, bucket or authentication material. Import with stable nonzero job/chunk UUIDs:

```http
POST /api/v1/telemetry/import/gcs/archive/jobs/<job-id>/chunks/<chunk-id>?offset=0
X-Project-Key: <project-pinned-server-key>
Content-Type: application/json

<the retained snapshot JSON>
```

Use nextByteOffset from the confirmed receipt for the next chunk. Read persisted status through
GET /api/v1/telemetry/import/jobs/<job-id>. The existing D1 job cap, immutable intent/latest receipt,
contract fences and uncertain-admission rules apply. Only the latest confirmed chunk has an exact
retry receipt; it is fetched at its original byte length and D1 verifies the retained SHA-256.
Never automatically replay a needs_reconciliation state or create a new job expecting deduplication.

## Immutable selection and privacy

HEAD retains a positive generation/metageneration, canonical strong ETag and matching stored/
response length. Reads select the retained generation explicitly and include generation,
metageneration and If-Match preconditions. Every response must return the selected metadata,
exact byte range and content length before any record reaches ingestion. Replacing the current
object does not redirect the job to its new generation. The old generation must remain available;
metadata changes or deleted generations can fence retries while persisted job status stays readable.

SourceId is a keyed integrity descriptor, not an authorization token or object checksum. It binds
the source name/project, endpoint/bucket/prefix, object name, generation/metageneration, ETag and
byte count without exposing a public digest for guessing private source settings. Stable settings
and identity key preserve descriptors across restart. HMAC credential rotation preserves resource
identity; rotating the private identity key or changing resource selection fences old descriptors.
This does not alter HTTP source header/URL rotation behavior.

The adapter accepts uncompressed UTF-8 NDJSON only. It requires stored content encoding identity
and a stored length, disables redirects/proxies/cookie storage/decompression, and rejects full-body
fallback, transcoding, weak/missing validators, missing/duplicate/malformed generation headers,
truncated/extra bytes and mismatched metadata. A complete-object CRC32C/MD5 is not a checksum of
an individual range; the adapter makes no range-checksum or event-level exactly-once claim.
See Google's [HEAD](https://docs.cloud.google.com/storage/docs/xml-api/head-object) and
[download](https://docs.cloud.google.com/storage/docs/xml-api/get-object-download) contracts.

## Bounds and failures

| Resource | Bound |
| --- | --- |
| Named declarations | 0–32, requiring NDJSON imports and jobs when nonempty |
| Source name | 1–64 ASCII letters/digits/dash/underscore |
| Project ID | Nonblank, at most 256 characters, resolving to a configured project |
| Endpoint | At most 4,096 characters; valid canonical host and root HTTP(S) origin |
| Bucket syntax | 3–222 lowercase ASCII letter/digit/dash/underscore/dot characters; components 1–63; no dotted IPv4 address |
| Object name/prefix | At most 1,024 strict UTF-8 bytes; no CR/LF; object names nonempty and within the prefix |
| Access ID / HMAC secret | 1–128 ASCII token characters / 1–256 visible ASCII characters |
| Private identity key | Exactly 32 Base64-encoded bytes |
| Object bound | Positive MaxObjectBytes, default 1 TiB |
| Remote deadline | 1–120 seconds, default 30, including body reads |
| Response headers | 16 KiB |
| Download / control body | min(MaxRequestBodyBytes, 1 MiB) / min(MaxRequestBodyBytes, 16 KiB) |
| GCS operations | One, without a waiting queue, plus existing global/import/D1 limits |

Bucket reserved-name/domain ownership and namespace-specific provider restrictions remain Google's
authority. The engine validates structural path-safe bucket syntax and uses path-style addressing.
Names are preserved/UTF-8 escaped without interpreting object-name dot/repeated segments as local
directory traversal. Provider-reserved dot names and .well-known/acme-challenge/ are rejected.
Native provider acceptance of unusual names must be verified separately from local wire fidelity.

Missing objects use 404, changed metadata/representation 409, body/object budgets 413, unsupported
encoding/media 415, busy admission 429, protocol/access/transport errors 502 and source deadline
504. Diagnostics never reflect provider bodies or credentials. Journal failure uses D1's 503 latch.
Fetch failures create no checkpoint. After admission, WAL and manifest ownership retain D1's
explicit ambiguity contract; remote storage, WAL, manifests and ClickHouse are not one transaction.
Remove GcsSources to disable the adapter while retaining ordinary recovery and persisted job history.
