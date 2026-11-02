# C5 parameterized grouped and time-series reports

Branch: `feat/analytics-customization-c5-report-shapes`. Selected 2026-11-03 after C4's focused
unit and real-server checks passed; C4 mutation qualification continues from its immutable source.

Extend saved report declarations with up to two groupable event columns, an optional UTC hour/day
interval, a declared row limit, and up to 16 named typed filter parameters. Parameters reference
existing allowlisted dimensions or measures; string parameters support equality and numeric
parameters support the existing finite Float64 comparisons. Names are strict and unique; defaults
validate at activation, required parameters without defaults validate before storage. Requests use
`param.<name>` and reject unknown, duplicate, blank, oversized or invalid values.

Preserve every authorization, fixed-segment, caller-filter and component event-type constraint.
Parameter filters participate in unit safety. Grouping by a physical unit dimension also satisfies
that component's unit constraint. No caller can choose SQL, physical columns, grouping or limits.

Scalar reports retain their fields contract. Grouped/time-series reports expose ordered rows with
named group values, an optional UTC bucket and finalized fields. Numeric values retain C4's exact
string/null/formula contracts. Execute one grouped query over the existing read pool; finalize
formulas per result row. Order buckets then group keys ascending, with explicit null ordering;
return sparse time series without implicit gap filling. Apply the bounded row limit after grouping.

The declared limit defaults to 100, cannot exceed MaxQueryLimit or 10,000, and the configured
projection/response shape cannot exceed 16,384 aggregate/output cells. Empty grouped selections
return an empty row list. No ingestion processing, DDL, new service, arbitrary sorting, historical
cohort grouping or stored report results are introduced.

Acceptance: definition and parameter bounds/defaults/types; input precedence and failures before
I/O; unit filtering/grouping; deterministic SQL ordering and bound values; scalar regression;
real grouped/time-bucket/null/empty/formula/tenant/segment results; cancellation and ownership;
scoped coverage/mutation and consolidated milestone suites/published executable checks.
