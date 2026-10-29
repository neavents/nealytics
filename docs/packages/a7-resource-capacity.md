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
