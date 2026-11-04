# D1 checkpoint qualification ledger

Status: bounded D1 scope implemented and functionally qualified. This does not complete category D
or the remaining roadmap. Category: feat/developer-operator-tools.
Step: feat/developer-operator-tools-d1-import-checkpoints.
Base: 1aa755b61961e7c857d8cfd4370bf2880a207c0b (qualified E3).
Qualified maintained C# and test source: 1eec23449e22fb8c28cd09942d34a7b0dd6afe7c.
Later documentation/category integration commits preserve that source.

## Outcome and boundary

Opt-in caller-driven NDJSON chunks have persisted confirmed counters and latest retry receipts.
Synchronized intent precedes admission; atomic synchronized receipt publication follows durable
WAL/handoff. Unconfirmed processing markers recover as needs_reconciliation and reject replay.
Project scope, source/chunk hashes, contract fingerprints, size/count limits and retained history
fence writes. Ordinary ingestion and its default backend stay compatible. See the
[brief](d1-import-checkpoints.md) and [operator guide](../import-jobs.md).

This provides confirmed resume/retry, not automatic recovery of an ambiguous admitted prefix.
Reconciliation resolution, scheduling, staging, source fetching, retention/deletion and general
job APIs remain separate packages. No WAL format, physical schema or mandatory service change.

## Passed evidence

| Gate | Result |
| --- | --- |
| Final local unit/coverage run | 3,372 passed, zero failures/skips |
| Clean Linux CI unit run | 3,371 passed, zero failures/skips |
| Focused component/HTTP cases | 141 passed |
| Final-source real ClickHouse CI | 309 passed, zero failures/skips |
| Changed maintained coverage | 485/485 executable lines, 258/258 branch outcomes |
| Complete managed mutation | 414/414 detected: 413 assertion kills, one investigated timeout; zero survivors/no coverage |
| Build and executable smoke | Passed; zero warnings/errors |
| Published actual Linux x64/ARM64 | Both passed with packaged io_uring and custom ingestion |
| Architecture/style/domain guards | Passed; Archy unavailable |

The one local/CI case difference is the local generated StrykerOutput/.gitignore file included by
the data-driven vocabulary inventory. Mutation reports were preserved outside src before the
final guard run; no maintained file or guard was excluded. The initial post-mutation guard run
correctly objected to full generated reports being inside the source tree. For future runs use:

```sh
cd src/Nealytics.Engine
dotnet stryker --config-file stryker-config.jobs.json \
  --output ../../artifacts/verification/d1-import-checkpoints/stryker
```

Final-source remote evidence:

- [CI build, units, real storage, smoke and pure harness-helper checks](https://github.com/neavents/nealytics/actions/runs/41768070039).
- [Actual x64/ARM64 published connector and checkpoint checks](https://github.com/neavents/nealytics/actions/runs/41768070018).

Each architecture qualifies canonical/mapped Kafka and canonical/mapped NDJSON. Job checks cover
confirmed checkpoint restart, duplicate receipts without additional aggregates, terminal receipts,
prefix rejection and startup fencing of an unconfirmed processing-marker fixture. The fixture is
restored while stopped; it is not an abrupt-crash timing claim. File-level fault tests separately
inject prepare/final open/write/flush/publish/directory-sync failures around real manifest storage.
Tests also assert native lease ownership independently of managed sharing, synchronization order,
read-only recovery, corruption, file/history bounds, cancellation, concurrency and buffer ownership.

## Coverage and mutation audit

The combined maintained-engine collector has 13,338/13,860 executable lines (96.23%) and
5,007/5,382 branch outcomes (93.03%). The remaining 522 lines and 375 branches are inherited gaps,
not D1 gaps. Generated code is excluded explicitly. Portable-PDB document checksums match final
maintained source before combining coverage. An earlier local integration collector predates the
final ImportJobJournal/ImportJobState refinements; those two files are excluded from that collector
and fully covered by the final unit collector. Final-source remote integration independently passes.

All 109 previous ranged mutation responsibilities preserve identical source fragments; 26 offsets
move with Program additions. The new D1 scope includes every new feature file, new registration/
routing logic and option defaults. Nineteen selected compile-invalid mutants and 97 nested
block-covered duplicates are reported separately from the 414 executable mutants. No thresholds
were lowered or maintained responsibilities excluded.

The single timeout negates read.IsCompleted, causing completed memory readers to be reread forever
without a caller cancellation token. Stryker detects this progress failure. The unmodified code
passes an explicit finite-reader case that rejects any additional read after completion, plus
real pipe/Kestrel cases. The first 95.70% run exposed missing assertions; the strengthened final
run has no survivors. Redundant source-length validation and unused expanded encoding scratch
were simplified with the accepted-state/resource reasoning retained in design notes.

## Provenance and limits

Raw evidence: artifacts/verification/d1-import-checkpoints, including TRX/OpenCover, PDB checksums,
coverage-audit.json, mutation-audit.json, preserved scopes and final published summaries. Keep
preliminary/failed runs separate: an old incompatible ClickHouse test volume could not start;
a first fresh launcher used an incorrect log-based readiness condition; both were replaced with
an isolated fresh pinned local server and external-port readiness. Neither failure is a passing
integration result. The corrected local suite passed all 309 cases before the final small refinements.

Archy check_violation reports no configured layer rule. archy verify reports an uninitialized
workspace. Both were attempted; neither passed. Executable guards provide separate evidence.
Main and the original unfinished rebase remain untouched. Benchmarks, capacity/throughput/latency
measurements and automatic backend enablement remain stopped.
