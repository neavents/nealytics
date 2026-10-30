# C1 named metrics qualification

Qualified production and test source: `e3736973421fef935298ad5a5d6ff3c1a0da8179`.
Date: 2026-10-30. Scope: [C1 brief](c1-named-metrics.md).

C1 is functionally complete: bounded immutable declarations, authenticated discovery and
execution, project/tenant isolation, unit checks, exact numeric strings and UTC millisecond
half-open ranges. No ingestion contract, backend default or main-branch change is included.
The [operator guide](../named-metrics.md) describes configuration and API behavior.

## Passed checks

| Check | Evidence |
| --- | --- |
| Solution build | Zero warnings and errors |
| Full portable unit suite | 2,216 passed, zero skipped |
| Full applicable integration suite | 207 passed, zero skipped, isolated pinned ClickHouse 25.3.14.14 |
| Combined changed-source coverage | Zero uncovered changed maintained lines or branch outcomes against A/B documentation snapshot `e51d07e` |
| Complete-level C1 mutation scope | 233 executable mutants assertion-killed; zero survivors, uncovered mutants or timeouts; score and thresholds 100% |
| Published Linux ARM64 executable | Packaged native io_uring/custom ingestion; unchanged track/beacon requests; named count and exact integer sum; authenticated schema; unauthorized rejection; clean shutdown |
| Feature CI | [Run 41052370931](https://github.com/neavents/nealytics/actions/runs/41052370931) passed build, unit, integration, helper correctness and published smoke jobs; image publishing skipped |

The standalone ARM64 binary has SHA-256
`9f6c1b02508f4cabd84d3133677be4ae12cb523e90646daf01dacf7cb4cf57bc`.
Its exact-integer smoke result is `9007199254740995`, beyond binary64's exact-integer range.
The tests assert values; no throughput, latency, memory profiling or other performance
measurement was run. The owner's measurement stop remains in effect.

## Coverage and mutation boundaries

Same-source unit and integration OpenCover points are unioned by maintained file, method,
line and branch identity. Generated/obj source is excluded explicitly. The five new maintained
files have complete coverage: 242/242 lines and 156/156 recorded branch outcomes. Changed
lines and branches in existing files are also covered. The generated minimal-API handler has
no maintained-source branch points in the collector; HTTP assertions and mutation cases
separately exercise its success and rejection paths.

Whole maintained-engine coverage is **10,202/10,784 lines (94.60%)** and **3,160/3,551 branch
outcomes (88.99%)**. The remaining 582 lines and 391 branch outcomes belong to inherited scope;
this is not a repository-wide 100% claim. Existing schema, startup and query files retain gaps
outside C1's changes. Actual native coverage from A/B is separate from these portable collectors.

The scoped mutation report additionally contains 82 compile-error mutants and 509 filtered or
structural ignored entries; neither group is counted as an executable assertion kill. Earlier
runs exposed theory-data discovery gaps and equivalent null-fallback/regex-option changes.
Serializable theory cases, explicit null validation and removal of a redundant regex flag
resolved them without lowering thresholds or suppressing maintained source. The final report
has no surviving, uncovered or timed-out executable mutants.

Raw local evidence is retained under ignored
`artifacts/verification/c1-functional-time-qualified-20261030/` and
`artifacts/verification/c1-metrics-time-qualified-20261030/`. The coverage collector identities
are `e3eeef15-77ae-44f9-89f4-f625470286c7` (unit) and
`796156e1-e833-4ac8-9bdf-2c86fc28a26f` (integration). Older evidence folders retain their
original source identities and are not mixed into this qualification.

## Findings and limits

Real-server tests caught empty widened sums returning null and driver timestamp inference
reducing values to seconds/the legacy DateTime calendar. C1 now returns zero for empty sums
and explicitly binds DateTime64-compatible timestamps. Tests cover 1900/2299 boundaries and a
four-millisecond interval. The [timestamp review](c1-time-boundary-review.md) records the
inherited legacy-endpoint limitation as a separate compatibility package.

Native backend code is unchanged from the qualified A/B milestone. The new packaged feature
passed ARM64 native/custom smoke and ordinary CI artifact smoke; this does not claim a new
all-platform native fault/sanitizer run for C1.

Architecture/style/domain executable guards passed in the unit suite. Archy preflight remains
unavailable because no layer rules are configured. `archy verify` was attempted and returned
an uninitialized-workspace error. Archy verification is not reported as passed.

Formulas, reusable segments, reports, project overrides, hybrid plans, caching and hosted
services remain outside C1. Definitions activate at restart and introduce no DDL or per-event
ingestion work. Removing a definition removes its endpoint name without rewriting stored data.
