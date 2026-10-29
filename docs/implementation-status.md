# Categories A and B implementation evidence

Objective: complete correctness/resource safety and ingestion performance, including native Linux
io_uring and custom ingestion, with existing callers unchanged. Main remains untouched.

This is an evidence ledger, not a completion claim. A package is complete only after its named
checks, platform requirements, and applicable coverage/mutation gates have passed.

| Requirement | Status | Evidence / remaining work |
| --- | --- | --- |
| A1 reproducible latency, offered load, resource and visibility baseline | Implemented; qualification open | 61 benchmark unit cases and four real-Kestrel/ClickHouse cases passed; sustained final-revision runs and harness-wide changed-line coverage remain |
| A2 HTTP compatibility and durability/failure fixtures | In progress | Nine additional real-Kestrel contract cases passed; deterministic write/flush-fault cases follow the B1 seam |
| A3 bounded pending WAL and flush groups | Shared-core checks passed; aggregate qualification open | 1,433 unit and 148 real-ClickHouse cases passed; changed WAL scope has 100% line/branch coverage and a 100% mutation score |
| Streaming recovery | Component/backend gates passed; aggregate durability work open | 343/343 maintained lines, 124/124 branch outcomes, 100% mutation; full integration now passes after the separate A5 routing fix |
| A5 persisted rollup readiness | Scoped gates passed | 1,680 portable units, 168 ClickHouse integrations, 172/172 maintained scoped lines, 40/40 branch outcomes and 99/99 executable mutants; delivery and rebuild semantics remain A6 |
| Replay-safe rollups and explicit delivery bounds | Pending | Persisted insert identity and pinned-server failure tests required |
| Reserved read/write/probe capacity and bounded admission | Pending | Current connection factory is shared |
| B1 shared WAL I/O contract with managed baseline | Implemented; aggregate qualification open | Managed file class: 100% line/branch coverage, six executable mutants killed; 42 focused WAL/HTTP cases passed |
| B2 native io_uring backend and packaging | Implemented; platform/artifact gates passed | Actual x64 and ARM64 native faults/sanitizers, scoped coverage/mutation, standalone and container artifacts passed; aggregate crash/performance work remains |
| B3 native integration and operator fallback | Implemented; platform/artifact gates passed | Both actual architectures passed run 40830818024 after the terminal-channel regression; shared recovery/HTTP contracts passed; aggregate crash qualification remains |
| B4 bounded beacon microbatches | Implemented; consolidated qualification open | 1,760 unit, 148 ClickHouse, 24 native and eleven real-native HTTP cases passed; see the B4 checkpoint |
| B5 custom ingestion dispatcher | In progress | Direct bounded request-pipe reading and durable dispatch ownership implemented behind the managed/custom switch; focused parity checks pass |
| B6 combined qualification/default selection | Pending | Matched-load four-combination latency/CPU/memory/visibility evidence required |
| Full build, unit, integration and published-artifact end-to-end | In progress | Capturing the unchanged baseline before implementation |
| Changed maintained-code line/branch coverage | Pending | Baseline unit collector includes generated source; classify and combine with integration evidence |
| Stryker mutation checks | Partial | Shared WAL: 174 killed, 54 timeout detections; measurement helpers: 120 killed, one timeout; native boundary: 87 killed, 14 timeout detections; all three scopes have zero survivors, surrounding harness remains |
| Source style, architecture and domain neutrality guards | Baseline passed | Included in the 1,303-test unit run |

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
