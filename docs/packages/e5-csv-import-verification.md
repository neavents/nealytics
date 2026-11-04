# E5 CSV import qualification ledger

Status: bounded runtime implementation functionally qualified. This is not completion of category E
or the remaining roadmap. Step: feat/integrations-e5-csv-import. Base:
8ace7376a5fe052adb730b3ecc2823bd0ca109f4, a documentation-only step base on qualified E4 merge 04bd594.
Runtime source: 8cadb8992964620718d1588720a44295770c138e. Test/filter refinements:
511ae904ebd6332f19e9eb32fbeebcaccc1779af. All 250 maintained engine source files are byte-identical
between those revisions; the refinements add pinned-project assertions, finite-reader progress
checks and the shared mapping test filter.

## Delivered behavior

Optional CSV-only or shared CSV/NDJSON hosts import strict UTF-8 headered records through existing
policy, sanitization and durable dispatch. Quote state persists across pipe segments; framing
records field boundaries outside quotes and decoding reuses them without a second quote scanner
or intermediate JSON row. Literal column bindings share existing mapping rules through constrained
value-reader structs. A cleared bounded raw-record buffer, 256-column cap, frozen header index and
shared queue-free import limiter bound request resources.

Receipts count data records and original decoded source bytes including header/BOM/separators.
Quoted newlines remain inside one record. Confirmed accepted-prefix receipts do not provide source
deduplication or durable CSV checkpoints. D1 and S3 remain NDJSON-only. See the
[brief](e5-csv-import.md) and [operator contract](../csv-import.md).

## Consolidated evidence

| Gate | Result |
| --- | --- |
| Full local unit suite | 3,723 passed, zero skipped; source/style/domain/architecture guards included |
| Clean Linux CI unit suite | 3,721 passed; one later local project-isolation assertion and one ignored-file text guard explain the count difference |
| Full real-dependency integration suite | 315 passed locally and in CI, zero skipped |
| Changed maintained production coverage | 329/329 executable lines, 176/176 branch outcomes |
| Complete Stryker.NET 5.0.0, CSV/shared mapping scope | 100.00%; 384/384 executable mutants killed, no survivors/uncovered mutants/timeouts |
| Complete shared NDJSON/import-startup scope | 100.00%; 154/154 executable mutants killed, no survivors/uncovered mutants/timeouts |
| Actual published Linux x64/ARM64 | All 14 summaries passed on actual x86_64/aarch64 hosts with io_uring/custom |
| Remote build, smoke and pure helper checks | Passed; warning-free final builds |
| PDB source audit | Both final collectors match maintained source; no mismatched-source exclusions |
| Archy | Unavailable: no configured layer rule and uninitialized workspace; executable guards pass separately |

Two real ClickHouse/Kestrel cases exercise managed/custom ingestion, quoted multiline UTF-8,
quoted header names, BOM/source offsets, accepted-prefix rejection, restart on the same WAL and
exactly two stored rows/sum six. Unit and HTTP cases additionally cover malformed grammar/UTF-8,
all framing states, segmented/backpressured input, exact bounds, headers/configuration, policy,
serialized sanitization, compression, body features, mapping text budgets, canceled reads,
caller disconnect, application shutdown, write/flush faults and pooled/reader ownership.

A blocked CSV request holds the shared import permit: CSV and NDJSON reject extra imports without
queueing, ordinary tracking still succeeds, and completion releases the permit. Server keys remain
project-pinned even when a mapped column supplies a foreign project. Existing track/beacon and
Kafka/NDJSON/S3 contracts stay unchanged.

Published CSV checks use the single-file io_uring/custom engine with CSV enabled independently of
NDJSON. They exercise chunked/BOM/multiline inputs, literal header bindings, compression, final
unterminated rows, exact record/body limits, rejected prefixes, header-only input, SIGKILL/restart,
exact sum nine and payload-log redaction. Each architecture also retains all six canonical/mapped
Kafka/NDJSON/S3 regression checks. All summaries identify the runtime revision and disabled
measurements. This is functional evidence, not a speed or capacity result.

Final-source remote evidence:

- [Build, unit, integration, smoke and pure helper CI](https://github.com/neavents/nealytics/actions/runs/41808447425).
- [Actual x64/ARM64 published connector qualification](https://github.com/neavents/nealytics/actions/runs/41808449027).

## Coverage and mutation audit

Combined maintained-engine coverage is 14,190/14,712 executable lines (96.45%) and 5,556/5,931 branch
outcomes (93.68%). The remaining 522 lines and 375 branches are inherited gaps, unchanged in count
from E4, not CSV gaps. Generated code is excluded explicitly. Portable-PDB document checksums
match current maintained source before combining collectors; runtime/test revision equivalence is
recorded separately. Local/remote per-method counts otherwise match, apart from the two listed
local additions; the ignored-file guard discovers StrykerOutput/.gitignore.

All 115 previous ranged responsibilities remain: 114 source fragments are identical and relocate
as needed; one NDJSON startup block splits into shared limits/limiter and its unchanged NDJSON
registrations. Mapping/import filters include CSV, and the import filter also includes source
mapping tests so moved registration and invalid-mapping responses have their proper assertions.
The CSV report separately records 33 compile-invalid mutants and 63 block-covered duplicates;
the shared import report records 34 compile-invalid mutants and 28 block-covered duplicates.
The scopes overlap; their mutant counts are separate gates, not a unique summed total.

Initial CSV mutation scored 95.82% (387 killed, 17 survivors and three detected timeouts among 407
executable mutants). It drove independent assertions for byte-level BOM preservation, per-record
state, name bounds, canceled-read progress, body/source ownership and serialized sanitization.
One framing state machine now records field boundaries, removing redundant quote bounds and the
second scanner's mutated nonprogress loop. Every valid first record already resolves preamble
state, so resetting it again is unnecessary. No maintained responsibility or threshold was reduced.

The first shared pass had one uncovered invalid-mapping response and a surviving mapping-service
registration removal because its older filter omitted mapping tests. Including those tests removed
both gaps. Four malformed NDJSON loop mutants were initially detected by timeout: three alter
framing/EOF conditions and one removes AdvanceTo. The finite input helper now enforces progress
and completed-input ownership; the final shared run kills all four without timeout. The unmodified
code passes all suites with that stricter helper.

## Provenance and limits

Raw evidence: artifacts/verification/e5-csv-import, including TRX/OpenCover, PDB/source checksums,
coverage-audit.json, both mutation audits/reports, preserved scopes, source equivalence,
unit-count audit, remote CI logs and all 14 published summaries.

Preliminary focused collectors and the first uncommitted ARM64 image predate the final framing
refactor; they remain separate from final evidence. The first published-script attempt failed
because a chunk generator captured the variable reassigned to that generator; retaining source
bytes separately fixes the harness. The corrected preliminary run and final clean-checkout actual
platform runs pass. Neither the failed attempt nor the older candidate is substituted for final
source qualification.

Archy check_violation reports no configured layer rule; archy verify reports an uninitialized
workspace. Both were attempted; neither passed. No new SDK/runtime dependency, DDL/WAL/checkpoint
format change, automatic retry or backend-default change is introduced. Main and the original
unfinished rebase remain untouched. Benchmarks and throughput/latency measurements remain stopped.
CSV durable jobs, S3 CSV, Parquet, further adapters/sinks and hosted services remain separate work.
