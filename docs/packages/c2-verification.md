# C2 event segments qualification

Production source: `f84e1c2758a2b3b28c091f25061542aa1f675a6d`.
Verification-only follow-up: `eff98197f6ce0b830204492b27e8ea625510012b`.
Final managed verification: `df4bbd55d168d18e1e8cd3a31df12da44bdcbfea`.
Storage mutation verification: `e1ee65d4099ce136fea688d46fed8421a7d99a67`.
Date: 2026-10-30. Scope: [C2 brief](c2-event-segments.md).

Status: functionally qualified. The implemented slice adds bounded immutable
event-segment declarations, authenticated discovery and an optional selection on named metrics.
See the [operator guide](../event-segments.md) for configuration, limits and rollout behavior.

## Functional evidence

| Check | Evidence |
| --- | --- |
| Solution build | Zero warnings or errors |
| Full portable unit suite | 2,283 passed, zero skipped, including the strengthened assertions |
| Full applicable integration suite | 219 passed, zero skipped, isolated ClickHouse 25.3.14.14 |
| Focused real-server slice | 34 named-metric/segment cases passed |
| Changed maintained-source coverage | Zero uncovered changed lines or branch outcomes against prerequisite category snapshot `1ca7153` |
| Published Linux ARM64 executable | Packaged io_uring/custom ingestion; unchanged track/beacon requests; segment selection, caller-filter intersection, empty selection, discovery, exact sum, authentication and clean shutdown |
| Complete-level mutation | 266/266 managed and 38/38 storage mutants assertion-killed; zero survivors, uncovered mutants or timeouts; both scores and thresholds 100% |
| Feature CI | [Run 41095267295](https://github.com/neavents/nealytics/actions/runs/41095267295) at `df4bbd5` passed build, full unit/integration suites, helper correctness and published smoke; image publishing skipped |

Maintained production C#, runtime configuration, integration tests and the smoke script are
unchanged by the verification follow-ups. They strengthen three unit-test files and separate
the managed/storage mutation configurations, with schema-changing checks serialized.
Source identities and exact changed-file lists are recorded in the local evidence manifest.
The real-server, coverage and published-artifact results at the production commit remain
applicable; the full unit and final mutation runs use the strengthened tests.

The published binary SHA-256 is
`58f114ca5e9f0cc1ee3acad609617429d782b6ca43698663b90687fff570edc3`.
The artifact assertions include an exact selected integer sum of `9007199254740993` and
unsegmented sum of `9007199254740995`. This tests numeric encoding and query selection, not
numeric-filter exactness: predicates retain the existing finite Float64 comparison contract.

## Coverage and mutation scope

Same-production-source unit and full integration OpenCover points are unioned by maintained
file, method, line and branch identity; generated/obj source is excluded explicitly. The new
registry has **57/57 lines and 42/42 branch outcomes covered**. The modified request factory
has **82/82 lines and 62/62 branch outcomes covered**. The remaining changed maintained lines
and recorded branches are also covered. Generated minimal-API handlers have no maintained
branch points in the collector; HTTP and mutation assertions exercise their result paths.

Whole maintained-engine coverage is **10,294/10,871 lines (94.69%)** and **3,215/3,601 branch
outcomes (89.28%)**. The remaining 577 lines and 386 outcomes are outside the changed scope.
This is not a repository-wide 100% claim, and portable collectors do not replace native gates.
Collector identities are `62d4efbe-2e46-4ab1-9c8c-97179e52d9ee` (unit) and
`00ae0df4-c3d9-4e24-b680-c572f812d6c2` (integration).

The two C2 configurations include C1 logic and the new declarations, composition, schema and
startup paths. A normalized mutation-identity audit confirms their union exactly matches the
original 304 executable mutants: zero missing, extra or overlapping cases. No source scope or
threshold was weakened. The reports additionally contain 81 managed/14 storage compile-error
entries and 445/271 ignored context/structural entries; these are not assertion kills.

The initial parallel run reported 99.01%. Follow-up diagnosis found shared-schema races while
fixtures added/dropped columns, causing missing-column failures in cleanup mutations. These
runs are diagnostic history and are not final qualification evidence. Schema-changing checks
now use one worker and `--disable-bail` so batches and cleanup complete normally. Pure logic
uses the unit project; query execution and schema response assignment use unit and real-server
tests in a separate scope. Both configurations retain Complete level and 100% thresholds.

The isolated checks exposed assertion gaps hidden by the combined failures. Tests now establish
the empty catalog's dependency guards, unequal filter counts, authentication forbid hooks,
HTTP end-range binding, fresh-host read-scope registration, DTO defaults, exact supported clock
years and mixed event/tenant-attribute conditions. No production logic changed to close them.

Final raw evidence is under ignored `artifacts/verification/c2-functional-20261030/`,
`artifacts/verification/c2-segments-managed-qualified-20261030/` and
`artifacts/verification/c2-segments-storage-20261030/`. Earlier named C2 folders preserve failed
or interrupted attempts with their own source identities and are not counted as passes.

From `src/Nealytics.Engine`, reproduce the final scopes with:

```sh
dotnet stryker --config-file stryker-config.segments.json --disable-bail
dotnet stryker --config-file stryker-config.segments-storage.json --disable-bail
```

The storage command requires the existing integration connection setting pointed at an isolated
database initialized from the repository SQL. The qualification runner used a fresh pinned
container and removed only its own labelled database after success. Never point fixture cleanup
at developer or production data. The managed scope requires no database.

## Boundaries

All conditions intersect with project, tenant/tenant-set membership, fixed metric event type,
traffic, UTC range and caller filters. Real-server tests cover all six numeric comparisons,
NULL exclusion, literal SQL-shaped values, contradictory conditions, another tenant, another
event type, unit safety and schema serialization. Internal compiled predicates are not exposed.

No ingestion work, DDL, new service, backend default, main integration, hosting or performance
measurement is included. The native backend is unchanged from the qualified A/B milestone;
the ARM64 published smoke verifies this feature with native/custom ingestion, not a new
all-platform native fault/sanitizer qualification. Executable architecture/style/domain guards
pass. Archy preflight lacks configured layer rules, and `archy verify` reports an uninitialized
workspace; Archy is not reported as passed.

Numeric-filter precision, OR composition, nested segments, user/session cohorts, project
overrides, formulas, reports and live editing remain separate packages. Before rolling back to
C1, clients must stop sending `segment`, because C1 ignores that query parameter. Stored data
and existing ingestion callers are unchanged.
