# Categories A and B: functional qualification

Recorded 2026-10-30. The owner stopped all benchmarks and performance measurement. This record
covers the agreed A/B implementation, correctness, compatibility, durability and packaging scope.
The agreed A/B functional milestone is complete. Backend defaults remain managed; io_uring and custom ingestion are explicit internal selections
behind the existing producer contract. Measurement and default-enablement decisions are deferred.

## Current source and functional checks

Source e3d2599 passed a warning-free solution build, 2,110 portable unit tests and 185 isolated
integration tests against pinned ClickHouse 25.3.14.14, with zero skips. Four measurement-harness
integration cases are excluded by the owner instruction, through their category and the default
integration run settings, script and CI filter. Pure harness helper tests do not run workloads.

| Check | Result |
| --- | --- |
| General CI at e3d2599 | Passed build, units, applicable integration, helper correctness and published smoke checks |
| Actual Linux x64 and ARM64 at e3d2599 | Both jobs passed native faults, the expanded complete coverage gate, sanitizers, native HTTP, denied-policy fallback, clean-host artifacts and native-selection mutation checks |
| Published native-enabled executable | All four managed/custom ingestion and managed/io_uring WAL combinations recovered exactly 48 acknowledged identifiers after forced termination, then shut down cleanly |
| Delivery/batching/admission unit mutation scope | 100% at 62b78fe: 402 assertion kills, ten progress/signal timeout detections, zero survivors/uncovered mutants; fifty selected compile errors |
| Pool/factory/lease mutation scope | 100% at 000bff5: 112 assertion kills, seven progress timeout detections, zero survivors/uncovered mutants; five selected compile errors |
| Database-delivery mutation scope | 100% at 6f0d9e6: eleven assertion kills, zero survivors/uncovered/timeouts; ten selected compile errors |
| Directory metadata/ownership mutation scope | 100% at 218cd69: 96 assertion kills, three progress timeout detections, zero survivors/uncovered mutants; 22 selected compile errors |
| B5 dispatcher mutation scope | 100% at 977978a: 57 assertion kills and eight progress timeout detections, zero survivors/uncovered mutants; seven selected compile errors |

The exact selected production fragments from all five mutation sources match the final source.
The directory mutation source and e3d2599 also share the entire engine tree
`d2254cbcb6121a77633358b79cc4b66d947bbf9f`. Source changes do not borrow a passing result
from different logic. Thresholds remain at 100; no maintained source was hidden or excluded to
close a failure. Timeout detections remain distinct from assertion failures. The three directory
timeouts remove termination from ancestor discovery or retry permanent opening failures forever.

The standalone Linux ARM64 executable SHA-256 is
`1a8a485db08f5e136b088900723a035e65094f4da5f1f257cf624e791313faec`.
Its forced-termination fixtures test process restart, exact identity recovery and clean shutdown;
they do not prove whole-host power-loss behavior or measure performance.

General CI: https://github.com/neavents/nealytics/actions/runs/41042503765.
Actual Linux qualification: https://github.com/neavents/nealytics/actions/runs/41042504184.

## Coverage boundaries

Same-source OpenCover unit and integration collectors are combined by file, method and branch
path, preserving distinct outcomes. They cover every maintained line and branch in the pool,
factory, lease, delivery journal, journaled writer, delivery fingerprint, batch/recovery processor,
channel broker, request admission, retained-backlog budget and directory metadata helpers.

| Current scope | Lines | Branch outcomes |
| --- | ---: | ---: |
| Connection pool | 209/209 | 44/44 |
| Connection factory | 64/64 | 6/6 |
| Shared connection lease | 22/22 | 2/2 |
| WAL directory | 135/135 | 40/40 |
| Directory durability | 49/49 | 26/26 |
| POSIX directory operations | 18/18 | 2/2 |
| Directory operation records | 2/2 | No executable branches |

The changed-line audit compares against f1cfe3e. Local gaps remain only in three Linux-specific
files: LiburingWalContext, NativeWalApi and WalFileFactory. The actual Linux gate explicitly
includes these paths, the directory helpers and the shared logger. Generated source is excluded
from maintained-code totals explicitly. The C bridge has its separate coverage and sanitizer gate.

The entire maintained engine C# collector covers 9,865/10,531 lines and 2,991/3,393 branch outcomes
locally. This is not a repository-wide 100% claim. Remaining inherited gaps include existing schema
migration/startup, schema discovery, rollup declarations, JWKS and query-validation paths. The
changed-line audit separates those from the A/B changes. Measurement tooling is outside this
functional development qualification.

Archy preflight reports missing layer rules; its CLI reports an uninitialized workspace. The
executable architecture, source-style and domain-vocabulary guards pass. Archy is unavailable,
not passed, and its absence does not replace the executable checks.

## Implemented guarantees and limits

- Existing ingestion routes, payloads, credentials, accepted-prefix beacon behavior and durable
  acknowledgment remain compatible. Custom dispatch and native I/O require no producer changes.
- The single self-contained executable packages its native asset. No I/O companion service or
  separate liburing installation is required. Actual Linux x64/ARM64 and macOS development
  behavior are covered; Windows metadata durability is not qualified.
- Request, channel and batch memory, WAL admission/backlog and database workload capacity have
  explicit bounds. Pool shutdown handles reentrant cancellation, drains acquisitions, attempts
  all idle cleanup and preserves faulted or canceled completion.
- Directory ownership is exclusive. Lease and active-file durable flushes precede publication.
  Native errors are captured at the syscall boundary; only EINTR retries. Borrowed and owned
  descriptors retain distinct cleanup behavior. Recovery generation overflow fails closed.
- Replay manifests freeze membership, identity, source table, conversion schema and ingestion
  time before insertion. Replay guarantees retain exclusive-writer, finite deduplication-window
  and retry-age requirements. Readiness fencing and raw fallback protect unsafe rollups.

The agreed A8 slice is startup sealing, immutable recovery generations and metadata ownership.
Live size-based rotation/checkpoint deletion, online rebuild orchestration, new connectors,
hosted services and automatic backend default changes remain separate future scope. No speed,
capacity or competitive claim is made by this qualification.

## Evidence locations

- Build log: /tmp/nealytics-ab-functional-final-build.log.
- Unit collectors: /tmp/nealytics-ab-functional-final-unit.
- Integration collectors: /tmp/nealytics-ab-functional-final-qualified-full-integration.
- Published artifact and recovery receipts: /tmp/nealytics-ab-functional-final-crash-20261030.
- Mutation reports under artifacts/verification: ab-sweep-unit-qualified-20261030,
  a7-pool-lifecycle-20261030, a6-delivery-database-20261030, a8-directory-retry-20261030,
  and b5-qualified.
- Combined coverage, changed-line audit, source identity and report checksums:
  artifacts/verification/a-b-functional-final-20261030.

The final documentation and CI display-label changes preserve the qualified engine and test trees.
Category integrations reuse that identical qualified snapshot, so full suites are not rerun for
those documentation/integration commits.

Implementation stays on category and step feature branches. Main and its unfinished rebase are
untouched. Historical checkpoints retain the evidence and open findings of their own source
versions; this record is the current functional milestone.
