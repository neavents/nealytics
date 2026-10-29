# Native WAL development and qualification

The native file backend uses liburing 2.15 at commit
`d41bf9220ec39277ff235379e9089d9e0fd6c2a5`. The build verifies the archive SHA-256
`e4db39641298c90f42c29e264bd1057d0342380e712d56dfd9049da953637f48`.
The bridge statically links liburing and dynamically depends only on the standard C runtime.
Its MIT license is embedded in the managed assembly.

The current implementation targets Linux x64 and ARM64 with write and fsync operation support
(Linux 5.6 or newer). Actual qualification so far uses Linux ARM64, kernel 6.12.76-linuxkit.
Real x64 execution remains an open gate. ARM64 single-executable extraction and live-ingestion
selection now have clean-container evidence. Managed remains the initial default.

## Startup selection

Set `TelemetryEngine__WalIoBackend` to `managed`, `io_uring` or `auto` and optionally set
`TelemetryEngine__WalRingEntries` within 8–4096 (default 8). Unknown modes and invalid bounds fail
before renaming a WAL. Existing HTTP clients, credentials and event bodies remain unchanged.
Explicit native mode fails on an unsupported host or unavailable bridge. Auto falls back only
for a missing library, an unsupported process platform, ring-create EPERM/EACCES/ENOSYS/EOPNOTSUPP,
or an unsupported startup preflight operation. Ordinary disk, allocation and ABI failures remain
errors. A structured startup event reports the selected backend, fixed-file status and fallback
reason. Backend selection never runs again after an admitted write or I/O failure.

The previous active WAL is sealed before opening the selected file. Native startup performs an
empty write/full-fsync preflight and transfers the context to its completion owner only after
that barrier succeeds. A failed preflight closes its context. An empty serializer scratch buffer
has no rented storage during constructor failure; shutdown owns scratch disposal after use.

The packaged bridge targets glibc-based Linux. The ARM64 artifact's inspected symbol references
require GLIBC 2.17 and 2.25; the complete executable was tested on the pinned runtime-dependencies
image, rather than certified against every older distribution. Musl/Alpine is not a native target.

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
bundle extraction cache must be writable by the engine user and permit executable library
mappings. A `noexec` cache mount prevents loading the extracted bridge. Production code never
changes mount policy; operators select a suitable cache path. Clean-host artifact tests pass only
the executable into a non-root, read-only runtime-dependencies container with bounded temporary
WAL/cache mounts, no SDK, no installed .NET runtime and no separate liburing installation.

`python3 native/test_artifact.py <published-executable> --backend io_uring --permit-native` checks
native extraction, existing track/beacon requests, ClickHouse visibility and graceful shutdown.
Use `--backend auto` without the permission flag to check the default Docker denial and managed
startup fallback. Each run owns its network, ClickHouse instance, engine and temporary data;
cleanup does not truncate an existing developer database. The permission flag affects only that
test's engine container. Actual x64 qualification remains separate from local ARM64 checks.
The reusable denied test now explicitly denies the three io_uring syscalls in its own container,
so a future Docker default-profile change cannot silently turn that negative check positive.
Artifact WAL/cache mounts are temporary test storage; these checks establish packaging, API,
visibility and shutdown behavior, and do not claim physical power-loss qualification.

The ordinary Dockerfile builds and embeds the bridge for the target architecture. Its native
compiler and pinned liburing remain in build stages; the runtime contains one application
executable and optional configuration. Its non-root user owns the default WAL and extraction
directories. `--engine-image <image>` exercises that image through the same artifact verifier.

## Qualification automation

`bash native/qualify.sh linux-x64` or `linux-arm64` requires a matching actual Linux host. It runs
native faults/sanitizers, full portable tests, real native startup/file tests, native HTTP
contracts, explicit kernel denial, the maintained-code coverage gate, standalone/container
artifact checks and complete-level mutation testing. Stryker stops on any initial test failure.
Reports are retained under `artifacts/platform/<rid>/`.

The feature-branch workflow uses GitHub's `ubuntu-24.04` and `ubuntu-24.04-arm` runners, as listed
in the [runner reference](https://docs.github.com/en/actions/reference/runners/github-hosted-runners).
It verifies the host architecture and does not substitute emulation for hardware evidence.
Adding the workflow does not mean either remote job has passed.

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
The completion owner checks pending work before sleeping, so coalesced work and shutdown pulses
cannot strand a queued request. Its final action signals logical exit. Disposal waits for that
signal before releasing the context and wake handle; the thread performs no resource access
afterward. A second thread join adds no ownership guarantee. The wake boundary permits tests to
verify idle blocking and cleanup without timing CPU samples or changing the native API.

A partial submission or unresolved completion failure stops the context. Destruction refuses to
free memory still referenced by the kernel and reports that a process restart is required.
No backend switch is permitted after such a failure. Normal write/flush errors drain their
completions before cleanup. Destruction clears the buffer's initialized extent without touching
its unused reserved capacity.
