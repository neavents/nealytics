# A7: bounded workload capacity

Branch: feat/correctness-and-safety-a7-resource-capacity

Prevent analytical reads from taking the connections needed to ingest data or answer a bounded
readiness probe. Give read, ingestion write, probe and maintenance work independent bounded
pools, with an acquisition deadline covering both pool waits and connection opening. A storage
failure evicts only the affected workload's idle connections. A lease can be copied or disposed
more than once without returning the same connection twice.

ConnectionPoolSize retains the read pool's existing size. Three new settings reserve one
connection each for ingestion writes, probes and maintenance by default, making the default
aggregate limit nineteen. A zero read pool size disables idle reuse and uses an explicitly bounded
unpooled concurrency limit. Operators can tune each reservation. Separate pools do not isolate
ClickHouse CPU, memory, merges or disk; server workload limits still require pressure qualification.

The next slice bounds admitted ingestion requests, channel payload bytes and durable WAL backlog,
before parsing and persistence. Admission must release every reservation on cancellation, failure,
dispatch and shutdown, and reject overload without changing the successful producer contract.

Acceptance includes progress under saturated reads, role-specific idle eviction, acquisition and
probe deadlines, shutdown with waiting/opening/borrowed connections, failed opens, stale reuse,
copied-lease disposal and bounded request memory under a stalled database. Use focused tests during
implementation and consolidate broad coverage, mutation, dependency and pressure checks at the
milestone. Category completion requires both slices, not only the pool split.

## Connection lifecycle follow-up

Opening and asynchronous connection disposal have narrow internal I/O delegates, following the
existing WAL fault-injection pattern. Their defaults call the same driver operations directly.
Public factory construction and connection leases are unchanged. Pooled borrowing, synchronous
return and query execution do not acquire an additional lifecycle wrapper.

Publish the cached shutdown task before invoking cancellation callbacks. A callback that re-enters
disposal must receive that existing task instead of starting another acquisition-drain owner.
Preserve faulted and canceled completion states, and always release owned synchronization handles.
A throwing cancellation callback does not bypass outstanding acquisition cleanup. A synchronous
failure disposing one idle connection is captured while the remaining disposals still start and
finish. Shutdown propagates the failure after those cleanup attempts complete.

Deterministic unit cases control post-open cancellation, a granted reservation racing with
shutdown, cleanup failure, reentrant disposal and owned synchronization-handle release. A queued
synchronization context controls the reservation race without timing sleeps. Real-server cases
verify stale replacement, all-workload shutdown waits, canceled cleanup and synchronous/asynchronous
cleanup failures. These tests establish ownership and completion behavior; no benchmark is run.
