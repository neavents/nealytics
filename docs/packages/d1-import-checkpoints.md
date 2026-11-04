# D1 durable NDJSON import checkpoints

Selected on the owner's continuation after qualified E3, 2026-11-04.
Category: `feat/developer-operator-tools`, advanced normally from its planning ancestor to the
qualified E3 foundation `1aa755b`. Step: `feat/developer-operator-tools-d1-import-checkpoints`.
Status: bounded scope implemented and functionally qualified; see the
[evidence ledger](d1-import-checkpoints-verification.md). Main and the original rebase stay untouched.

## Outcome and compatibility

Opt-in project-scoped server-key endpoints submit bounded NDJSON chunks under stable job/chunk
identities and read persisted job progress. Reuse E2/E3 parsing, mapping, gate/sanitizers, WAL and
handoff. Existing track/beacon and standalone NDJSON contracts remain unchanged. This is a
caller-driven import workflow, not a background scheduler or staged-file upload service.

`POST /api/v1/telemetry/import/jobs/{jobId}/chunks/{chunkId}?offset=<decoded-byte-offset>` uses
X-Import-Source (a caller-asserted 64-hex immutable-source identity), X-Import-Source-Bytes
(nonnegative total decoded length), application/x-ndjson and a pinned server ingestion key.
`GET /api/v1/telemetry/import/jobs/{jobId}` returns confirmed totals, state and latest receipt.
The engine hashes each bounded decoded chunk before admitting any record. A confirmed retry of
the latest matching chunk returns its persisted receipt without another WAL append. Older or
changed chunks, offsets, source identities and completed/rejected jobs reject explicitly.

## Ownership and failure boundaries

Synchronize a checksummed processing manifest before WAL admission, then atomically replace it
with the completed chunk receipt after durable handoff. Synchronize file contents and directory
metadata. A crash, shutdown, disconnect during ingestion, storage failure or uncertain checkpoint
can leave an unconfirmed prefix. Processing manifests recover as needs_reconciliation and block
replay; do not infer exactly-once ingestion or silently replay them. Confirmed ready jobs resume
at their stored decoded offset. Validation failures retain the prefix and make the job rejected.

Scope jobs to the authenticated project; never store credentials or payload bytes. Bind new chunks
to a frozen ingestion-contract fingerprint including schema, mapping and import limits. Persisted
receipts remain retrievable after a contract change; further ingestion requires a matching contract.
WAL records already contain normalized envelopes and do not depend on job metadata for recovery.

## Resource and storage bounds

EnableImportJobs defaults false and requires EnableNdjsonImport. ImportJobMaxJobs defaults 256,
allowed 1–4096, including terminal jobs. Never evict history automatically to admit another job.
Each checksummed manifest is at most 8 KiB, with one temporary replacement. One job chunk executes
at a time with no waiting queue, outside normal track/beacon dispatch; existing global write and
NDJSON admission also apply. Buffer at most min(MaxRequestBodyBytes, 1 MiB) decoded source bytes
before hashing/admission; pool buffers are cleared on return. Nonfinal chunks must end at LF.
Job metadata lives under the WAL directory in import-jobs with its own process lease.

## Acceptance and rollout

Test authenticated project isolation, metadata/body boundaries, mapping/policy preservation,
duplicate receipts, conflicting retries, offsets, source/contract changes, terminal states,
concurrency, cancellation, checksum/version/counter corruption, torn temporary files, process
leases, write/flush/rename/directory-sync failures and restart ambiguity. Run changed-code coverage
and Complete mutation checks, then consolidated unit/integration and published Linux x64/ARM64
functional checks with io_uring/custom. No benchmarks or default-backend changes.

Disabling removes job routes/services and preserves manifests. Older binaries ignore the new
metadata folder and retain ordinary WAL recovery; they cannot serve the new job protocol.
Scheduling, cancellation APIs, reconciliation resolution, history deletion/retention, durable
deduplication, file staging, S3/GCS/URL sources and additional formats are separate packages.
