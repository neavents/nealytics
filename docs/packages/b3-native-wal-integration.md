# B3: native WAL integration and startup selection

Branch: feat/ingestion-performance-b3-native-wal-integration

Outcome: select managed or Linux io_uring file I/O behind the shared WAL without changing an
ingestion URL, body, credential, response, framing or durable acknowledgment. Kestrel and the
existing ingestion handlers remain shared. Custom dispatch and beacon groups are separate steps.

Provide operator modes `managed`, `io_uring` and `auto`, with `managed` retained as the initial
default until B6 measurements qualify a change. Native mode requires a supported process/kernel
and the packaged bridge; failure is explicit. Auto may choose managed only during startup for a
known platform/library/kernel capability denial. Disk, permission, allocation and unexpected
errors must not be relabeled as unsupported capability. No write or completion failure permits
an operational backend switch.

Seal the previous active WAL before constructing either file backend. Opening a native context
before that rename would leave its descriptor targeting the replay segment. A selected native
context has one bounded completion owner and uses the same admission, serialization, grouping,
accounting and shutdown rules as managed I/O. Report the selected mode and a clear startup
fallback reason through diagnostics without placing details in producer responses.

Acceptance requires selector policy and failure tests, resource cleanup on failed construction,
managed/native existing-file replay and concurrent append/flush/truncate parity, unchanged
real-Kestrel track/beacon contracts, permitted and denied-kernel startup, and a clean-host run of
the published self-contained executable with its embedded native bridge. Exercise actual Linux
ARM64 and x64. Cover changed maintained logic completely and run complete-level Stryker at the
existing 100% threshold. Native fault tests remain required independently of managed mutation.

Operators can select managed on a restart. WAL bytes remain compatible between the modes; the
switch never weakens durability or discards a replay segment. Recovery storage changes belong
to their own correctness package and require a separate rollback review.
