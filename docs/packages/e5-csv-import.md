# E5 bounded headered CSV import

Selected from the continuing integration roadmap after qualified E4, 2026-11-04.
Category: feat/integrations. Step: feat/integrations-e5-csv-import.
Status: runtime implemented; qualification and mutation investigation are in progress.
See the [operator contract](../csv-import.md). This brief does not declare CSV qualified.

## Outcome and compatibility

Add optional POST /api/v1/telemetry/import/csv for one streamed UTF-8 CSV body with an explicit
header row and project-pinned server ingestion key. Operators declare header-to-envelope bindings
using the existing source-mapping vocabulary, defaults, dimensions and measures. Event identity
and UTC event time remain explicit input; the adapter must not generate them. Existing policy,
sanitization, WAL ownership and dispatch guarantees apply. Existing routes/defaults stay unchanged.

Return a bounded accepted-prefix receipt with durable record count, next data-record position and
decoded source-byte offset, completion and a fixed error code. Header bytes count toward source
position but not accepted data records. Stop at the first invalid row. A successful receipt means
WAL persistence and durable dispatch handoff, not immediate query visibility or source deduplication.
Disconnect/storage faults or a missing receipt can leave an uncertain prefix. No automatic retry.

## Format and declarations

Use comma delimiters, double-quoted fields, doubled quote escaping, CRLF/LF record separators
and a final unterminated record. Quoted fields may contain commas, quotes and embedded CR/LF.
Preserve spaces and empty string cells. Reject invalid UTF-8, unescaped quotes, nonseparator
characters after a closing quote, incomplete quoted fields and inconsistent column counts.
No delimiter guessing, legacy encodings, spreadsheet evaluation or implicit JSON/numeric coercion.
An optional initial UTF-8 BOM is recognized only at the start of the header.

Require one nonempty unique ordinal header name per column, with explicit name/column/record-byte
bounds. Allow at most 256 columns, with nonempty ordinal-unique header names up to 128 UTF-16 code units.
ImportMaxLineBytes bounds the entire logical record including BOM/quotes/record separators;
ImportMaxRecords and shared ImportMaxConcurrency retain their existing limits.
Bind fields by literal header name. Missing declared columns must fail before data admission;
constant defaults may supply optional/fixed envelope fields, with no identity/time defaults.
Duplicate/empty headers, unsupported nested paths and invalid declarations fail safely. A mapping
cannot set internal WAL identity or bypass the authenticated project and current schema policy.

## Resource and implementation boundaries

Keep the feature off by default. Reuse existing decoded request/global-write budgets and the
queue-free import admission policy. Bound header state, columns, each entire logical record
including quoted newlines, total request bytes and accepted records. Consume incomplete pipe
segments into at most one bounded cleared pooled record buffer so backpressure cannot deadlock
multiline framing. One record is validated and handed off at a time; no whole-file materialization.

Keep transport/authentication, CSV framing/decoding, compiled field bindings and durable ingestion
cohesive. Reuse the existing source-mapping/validation semantics through a narrow value-reading
boundary; avoid serializing every CSV row into an intermediate JSON document or introducing a
runtime plugin framework. Any shared refactor retains the qualified Kafka/NDJSON behavior and
mutation responsibilities. Source-generated response serialization and single-binary deployment
remain required; no format SDK, mandatory service, DDL or persisted-format change is planned.

D1 currently checkpoints raw NDJSON chunks, and E4 cuts S3 ranges at LF. This package does not
silently reuse those rules for CSV with quoted newlines. Durable CSV jobs, cross-chunk header state,
S3 CSV support, URL/GCS sources and Parquet need separately defined source/checkpoint contracts.
A caller must not restart a failed CSV upload with a new identity and assume duplicate suppression.

## Qualification and rollout

Unit tests exercise every parser state/transition across segmented input, quote/comma/CRLF/UTF-8
boundaries, exact record/header/column limits, final rows, BOM, empty cells, malformed input and
reader/buffer disposal. Mapping tests establish independent input identity/time and shared schema
validation. HTTP tests cover disabled routes, keys/project isolation, content types, compression,
known/decoded body limits, accepted-prefix positions, cancellation, queue-free concurrency and WAL
faults on managed/custom paths. Real ClickHouse and published actual Linux x64/ARM64 checks must
establish exact persisted/query results, restart behavior and Kafka/NDJSON/S3 compatibility.

Use focused tests during development, then consolidated suites, source-bound changed-code coverage,
Complete mutation with existing thresholds and warning-free platform builds. Investigate inherited
gaps and every surviving/uncovered/timed-out mutant. No benchmarking or performance claims.
Disabling the route preserves ordinary acknowledged WAL recovery; no storage rollback is required.
Do not merge into main or touch the original unfinished rebase.

Format reference: [RFC 4180](https://www.rfc-editor.org/rfc/rfc4180), with explicit UTF-8/LF/BOM
handling above. The adapter's bounded contract is authoritative for its supported dialect.
