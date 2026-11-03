# E3 source mapping verification

Date: 2026-11-04. Scope: optional compiled JSON field mapping shared by Kafka and project-pinned
NDJSON. [Package brief](e3-source-mapping.md), [configuration guide](../source-mapping.md).

Base: `0f092b69356733c1adfa34ca8d7fdfaade39e4a6`. Runtime source: `64155ad16c36855face399c9b611262a8ae933b0`.
Final assertion/mutation source: `f41e0f4c0608f126bb9e638e5bc92f0b296e9fb0`. All 223 maintained C# files
are byte-identical between these revisions. The maintained-source manifest SHA-256 is
`619555dcbf44b606b577268e588b65801038073fd8f1f7bc93706954941aab5c`.

| Gate | Result |
| --- | --- |
| Solution build | Passed with zero warnings/errors |
| Full unit suite | 3,210 passed, zero failed/skipped at final test source; downloaded CI TRX retained |
| Focused mapping cases | 89 passed: compilation, limits, paths, defaults, scalar conversions, snapshots, policies, project selection, receipts and Unicode failures |
| Full real-dependency integration suite | 305 passed, zero failed/skipped; mapped/canonical Kafka and NDJSON on managed/custom paths |
| Changed maintained C# coverage | 191/191 executable lines and 150/150 recorded branch outcomes |
| Maintained engine coverage | 12,853/13,375 lines (96.10%); 4,749/5,124 branches (92.68%) |
| Complete changed-logic mutation gate | 232/232 executable mutants killed; zero survivors, uncovered mutants or timeouts |
| Compile-invalid mutations | 22 in selected locations, reported separately; no changed method discarded by safe mode |
| Existing mutation responsibilities | All 87 base ranged responsibilities retained: 85 identical fragments, two NDJSON registration/options spans expanded to include explicit E3 changes |
| Published actual Linux x64/ARM64 | Canonical and mapped Kafka/NDJSON, io_uring/custom, persistence, query visibility, restart, poison-prefix handling and receipts passed |
| Existing compatibility | Track/beacon contracts, backend defaults, project schemas, storage/WAL formats and ordinary executable smoke retained |
| Benchmarks/profiling | Stopped; none run, no speed or capacity claim |
| Archy | Unavailable: preflight requires configured layer rules; worktree verify reports an uninitialized workspace |

## Coverage provenance

The final-source local unit collector covers all changed maintained points. The earlier full
integration collector differs in SourceField.cs and SourceRecordMapping.cs: review subsequently
added safe Unicode rejection and explicit local initialization for reliable mutation compilation.
Those two files' older integration coverage is excluded. Portable-PDB source checksums confirm
that the other 221 measured C# files match the final source byte-for-byte. Current-source unit
coverage supplies both excluded files. No maintained source exclusion was introduced.

The remaining 522 uncovered lines and 375 branch outcomes are outside changed scope. Differences
from the previous milestone's branch-hit totals occur in unchanged beacon and legacy query code;
this ledger reports this run's actual totals rather than carrying forward historical hits.

## Review and mutation findings

Mapping directly materializes the existing envelope without a rewritten JSON buffer. Paths,
defaults, field counts, project mappings and total mapped value bytes have explicit caps. Startup
copies declarations; each record owns its value dictionaries. Normal gate/sanitizer processing
remains after mapping. NDJSON selects by authenticated pinned project and counts original decoded
input bytes. Kafka retains durable handoff before offset commit and its at-least-once contract.

An invalid UTF-8 or unpaired escaped surrogate can make JsonElement string decoding throw outside
the ordinary JSON parse error. Mapping now converts that failure to a safe fixed diagnostic;
NDJSON returns its accepted-prefix invalid_mapping receipt. Tests exercise real HTTP rejection,
raw invalid UTF-8, pooled fragmented input, source/project isolation and WAL contents.

The first full unit qualification overlapped a mutation build using shared test output, invalidating
HTTP startup files. It was discarded and rerun separately. A separate inherited replay test raced
shutdown against sealed-WAL cleanup after its writer callback; it now waits for cleanup before
requesting shutdown. Runtime recovery behavior was not changed. The isolated full rerun passed.

The changed-logic Complete mutation configuration includes all mapping files, the changed Kafka
parser, and explicit changed ranges in existing adapters/configuration/startup. Prior full package
configurations remain available and retain their responsibilities. This avoids repeating all
unchanged connector/framing mutants for every mapping change. The final report also contains 33
duplicate nested mutations removed by Stryker's block-covered filter and 29 compile-invalid
locations outside selected ranges; these are not counted as killed executable mutants.

Three initial survivors exposed missing assertions for absent mapped projectId, tenantId and
eventType. New cases establish the empty-string fallbacks before normal ingestion policy checks.
The final Complete run kills all 232 executable mutants. Thresholds remain 100%; no mutant was
suppressed or declared equivalent. Safe-mode removals in unrelated existing methods are outside
the changed responsibility.

## Published evidence and boundaries

[Runtime source CI](https://github.com/neavents/nealytics/actions/runs/41735328595) passed build,
unit, real-dependency integration, ordinary executable smoke and pure harness-helper checks.
[Final test-source actual-architecture checks](https://github.com/neavents/nealytics/actions/runs/41736073873)
passed on ubuntu-24.04 and ubuntu-24.04-arm without emulation. Each builds one self-contained
image and checks canonical plus mapped Kafka and NDJSON, including io_uring/custom execution.
The final unit TRX is from [final test-source CI](https://github.com/neavents/nealytics/actions/runs/41736073830).

Raw evidence is under artifacts/verification/e3-source-mapping: source manifests, PDB checksums,
unit/integration TRX and collectors, source-aware coverage audit, preserved scope fragments,
published ARM64 logs and mutation-v4/reports. Earlier interrupted or unsuccessful runs retain
their own output directories and do not contribute a passing mutation result.

The package adds no user-defined transformation scripts, custom date formats, seq mapping, automatic retries/deduplication,
durable import jobs, additional sources/formats/sinks or hosted services. Missing receipts and
uncertain Kafka commits retain their prior reconciliation requirements. Removing a mapping
restores canonical input; acknowledged WAL envelopes are never remapped. Main remains
`cc8623a7d5f01fbe9834a493cc98e623bf03059e`.
