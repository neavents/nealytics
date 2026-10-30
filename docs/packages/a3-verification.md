# A3 verification and mutation review

Scope: shared WAL admission, byte/record budgets, serialization, grouping, file accounting,
shutdown and managed file I/O. Native code and the unchanged sealed recovery reader are separate
qualification scopes. This is not a completion claim for categories A/B.

## Final shared-core checks

| Check | Result |
| --- | --- |
| Solution build | Zero warnings and errors |
| Unit suite | 1,433 passed; zero skips |
| Real ClickHouse integration suite | 148 passed; zero skips on 25.3.14.14 |
| Changed maintained shared-WAL collector coverage | 100% lines and branches |
| Complete-level Stryker run | 174 killed; 54 timeout detections; zero survivors; score 100% |
| Archy preflight | Unavailable: no configured layer rule |
| Archy CLI verify | Unavailable: feature worktree is not initialized |
| Existing architecture, style and domain guards | Passed in the unit suite |

The final mutation report is the local generated artifact
`tests/Nealytics.Engine.Tests.Unit/StrykerOutput/2026-10-29.01-42-44/reports/mutation-report.json`.
Reports are ignored build artifacts. Reproduce with the pinned local tool and the committed WAL
configuration from the unit project directory. The test filter includes the WAL fixture classes;
the complete HTTP and database suites run separately.

## What the mutation review changed

The first focused run scored 82.46% with 40 survivors. The next scored 98.22% with four survivors.
No threshold was reduced and no maintained WAL implementation was excluded to obtain the final
score. Strengthening the checks led to these concrete changes:

- A deterministic multi-waiter test proves one shared admission notification wakes every waiter.
- Exact pool substitutes verify writable capacity, bounded growth, clear-on-return and return of
  every serializer loan on resize and shutdown. Cancellation and reserved-producer races check
  that no buffer can enter an abandoned queue or be rented after a terminal admission failure.
- Exact-fit groups, small initial allocation and single-reader/writer options have explicit
  configuration checks. Flush statistics have deterministic tests for counts and time units.
- I/O failure is recorded while the file lock is held. Queued commits, truncations and flushes
  therefore cannot start a storage operation after a preceding operation failed.
- Shutdown joins both the reader and its active resource users. Tests verify disposal of
  transferred synchronization resources, and the generated JSON writer's fast settings are an
  explicit encoding contract.

## Timeout investigation

Stryker counts a timed-out mutated run as detected. That is distinct from an assertion kill.
The unmutated unit/integration runs completed without test timeouts. All 54 final timeout
mutants were inspected in these groups:

| File | Count | Broken contract |
| --- | ---: | --- |
| WalAdmission.cs | 18 | Cancellation/progress checks, reservation accounting or capacity-change notifications removed/inverted; producers spin or remain parked |
| WalOperationGate.cs | 8 | Operation entry/exit, stopped-state checks or idle completion corrupted; shutdown cannot establish that users have left |
| WriteAheadLogger.Append.cs | 19 | Serialization unlock, queue ownership, reader progress, group completion, budget return or append result removed/inverted; work or cleanup cannot finish |
| WriteAheadLogger.cs | 9 | File unlock, failure propagation or shutdown stop/close operations removed; waiting operations and drain cannot finish |

Some mutations fail a progress assertion and leave a dependent fixture cleanup waiting; others
directly create a nonterminating loop or strand a completion. They demonstrate failure of the
mutated contract, not proof that every detection was an assertion failure or a physical disk
hang. The per-mutant source locations and replacements remain in the report for audit.
Eight target-scope mutants were rejected by compilation and 268 were filtered by Stryker's
structural/scope filters. They are not counted as executable detections.

## Remaining aggregate work

Repeat published-artifact, crash and performance qualification after native integration and the
custom dispatcher. Streaming recovery, crash-consistent segment metadata, stable database replay
identity and workload capacity isolation remain distinct A requirements. Existing JSON WAL
framing does not by itself provide exactly-once ClickHouse rollups.
