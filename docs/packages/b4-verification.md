# B4 verification checkpoint

Source: 183b1def96ad004099e06a618dd96c87a8b88424, on the B4 step branch.
This checkpoint retains open qualification work; it does not mark category B complete.

Passed checks at this revision:

- 1,760 unit cases with coverage collection and no skips.
- 148 integration cases against a fresh, isolated ClickHouse 25.3.14.14 server.
- 24 actual Linux ARM64 native file/startup cases, including grouped append and managed recovery.
- Eleven real-Kestrel durability contracts using io_uring.
- Managed and io_uring standalone executable checks through exact ClickHouse visibility and clean
  shutdown in a runtime-free image. The executable is 50,873,001 bytes and embeds the native bridge.
- Beacon endpoint contract coverage reaches 100% maintained lines and branch outcomes, including
  body-reader completion, authentication, body limits, trace counts, warnings and endpoint policies.
- [General CI](https://github.com/neavents/nealytics/actions/runs/40868868548) passed.

The first batching mutation run found fourteen survivors. Strengthened assertions cover combined
logical-group limits, terminal failure while a serializer waits, empty-input contention and exact
rejection metrics. Redundant control flow was simplified. The consolidated complete-level rerun
includes endpoint wiring, keeps thresholds at 100%, and remains open at this checkpoint. An older
superseded run was stopped after the owner requested less repeated broad verification.

Kestrel's HTTP/1 chunked-body limit includes chunk framing. Existing host behavior is preserved;
the decoded reader's exact boundary is tested separately. This follows the pinned
[Kestrel implementation](https://github.com/dotnet/aspnetcore/blob/v10.0.0/src/Servers/Kestrel/Core/src/Internal/Http/Http1ChunkedEncodingMessageBody.cs).

Actual x64/ARM64 native CI, final mutation results, maintained-code coverage aggregation and the
matched low-concurrency/resource comparison remain milestone checks. B6 combines these with
the custom dispatcher. No performance improvement is claimed from compatibility tests alone.

Archy cannot run without configured layer rules; executable source, dependency and domain guards
remain required. Main and the original unfinished rebase are untouched.
