# E1 Kafka verification

Date: 2026-11-04. Scope: one opt-in Kafka topic/group bound to one server-key project, using the
existing ingestion/WAL pipeline. [Operator contract](../kafka-connector.md).

Managed runtime source: `51a7c8bb2600775c782076dfaf9e40963f5fb1de`.
`f0a1e37` adds the managed client's full upstream license to the bundled notices; maintained C#
and tests are identical. Base: `3eaa1b5ce6108738554fbc60dda6299cf8048e17` on `feat/integrations`.
The deferred NDJSON branch is separate and is neither a prerequisite nor qualified by this work.

| Gate | Result |
| --- | --- |
| Solution build | Zero warnings/errors |
| Full unit suite | 2,988 passed, zero failed/skipped at f0a1e37 |
| Focused Kafka cases | 101 passed, including configuration, parsing, native consumer construction, host registration, project isolation, write/flush failures, commit failure and shutdown under backpressure |
| Full real-ClickHouse integration suite | 299 passed, zero failed/skipped at 06ae1e7; includes two actual Kafka restart/partition/poison-prefix cases, one per execution path |
| Changed maintained C# coverage | 226/226 executable lines and 124/124 recorded branch outcomes |
| Maintained engine coverage | 12,476/12,998 lines (95.98%); 4,523/4,888 branches (92.53%) |
| Complete Kafka mutation gate | 179/179 executable mutants killed, zero survivors/uncovered/timeouts; 14 invalid compile mutations reported separately |
| Existing mutation responsibilities | All 82 prior ranged scopes retain identical source fragments after offset rebasing |
| Published actual Linux x64 and ARM64 | Kafka source → durable pipeline → ClickHouse, named metric visibility, committed-offset restart and poison-prefix stop passed with io_uring/custom |
| Benchmarks/profiling | Stopped; none executed and no performance claim |
| Archy | Unavailable: preflight requires configured layer rules; verify reports this worktree uninitialized. No rules/init were invented to turn this into a pass |

The full integration collector predates the final replay-sensitive rollup startup guard. Coverage
combination compares each measured source file with the final file byte-for-byte: the older
KafkaConnectorRegistration.cs collector is excluded, and its current-source unit collector covers
all its lines/branches. Other measured files match. The audit records this exclusion explicitly.
Remaining 522 uncovered lines and 365 branch outcomes are inherited from the qualified foundation;
no new/changed maintained point is uncovered. Generated sources are outside maintained totals.

Earlier mutation runs exposed missing assertions for safe diagnostic messages, a malformed-JSON
catch fallback, and an unbounded failure test. Tests now distinguish format rejection from missing
fields and check processing stages; failure awaits are bounded. Redundant checked arithmetic was
removed after range validation proved overflow impossible; a cancellation check immediately before
an already-safe durable commit was removed rather than pretending it made commit/shutdown atomic.
No threshold or maintained mutation scope was reduced. Compiler-invalid boolean/out-variable
mutations remain separately reported; no changed method was discarded by Stryker safe mode in the
passing run.

[Actual-architecture published jobs at 51a7c8b](https://github.com/neavents/nealytics/actions/runs/41725425067)
ran on ubuntu-24.04 and ubuntu-24.04-arm, without emulation. The local ARM64 binary SHA-256 was
`6981140d6c8477092045b24ad9bda5a9dd689bffc2e563b6aa08b23599b41039`.
The [notices-complete x64/ARM64 artifact run](https://github.com/neavents/nealytics/actions/runs/41725746754)
also passed both actual architectures at f0a1e37; do not assign the earlier hash to those artifacts.
[Full source CI at 51a7c8b](https://github.com/neavents/nealytics/actions/runs/41725424958)
passed every required code job, including integration and ordinary executable smoke.

Raw local evidence: `artifacts/verification/e1-kafka-final-20261104` (TRX, OpenCover/Cobertura,
source-aware coverage audit, build/application logs, preserved scope fragments and milestone
manifest). The full Complete mutation report remains under
`artifacts/verification/e1-kafka-mutation-v4-20261104`; failed earlier reports retain their sources.

The actual broker fixtures are plaintext, two-partition Kafka 4.1.1 and isolated pinned ClickHouse
25.3.14.14. Consumer-group restart and poison-prefix behavior are real dependency checks; forced
late-commit/ownership errors and storage faults are deterministic unit boundaries. These results
do not claim exactly-once delivery, strict process-memory quotas, live TLS/SASL interoperability
for every deployment, cloud hosting or performance superiority. Existing HTTP contracts and
backend defaults stay intact. Main remains `cc8623a7d5f01fbe9834a493cc98e623bf03059e`.
