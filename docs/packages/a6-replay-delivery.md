# A6: persisted delivery units

Branch: feat/correctness-and-safety-a6-replay-delivery

Persist a checksummed, fsynced delivery manifest before the first database insert. Freeze its
random unit ID, ordered WAL record IDs and payloads, source table UUID, ingestion timestamp and
conversion schema fingerprint. A successful insert creates a durable completion marker. Restart
finishes pending units before considering newly grouped replay records, then skips the exact WAL
identities already completed. Distinct accepted appends receive distinct internal identities even
when their caller event IDs match. Legacy records receive identities derived from their immutable
recovery segment and ordinal. Producers keep their current payload and acknowledgment contract.

A checkpoint may remove manifests only after all source WAL records have committed and their
files have been durably truncated or removed. WAL and journal cleanup share the file lock. A
crash between source cleanup and manifest cleanup leaves redundant delivery metadata, not missing
payloads. Checksum, membership, schema, table identity, byte and record capacity errors fail closed.

ClickHouse 25.3 requires synchronous delivery for this package. EnableAsyncInsert defaults to false;
an explicit true value is refused with a migration diagnostic. The source and aggregate targets
retain 100,000 deduplication block IDs, and every attempt uses explicit source and dependent-view
deduplication settings plus the persisted token. One unresolved unit blocks later units. This is a
bounded retry protocol, not an unlimited exactly-once claim: supported operation requires one
engine to own writes to these tables, unchanged block settings, and no external direct inserts,
backfills, schema replacement or deduplication-window changes during recovery. A pending unit
older than DeliveryRetryMaxAgeSeconds (default 900) requires offline reconciliation instead of an
automatic insert. The age guard supplements the exclusive-writer rule; elapsed time alone does
not prove that a ClickHouse deduplication token remains retained.

Backfill and rebuild are offline operations in this package. Stop admission, drain accepted WAL
and pending delivery units with the original declaration, stop every writer, and take a backup.
Rebuild a fresh aggregate table and view from the fixed raw source. Verify scoped raw and aggregate
counts and measures before recording readiness or routing traffic. Resume only after schema and
readiness verification succeeds. Online concurrent backfill is outside this boundary and must not
be presented as safe. Existing incomplete or drifted aggregates keep A5's raw fallback.

Qualification must exercise ambiguous commit/restart, reordered recovery batches, frozen timestamp
and conversion behavior, source/schema mismatch, checksummed corruption, capacity exhaustion,
marker/checkpoint ordering, repeated tokens, multiple partitions, partial materialized-view failure,
and actual deduplication-window eviction on the pinned server. Broad coverage, mutation, native
and artifact checks follow the implementation milestone; this brief does not claim they passed.


The conversion fingerprint uses versioned canonical JSON rather than record display formatting.
It covers ordered columns, storage types, conversion kinds and invariant nullable measure bounds.
Process locale and query-only metadata do not change it. Fixed digest regressions guard its
persisted representation. Resolve pending manifests with their original feature build before
upgrading from an earlier intermediate fingerprint format; a mismatch fails closed rather than
rewriting the fingerprint or automatically reinserting an uncertain unit.
