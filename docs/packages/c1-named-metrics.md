# C1: named metric declarations and execution

Branch: feat/analytics-customization-c1-named-metrics

Define version-1 named metrics in the existing TelemetryEngine schema configuration. Support
event counts and permitted count/sum/avg/min/max aggregations of active declared measures,
optionally restricted to one event type. Validate declarations before startup activation and
publish immutable definitions through the existing authenticated schema response.

Execute GET /api/v1/analytics/metrics/{name} using the existing JWT project/tenant identity and
the new metrics read scope. Tenant-set claims retain their existing membership semantics.
Accept bounded existing typed filters and traffic selection; enforce a measure's unit filter
before aggregation. Never accept caller SQL or caller-selected physical identifiers.

New metric ranges are UTC [from, to), normalized to stored millisecond precision, within
ClickHouse DateTime64's supported 1900 through 2299 calendar years. Missing ranges use the
existing configured lookback. Legacy endpoints retain their inclusive range behavior.

Return a nullable numeric string so integer/decimal results survive JSON clients without
binary64 coercion. Integer sums widen to Int256. Other aggregations retain ClickHouse numeric
semantics, including floating-point averages. Empty counts/sums are zero; empty min/max/avg and
nonfinite floating results are null. Count operates on stored rows, or nonnull measure values;
it does not introduce UUID deduplication or stitched-actor semantics.

The registry is bounded by MaxNamedMetrics (default 128, maximum 1024). Definitions activate at
restart and introduce no DDL or ingestion-path work. Configurations without named metrics keep
working. Removing declarations disables the new names without rewriting historical data.

This package excludes formulas, reusable segments, project overrides, reports, percentile/distinct
metrics, hybrid plans, caching, live deployment APIs and hosted services. Follow-up packages keep
these contracts explicit. The technical guide's older C1 database-capacity identifier is separate
from this category-C package number.

Acceptance: configuration binding and rejection boundaries; immutable registry; SQL injection,
authorization and tenant/project isolation; unit/time/null/large-integer behavior; schema discovery;
real ClickHouse reference results and published executable smoke; focused coverage and mutation
checks followed by milestone suites. No benchmarks or performance measurements are run.
