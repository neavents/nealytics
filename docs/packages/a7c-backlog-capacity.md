# A7c: retained ingestion capacity

Branch: feat/correctness-and-safety-a7c-backlog-capacity

The channel reserves both record slots and estimated retained payload bytes before publication.
Its reader returns the exact stored reservation on removal; peeking does not release ownership.
Cancellation before publication returns the reservation. The estimate counts UTF-16 text,
dictionary entries and conservative object overhead. It is an admission accounting model, not a
claim to measure exact CLR heap usage. Runtime working set still needs B6 measurement.

Database fill, concurrent insert, deferred-event and shutdown buffers observe a separate byte
budget as well as the existing record limit. Recovery observes both framed-WAL bytes and estimated
payload bytes. A single event larger than a configured channel or batch budget is refused; a
retained legacy event that no longer fits needs the operator to raise the budget for recovery.

The WAL reserves actual encoded bytes before queueing. Pending writes and all retained recovery
files count against WalMaxBacklogBytes. A full budget refuses another append with the existing
storage-unavailable response; it does not poison the healthy WAL. Durable truncation or removal
returns only the bytes of the removed files, preserving reservations for not-yet-flushed appends.
After an outage, retained source files may exceed a newly lowered setting; recovery remains
available while new appends wait for sufficient space. Delivery manifests have their own byte,
record and per-unit recovery limits. No cleanup depends on FIFO assumptions about producer tasks.

Defaults: channel 64 MiB, each database buffer 16 MiB, retained source WAL 1 GiB, delivery journal
256 MiB/100,000 records. Configure these with request admission, WAL pending/flush bounds and
reserved database pools. These are explicit capacities, not an assertion that total RSS equals
their sum. Disk saturation and consumer failure must preserve acknowledged records while keeping
admission bounded. Existing callers keep the same endpoints and durable acknowledgment contract.
