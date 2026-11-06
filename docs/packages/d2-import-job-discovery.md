# D2 bounded import-job discovery

Next bounded operator prerequisite after locally qualified E7. Category branch:
feat/developer-operator-tools. Step: feat/developer-operator-tools-d2-import-job-discovery.
Base includes the local E7 integration merge. Status: locally qualified; see the [evidence ledger](d2-import-job-discovery-verification.md). Main stays untouched; no benchmarks.

Outcome: existing project-pinned server ingestion keys can enumerate their own persisted import
jobs without knowing job UUIDs in advance. Add GET /api/v1/telemetry/import/jobs with keyset
pagination. Reuse existing authentication, responses and job-store readiness/fault ownership.
No new hosting component, scheduler, retry policy, retention, deletion or reconciliation action.

Contract: optional limit defaults to 50 and accepts strict decimal 1..100; optional after accepts
one nonzero D-format UUID. Reject duplicates, blank/malformed values and out-of-range limits with
400 invalid_job_list_request after authentication. Return jobs in ascending Guid.CompareTo order,
using only IDs strictly greater than after; at most limit complete existing ImportJobResponse
records and nullable nextAfter. nextAfter is the last returned job ID only when a further matching
job exists. No total count or cross-project cursor-existence lookup. Ignore unrelated query fields,
as current endpoints do; a request cannot choose a project through a query parameter.

Take one immutable page from the synchronized, bounded existing store (maximum 4,096 jobs), release the
lock before serialization, and introduce no disk writes. State may change between pages; this is
live discovery, not a historical snapshot. IDs at/below a prior cursor that are created later can
be discovered by restarting at the first page. A cursor is an ordering hint, never authorization.
Unavailable journal/store returns existing 503 job_storage_unavailable and no partial page. Keep
single-job reads, upload routes, all receipts and manifest version unchanged. Disabling import
jobs also disables discovery. Preserve source-generated serialization and all prior mutation scopes.

Acceptance: parser boundaries/duplicates, exact page/continuation boundaries, default/maximum
limits, ascending order with unordered insertion, absent/foreign-project cursors, project isolation,
all job states including restart-derived needs_reconciliation, concurrent immutable page behavior,
fault/disposal guards, no journal writes from reads, route-disable and credential precedence,
source-generation roundtrip, real HTTP/restart/ClickHouse behavior and published ARM64 NDJSON/job
checks. Cover changed maintained lines/branches, investigate inherited gaps, and mutate changed
managed logic at unchanged 100% thresholds. Run focused tests during edits and broad milestone
checks once stable. Remote CI/actual x64 remain owner-deferred, not passed. Run Archy preflight and
verify, recording their known unavailability accurately.
