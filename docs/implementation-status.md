# Categories A and B implementation evidence

Next-track status: C1 named metrics is functionally complete at `e373697`; see its
[qualification](packages/c1-verification.md). The historical A/B counts below describe their
own qualified snapshot. Current C1 suites pass 2,216 unit and 207 integration cases.

Objective: complete correctness/resource safety and ingestion performance, including native Linux
io_uring and custom ingestion, with existing callers unchanged. Main remains untouched.

Current functional source: e3d2599. The agreed A/B functional milestone is complete. The owner stopped all benchmarking and performance
measurement on 2026-10-30; those acceptance requirements and automatic default enablement are
deferred. Current tests and limits are recorded in the [functional qualification](a-b-functional-qualification.md).

| Requirement | Current status | Evidence / boundary |
| --- | --- | --- |
| A1 measurement baseline | Deferred by owner | Historical reports retained; no measurements are run or used for current performance claims |
| A2 compatibility and durability | Functional checks passed | Existing routes, credentials, compression/body limits, durable ACK, partial beacon and disconnect cases; four-mode published restart recovery |
| A3/A3b bounded WAL admission and encoding | Scoped checks passed | Pending bytes/records, flush groups and encoding ownership; full suites and existing 100% mutation gates |
| A4 streaming recovery | Scoped checks passed | Bounded reader/batch ownership, malformed/torn records and restart checks |
| A5 persisted rollup readiness | Scoped checks passed | Persisted readiness, storage identity fencing, raw fallback and real-server failure tests |
| A6 replay delivery | Scoped checks passed | Immutable manifests, stable membership/schema/source/time, explicit deduplication and expiry bounds; unit and real-server mutation scopes pass |
| A7/A7b/A7c capacity and ownership | Scoped checks passed | Separate workload pools, admission/channel/batch/backlog bounds; shutdown/failure cleanup; 100% pool and delivery/admission mutation gates |
| A8 startup segments and metadata | Scoped and actual-platform checks passed | Exclusive directory ownership, immutable recovery generations, durable publication, EINTR/error and descriptor ownership checks; 100% directory mutation gate |
| B1 managed WAL I/O boundary | Scoped checks passed | Shared framing/accounting and explicit durable barriers, failure propagation and physical-file tests |
| B2/B3 native backend/integration | Scoped and actual-platform checks passed | Packaged io_uring bridge, explicit operator selection/fallback and existing producer contract; actual x64/ARM64 qualification passed |
| B4 bounded beacon groups | Scoped checks passed | Valid-prefix/rejection parity, durable grouping and explicit record/byte bounds |
| B5 custom ingestion | Scoped checks passed | Direct bounded body reading and durable dispatch under unchanged HTTP contracts; current source fragments match passing B5 mutation scope |
| B6 combined functional qualification | Functional qualification passed; measurement/enablement deferred | Four-mode published crash/restart, full functional suites and same-source coverage; defaults remain managed |
| Build / unit / integration / artifact | Passed | Warning-free build, 2,110 portable units, 185 applicable pinned-server integrations, four crash/restart combinations; general CI at e3d2599 passed |
| Changed maintained-code coverage | Combined local and actual-platform gates passed | Directory and pool files now have complete line/branch coverage; three Linux-only files are covered by the passing actual native gate |
| Mutation | Scoped checks passed | Delivery/admission, pool, database delivery, directory and custom-ingestion configurations retain 100% thresholds and zero survivors/uncovered mutants |
| Architecture/style/domain guards | Passed; Archy unavailable | Executable guards pass; Archy lacks configured layer rules and an initialized workspace |

The agreed A8 scope is startup sealing and metadata ownership. Live rotation, online rebuild
orchestration and new product/integration/hosting capabilities remain separate future work.
Repository-wide inherited coverage gaps are reported, not described as 100% coverage.

## Historical checkpoints

The source-specific records below are retained for traceability. Their open findings and test
counts describe those older snapshots; the current qualification above supersedes their status.
Historical timing is quarantined by the owner's measurement stop.

Baseline snapshot: engine f1cfe3e0c4e69bbbafc6096c02659a68986d0836; planning commit 90babda.
Environment: macOS ARM64, .NET SDK 10.0.103; Docker provides a Linux ARM64 environment.
An isolated ClickHouse 25.3 container is used rather than touching existing developer services.

Known baseline observations:

- Unit tests passed with coverage collection. Overall collector totals are 9,347/17,363 lines and
  2,340/4,058 branches; these are not a claim about maintained-code-only or combined coverage.
- Integration restore reports vulnerable transitive SSH.NET 2025.1.0 dependencies. Resolve the
  dependency in the verification foundation. A2 now pins patched SSH.NET 2026.0.0 in the test
  project; restore no longer reports those advisories. No audit suppression was added.
- Archy deterministic checks are unavailable because configured layer rules are absent; the
  existing executable architecture/source guards remain required.

A1 evidence recorded 2026-10-29:

- The unchanged real-ClickHouse suite passed 144 cases with zero skips on ClickHouse 25.3.14.14.
- Measurement helper tests passed 61 cases. The four maintained measurement classes reached
  100% collector line and branch coverage. This does not certify the surrounding harness.
- Four additional integration cases passed against real Kestrel and ClickHouse: track and beacon,
  each at fixed-arrival and worker-driven load. They assert exact stored counts, manifest values,
  visibility observations, report output and a successful exit code.
- The helper mutation run detected all 116 executable mutants: 115 assertion failures and one
  timeout. Removing the histogram scan's index increment causes a nonterminating scan when its
  first bucket cannot satisfy the requested rank; the timeout is expected detection of that defect.
  Stryker also reported 51 compile-error mutants and 609 filtered/structural ignored mutants.
  Program.cs is outside this helper mutation scope and remains to be qualified.
- An initial 30-second, 100-request/second track check accepted and stored 3,000 events without
  errors. It was a local functional measurement before final instrumentation, not a performance
  comparison or final reference result. Repeat both modes at the committed revision for B6.

A2 contract fixtures now cover credential precedence, durable track acknowledgment, rejected
beacon elements, malformed streamed tails, accepted prefixes after disconnect and incomplete
track bodies. These run through real Kestrel with the database consumer disabled so the durable
WAL and broker can be asserted directly. Feature branches now trigger CI build, tests and helper
mutation checks; no image is published from a feature branch. Remote CI has not been run yet.

B1 now isolates append, durable flush, truncation and disposal behind an internal file boundary.
Gated tests establish no acknowledgment before the durability barrier and propagation of write,
flush and truncation failures. An instrumented FileStream verifies that the managed barrier waits
for buffered writes and requests a disk flush. Physical-file tests verify byte framing, reopening,
truncation and appending without gaps. Native I/O is not implemented by this extraction.
The warning-free build, all 1,327 unit cases and six real-ClickHouse/HTTP recovery and benchmark
cases passed after extraction, with zero skips. The full integration and published-artifact
qualification will also be repeated after the remaining A/B changes.

A3 component evidence:

- Pending records and framed bytes are reserved before renting a pending buffer. A bounded
  serializer scratch buffer is shared, and each flush group has explicit record/byte limits.
- Write, flush and truncation failures stop admission and fault queued work. Cancellation before
  queueing releases reservations; queued appends await durability. Shutdown and allocation-failure
  fixtures verify exact buffer return and no stranded reservations.
- One hundred focused cases passed, followed by all 1,401 unit cases with zero skips. The collector
  reports 100% line and branch coverage for the changed append/accounting/lifecycle code and new
  WAL helpers. The unchanged recovery reader still has inherited branch gaps. Repository-wide
  unit totals, including generated code, are 9,655/17,636 lines and 2,416/4,124 branches.
- The complete-level mutation run covers the changed WAL files and helpers with thresholds at
  100%. The final run detected all 228 executable mutants: 174 assertion kills, 54 timeout
  detections and zero survivors. The first broad run
  was interrupted after repeated HTTP-host initialization made mutation sessions expensive.
  The rerun selects the six WAL fixture classes while keeping the same mutated files and
  thresholds. Real-HTTP contracts continue to run in the full unit/integration qualification.

Final A3 follow-up checks: a warning-free solution build, 1,433 unit cases and all 148 integration
cases against ClickHouse 25.3.14.14 passed with zero skips. Changed maintained shared-WAL code
has 100% collector line and branch coverage. Raw whole-unit collector totals, including generated
source, are 9,678/17,659 lines and 2,416/4,124 branches; these are not repository-wide maintained
coverage totals. The unchanged recovery reader retains its inherited branch gaps.
See [A3 verification review](packages/a3-verification.md) for mutation/timeout interpretation.
Published-artifact and matched-load qualification remain part of the A/B aggregate gate.

The measurement-helper rerun after adding WAL budget fields detected all 121 executable mutants:
120 assertion kills and the same nonterminating histogram-scan timeout. There were no survivors.

Native preflight experiment, separate from production implementation: Docker's Linux ARM64 kernel
6.12.76-linuxkit accepts io_uring when the isolated test container permits it. A scratch .NET probe
submitted a linked write and fsync, observed completion results 22 and 0, and read the expected
22 bytes back from a temporary file. The default Docker profile returned EPERM at ring creation.
The permitted experiment used a network-disabled, capability-free, 512-MiB/one-CPU container with
seccomp disabled for that one container. Host and existing container policies were untouched.
This is not backend qualification, a crash test, x64 evidence, or a performance result.

B2 now contains the native file backend, statically linked pinned liburing bridge and embedded
native-library packaging configuration. It remains outside live ingestion until B3 selection.
After A3 integration, all 1,494 portable unit cases and thirteen real Linux ARM64 native cases
passed with zero skips. Combined same-source collectors cover all 229 maintained native-boundary
lines and branch outcomes. The native C bridge separately covers all 145 maintained statements
and 112 branch outcomes with clean address, undefined-behavior and leak sanitizer runs.
The native managed mutation rerun detects all 101 executable mutants: 87 assertion kills and
fourteen progress/lifecycle timeout detections, with no survivors or uncovered mutants. The
earlier 84.16% run drove stronger checks; no threshold was reduced. See the
[B2 review](packages/b2-native-backend.md) for timeout interpretation and open platform gates.

B3 current evidence: 1,578 full unit cases and eighteen native file/startup cases passed on actual
Linux ARM64 with zero skips. Nine existing real-Kestrel contract cases passed with io_uring.
Explicit native failure and auto-to-managed fallback passed under Docker's default denial.
Standalone and ordinary-container artifacts ingested track/beacon events, verified ClickHouse
visibility and shut down cleanly, with no SDK, installed .NET runtime or separate liburing.
The maintained startup/native coverage gate passed all 434 executable lines and 88 branch
outcomes. The separate unchanged C bridge gate passed 145 statements and 112 branch outcomes.
The broader mutation checks and final integration rerun are still in progress; remote native
jobs and crash/performance qualification remain open. This does not mark category A or B complete.
