# A7b: admission before ingestion parsing

Branch: feat/correctness-and-safety-a7b-request-admission

Bound simultaneous track and beacon bodies before parsing or WAL serialization. Keep the existing
fixed-window rate policy and add a concurrency limit selected through endpoint workload metadata.
Other requests keep their own policies. The concurrency lease covers the full handler, including
durable dispatch; successful request and acknowledgment contracts remain unchanged.

Each admitted request reserves its maximum decoded body size conservatively. Effective concurrency
is the smaller of MaxConcurrentIngestionRequests and IngestionRequestByteBudget divided by
MaxRequestBodyBytes. Reject invalid configurations that cannot fit one body. The default raw-body
budget is sixteen MiB, so a one-MiB maximum admits sixteen simultaneous bodies. This is a bound on
admitted body bytes, not an exact managed-heap accounting claim. Decoded models, channel contents,
WAL scratch space and database buffers need their separate budgets and pressure measurements.

Use the framework's concurrency limiter with no waiting queue. Saturation returns the existing
429 response before parsing instead of retaining another decoded body. Operators can raise both
limits for their workload; benchmark manifests must record the effective settings. Caller
cancellation, parser/storage faults and host shutdown must release admission ownership.

Acceptance includes count and byte-derived limits, non-ingestion isolation, zero queueing,
release after completion/failure/cancellation and real HTTP overload before a body is read.
Channel-byte and durable-backlog limits remain A7 work and depend on the delivery ownership in A6.
