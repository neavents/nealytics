# Engineering rules and branch policy

Status: Owner requirements recorded 2026-10-28

Scope: All implementation packages in the [delivery index](work-packages.md).

## 1. North star

Customizability, speed, performance, and a low memory footprint guide design decisions. Build
production-quality code with clear responsibilities and demonstrated behavior. Quality is
established through review, measurements, and failure testing, rather than a company-level label.

The engine must be domain agnostic: no business vertical baked into identifiers, SQL, control
flow, or defaults. Customers supply their vocabulary through typed declarations. Preserve the
same ingestion contract while changing internal dispatch or WAL I/O.

## 2. Architecture and implementation style

- Apply SOLID with cohesive types, narrow contracts, substitutable implementations, and dependency
  direction that matches the existing architecture. An abstraction must solve a concrete coupling,
  substitution, or testability need; do not add interfaces and factories around every pure function.
- Preserve vertical feature slices. Keep transport handling, input validation, domain-neutral
  query planning, and storage responsibilities clear. Infrastructure must not depend on Features.
- Match current source layout: explicit production local types, sealed classes where appropriate,
  small methods, useful names, and consistently formatted multiline code. Avoid one-line method
  chains and giant classes that hide ownership or failure behavior.
- Production source, SQL, and configuration contain no comments, XML doc comments, or comment-key
  substitutes. Explain non-obvious reasoning in [design notes](design-notes.md).
- Test code may use brief comments when they clarify an unusual scenario or assertion. Keep
  explanations short and prefer descriptive test names. Existing test style permits var.
- Keep JSON serialization source generated. Avoid reflection, runtime code generation, unnecessary
  boxing/copying, and incidental per-event allocations on hot paths. Measure before claiming an
  optimization or choosing a less readable implementation for performance.

## 3. Packaging and resource constraints

Ship a single self-contained executable/container with ClickHouse as the existing storage
dependency. Native io_uring support must be packaged within that deployment: do not require
customers to install a separate liburing companion or run an I/O helper daemon. Select an
embedding/linking/extraction strategy and verify the published artifact on a clean supported host.
System/runtime compatibility requirements must be explicit.

Bound queues, admitted request memory, WAL buffers, pinned native memory, ring entries, caches,
and background concurrency. Pair every resource acquisition with defined completion, cancellation,
failure, and disposal ownership. Pooling must not leak tenant data or retain buffers indefinitely.
Evaluate CPU, allocations, working set, visibility lag, and p95/p99 at matched offered load.
No latency gain may come from weakening persistence or changing producer behavior.

## 4. Tests and evidence

Every implementation package includes tests and a second review against its brief and acceptance
criteria. The owner requires extensive verification, not a minimal happy-path test.

| Layer | Required evidence |
| --- | --- |
| Unit and component | Normal, boundary, invalid, empty/null where applicable, policy, and error behavior; isolated deterministic tests for pure logic |
| Integration | Real ClickHouse on the pinned supported version, actual storage operations, migrations, scope isolation, and retries |
| HTTP/end-to-end | Existing requests against real Kestrel and smoke checks against the published executable, including ingestion through persistence and query visibility |
| Durability and concurrency | Cancellation/disconnect, shutdown, restart/replay, disk/write/flush faults, queue saturation, truncation safety, and buffer ownership as applicable |
| Coverage | 100% line and branch coverage targets for new/changed maintained production code; report repository-wide totals and remaining gaps |
| Mutation | Stryker.NET over changed managed logic, with every survivor, uncovered mutant, and timeout investigated; mutation-sensitive assertions for conditions and failure paths |
| Native/platform | Real Linux io_uring execution on supported x64/ARM64, unsupported-host selection, completion/error cases, and native boundary/fault tests |
| Performance | Repeatable baseline and after results with workload, hardware, offered load, durability, memory, errors, p95/p99, and freshness recorded |

Coverage measures execution; assertions must also establish the intended result. Do not create
tests that merely repeat implementation or mock away the dependency under test. Stryker.NET
does not mutate native code, so a managed mutation report cannot certify Linux interop correctness.

Keep generated-source exclusions explicit. Do not exclude maintained code to inflate scores,
weaken thresholds to turn a failing result green, or call mutants equivalent without a documented
reason. Target complete detection of non-equivalent mutants in the changed scope. Report any
unmet coverage/mutation target directly, with the remaining work and reason.

Run a warning-free build, unit and integration suites, published-artifact end-to-end checks,
coverage, mutation checks, and relevant platform/performance checks before declaring an
implementation package complete. Isolate test databases, ports, volumes, and WAL directories;
never let suite cleanup destroy another run or a developer's data.

Existing setup at the time these rules were recorded includes xUnit, Coverlet collectors,
ClickHouse integration tests, source/architecture/domain guards, and published-artifact smoke
checks. Stryker.NET tooling/configuration and enforced coverage gates are not yet configured.
Those need bounded setup work in the verification foundation, including checks on category/step
branches: current CI triggers only on main pushes or pull requests targeting main. This document
does not claim new tooling or gates have been installed or run. Linux-native execution cannot be
certified by a macOS run.

## 5. Branches

Use the following category branches. F reserves a namespace for future work; creating its branch
does not bring hosted services into current scope.

| Category | Branch |
| --- | --- |
| A. Correctness and safety | feat/correctness-and-safety |
| B. Ingestion performance | feat/ingestion-performance |
| C. Analytics customization | feat/analytics-customization |
| D. Developer and operator tools | feat/developer-operator-tools |
| E. Optional integrations | feat/integrations |
| F. Future hosted platform | feat/hosted-platform |

Defined packages have separate sibling branches. Use a hyphenated suffix because a Git branch
named feat/correctness-and-safety cannot also contain child refs beneath that same name.

| Package | Branch |
| --- | --- |
| A1 | feat/correctness-and-safety-a1-latency-baseline |
| A2 | feat/correctness-and-safety-a2-compatibility-and-durability |
| A3 | feat/correctness-and-safety-a3-bounded-wal-admission |
| A4 | feat/correctness-and-safety-a4-streaming-recovery |
| B1 | feat/ingestion-performance-b1-wal-io-boundary |
| B2 | feat/ingestion-performance-b2-native-backend |
| B3 | feat/ingestion-performance-b3-native-wal-integration |
| B4 | feat/ingestion-performance-b4-beacon-microbatches |
| B5 | feat/ingestion-performance-b5-custom-ingestion |
| B6 | feat/ingestion-performance-b6-qualification |

New C-F steps receive branches when their bounded package scope is defined. Do not invent an
implementation sequence for those categories just to populate branch names.

Category and step refs initially share a documentation-only planning commit based on inspected
code snapshot f1cfe3e0c4e69bbbafc6096c02659a68986d0836. The original checkout was in an unfinished
rebase of main, so preparation uses an isolated feature worktree and does not finish, abort,
amend, or otherwise alter that rebase. No implementation package is started by branch creation.

When a step starts, verify its base includes the agreed prerequisite feature work. Branches
reserved in advance are not automatically current. Make prerequisite/base adjustments explicitly
and preserve any existing step commits; do not reset or force-update a branch to hide conflicts.
Keep each package separately reviewable, with its tests and evidence. Integration into a category
branch may be prepared as feature work, but do not merge any category or step into main or rewrite
main until the owner changes that instruction.
