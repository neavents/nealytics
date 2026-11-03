# B1: shared WAL file boundary

Branch: feat/ingestion-performance-b1-wal-io-boundary

Outcome: shared serialization, record accounting, grouping, acknowledgment and recovery use a
narrow internal file contract. The default implementation remains managed FileStream I/O.
No HTTP, producer, serializer or WAL record-format change is introduced.

The file contract owns complete append, durable flush, durable truncation and disposal. Its
caller serializes file access; an append buffer remains owned by the caller until the returned
operation completes. File operations are not canceled once admitted. A successful flush must
persist all prior appends before WAL acknowledgment. Truncation now explicitly flushes to disk,
matching the append barrier rather than relying on an asynchronous buffer flush alone.

Inject a gated/faulting file in tests to prove no early acknowledgment, propagation of append
and flush errors, grouping boundaries and disposal ownership. Run the existing framing, restart,
replay and real-HTTP fixtures unchanged. Keep native selection out of this package.

Failing closed after uncertain partial I/O and bounded admission belong to A3. Crash-consistent
segment rotation and replay-safe database batches remain separate A-category requirements.
