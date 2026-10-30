# Nealytics implementation rules

The owner's engineering requirements are defined in [docs/engineering-rules.md](docs/engineering-rules.md).
Read that document and [CONTRIBUTING.md](CONTRIBUTING.md) before changing implementation.
Use [docs/work-packages.md](docs/work-packages.md) to select a bounded package.

## Product and architecture

- Prioritize customizability, speed, performance, and low memory use.
- Keep the engine domain agnostic. Deployment-specific vocabulary belongs in declarations.
- Preserve a single self-contained executable/container deployment. Package required native
  assets with it; do not introduce a mandatory helper service or separate native-library installation.
- Follow SOLID through cohesive responsibilities, explicit contracts, and useful I/O boundaries.
  Preserve vertical feature slices and avoid speculative abstractions or dependency layers.
- Keep existing ingestion endpoints, payloads, credentials, responses, and durability guarantees
  compatible. Native io_uring and custom ingestion are planned internal implementation changes.

## Source style

- Production code, SQL, and configuration must contain no comments, including XML documentation
  comments and comment-shaped configuration keys. Put design reasoning in docs/design-notes.md.
- Tests may contain short, useful comments. Prefer clear test names and setup over long commentary.
- Match existing formatting: explicit production local types, sealed classes where appropriate,
  small cohesive methods, and readable multiline declarations and expressions. No dense code walls.
- Preserve source-generated serialization and avoid reflection and avoidable allocations on hot paths.

## Verification and branches

- The owner stopped all benchmarking and performance measurement on 2026-10-30. Do not start
  load sweeps, throughput/latency measurements or profiling unless the owner re-enables them.
  Continue implementation, unit/integration/end-to-end tests, coverage, mutation and native checks.
- Add meaningful unit, real-dependency integration, end-to-end, and failure-path coverage for
  changed behavior. Cover all new/changed maintained production lines and branches; report
  repository-wide coverage and investigate inherited gaps rather than conceal them.
- Add and run Stryker.NET mutation checks for changed managed logic. Investigate surviving,
  uncovered, and timed-out mutants. Test native I/O separately on Linux x64 and ARM64.
- Double-check correctness, resource ownership, formatting, compatibility, and performance evidence
  before delivery. Never claim a suite, coverage target, mutation result, or platform passed if unrun.
- Use category and step feature branches listed in docs/engineering-rules.md. Do not merge into
  main, push main, or rewrite main. Keep unrelated work and unfinished rebases untouched.
- Run Archy preflight before risky changes and archy verify before delivery. Report unavailable
  verification accurately; it is not a reason to claim architecture checks passed.
