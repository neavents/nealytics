# A4: bounded streaming WAL recovery

Branch: feat/correctness-and-safety-a4-streaming-recovery

Outcome: recover a large sealed WAL without materializing all its records. Keep the producer's
HTTP and durable acknowledgment contracts and the existing UTF-8 newline-delimited file format.
Use source-generated deserialization and bounded reusable buffers. Recovery batches must respect
both the database record limit and the WAL flush byte budget; retry the same bounded batch
without rereading the entire file or clearing its records before a successful write.

The production batch processor consumes a stream. Retain the existing materializing read method
for explicit diagnostic/test callers, and keep it outside production recovery. Empty/blank lines
and CRLF framing remain readable. A torn final frame has no durable acknowledgment and may be
ignored after its complete prefix is recovered. A complete malformed frame, missing stable event
identity, missing required scope/type or over-budget frame fails recovery and retains the sealed
file. Silent skipping of a complete corrupt frame is an existing data-loss defect and is corrected
deliberately. An explicitly supplied zero UUID is stable and remains valid because ingestion
already accepts it; distinguish it from an absent or malformed identity. Do not change inbound API
default identity generation.

Cancellation, reader disposal, partial reads, I/O faults and parse faults must return and clear
owned pooled buffers. Stopping enumeration must close the file. Recovery must not decrement live
queue counters or truncate the active WAL. The sealed file is deleted only after every complete
record was committed; cancellation or corruption keeps it for retry/repair.
The same processor's shutdown owner must flush an already full buffer before adding queued
records, preserve the configured record bound, and report interrupted-drain records from the
WAL count. Its former drain path could overfill a full buffer and report zero after clearing it.

Acceptance includes tiny/changing read chunks, multi-byte UTF-8, framing boundaries, exact and
oversized records, malformed and torn input, large-file bounded-memory behavior, early disposal,
cancellation, failed reads and pool ownership. Processor tests establish byte/count limits,
unchanged retries, cancellation, complete-prefix behavior and exact active/sealed ownership.
Run managed/native recovery compatibility, pinned ClickHouse integration, full existing suites,
100% changed maintained line/branch coverage and complete-level mutation checks.

This package does not claim replay idempotency or power-loss-safe rotation. The existing
copy/merge of leftover segments, persisted delivery identity, rollup duplicate prevention,
directory ownership/metadata durability and reserved database/request capacity remain required
A-category work before category completion. They require separate bounded briefs and branches.
