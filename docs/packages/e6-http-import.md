# E6 checkpointed named HTTP object import

Selected from the continuing source-expansion roadmap after qualified E5, 2026-11-04.
Category: feat/integrations, based on qualified merge 8a0c2aa.
Step: feat/integrations-e6-http-import. Status: contract preparation; no HTTP adapter runtime is
implemented or qualified by this brief. GCS and additional formats remain separate deliveries.

## Outcome and caller contract

Add optional named HTTP sources for operator-configured NDJSON export URLs. A project-pinned
server ingestion key selects a source, inspects its current representation and submits bounded
chunks through D1's existing persisted checkpoint/receipt contract. Existing track/beacon,
Kafka, CSV, NDJSON and S3 contracts and backend defaults stay unchanged. No hosted control plane,
provider SDK, arbitrary request-supplied URL or mandatory service is introduced.

Proposed routes: POST /api/v1/telemetry/import/http/{source}/inspect and
POST /api/v1/telemetry/import/http/{source}/jobs/{jobId}/chunks/{chunkId}?offset=... .
Inspection returns only a bounded representation descriptor, never the configured URL, query,
headers or credentials. Chunk requests retain that descriptor and stable nonzero job/chunk IDs;
existing D1 GET status exposes confirmed progress and uncertain admissions remain fenced.

The operator selects the full resource URL and bounded static request headers at startup. Request
callers cannot change its host, path, query, headers or authentication. HTTPS is the default;
plaintext is an explicit local-compatible-source choice. URLs must not contain user information
or fragments. Redirects, ambient proxies, cookies and automatic decompression remain disabled.
The engine owns conditional/range/encoding/framing headers; declarations cannot override them.
Credential discovery, refresh, signed-URL renewal, scans and scheduling remain separate contracts.

## Representation and resume

Inspection requires a usable strong ETag and nonnegative content length within the configured
object bound. Conditional single-range reads must return the retained validator, exact length
and Content-Range before body admission. Reject full-body fallback, multipart ranges, weak or
missing validators, unsupported content encoding, redirects and inconsistent metadata. An empty
object still requires a conditional inspection. The provider must support this explicit contract;
there is no silent fallback to an unverifiable resumable download.

Snapshot identity must bind the configured resource and representation selection without exposing
URL/query/header secrets or treating the identifier as authorization. Resolve and test the key,
rotation and restart lifetime before implementation; do not reuse a public configuration checksum
as a secret verifier. Source changes and credential/selection changes need an explicit fail-closed
contract. Confirmed status remains readable even when old source bytes become unavailable.

Use NDJSON LF framing and the existing mapping/project/policy path. Fetch at most the D1 chunk
budget and cut only complete records, with final unterminated lines supported. A confirmed latest
retry refetches the stored original byte length and lets D1 verify its chunk hash. A failed fetch
creates no checkpoint. Once admitted, D1's synchronized intent/receipt protocol owns crash and
cancellation ambiguity. No transaction spans HTTP, WAL, manifests and ClickHouse; no event-level
exactly-once or automatic ambiguous replay is promised.

HTTP conditional/range semantics are defined in [RFC 9110](https://www.rfc-editor.org/rfc/rfc9110.html),
notably validator comparison, If-Match, HEAD and Content-Range. The supported contract above requires
specific responses rather than assuming every HTTP server honors a range or precondition.

## Resources and code boundaries

Keep declarations empty by default. Plan at most 32 named project-scoped sources, bounded URL/
header names and aggregate header bytes, a per-source object-size ceiling and 1–120 second remote
operation lifetime. One queue-free HTTP source operation composes with global/import/D1 budgets.
Keep response headers bounded and downloads within min(MaxRequestBodyBytes, 1 MiB). Clear pooled
source storage and retain response/request ownership through success, rejection, cancellation,
short/overlong bodies and disposal faults. Ordinary ingestion remains independent of source I/O.

Reuse concrete shared HTTP transport, bounded reads and validator handling where S3 and HTTP have
the same contract; keep S3 signing/version rules and HTTP resource selection in their own feature
slices. Preserve old S3 mutation responsibilities and tests during any extraction. Avoid a general
plugin framework, speculative provider interfaces and a mandatory new dependency. Any persistent
identity metadata must have explicit publication/recovery/backup/rollback semantics before use;
D1 manifest and WAL formats must not silently change.

## Qualification and rollout

Unit/fault tests cover declaration bounds, project scope, secret-safe identities/diagnostics,
conditional metadata, exact ranges/EOF, framing, retries, budgets, deadlines, cancellation and
buffer/response ownership. Real loopback HTTP fixtures must change objects, ignore ranges, send
redirects/invalid headers, truncate/extend bodies and prove that no unverified bytes reach the WAL.
Exercise real Kestrel, ClickHouse, persisted checkpoints, disconnect/shutdown/restart and exact
query results. Qualify the published io_uring/custom binary on actual Linux x64/ARM64 while
retaining Kafka/NDJSON/S3/CSV regressions. Source-bound coverage and Complete mutation targets
remain unchanged; report whole-engine inherited gaps and every unresolved mutation.

Disabled sources remove their routes/services while preserving acknowledged WAL and checkpoint
history. Document the identity/credential rotation fence and unavailable-old-resource behavior.
CSV/Parquet fetching, GCS, database adapters, sinks, retention and reconciliation resolution remain
separate packages. No benchmarks, main integration or default selection changes.
