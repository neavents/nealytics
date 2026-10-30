# A7 workload-pool checkpoint

## Current follow-up, 2026-10-30

Source 000bff5 passes all 2,077 portable unit cases and all 189 isolated real-ClickHouse cases,
with zero skips. Same-source OpenCover unit/integration collectors cover every maintained pool,
factory and lease line and branch: 209/209 lines and 44/44 branch outcomes for the pool, 64/64
and 6/6 for the factory, and 22/22 and 2/2 for the lease.

The Complete-level pool mutation gate passes its unchanged 100% threshold: 112 assertion kills,
seven timeout detections, zero survivors/uncovered mutants and five selected compile errors.
The timeouts remove reservation/lease return or force an acquisition-idle wait when no acquisition
can complete it. They detect lost progress and remain distinct from assertion kills. Evidence is
retained in artifacts/verification/a7-pool-lifecycle-20261030, including its immutable source.

Shutdown now publishes one cached task before cancellation callbacks can re-enter disposal.
Cancellation callback failures and individual idle-connection cleanup failures cannot skip other
cleanup or leave synchronization handles undisposed. Faulted and canceled completion states are
preserved. Narrow internal asynchronous I/O delegates make these cases deterministic without
changing public factory construction or wrapping pooled query execution.

The owner stopped all benchmarks and measurement. Qualification continues through correctness,
durability, coverage, mutation and platform tests. The historical checkpoint below identifies
earlier evidence; its unfinished measurement items are not authorization to run them.

## Earlier checkpoint

The pool slice reserves separate read, ingestion-write, probe and maintenance capacity and bounds
the complete acquisition operation. Ingestion writes and bounded readiness probes can progress
while all read connections are borrowed. Scope-aware eviction preserves the other pools.

Focused checks passed: 240 unit/style/architecture cases and eighteen real ClickHouse 25.3.14.14
pool cases, including actual batch insertion under saturated reads, deadline-limited handshakes,
failed opens, copied/discarded leases, cancellation and idle reuse. A new shutdown regression
initially caught disposal of semaphore state before canceled acquisitions had settled. The pool
now cancels acquisition ownership, waits for it to finish, and then disposes synchronization;
the targeted regression and the full eighteen-case pool group passed after that change.

Read ConnectionPoolSize remains compatible. Additional default reservations are one connection
per non-read workload, for nineteen total at the default read capacity. Zero read pooling now
retains no idle connections and uses UnpooledConnectionLimit rather than unlimited concurrency.
ConnectionAcquireTimeoutSeconds covers waiting and connection opening. ProbeTimeoutSeconds
covers the readiness operation; its token reaches acquisition and SELECT execution.

This is a functional checkpoint. Full maintained line/branch coverage, mutation, aggregate
integration/platform checks and resource-pressure qualification remain milestone gates. Request,
channel-byte and durable-backlog admission are still open A7 work. Pool reservations do not
isolate ClickHouse CPU, memory or disk.

Archy reports no configured layer rules; its CLI reports an uninitialized workspace. Executable
source and dependency guards passed. Main and the original unfinished rebase remain untouched.
