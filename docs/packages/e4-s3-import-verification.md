# E4 S3 import qualification ledger

Status: bounded implementation functionally qualified. This is not
completion of category E or the full roadmap. Step: feat/integrations-e4-s3-import. Base:
c1f3f7c1b74bd9445086ca71f70e4f18778a6cbe, a normal category merge of qualified D1.
Runtime source: 06a7b3f5db5b3958122c7d8d4b98b2ed0ce12fb8. Final fixture revision:
f57054565ceeab715b187bcb36390265ed48081f. Its only changes are three qualification-script readiness
checks; all 243 maintained engine source files are byte-identical to the runtime source.

## Delivered behavior

Optional project-pinned S3 sources inspect and conditionally fetch one object's bounded NDJSON
chunks into D1's checkpoint/WAL path. Exact encoded keys, ETags, version IDs, response lengths/
ranges, source identities, record framing and budgets are checked before record admission.
Confirmed retries refetch the stored byte length; unconfirmed admissions retain D1 reconciliation.
No generic URL fetcher, scan/scheduler, new format, mandatory SDK/service or backend-default change.
See the [brief](e4-s3-import.md) and [operator guide](../s3-import.md).

## Consolidated evidence

| Gate | Result |
| --- | --- |
| Full unit suite | 3,589 local / 3,588 clean-checkout Linux cases passed; source/style/domain/architecture guards included |
| Focused S3 protocol/API/checkpoint suite | 193 passed |
| Full functional integration collector | 313 passed, zero skipped, fresh pinned ClickHouse |
| New/changed maintained production coverage | 527/527 executable lines, 375/375 branch outcomes |
| Complete Stryker.NET 5.0.0 | 100.00%; 518/518 executable mutants killed; zero survivors, uncovered mutants or timeouts |
| PDB source audit | Both collectors match final maintained source; no mismatched-source exclusions |
| Actual published Linux x64/ARM64 | Passed on actual x86_64/aarch64 hosts; all 12 canonical/mapped connector summaries verified |
| Remote CI | Runtime-source and final fixture-revision build/unit/integration/smoke/helper gates passed |
| Archy | Unavailable; no configured layer rule and uninitialized workspace |

The local textual domain guard discovers one additional ignored StrykerOutput/.gitignore file;
per-method/theory case counts otherwise match the clean Linux run. No maintained test is missing.

Four real S3-compatible-service/ClickHouse cases exercise managed/custom and canonical/mapped
inputs. They pin an object version, overwrite the key, restart the engine with persisted progress,
retry a confirmed chunk and verify the original two records/sum six without an extra aggregate.
Unit/HTTP tests cover official AWS signing data and independent short-secret/session-token vectors,
strict URI encoding, real loopback transport, conditional metadata, exact sizes/EOF, cancellation,
deadlines, faults, response/buffer ownership, project policy and real Kestrel/WAL behavior.

Published tests additionally use the packaged io_uring/custom engine, SIGKILL after a confirmed
first receipt, restart, repeated receipts, final original-version totals, changed-source conflicts
and credential/key log redaction. Each architecture also runs canonical/mapped Kafka and NDJSON
regressions. All 12 clean-checkout summaries identify f57054565ceeab715b187bcb36390265ed48081f, report passed and keep measurements disabled.

Commands for the final local gates:

```sh
dotnet test tests/Nealytics.Engine.Tests.Unit --no-restore --collect:'XPlat Code Coverage'
dotnet test tests/Nealytics.Engine.Tests.Integration --no-restore \
  --filter 'Category!=PerformanceMeasurement' --collect:'XPlat Code Coverage'
cd src/Nealytics.Engine
dotnet stryker --config-file stryker-config.s3.json \
  --output ../../artifacts/verification/e4-s3-import/stryker-v3
```

The integration collector uses an isolated fresh pinned server and explicit connection string;
see its retained launcher for exact ports, readiness and cleanup. It requires the test-only S3
fixture described below. Coverage collection includes OpenCover for branch/source auditing.

Remote evidence:

- [Runtime-source build, unit, integration, smoke and pure harness-helper checks](https://github.com/neavents/nealytics/actions/runs/41775470906).
- [Final fixture-revision CI](https://github.com/neavents/nealytics/actions/runs/41776169654).
- [Final actual x64/ARM64 published connector checks](https://github.com/neavents/nealytics/actions/runs/41776169632).

## Coverage and mutation audit

Combined maintained-engine coverage is 13,865/14,387 executable lines (96.37%) and 5,382/5,757
branch outcomes (93.49%). The remaining 522 lines and 375 branches are inherited gaps, unchanged
in count from D1, not E4 gaps. Generated code is excluded explicitly. Portable-PDB document
checksums match maintained source before combining collectors. Runtime-source/fixture-revision
byte equivalence is recorded separately rather than assuming different revisions are equivalent.

All 112 previous ranged mutation responsibilities preserve identical source fragments; 28 offsets
move with S3 registration. The new scope covers every S3 feature file, source options and new
registration/routing/option-default logic. The report separately records 124 compile-invalid
mutants and 85 nested block-covered duplicates. Thresholds and maintained responsibilities remain
unchanged. Initial 95.43% and 99.05% runs drove stronger assertions and equivalent-setting
simplifications; the final run contains no survivors, uncovered mutants or timeouts.

## Fixture provenance and failed attempts

Old upstream container tags were unavailable. Tests build the
[pinned-source S3 fixture](../../tests/Fixtures/S3/README.md) from upstream MinIO commit
9e49d5e7a648f00e26f2246f4dc28e6b07f8c84a, with archive/toolchain/runtime image checksums.
This isolated test dependency adds no engine service, SDK or production build requirement.

The first published run passed ARM64 but failed x64 query visibility. Retained logs show engine
startup schema reconciliation hit connection refused, leaving its declared amount column absent.
The pinned ClickHouse entrypoint temporarily serves initialization queries only on localhost,
then replaces that server. Qualification scripts had queried localhost and could start the engine
in that gap. All three connector probes now query the same network alias used by the engine.
A controlled delayed-initialization reproduction proves local readiness succeeds while network
readiness refuses, followed by network success after final startup. No runtime retry/readiness
behavior or durability contract changed. Superseded intermediate workflow runs are cancelled;
the final clean-checkout run remains the platform gate.

Preliminary 3,577-case units, older focused coverage and local uncommitted ARM64 checks remain
separate from final evidence. The first readiness reproduction failed because its temporary shell
fixture lacked execute permission; the corrected reproduction passed. Failed Stryker JSON-output
configuration attempts are distinct from actual mutation runs; output is a CLI flag.

Raw evidence: artifacts/verification/e4-s3-import, including TRX/OpenCover, PDB checksums,
coverage-audit.json, mutation-audit.json, preserved scopes, source equivalence, readiness reproduction
and remote artifacts/logs. Archy check_violation reports no configured layer rule; archy verify
reports an uninitialized workspace. Both were attempted; neither passed. Main and the original
unfinished rebase remain untouched. Benchmarks, throughput/latency measurements and automatic
backend enablement remain stopped.
