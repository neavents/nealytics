# E4 checkpointed S3 object import

Selected by the owner's continuing roadmap goal after qualified D1, 2026-11-04.
Category: feat/integrations, including D1 through a normal feature merge.
Step: feat/integrations-e4-s3-import. Status: implementation and qualification in progress.
Main and the original unfinished rebase remain untouched.

## Outcome and compatibility

Optional named, project-pinned S3 sources inspect one object and import bounded NDJSON chunks into
the existing checkpoint/WAL pipeline. An operator configures each source's endpoint, bucket,
permitted key prefix, signing region and credentials. A pinned server ingestion key selects only
sources in its project. Existing track/beacon, standalone NDJSON, Kafka and D1 contracts remain
compatible. No cloud hosting, new mandatory service or external SDK/runtime installation.

POST /api/v1/telemetry/import/s3/{source}/inspect accepts an object key and optional version ID.
It returns a snapshot descriptor containing key, version, strong ETag and byte length, plus a
server-derived source identity. POST /api/v1/telemetry/import/s3/{source}/jobs/{jobId}/chunks/{chunkId}
with offset query and the retained descriptor imports one bounded chunk. Status and confirmed
receipts remain available through D1's project-scoped job GET route.

The caller keeps stable UUIDs and the inspection descriptor. Each request checks its source
identity, validates conditional range metadata, bounds the response and cuts at LF/per-request
record limits before calling D1. Confirmed ready progress resumes at its byte offset. Matching
latest-chunk retries use D1's stored receipt; if the source can no longer be fetched, the persisted
status remains available but the adapter cannot reconstruct missing bytes. Unconfirmed admissions
still require reconciliation. No event-level exactly-once promise or automatic ambiguous replay.

## Boundaries and resources

S3Sources defaults empty. At most 32 named declarations; enabling requires NDJSON and import jobs.
Compile immutable source settings and project access at startup. Keep credentials in deployment
secrets and out of API responses, manifests and diagnostics. Support explicit access/secret keys
and optional session tokens with AWS SigV4. No metadata-service credential probing or automatic
credential refresh. Support ordinary general-purpose S3 and explicitly configured compatible
endpoints, path/virtual addressing, regional endpoints and version IDs. Directory/access-point/
Object-Lambda buckets, SSE-C, archive restore, notifications, scans and prefix scheduling are separate.

Use HTTPS; plaintext requires an explicit operator option for local compatible stores. Disable
redirects, proxy discovery, cookies and automatic decompression. Preserve exact encoded object
paths, including Unicode, percent signs, repeated slashes and dot segments. Object keys are bounded
to 1,024 UTF-8 bytes, checked against the configured ordinal prefix. The request cannot supply a URL,
bucket, credentials or region. Source identity hashes the configured resource and retained snapshot.

One S3 operation at a time, no waiting queue, in addition to existing global/import admission.
Bound object size per source, response headers, transport lifetime, control JSON and downloaded
bytes. Fetch at most the D1 decoded chunk cap, at most 1 MiB; use bounded cleared pooled storage.
Passing the framed bytes to D1 may use a second bounded copy, at most one S3 chunk globally; normal
ingestion stays independent. Nonfinal records must fit inside a chunk including LF. Unsupported
compression, missing/mismatched ETags, versions, ranges/lengths, truncated or overlong bodies,
source changes and redirects fail before WAL admission. Remote errors use fixed safe diagnostics.

## Failure and qualification contract

No checkpoint is created for a failed fetch. Once D1 admits a chunk, its synchronized intent and
receipt protocol owns cancellation, shutdown and crash ambiguity. Do not claim a transaction across
S3, WAL, checkpoint files and ClickHouse. Object ETags are opaque representation validators, not
content-MD5 or full-file SHA-256 guarantees. Preserve the D1 ingestion-contract fingerprint.

Verify official SigV4 vectors, credentials/token signing, raw URI encoding, startup bounds/project
scope, headers/metadata, conditional reads, cancellation/timeouts, response/buffer ownership,
framing and record limits, confirmed retry/restart, rejection prefixes and changing objects.
Use deterministic transport faults plus a real isolated S3-compatible service and ClickHouse.
Run focused tests during edits, then consolidated suites, changed-code coverage, Complete mutation
and published actual Linux x64/ARM64 checks with io_uring/custom. No benchmarks/default changes.

Disabling S3Sources removes adapter routes/services while preserving D1 manifests and ordinary WAL
recovery. No persisted format or DDL changes. CSV/Parquet, GCS, generic URL fetching, scans,
scheduling, sinks, retention and reconciliation resolution remain separate bounded deliveries.

Protocol references: [S3 GetObject](https://docs.aws.amazon.com/AmazonS3/latest/API/API_GetObject.html),
[HeadObject](https://docs.aws.amazon.com/AmazonS3/latest/API/API_HeadObject.html),
[AWS signing procedure](https://docs.aws.amazon.com/AmazonS3/latest/developerguide/sig-v4-header-based-auth.html),
[raw URI support](https://learn.microsoft.com/en-us/dotnet/api/system.uricreationoptions.dangerousdisablepathandquerycanonicalization?view=net-10.0).
