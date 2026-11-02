# C3 saved scalar reports qualification

Production and test source: `a540b921da9f64d20fc5fed6e71937d7bcee2846`.
Date: 2026-11-03. Scope: [C3 brief](c3-saved-reports.md).
Status: functionally qualified.
See the [operator guide](../saved-reports.md) for configuration and rollout.

This slice adds immutable bounded report declarations, authenticated discovery, and one-query
execution of up to 16 ordered named metrics with one optional shared segment. Each field keeps
its metric's event-type, unit, numeric and null contract. Existing ingestion and named-metric
requests retain their behavior. Reports use the existing read pool and raw event source.

## Functional evidence

| Check | Evidence |
| --- | --- |
| Build | Zero warnings or errors |
| Full portable unit suite | 2,386 passed, zero skipped |
| Full applicable integration suite | 237 passed, zero skipped, isolated ClickHouse 25.3.14.14 |
| Focused metric/segment/report unit checks | 242 passed |
| Focused real-server checks | 52 passed |
| Changed maintained-source coverage | Every recorded changed line and branch outcome covered against prerequisite category `1f70b02` |
| Published Linux ARM64 executable | Packaged io_uring/custom ingestion; unchanged track/beacon calls, reports, shared segments, exact integer results, discovery, authentication and clean shutdown passed |
| Complete-level mutation | 181/181 managed and 46/46 storage executable mutants assertion-killed; both scores and thresholds 100%; zero survivors, uncovered mutants or timeouts |
| Feature CI | [Run 41539039336](https://github.com/neavents/nealytics/actions/runs/41539039336) passed build, unit/integration suites, helper correctness and published smoke; image publishing skipped |

The published binary SHA-256 is
`4d7b27d357cc3bd35c1955ddb0d0331705e393a3fbf87c4a8aa82d1e7b9f0885`.
Reports returned exact integer sums `9007199254740995` and `9007199254740993` through the
published executable. Real-server fixtures additionally returned the widened integer sum
`18446744073709551614`, exact decimal results, explicit nulls for nonfinite/empty values,
and independent results for different per-field event types. These are functional assertions;
numeric predicates retain the existing finite Float64 comparison contract.

## Coverage

Same-source unit and full integration OpenCover points are unioned by maintained file, method,
line and branch identity. Generated and obj source is excluded explicitly.

| New maintained source | Covered lines | Covered branch outcomes |
| --- | --- | --- |
| SavedReportRegistry | 77/77 | 52/52 |
| SavedReportRequestFactory | 55/55 | 40/40 |
| GetSavedReportQuery | 67/67 | 16/16 |
| MetricAggregateSql | 45/45 | 28/28 |
| GetSavedReportEndpoint | 36/36 | No maintained branch points recorded |
| SavedReportRequest and response models | 14/14 | No branch points recorded |

The remaining changed maintained points are covered too. HTTP and mutation assertions exercise
the generated minimal-API handler paths that lack maintained branch points in the collector.
Whole maintained-engine coverage is 10,579/11,156 lines and 3,337/3,723 branch outcomes. The
inherited 577 uncovered lines and 386 outcomes remain outside this change; this is not a
repository-wide 100% claim. Portable collectors do not replace actual-platform native checks.
Collector identities are `a8351403-8ac2-42c3-9919-8767b9be2ad4` (unit) and
`f75805d3-b0ba-44b3-94bf-120cc6f78910` (integration).

## Mutation and boundaries

The managed scope covers declaration validation, request factories, response models, endpoints,
shared aggregate expressions, and the new startup/configuration/discovery properties. Storage
execution and schema assignment use a separate real-server scope. Both use Complete level,
100% thresholds, one worker and early abort disabled. The storage scope also retains the
modified C1 query so the aggregate-builder extraction receives regression mutation coverage.
Existing C1/C2 scopes now include the shared helper and its report assertions. Existing ranged
scopes were rebased by their exact source fragments, preserving their earlier responsibilities.

The reports additionally contain 39 managed/14 storage compile-error entries and 300/277
ignored context/structural entries. These are not assertion kills. No threshold or source scope
was weakened. Runtime source and tests remain unchanged by the documentation and existing-scope
filter follow-ups. Raw reports and source identities are retained under ignored
`artifacts/verification/c3-functional-20261103/`, `c3-reports-managed-20261103/` and
`c3-reports-storage-20261103/`.

Reproduce with pinned tools from `src/Nealytics.Engine`, using an isolated disposable database
and `TelemetryEngine__ClickHouseConnectionString` for the storage scope:

```sh
dotnet stryker --config-file stryker-config.reports.json --disable-bail
dotnet stryker --config-file stryker-config.reports-storage.json --disable-bail
```

No benchmark, throughput, latency or profiling workload was run. No data migration, hosted
service, ingestion default change or main integration is part of C3. Archy preflight remains
unavailable because no layer rule is configured; `archy verify` reports the worktree is not
initialized. Executable architecture, source-style and domain guards passed in the unit suite.
