# A3: bounded WAL admission and fail-closed storage

Branch: feat/correctness-and-safety-a3-bounded-wal-admission

Outcome: bound pending framed record bytes, pending records, serialization scratch space and
flush group size. Backpressure waits before renting a record buffer. A single reusable serializer
buffer allows a producer waiting for byte capacity without retaining a separate serialized copy.
The existing HTTP endpoints, source-generated WAL JSON and durability barrier remain intact.

Controls: WalMaxPendingRecords, WalMaxPendingBytes, WalMaxRecordBytes, WalMaxFlushRecords and
WalMaxFlushBytes. Require positive capacities, record <= flush <= pending byte limits, and
flush records <= pending records. A record limit bounds serializer buffer requests as well as
the final newline-framed record; JSON writer reservation hints can reject a record before its
final size is reached. Defaults allow expansion of the default one-megabyte HTTP request.

The pending byte budget counts framed bytes. ArrayPool buckets may retain more physical capacity
than the requested bytes; account for bucket rounding and one serializer buffer when sizing
working memory. Request bodies and payload objects awaiting admission are outside this WAL
buffer budget and still require the separate HTTP admission work in category A.

Cancellation before admission publishes nothing. Once queued, an append waits for its actual
durability result even if the caller disconnects. Write, flush or truncation failures close
admission and fault queued requests; no later write is appended after an uncertain partial write.
Already acknowledged records remain recoverable. Disposal stops admission, wakes waiting
producers, drains queued operations and waits for resource users before releasing storage.

Acceptance: deterministic saturation, byte and record limits, bounded groups, cancellation on
both sides of admission, write/flush/truncation faults, repeated disposal, no resource leaks,
physical-file restart and unchanged real-Kestrel compatibility. Complete helper/changed-code
coverage and mutation checks, then measure performance before claiming a latency improvement.

The sealed recovery reader, crash-consistent segment rotation, durable batch identity for
ClickHouse retries, reserved database capacity and bounded HTTP admission remain separate A
requirements. This package does not make rollup replay exactly once.
