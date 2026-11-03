# B4: bounded beacon durability groups

Branch: feat/ingestion-performance-b4-beacon-microbatches

Outcome: reduce per-element WAL barriers for beacon arrays while preserving the existing route,
authentication, validation, sanitization, status codes and durable accepted-prefix behavior.
The shared WAL remains the sole owner of serialization, admission, file I/O and acknowledgment.
Both managed and native backends must use the same grouping/accounting protocol.

Collect only records immediately available from the streaming parser. Flush a pending prefix
before waiting for another body read. Do not add a timer to make low-concurrency requests wait
for more traffic, materialize the entire request, or lose the valid prefix when later input is
malformed, rejected, oversized or disconnected. Validation and rejection accounting remain per
element. Invalid elements cannot consume durable record counts or enter the live channel.

The WAL needs a bounded group command whose physical append is contiguous and whose one
durability barrier covers every record in the command. Admission, uncommitted counts and flush
statistics count logical records, while the pending queue owns group buffers. Reserve complete
groups before queueing so a group cannot deadlock by waiting for its own unflushed reservation.
Enforce individual record, group byte/count and global pending byte/count budgets separately.
Encoding failure after a valid prefix must preserve that prefix and its original failure rather
than turn partial parsing into an all-or-nothing request. No group is published before durability.

There is at most one outstanding parser move. On a storage failure, cancel and settle a pending
move before disposing the enumerator. Requests cannot abandon queued WAL work or return pooled
buffers while file I/O still owns them. Cancellation before admission and cancellation after
queueing retain the shared WAL's existing ownership guarantees.

Acceptance: exact group bounds, a single barrier for each admitted group, per-record accounting,
mixed single/group appends, queue saturation, allocation/encoding/write/flush failures, partial
groups, malformed tails, rejected records, delayed body chunks, disconnect and shutdown races.
Repeat captured real-Kestrel contracts and ClickHouse visibility with both managed and native
I/O, published-artifact checks, maintained line/branch coverage and complete-level mutation.
Measure barriers per event, low-concurrency latency and bounded resource use. The four-mode
performance comparison and default decisions remain B6. B5 owns the custom dispatcher switch;
this step does not change the public ingestion protocol.
