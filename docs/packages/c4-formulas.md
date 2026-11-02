# C4 typed metric formulas

Branch: `feat/analytics-customization-c4-formulas`. Selected 2026-11-03.

Extend named metrics with `Aggregation: formula` and a typed expression tree. Supported nodes
are metric references, decimal constants, add, subtract, multiply, divide and negate. Compile
forward references at startup; reject cycles, unknown references, malformed nodes, unsupported
operators, depth above eight and more than 64 expanded nodes. Constants have at most 38 digits.
Freeze public expression metadata and canonical primitive dependencies.

Unit inference permits add/subtract only for matching dimensions, multiplication with at least
one dimensionless operand, and division by a dimensionless operand or like-dimension cancellation.
Unit labels do not perform conversions. Every primitive dependency keeps its measure unit check,
event type and authenticated selection. Formula event-type/measure restrictions belong on its
component metrics, not the formula declaration.

Use one bounded aggregate projection for all primitive dependencies. Finalize formulas after
aggregates are read. Preserve exact integer/decimal input using bounded rational arithmetic,
return numeric strings, propagate null/nonfinite inputs, and return null for division by zero or
arithmetic overflow. Integers remain exact; fractional results round to 34 decimal places with
ties away from zero and no insignificant trailing zeros. Floating components retain the precision
of their native aggregate before formula evaluation. Bound intermediate numerator/denominator
bit lengths to 4096; validate input representation bounds before allocating large powers.

Existing primitive SQL and HTTP results remain compatible. Saved reports deduplicate primitive
dependencies into a maximum 128-column projection, preserve field order and aliases, and reject
an oversized projection at activation. No expression text is accepted as SQL; no reflection,
runtime code generation, new service, ingestion processing or data migration is introduced.

Acceptance: invalid/forward/cyclic/depth/size declarations, unit inference and every dependency's
unit validation; exact rational/rounding/zero/null/overflow cases; immutable metadata and generated
JSON; primitive regression SQL; deduplicated one-query scalar/report reference results on real
ClickHouse; scoped coverage/mutation plus milestone suites and published executable smoke.
