# A4 verification review

Streaming recovery is implemented at d9e6d6a, on the A4 step branch. The production processor
reads framed records with bounded pooled storage, commits count/byte bounded batches and retains
the same objects across retries. Corrupt complete records stop recovery without deleting its
segment. Torn tails remain distinguishable from durable records. Shutdown preserves buffer
ownership and the configured record bound.

The full portable unit suite passed 1,662 cases with no skips. The A4 maintained scope contains
343 executable lines and 124 branch outcomes, all covered. Raw whole-unit collector totals are
10,140/18,206 lines and 2,559/4,262 branches, including generated code; these do not represent
maintained repository-wide coverage. The focused recovery/mutation fixture selection has 105
cases, covering reader boundaries, corruption, pool cleanup, retry identity, cancellation,
diagnostics, live/replay accounting and shutdown ownership.

The immutable-source complete-level Stryker run passed at 100%: 193 assertion kills, five timeout
detections, thirteen compile errors and 73 structural ignores in the selected maintained files.
There are no survivors or uncovered mutants. Thresholds and production scope were retained.
An earlier 99.49% run exposed a scheduling-sensitive shutdown assertion. Holding the writer while
observing that shutdown remains pending rejects the exact removed-await mutant, and the full
rerun passes. A prior mutable-source snapshot was incomplete during copying; it was rejected as
verification evidence and replaced with an immutable Git archive.

The five timeout detections change progress/ownership: always selecting the same fill buffer,
negating the shutdown queue-drain condition, removing replay-buffer insertion, inverting retry
termination and removing the replay retry delay. They can strand a gated writer, spin on empty
queue entries or run a failing retry loop without yielding. These are timeout detections, not
assertion kills. Ordinary component and full suites finish without those timeouts.

Real ClickHouse recovery and steady-state persistence passed with managed I/O and with actual
Linux ARM64 io_uring. Recovery uses three-record batches for 25 events and verifies exact stored
counts and sealed-file deletion. Native selection is asserted explicitly, so managed fallback
cannot make that check pass. Existing real-native file/startup and producer contracts also pass
the B3 actual x64/ARM64 pipeline recorded in the B3 review.

The broader ClickHouse suite initially passed 147 cases and failed one session-backfill case.
Server logs prove a memory-limit failure after some backfill slices had committed; the existing
migrator then left partial aggregates routable. A later isolated pass does not clear the failure.
A5 fixes the routing proof separately. Full integration acceptance remains open until that fix
and the full rerun pass. Persisted delivery identity, crash-consistent rotation/directory metadata,
process-kill qualification and final matched-load measurements remain aggregate A/B gates.

Archy preflight remains unavailable because no layer rule is configured. Existing executable
style, dependency and domain checks pass in the unit suite. Main and the unrelated unfinished
rebase remain untouched.
