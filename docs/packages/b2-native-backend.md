# B2: native WAL file backend and single-executable packaging

Branch: feat/ingestion-performance-b2-native-backend

Outcome: implement Linux io_uring behind the existing internal file boundary, outside live
ingestion until its tests and packaging are qualified. Existing HTTP handling, WAL framing,
admission, replay and default managed selection remain shared.

Use liburing 2.15, commit d41bf9220ec39277ff235379e9089d9e0fd6c2a5. Verify the pinned archive hash
and statically link the maintained library into a small bridge with an opaque context. Package
the bridge inside the self-contained executable with .NET native-library extraction. No separate
liburing installation, helper service or runtime compiler is required. Include the upstream MIT
license and record the extraction/writable-cache requirements.

One reusable native buffer is bounded by the flush byte limit. Copying each submitted record into
that buffer ends its borrowed managed-buffer lifetime. Submit an ordered write and full fsync;
wait for every completion before success. Handle short writes, negative results, linked-operation
cancellation, interrupted submission, completion IDs, offset reset and truncation explicitly.
Use one long-lived completion owner, with bounded work and no thread-per-flush scheduling.

Kernel ownership must outlive all submitted I/O. A terminal submission/completion failure stops
new work; destruction cannot free memory still referenced by the kernel. Test this boundary as
well as ordinary cleanup. No uncertain in-flight operation permits switching to managed I/O.

Acceptance includes real Linux ARM64 and x64 execution, ABI/layout and error checks, exact file
contents, repeated flush/truncation, short/error completions, bounded resource ownership, and
clean-host execution of the published binary. Native coverage/fault checks supplement managed
coverage and Stryker, which cannot mutate the bridge or liburing. Backend selection and live
compatibility are B3; default/performance qualification is B6.

Current evidence (not a package completion claim):

- Strict-warning native build and warning-free managed build passed.
- Thirteen completion-owner cases and 21 native-boundary cases passed on macOS ARM64 using
  controlled native API/context substitutes. Completion-owner line/branch coverage is 100%.
- Thirteen actual Linux ARM64 file/backend cases passed with zero skips, including the shared
  WAL's managed recovery compatibility and a real `/dev/full` failure.
- The native C bridge reached 100% of 145 maintained statements and all 112 branch outcomes.
  The same checks passed address, undefined-behavior and leak sanitizers. These totals do not
  claim coverage of the upstream library or its headers.
- The full portable unit suite passed 1,452 cases with zero skips before the final access-mode
  adjustment. Repeat after integrating the final A3 changes and native qualification work.
- The first Linux managed-native mutation run scored 84.16%: 68 killed, 17 timeout detections,
  14 survivors and two uncovered mutants. Strengthening those checks remains open; no threshold
  has been reduced. Real Linux x64 and standalone artifact checks also remain open.
  Reproduction and ownership details are in [native/README.md](../../native/README.md).
