# A8: WAL directory ownership and durable segment metadata

Branch: feat/correctness-and-safety-a8-wal-segments

One engine owns a WAL directory at a time. Hold an operating-system lease for the complete WAL
lifetime; release it on failed startup and disposal without unlinking the lock file. A second
instance must fail before opening, sealing or deleting any data file.

Preserve each recovery segment as an immutable file. When a recovery file and a nonempty active
file coexist, seal the active file under a new ordered generation instead of copying it onto an
earlier segment. This prevents repeated-startup duplication and concatenation after a torn tail.
Retain legacy active and recovery names and NDJSON record compatibility.

Older binaries do not discover generation files. Recover and drain them before a downgrade;
unchanged record encoding alone does not make that downgrade safe.

Directory creation, active-file creation, rename and deletion require directory synchronization.
Errors fail closed. The first implementation targets the Linux production platform and macOS
development platform; other hosts require an explicitly verified metadata-durability strategy.
Do not silently treat an unsupported directory flush as success.

Acceptance includes two processes competing for a directory, repeated restart with several
segments and torn tails, injected metadata failures, retained files after interrupted recovery,
and actual Linux x64/ARM64 execution. Process-kill tests qualify sequencing; they cannot emulate
every storage controller's power-loss behavior.

This slice establishes startup sealing and metadata ownership. Enabling live size-based rotation
and checkpoint deletion also requires A6's persisted delivery identities, so committed records
are not silently regrouped into additive rollups after restart. Those gates remain explicit.

Directory metadata calls now use a narrow internal POSIX I/O boundary. Each result captures its
error code at the syscall boundary instead of rereading ambient thread-local error state later.
Opening transfers its successful handle to synchronization; synchronization always closes it,
whereas lease acquisition borrows the caller's handle. Only EINTR is retried. Other open, sync
and lock errors fail immediately with their captured native error.

Deterministic fault cases cover interrupted opening/synchronization/locking, stale error codes
on successful calls, permanent errors, handle ownership and exceptions during synchronization.
Missing-ancestor discovery is a pure startup helper with a filesystem probe, preserving the
existing deepest-first ordering and the boundary where no ancestor can be observed. Native
coverage now explicitly includes the directory owner and these metadata helpers; the prior
native gate did not qualify their complete fault paths.
