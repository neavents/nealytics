# E2 bounded NDJSON file import

Selected as the next bounded integration on 2026-11-04 after the owner asked to continue E1.
Branch: `feat/integrations-e2-ndjson-import`, based on `5ee510d` and the qualified A/B/C/E1 foundation.
Category: `feat/integrations`. Main remains untouched. The historical unfinished E1 NDJSON branch
is preserved; its code and tests are carried forward explicitly without its stale mutation ranges.
Status: bounded functional implementation qualified; see the [evidence ledger](e2-ndjson-verification.md)
and [operator guide](../ndjson-import.md). No benchmarks or performance claims.

## Outcome and boundaries

Opt-in `POST /api/v1/telemetry/import/ndjson` accepts a streamed UTF-8 NDJSON file body using
a pinned server ingestion key and the existing project context. Every line is one telemetry
object with an explicit nonzero eventId and UTC timestamp in the 1900–2299 storage calendar. LF, CRLF and a final unterminated
line are supported. Empty lines, arrays, malformed JSON and implicit identities/times reject.
Normal field sanitization, event/project restrictions, logical measure bounds and WAL/delivery
semantics remain shared with existing ingestion. Existing track/beacon contracts stay unchanged.

Return a bounded receipt with the durable accepted-record count, next one-based line and byte
offset within this request, completion and a fixed error code. Stop at the first invalid record;
never skip it. A successful receipt acknowledges WAL persistence and broker handoff, not database
visibility. Invalid/oversized input can retain a durably accepted prefix and returns its receipt.
Storage failures, disconnects or missing receipts can leave an uncertain prefix: do not promise
exactly-once replay or automatically retry that chunk. Stable supplied event identities and source
content support reconciliation; they do not introduce HTTP deduplication.

The caller uploads bounded chunks and retains confirmed receipts/source offsets. There is no
server-side durable import job, durable resume token, file path access, URL fetch, automatic retry,
format detection, CSV/Parquet conversion, external credentials or mandatory helper service.
Robust cross-request checkpoint/retry orchestration remains a separately scoped integration step.

## Resource and ownership contracts

The feature is disabled by default. ImportMaxLineBytes defaults to 65,536 (1–1,048,576 and no
larger than MaxRequestBodyBytes), ImportMaxRecords to 1,000 (1–10,000), and ImportMaxConcurrency
to 2 (1–64). Keep the existing decoded request-body and global write-admission budgets, plus a
separate import concurrency limiter with no waiting queue. Process one bounded record at a time.
Parse complete buffered lines directly; transfer incomplete lines to one lazily rented bounded
buffer and consume the pipe segments so producer backpressure cannot deadlock framing. Return
that buffer with clearing on completion, cancellation, rejection, early exit and reader failure.
After a validated record enters persistence, client disconnect does not transfer ownership away
from the engine; application shutdown still cancels outstanding work. Release pipe and rate-limit
ownership on success, validation failure, cancellation and storage faults.

## Acceptance and rollout

Verify segmented UTF-8/line boundaries, exact caps, final lines, invalid values/trailing JSON,
identity/time requirements, cancellation and reader ownership. Exercise HTTP authentication,
content types, project catalogs, sanitization, rejected prefixes, compression/body limits,
concurrency, WAL flush failures, shutdown and source-generated receipts on both execution paths.
Use an isolated real ClickHouse and a published executable for persistence/query visibility.
Run changed-code coverage and Complete Stryker checks with unchanged 100% thresholds, followed
by consolidated milestone suites. No benchmarks, performance claims or default-backend changes.
Disable the feature to remove its route; already acknowledged WAL records retain normal replay
and schema-fingerprint requirements. Physical schema migrations are not introduced by E2.
