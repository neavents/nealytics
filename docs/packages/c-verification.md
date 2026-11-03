# Category C qualification ledger

Date: 2026-11-03. Selected scope: [C completion contract](c-completion-plan.md).
The selected C1–C8 scope is implemented and functionally qualified. All C4–C8 managed/storage
mutation gates pass; category integration is confined to `feat/analytics-customization`.
Separate step branches remain available. Main and the original unfinished rebase are untouched.

## Implemented contracts

| Step | Delivered behavior | Step source |
| --- | --- | --- |
| C1–C3 | Named metrics, reusable event segments and scalar saved reports | Qualified prerequisite `ad2d943`; retained [C1](c1-verification.md), [C2](c2-verification.md), [C3](c3-verification.md) evidence |
| C4 | Bounded typed formulas, dependency/unit validation, exact rational finalization and deduplicated projections | `023e02a` |
| C5 | Typed declared parameters, bounded grouped reports and UTC hour/day time series | `1bd49f6` |
| C6 | Deterministic read-time mapping, normalization, substring and numeric buckets | `50e8107` |
| C7 | Authenticated logical project catalogs, compatible bounded physical union and project-specific ingestion constraints | `2bdc02d` |
| C8 | Explicit accuracy contracts, opt-in raw/rollup/hybrid state plans and administrative explanations | Runtime `747b348`; final routing/row assertions `0b946ba` |

The [operator guide](../analytics-customization.md) defines activation, numerical/null semantics,
limits, authorization, raw fallback, migration and rollback preconditions. Declarations activate
at restart. The existing executable and ClickHouse dependency remain the deployment model.

## Functional milestone

The frozen runtime source `747b34826ae358fa784ec2d4c8f356c2cffacae7` passed 2,869 unit cases and
296 pinned-server integration cases, with zero failures or skips. The integration run includes
real database migrations, unchanged ingestion endpoints, durability/replay, project isolation,
computed/grouped/filter results and raw/state-reference parity. The integration collector is
`a90fb624-c9ae-46fb-90e7-bc11b4233ac6`; the unit collector is
`5ff6a50b-66dc-4cf6-8688-7eb5c1932e1b`.

The final warning-free build and all **2,874 unit cases** passed at `db1bd02`, including five
additional C7 HTTP cases for pinned-project tenant-attribute authorization and logical allowlists.
The fresh full integration run at `0b946ba` passed **297 cases**, including the routing check,
with zero failures or skips. Its collector is `5fc437c3-b65e-4941-b115-e11a64b7a617`.
The production source tree is unchanged from `747b348`. The final unit collector is
`abc3b091-4a69-4b57-90d7-6b946307a125`. All 24 C8 database cases also passed using the shared
schema fixture and added routing/row assertions through `0b946ba`; each case still has its own
project/data and all existing assertions. The fixture shares initialization, not query results
or mutable project identity. Tests restore readiness fences before another case executes.

CI [41628828094](https://github.com/neavents/nealytics/actions/runs/41628828094) passed build,
unit, integration, pure helper correctness and published smoke at `747b348`. Image publication
was skipped on the feature branch. The latest code/test source `0b946ba` also passed all these
jobs in [41632675874](https://github.com/neavents/nealytics/actions/runs/41632675874).
No benchmark workloads were invoked.

An earlier full integration run at `63eddfe` had 295 passing cases and one Docker container
cleanup error, without a product assertion failure. The isolated same-source retry passed, and
the fresh 296- and 297-case runs above passed without that infrastructure failure.

## Coverage

Final `db1bd02` unit and `0b946ba` integration OpenCover collectors over the identical maintained
production source tree are combined by maintained engine file, method and sequence/branch identity. Generated sources under `obj` are excluded; maintained
production code is not excluded. Against the C3 prerequisite `ad2d943`, every changed maintained
sequence point and recorded branch outcome is covered. This is a changed-code claim, not a
repository-wide 100% claim. The changed audit contains 1,748/1,748 covered sequence
points and 1,143/1,143 covered branch outcomes.

Repository-wide maintained engine C# coverage at `747b348` is **12,250/12,772 lines (95.91%)**
and **4,399/4,764 branch outcomes (92.34%)**. The inherited gaps include older startup paths,
migration/rollup cases, external JWKS failures, legacy analytics request/query paths and native
platform-specific paths in the portable collector. Historical A/B Linux qualification covers
its stated native scope separately; the portable union does not certify every native path.

The machine-readable combined audit is retained locally under
`artifacts/verification/c-category-final-20261103/combined-coverage.json`.

## Mutation gates

Complete-level Stryker scopes retain 100/100/100 thresholds. Counts below distinguish assertion
kills from timeout detections, compile errors and filtered/structural ignored mutants.
No passing run has surviving or uncovered executable mutants.

| Scope / immutable source | Killed | Timeout | Compile error | Ignored | Detection |
| --- | ---: | ---: | ---: | ---: | ---: |
| C4 managed / `023e02a` | 638 | 0 | 71 | 298 | 100% |
| C4 storage / `9ac9555` | 33 | 0 | 7 | 206 | 100% |
| C5 managed / `1bd49f6` | 446 | 0 | 68 | 280 | 100% |
| C5 storage / `1bd49f6` | 66 | 0 | 9 | 210 | 100% |
| C6 managed / `50e8107` | 624 | 2 | 107 | 360 | 100% |
| C6 storage / `d549675` | 35 | 0 | 11 | 275 | 100% |
| C7 managed / `23166e3` | 244 | 6 | 235 | 991 | 100% |
| C7 storage / `c61a3f3` | 5 | 0 | 5 | 300 | 100% |
| C8 managed / `747b348` | 478 | 0 | 50 | 664 | 100% |
| C8 storage / `0b946ba` | 268 | 0 | 88 | 284 | 100% |

C6 timeouts remove cycle/depth protection and cause nontermination. C7 timeouts mutate batch
loop bounds/progress or remove failure propagation, causing nontermination in the durable
batch dispatcher. They are defect detections, not assertion kills. Earlier survivors drove
additional assertions and refactoring of redundant/equivalent expressions; no threshold or
maintained-source scope was reduced to obtain a pass.

Storage test filters retain relevant unit SQL/contract assertions plus real dependency cases.
C8 uses all 24 planning integration cases with one shared class schema fixture and isolated
per-case project data; it no longer repeatedly runs 87 unrelated legacy named-metric database
cases for every mutant. Those legacy cases still run in the full integration milestone.
The completed C8 storage diagnostic at `9770a42` killed 262 mutants and left six survivors:
two could falsely route selected state plans through raw SQL, and four could skip/erase grouped
rows or group metadata while weak parity assertions still agreed. Added checks verify populated
row/group/field metadata and reference aggregate totals. A routing failure-path case temporarily
renames the raw table, verifies fully summarized metric/report reads, then restores it in a
finally block. All 24 C8 cases pass these assertions, and the final unchanged Complete storage
scope kills all 268 executable mutants. Earlier response-shape survivors similarly
drove explicit scalar/table/time-series assertions. No product behavior was weakened to fit tests.
Interrupted diagnostic runs are retained as interrupted and are not qualification evidence.

Stryker compiles the full engine before filtering. Its inherited Safe Mode rollbacks in
`WalDirectory.Generation` and `PivotRequestFactory.Create` are outside these selected changed
scopes. No blanket rollback of changed scoped logic is counted as passing evidence. Source
snapshots, raw reports, logs and source identities are retained in the corresponding local
`artifacts/verification/c4-*` through `c8-*` directories; these generated artifacts are ignored
by Git. Later package changes to shared code receive their own scopes and milestone regressions.

## Published executable and platform boundary

The fresh self-contained Linux ARM64 executable at `db1bd02` passed functional C1–C8 checks
with packaged **io_uring plus custom ingestion**. It ran without an SDK, installed .NET runtime,
separate liburing installation or helper service. Checks include unchanged track 202/beacon 204,
two authenticated catalogs over one physical union, exact widened integer sums above binary64
integer precision, formulas, grouped computed dimensions, hourly hybrid reports, raw parity,
typed report parameters, segments, administrative explain, source-generated discovery,
unauthorized read rejection and clean shutdown.

Artifact SHA-256: `1d83ba10d2384a1fe7be1ba7021900548d14bbb8648ff758617c713d4902e6e1`.
The local artifact and summary are under `/tmp/nealytics-c-qualified-published-smoke`;
the summary is also retained in `artifacts/verification/c-category-final-20261103`.

C does not modify the native bridge. The fresh native execution above is ARM64 evidence;
it is not a new x64 native-boundary qualification. Existing A/B actual x64/ARM64 qualification
remains source-specific historical evidence. General x64 CI runs the current managed application.

## Architecture and delivery limits

Executable architecture, source-style and domain-neutrality guards run in the unit suite.
Archy preflight is unavailable because no layer rules are configured. `archy verify` reports
that this managed worktree is uninitialized. Neither result is recorded as a pass, and no
arbitrary architecture configuration was introduced merely to silence the tool.

Planning defaults remain disabled. Rollup selection additionally requires the operator's
append-only source assertion, governed exclusive delivery, unique ordering keys, verified
readiness/rebuilds, replay retention and compatible raw-retention bounds. Raw execution is the
reference fallback. Exact accuracy names the algorithm; it does not promise arbitrary floating
precision, UUID deduplication or a transactionally consistent snapshot across concurrent writes.

Benchmarks, profiling, throughput/latency measurements and default-backend changes remain
stopped. This qualification makes no speed, capacity or p95/p99 improvement claim. Arbitrary
scripts/SQL, historical cohorts, external enrichment, automatic rollup creation, result caching,
live definition deployment/CRUD and hosting remain explicitly deferred in the selected contract.
