# C3: saved scalar reports

Branch: `feat/analytics-customization-c3-saved-reports`.
Prerequisite: qualified C2 category snapshot `1f70b02`. Selected 2026-11-03.

Declare version-1 saved reports in the existing TelemetryEngine configuration. Each report has
one to 16 ordered, uniquely named fields referencing existing named metrics, and optionally one
shared event segment. Validate references, names, format version and bounds before activation.
Keep an immutable catalog with MaxSavedReports default 128, supported range 1 to 1024.

Execute `GET /api/v1/analytics/reports/{name}` with the existing typed from/to, traffic and filter
parameters and authenticated project/tenant or tenant-set scope. Require the reports read scope
when scoped reads are configured. A report's segment is fixed by its declaration; requests cannot
replace it. Every metric retains its event-type restriction, numeric/null semantics, precision
encoding and unit check. Validate all fields before storage access and return the whole report
or an error. Fields preserve declaration order and their configured aliases.

Use one parameterized SELECT over the raw event source, with one shared authorized selection and
conditional aggregates for metrics restricted to an event type. Acquire one existing read lease,
bind DateTime64-compatible timestamps, propagate cancellation and release commands/readers/leases.
Return nullable numeric strings and explicit null values. Publish declarations in authenticated
schema discovery without exposing compiled metric references. Preserve named-metric SQL and API
behavior while sharing the aggregate-expression builder.

This slice excludes formulas, grouping, per-field segments, arbitrary SQL/parameters, caching,
rollup/hybrid plans, report CRUD/deployment APIs, UI, project overrides and hosted services.
The older technical roadmap's C3 approximate-rollup identifier is a separate proposal.

Acceptance: invalid and exact-limit declarations; immutable copies and configuration binding;
identity/range/filter/traffic/each-field unit failures; endpoint authentication and scope;
generated JSON and schema metadata; one-query SQL/reference parity across all aggregates;
empty/null/large integer/decimal/nonfinite values; project/tenant/tenant-set and event isolation;
published native/custom executable smoke; changed-code coverage and Complete-level mutation
checks followed by milestone suites. Use separate managed/storage mutation scopes, one storage
worker and early abort disabled. No benchmarks or performance measurements.

Definitions activate at restart. Remove a report declaration to disable its name. No DDL or
ingestion-path work is introduced; stored data and existing ingestion callers remain unchanged.
Rolling back removes the new endpoint, so report clients must stop using it before rollback.
