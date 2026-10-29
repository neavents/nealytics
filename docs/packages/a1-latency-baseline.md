# A1: reproducible ingestion latency baseline

Branch: feat/correctness-and-safety-a1-latency-baseline

Outcome: capture repeatable track/beacon acknowledgment and visibility measurements with offered
load, errors, resource context, and a machine-readable manifest. Existing benchmark modes remain usable.

Scope: benchmark instrumentation and its tests. No engine behavior, payload contract, WAL format,
backend default, deployment schema, or unrelated test dependency changes.

Implementation boundaries:

- Extend the existing harness with fixed-arrival scheduling and bounded latency histograms.
- Keep request/credential values out of manifests; identify the target without URL credentials.
- Distinguish successful-request acknowledgment, scheduling-inclusive latency, visibility observation,
  failed requests, and probe timeouts/drops. Never report missing measurements as successful zeroes.
- Require exact accepted/stored accounting when loss verification is selected.
- Label client host resources and declared engine settings; do not present them as measured remote
  server hardware or effective configuration.

Checks: measurement/scheduling/manifest unit tests, existing unit baseline, isolated real-ClickHouse
integration baseline, real-Kestrel runs, collector coverage, and focused mutation tests for new
measurement logic. Record short functional checks separately from sustained performance evidence.

Rollout: additive command options and optional JSON manifests. No service-side rollout is involved.
