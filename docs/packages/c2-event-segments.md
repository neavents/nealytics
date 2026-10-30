# C2: reusable event segments

Branch: `feat/analytics-customization-c2-event-segments`.
Prerequisite: qualified C1 category snapshot `1ca7153`.

Status: functionally qualified; see [verification evidence](c2-verification.md).

Declare named version-1 event segments in the existing TelemetryEngine schema configuration.
Each segment contains one to 32 existing typed filter expressions, combined with AND. Permit
core event columns, active event dimensions and active measures. Exclude tenant-attribute
membership filters: these are event selections, not mutable user/session/organization cohorts.
Dimension filters use exact string equality; numeric comparisons retain the existing finite
Float64 filter contract. Exact integer/decimal numeric predicates require a separate contract.

Select one segment with `segment=<name>` on the C1 named-metric endpoint. Combine its compiled
conditions with the caller's existing bounded filters, fixed event type, traffic choice, UTC
range and authenticated project/tenant scope. A segment cannot grant access or replace a
scope constraint. The unit-safety check sees the combined filters. Unknown segment names fail
before database I/O; requests without a segment preserve C1 behavior. Responses identify the
selected segment and authenticated schema discovery publishes its declarative definition.

Validate and compile once at startup into an immutable, bounded registry. Default capacity is
128 definitions, configurable from 1 to 1024. Names follow the existing metric-name syntax;
values retain the existing 256-character limit. Reject empty/blank definitions, duplicate names,
unknown or inactive fields, unsupported versions and nonfinite numeric predicates. No arbitrary
SQL, regex, OR trees, segment recursion, live deployment, per-project overrides or DDL is added.
No work is added to ingestion, and the single-executable deployment remains unchanged.

Acceptance includes invalid/exact-limit configuration tests, immutable compiled definitions,
scope intersection and injection cases, unit safety, schema discovery and generated JSON,
real ClickHouse reference results, published executable smoke, changed-source coverage and
complete-level mutation checks. Consolidate full suites at the package milestone. No benchmarks.

Definitions activate at restart. Remove declarations and stop selecting their names to disable
the feature. Stored data is unchanged and remains readable by the previous executable. Clients
using the new query parameter must stop sending it before rolling back to C1, whose endpoint
does not interpret segment selection.
