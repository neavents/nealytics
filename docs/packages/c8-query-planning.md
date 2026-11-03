# C8 query planning and accuracy

Branch: `feat/analytics-customization-c8-query-planning`. Implementation in progress.

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
