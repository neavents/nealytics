# B5: custom ingestion execution

Branch: feat/ingestion-performance-b5-custom-ingestion

Keep Kestrel, existing routes, credentials, validation, response headers, statuses and durable
acknowledgments. Add an operator-selected custom execution path while retaining the managed
path for rollback and matched comparisons. Neither path requires caller changes or another service.

The first slice reads directly from the request's pipe in custom mode. Count newly received bytes
once, including when a parser examines retained data repeatedly. Preserve decompression and body
limits without Content-Length. Release borrowed pipe buffers without completing the server-owned
reader. Managed mode retains the existing limited stream and owned reader.

The dispatcher shares the validated WAL and beacon grouping protocol. In custom mode, a caller
disconnect after persistence must not cancel the live dispatch handoff while the host is running.
Host shutdown may interrupt that handoff, with the durable WAL retained for recovery. Track and
beacon use the same ownership rule. Global request/backlog admission is the separate A7 slice.

Acceptance includes captured HTTP parity in both modes, exact and cumulative body limits,
fragmented/repeated reads, cancellation, decompression, durable-prefix parsing and live dispatch
after caller disconnect. Run focused checks while implementing, with full managed/native,
published-artifact, coverage and mutation checks at the consolidated milestone. B6 owns the
matched four-mode performance comparison and any default change. Managed remains the default.

Persistent records remain compatible with B4. The execution-path switch does not migrate storage
or waive the remaining delivery-identity and crash-consistent directory work in category A.
