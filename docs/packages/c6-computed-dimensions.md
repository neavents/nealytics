# C6 deterministic computed dimensions

Branch: `feat/analytics-customization-c6-computed-dimensions`. Selected 2026-11-03 after C5's
focused unit and real-server contracts passed. Mutation qualification continues from isolated
package snapshots; neither C4 nor C5 is declared fully qualified yet.

Declare version-1 read-time dimensions with a strict name, one source and a deterministic
operation: map, lower, upper, trim, bounded UTF-8 substring, or numeric bucket. Compile forward
computed-source references at activation; reject cycles, unknown/retired sources, incompatible
types, unsupported/extraneous settings, depth above eight, more than 32 mapping entries and
expanded SQL above 4096 ASCII characters. Mapping keys/values/defaults are bounded to 256
characters and emitted as UTF-8 hexadecimal data, never caller SQL. Numeric widths must be
finite and positive. Bucket arithmetic retains native Float64 precision and maps nonfinite
results to null. Preserve null source values for every operation. Maps use exact ordinal keys;
unmatched values retain the input unless an explicit default is declared.

MaxComputedDimensions defaults to 32, supported range 1–128. Definitions and metadata are
immutable. Physical names, active measures and other computed names cannot collide. No physical
column, DDL, stored rewrite, arbitrary script/SQL, plugin runtime or ingestion transform is added.

Expose computed metadata through authenticated schema discovery without compiled expressions.
Use the extended analytics allowlist for named-metric filters, reusable segments, saved-report
parameters and saved-report grouping. Keep legacy endpoint allowlists and response contracts
unchanged. Public report group names remain logical names. Unit constraints still require the
original physical unit column; a transformed unit name cannot silently combine different units.

Accept typed filter values independently of compiled expressions and cap composed analytics
query size before I/O. Unsupported rollup coverage uses raw input; C8 explains that choice.

Acceptance: operation/type/bounds/default/null/cycle/forward-reference/immutability fixtures,
injection-shaped mapping data, filter and group reference results on real ClickHouse, source
discovery, legacy allowlist isolation, unit safety, changed-code coverage and mutation, followed
by consolidated category suites and single-executable checks. Benchmarks remain stopped.
