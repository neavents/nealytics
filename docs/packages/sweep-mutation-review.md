# Combined sweep mutation review

The first broad Complete-level run at source 943f93e tested 523 executable mutations and identified
one additional uncovered mutation. It reported 428 assertion kills, eleven timeout detections,
84 survivors, 56 selected compile errors, and a raw score of 83.78%. Its report is retained at
artifacts/verification/ab-sweep/reports/mutation-report.json. This is a failed qualification gate,
not a completion claim. Thresholds remain at 100.

The initial report drives the following focused corrections and tests:

- Canonical persisted conversion fingerprints replace record display formatting. Fixed digest
  and culture/bound/type/order regressions verify the representation.
- An explicit clock makes the exact expiry/future boundaries testable and consistent. New units
  freeze its time before insertion. Manifests require schema and membership properties.
- Processor tests exercise real delivery journals through live and restart paths and verify that
  committed source cleanup is followed by journal checkpointing. They cover the previously
  uncovered live checkpoint and the previously unasserted recovery checkpoint.
- Five-record shutdown scenarios exercise deferred ownership and byte accounting after a split.
  Exact-size batches and overflow fixtures cover both sides of capacity boundaries.
- Tests verify published ingestion metrics, configuration parameter diagnostics, complete
  checksum/header accounting, required metadata, signed invalid headers, scratch reservations,
  per-unit recovery limits, durable metadata barriers and temporary-file naming.
- Existing idle-connection expiry tests are included in the broad mutation selection. Their
  omission from the earlier filter left already tested behavior without mutation assertions.
- Deadline tests distinguish the pool's own timeout from the fixture's outer observation timeout.
  An isolated mutation removing CancelAfter is detected by an assertion, with a 100% one-mutant
  pilot result. This does not replace full pool qualification.

The channel now uses the bounded-capacity overload, which keeps wait-on-full behavior and
asynchronous continuations. In the supported .NET 10 implementation, the bounded-options overload
does not consume SingleReader or SingleWriter; removing those unused hints preserves the actual
channel implementation. See the [runtime source](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Threading.Channels/src/System/Threading/Channels/Channel.cs).
Admission partitions use workload enum keys instead of arbitrary string names.

The focused regressions pass. The next mutation reports, final pool lifecycle review and combined
same-source coverage must establish which findings are closed. No survivor is silently accepted,
no maintained source is excluded, and no threshold is reduced by this checkpoint.

The follow-up unit-only run at source 62b78fe passed its unchanged 100% threshold: 402 assertion
kills, ten timeout detections, zero survivors and zero uncovered mutants. Fifty selected mutants
did not compile. Its immutable source and complete report are retained in
artifacts/verification/ab-sweep-unit-qualified-20261030. The timeout cases involve missing
registration/completion signals, drain/read progress, invalid retained-record limits and replay
retry paths; they remain explicitly distinct from assertion kills. This does not qualify the
separate pool scope or establish repository-wide coverage.

The pool follow-up adds deterministic late-cancellation, reservation/shutdown ordering, cleanup
failure, reentrancy and synchronization-resource ownership tests, plus gated real-server disposal
tests. A separate Complete-level pool configuration covers the factory, pool and lease with the
same 100% thresholds and no maintained-source exclusions. Its result is not assumed passed.

That pool run at 000bff5 now passes: 112 assertion kills and seven investigated progress timeouts,
with no survivors or uncovered mutants. Five selected mutants fail compilation. The remaining
database-delivery range is qualified separately, including the operator diagnostic for unsupported
source tables; a passing unit-only or pool gate does not substitute for it.
