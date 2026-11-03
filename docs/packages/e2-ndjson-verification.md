# E2 NDJSON verification

Date: 2026-11-04. Scope: opt-in streamed NDJSON requests bound to one pinned server-key project,
with durable accepted-prefix receipts, bounded framing/admission and explicit uncertain-retry
semantics. [Operator guide](../ndjson-import.md), [package brief](e2-ndjson-import.md).

Maintained engine source tree: `a595efb6c82d99729a472923913ade0e9edb2dd3`, unchanged from d84b3de
through the final test-only commits. Full unit/test source: 980c6c8; Complete mutation source:
b9a1d60. Base: `5ee510dfd231cd73dfb468f66ee46d3f8e29a475` on feat/integrations.

| Gate | Result |
| --- | --- |
| Solution build | Zero warnings/errors |
| Full unit suite | 3,111 passed, zero failed/skipped |
| Focused import cases | 111 passed: framing, UTF-8, caps, validation, serialization, permissions, project contexts, decompression, rate limits, storage faults, cancellation and ownership |
| Full real-dependency integration suite | 301 passed, zero failed/skipped at 0293ec7; includes managed/custom import-to-ClickHouse visibility and existing Kafka cases |
| Changed maintained C# coverage | 202/202 executable lines and 112/112 recorded branch outcomes |
| Maintained engine coverage | 12,678/13,200 lines (96.05%); 4,635/5,000 branches (92.70%) |
| Complete import mutation gate | 149/149 executable mutants killed; zero survivors/uncovered/timeouts; 28 invalid compile mutations reported separately |
| Existing mutation responsibilities | All 84 prior ranged scopes retain identical source fragments after offset rebasing |
| Published actual Linux x64/ARM64 | io_uring/custom, authentication, streamed UTF-8 prefix receipts, project schema, sanitization, limits, compressed offsets, query visibility and restart checks passed |
| Existing ingestion compatibility | Full suites and ordinary published smoke passed; existing track/beacon routes and backend defaults remain intact |
| Benchmarks/profiling | Stopped; none run and no performance/capacity claim |
| Archy | Unavailable: preflight requires configured layer rules; worktree verify reports uninitialized workspace. No rules/init or success claim invented |

The full integration collector predates removal of one redundant final-empty-buffer AdvanceTo
call. Combination compares each collector's source files with final files byte-for-byte; the older
NdjsonRecordReader.cs coverage is excluded, and its current-source unit collector covers every
maintained point. Other measured files match. Published final-code checks exercise the actual
Kestrel/native path. Remaining 522 uncovered lines and 365 branch outcomes are inherited from the
qualified foundation, with no new/changed uncovered maintained point. Generated source is outside
maintained totals. Repeated rate-limit options construction also covers the compiler's delegate
reuse branch mapped to the new registration line; the branch was investigated, not excluded.

Review found that retaining an incomplete line in a pipe can deadlock against producer pause
thresholds. Framing now transfers partial bytes into one bounded lazily pooled buffer and releases
pipe ownership before waiting for more input. Real Pipe tests use pause thresholds below one record
and assert progress for terminated and final unterminated lines. Pool tests verify clearing/return
on success, malformed/oversized input, cancellation, read/completion faults and early disposal.
No buffering tuning or speed improvement is claimed from these correctness checks.

Earlier mutation runs identified missing metadata/body-feature/buffer-release assertions and
unbounded failure awaits. Tests now check those observable contracts and bound progress failures
with cancellation. A route metadata assertion constructs its own host so startup mutation activation
and assertions share a test lifetime. The redundant final-empty-buffer advance was removed because
finally already completes that reader. The oversize advance remains and is asserted before the
rejection is yielded. No mutation threshold, maintained file or existing responsibility was reduced;
no changed method was discarded by Stryker safe mode in the passing run.

[Final-code actual-architecture jobs at b9a1d60](https://github.com/neavents/nealytics/actions/runs/41729417616)
passed on ubuntu-24.04 and ubuntu-24.04-arm, without emulation. They run both E1 Kafka regression
and E2 import checks against the same self-contained image.
[Full source CI](https://github.com/neavents/nealytics/actions/runs/41729417629) passed build, unit,
integration, ordinary executable smoke and pure harness-helper correctness jobs. The later test-only
commit adds the repeated-options branch assertion; maintained engine source is identical.

Raw local evidence: artifacts/verification/e2-ndjson-final-20261104 contains TRX, OpenCover/Cobertura,
the source-aware coverage audit, logs, source manifest and preserved scope fragments. The Complete
report remains in artifacts/verification/e2-ndjson-mutation-v3-20261104; earlier unsuccessful reports
retain their own source identities. The local initial ARM64 artifact checks were at 71386c8; final
published qualification is identified separately by the linked CI run.

The implementation adds no physical schema/WAL format, durable import job, retry/deduplication
protocol, remote URL access or hosted service. Missing receipts can leave an uncertain durable
prefix; stable event IDs do not establish exactly-once aggregates. Main remains
`cc8623a7d5f01fbe9834a493cc98e623bf03059e`.
