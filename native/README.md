# Native WAL development and qualification

The native file backend uses liburing 2.15 at commit
`d41bf9220ec39277ff235379e9089d9e0fd6c2a5`. The build verifies the archive SHA-256
`e4db39641298c90f42c29e264bd1057d0342380e712d56dfd9049da953637f48`.
The bridge statically links liburing and dynamically depends only on the standard C runtime.
Its MIT license is embedded in the managed assembly.

The current implementation targets Linux x64 and ARM64 with write and fsync operation support
(Linux 5.6 or newer). Actual qualification so far uses Linux ARM64, kernel 6.12.76-linuxkit.
Real x64 execution, single-executable extraction and live-ingestion selection remain open gates.
The production engine still uses managed WAL I/O by default at the B2 step.

## Build

From the repository root:

```bash
docker build --file native/Dockerfile --tag nealytics-native-build-tools:ab .
```

The image contains the compiler, pinned library source and `/native-output/libnealytics_wal.so`.
The compiler and upstream library are build dependencies. Neither needs a separate installation
on an engine host. To rebuild after bridge changes, mount `native/` at `/native` and invoke:

```bash
bash /native/build.sh /workspace/artifacts/native/linux-arm64 /build/liburing
```

Pass the resulting bridge path when publishing the engine:

```bash
dotnet publish src/Nealytics.Engine --configuration Release --runtime linux-arm64 \
  --self-contained true -p:NealyticsNativeLibrary=/absolute/path/libnealytics_wal.so
```

`IncludeNativeLibrariesForSelfExtract` packages native libraries inside the executable. The .NET
bundle extraction cache must be writable by the engine user. Its clean-host behavior must be
verified with the published artifact before enabling native ingestion.

## Native checks

`bash native/test.sh <output-directory> <pinned-liburing-source>` runs the bridge tests, gcov branch
coverage and address/undefined-behavior/leak sanitizers. Tests cover real files, full disks,
short writes, interrupted submission/completion, malformed completion IDs, fixed-file fallback,
partial submission, capacity checks, truncation and kernel ownership during failure.
Fault injection is confined to the test executable through linker wrapping.

`tests/Nealytics.Engine.Tests.Native` is an explicit Linux qualification project, separate from
the portable solution test run. It deliberately fails on an unsupported host or missing bridge.
It does not silently skip native execution. With the bridge available:

```bash
dotnet test tests/Nealytics.Engine.Tests.Native \
  -p:NealyticsNativeLibrary=/absolute/path/libnealytics_wal.so \
  --collect:"XPlat Code Coverage"
```

The desktop Docker default profile rejects ring creation with EPERM. The recorded permitted
tests used isolated containers with no network, all capabilities dropped, explicit memory/CPU
limits and `--security-opt seccomp=unconfined` for those test containers. Host and existing
container policies were unchanged. Production code does not relax host or container policy.
The blocked-host startup/fallback checks are separate from permitted-kernel qualification.

## Ownership and failure

One completion owner serves each native file. Appends copy into a reusable native buffer bounded
by the flush byte budget. The bridge waits for the write and full fsync completions; short writes
finish before success. It does not use mandatory busy polling or create a thread per flush.

A partial submission or unresolved completion failure stops the context. Destruction refuses to
free memory still referenced by the kernel and reports that a process restart is required.
No backend switch is permitted after such a failure. Normal write/flush errors drain their
completions before cleanup. Destruction clears the buffer's initialized extent without touching
its unused reserved capacity.
