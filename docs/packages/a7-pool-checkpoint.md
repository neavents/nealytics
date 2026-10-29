# A7 workload-pool checkpoint

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
