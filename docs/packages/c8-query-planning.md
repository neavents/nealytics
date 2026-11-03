# C8 query planning and accuracy

Branch: `feat/analytics-customization-c8-query-planning`. Implemented and functionally qualified.

Keep analytics rollup planning explicitly opt-in through EnableAnalyticsRollupPlanning. Auto
execution selects a verified, semantically compatible hour/day rollup and disjoint raw edges;
raw execution provides the reference path. Combine aggregate states inside one query and finalize
formulas afterwards. Never add averages, average averages, or add distinct counts. Retain exact
widened integer sums on raw storage when existing rollup states cannot represent them.

Reject candidates with missing coverage/readiness, insertion-time tenant groups, measure-row
filters, unsupported aggregates, nullable grouped/filtered dimensions, computed expressions,
calendar incompatibility, or a coarser bucket than the requested report. Regular per-tenant
rollups can use current membership subqueries. Existing persisted readiness and delivery fences
remain authoritative. Explain is administrative metadata, without executing the query or
collecting execution statistics. No benchmarks or backend-default changes.

Quantile metrics declare exact or approximate accuracy. Approximate definitions require explicit
caller opt-in, and exact formulas cannot conceal approximate dependencies. Initial state planning
supports count, sum, average, minimum and maximum; quantiles use raw data with an explanation.

Rollup admission also requires AnalyticsRollupSourceIsAppendOnly. This is an operator assertion,
not a duplicate detector: use exclusively governed delivery writes with unique event ordering keys,
no out-of-band UPDATE/DELETE/truncate or reused event identities, and verified rebuilds after any
mutation. ReplacingMergeTree replacement and materialized aggregate states cannot retract the same
rows automatically. Without this assertion, keep the raw path. Standard finite delivery-token
retention and exclusive-writer recovery preconditions still apply.

Queries crossing the shortest configured raw-retention horizon fall back to raw data, with a
conservative whole-second TTL boundary. A nonpositive/unreconciled retention policy also falls back.
This avoids serving retained rollup rows after corresponding raw rows may have expired.

Qualification: all 478 managed and 268 storage executable mutants are assertion-killed at
Complete level with unchanged 100% thresholds. Full suites, changed maintained-code coverage,
actual state routing/reference results and published io_uring/custom-ingestion behavior pass.
See the [category ledger](c-verification.md) and [operator guide](../analytics-customization.md)
for immutable sources, deployment preconditions and deferred work. No performance claim is made.
