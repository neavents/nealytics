# B3 verification review

Native startup selection is implemented behind the shared WAL. Managed remains the default;
explicit native and auto selection preserve existing HTTP producers. This review records passed
checks and retains the remaining aggregate gates.

Local checks passed: a warning-free build, 1,578 Linux ARM64 unit cases, eighteen actual native
file/startup cases, nine real-Kestrel native HTTP contract cases and all 148 ClickHouse integration
cases. The maintained startup/native union covers all 434 lines and 88 branch outcomes. The C
bridge separately covers 145 statements and 112 branch outcomes and passes address, undefined
behavior and leak sanitizers. The complete-level startup/native mutation scope detected all 191
executable mutants: 166 assertion kills and 25 timeout detections, with no survivors or uncovered
mutants. There were eight compile errors and 83 structural ignores in the selected files.

Actual Linux x64 and ARM64 qualification both passed in
[run 40809328183](https://github.com/neavents/nealytics/actions/runs/40809328183).
The pipeline verifies host architecture without emulation, executes native faults/sanitizers,
full units, native startup/file and HTTP contracts, explicit syscall denial, coverage gates,
standalone and ordinary-container artifacts, and complete-level mutation checks. Each artifact
check verifies ingestion through ClickHouse visibility and clean shutdown with no SDK, installed
.NET runtime or separate liburing dependency. Native assets are embedded in the executable.

An earlier ARM run detected only 190 of 191 mutants. Its survivor removed the pending-channel
completion after a terminal WAL error. End-to-end failure outcomes did not reliably establish
that the channel itself was faulted, leaving a scheduling-sensitive gap around late producers.
The regression now awaits channel completion and requires the original storage exception.
A separate temporary-source check removed that completion call and verified an assertion failure;
the unmutated regression passes. Repeat the full platform gates after this strengthened assertion.

The 25 timeout detections erase completion, draining, cancellation or shutdown progress. They
are detections rather than assertion kills; ordinary suites complete without timeouts. Thresholds
remain 100%, and no maintained-code scope was reduced to resolve survivors. One local command
reached 100% before its mutable wrapper script failed after being edited during execution.
The immutable-wrapper rerun completed successfully with the same 100% score.

The first remote qualification attempts found checkout ownership and offline tooling-cache
problems. The diagnostic pipeline briefly masked a failure through its log writer; runtime and
artifact inspection exposed that false green and it was not accepted as qualification. The
workflow now propagates pipeline failure, runs containers as the checkout owner, restores tools
before network isolation, and emits bounded failure diagnostics. Test containers use explicit
permissions; production code never relaxes host policy.

Process-kill/crash parity, crash-consistent directory metadata/rotation, replay delivery identity,
bounded ingestion dispatch and matched-load performance/default qualification remain required A/B
work. Archy preflight remains unavailable without layer rules, and the CLI reports an uninitialized
workspace. Executable style, dependency and domain guards pass. Main remains unchanged.
