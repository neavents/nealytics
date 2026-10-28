# Categories A and B implementation evidence

Objective: complete correctness/resource safety and ingestion performance, including native Linux
io_uring and custom ingestion, with existing callers unchanged. Main remains untouched.

This is an evidence ledger, not a completion claim. A package is complete only after its named
checks, platform requirements, and applicable coverage/mutation gates have passed.

| Requirement | Status | Evidence / remaining work |
| --- | --- | --- |
| A1 reproducible latency, offered load, resource and visibility baseline | Implemented; qualification open | 61 benchmark unit cases and four real-Kestrel/ClickHouse cases passed; sustained final-revision runs and harness-wide changed-line coverage remain |
| A2 HTTP compatibility and durability/failure fixtures | In progress | Nine additional real-Kestrel contract cases passed; deterministic write/flush-fault cases follow the B1 seam |
| A3 bounded pending WAL and flush groups | Pending | Current pending channel is unbounded |
| Streaming recovery | Pending | Current replay materializes the sealed WAL |
| Replay-safe rollups and explicit delivery bounds | Pending | Persisted insert identity and pinned-server failure tests required |
| Reserved read/write/probe capacity and bounded admission | Pending | Current connection factory is shared |
| B1 shared WAL I/O contract with managed baseline | Implemented; aggregate qualification open | Managed file class: 100% line/branch coverage, six executable mutants killed; 42 focused WAL/HTTP cases passed |
| B2 native io_uring backend and packaging | Pending | Real Linux x64/ARM64 execution and standalone published artifact required |
| B3 native integration and operator fallback | Pending | Native/managed parity, unsupported-host and crash tests required |
| B4 bounded beacon microbatches | Pending | Preserve accepted-prefix, rejection and cancellation semantics |
| B5 custom ingestion dispatcher | Pending | Existing HTTP requests and policy behavior must remain compatible |
| B6 combined qualification/default selection | Pending | Matched-load four-combination latency/CPU/memory/visibility evidence required |
| Full build, unit, integration and published-artifact end-to-end | In progress | Capturing the unchanged baseline before implementation |
| Changed maintained-code line/branch coverage | Pending | Baseline unit collector includes generated source; classify and combine with integration evidence |
| Stryker mutation checks | Partial | Pinned Stryker.NET 5.0.0; measurement helpers: 115 killed, one nontermination timeout, zero survivors; engine and surrounding harness checks remain |
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
