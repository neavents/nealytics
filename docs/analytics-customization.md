# Analytics customization

Nealytics runs the C1–C8 analytics features inside the existing self-hosted executable. Declare
metrics, segments, reports, computed dimensions and project schemas in configuration, then restart.
No hosted control plane, runtime scripts or SQL supplied by callers are required. See
[the completion contract](packages/c-completion-plan.md) and [qualification](packages/c-verification.md)
for the selected scope and evidence.

## Typed formulas

A named metric can reference other named metrics and decimal constants:

```json
{
  "NamedMetrics": [
    { "Name": "actions", "EventType": "action" },
    { "Name": "events" },
    {
      "Name": "share",
      "Aggregation": "formula",
      "Expression": {
        "Operator": "divide",
        "Arguments": [
          { "Operator": "metric", "Metric": "actions" },
          { "Operator": "metric", "Metric": "events" }
        ]
      }
    }
  ]
}
```

Put these entries under `TelemetryEngine`, or inside a project's declaration. Operators are
`constant`, `metric`, `add`, `subtract`, `multiply`, `divide` and `negate`. Constants use `Value`
as a decimal string. References use `Metric`. Arithmetic nodes use `Arguments`; unused fields,
wrong arity, cycles, unresolved references and unsupported versions prevent activation. Forward
references work. Formulas cannot declare their own `Measure` or `EventType`.

Limits are eight dependency/expression levels, 64 expanded nodes, 38 constant digits and 128
unique primitive aggregates per report. Evaluation follows aggregation, including across rollup
states. Integer/decimal aggregate strings enter bounded rational arithmetic without a Float64
conversion. Fractional answers round to 34 decimal places, ties away from zero. Null/nonfinite
inputs, division by zero and intermediates above 4,096 bits return null. Floating aggregates retain
their native precision; formulas cannot recover precision already lost there.

Addition/subtraction require matching dimensional units. Multiplication needs a dimensionless
operand; division permits a dimensionless denominator or cancellation of matching units. Unit
labels do not convert values. Every dependency retains its measure's unit-dimension constraint,
even when the final formula is dimensionless. Constrain that dimension or group by it.

## Report shapes and parameters

Scalar reports keep ordered `fields` and an empty `rows` array. A declared `GroupBy` produces a
`table`; `Interval: hour` or `day` produces `timeseries`. Time buckets are UTC and contain only
occupied groups. No automatic zero filling is performed.

```json
{
  "SavedReports": [
    {
      "Name": "activity_by_region",
      "Interval": "hour",
      "GroupBy": ["region"],
      "Limit": 100,
      "Fields": [
        { "Name": "events", "Metric": "events" },
        { "Name": "share", "Metric": "share" }
      ],
      "Parameters": [
        { "Name": "region", "Column": "region", "Required": true },
        { "Name": "minimum", "Column": "amount", "Type": "number", "Comparison": "gte", "Default": "0" }
      ]
    }
  ]
}
```

Referenced dimensions/measures/metrics must be declared in the same logical schema. Request
parameters are `param.region=...` and `param.minimum=...`. String parameters apply equality;
number parameters use finite Float64 comparisons `eq`, `ne`, `lt`, `lte`, `gt` or `gte`.
They add AND filters to the authenticated selection and configured segment. They cannot replace
identity, grouping, event types, report fields or segments. Unknown, repeated, missing required,
blank, over-256-character and invalid numeric values return 400 before storage.

Reports allow 1–16 fields, at most two groups and 16 parameters. `Limit` defaults to 100 and must
fit 1–min(`MaxQueryLimit`, 10000). Grouped reports also enforce a 16,384-cell budget against the
larger of public fields and primitive aggregate columns. Groups sort ascending with nulls first,
then the declared limit applies. Each row has `groups`, optional `bucket` and ordered `fields`;
group values retain their public names and explicit nulls. Scalar and row values use numeric
strings. See [saved reports](saved-reports.md) for endpoint and authentication details.

## Computed dimensions

Read-time transforms compile into validated expressions. They add no stored column or arbitrary
code execution, and do not rewrite historical data.

```json
{
  "ComputedDimensions": [
    { "Name": "normalized_region", "Source": "region", "Operation": "lower" },
    { "Name": "amount_band", "Source": "amount", "Operation": "bucket", "Width": 10 },
    {
      "Name": "region_label",
      "Source": "normalized_region",
      "Operation": "map",
      "Mapping": [ { "From": "gb", "To": "selected" } ],
      "Default": "other"
    }
  ]
}
```

Operations are UTF-8 `lower`/`upper`, ClickHouse `trimBoth` semantics via `trim`, UTF-8 `substring`,
`map`, and numeric `bucket`. Substring `Start` is one-based, defaults to 1 and supports 1–256;
`Length` defaults to 64 and supports 1–256. String transforms explicitly stringify numeric sources.
Buckets require a numeric source and finite positive width, use floor division (including negative
values), and return null on nonfinite results. Maps are case-sensitive, contain 1–32 unique keys,
and bound each key/value/default to 256 characters. Unmatched input keeps its original string
unless a default is declared; null remains null even when a default exists.

Sources can be active physical fields or other computed dimensions. Cycles, retired/unknown
sources, name collisions, irrelevant settings and chains above eight levels prevent activation.
`MaxComputedDimensions` defaults to 32 and supports 1–128. An expanded expression is bounded to
4,096 characters; request filter/group expression expansion is bounded to 32,768 characters.

Computed names work in named-metric/report filters, segments, report parameters and report groups.
Numeric buckets support numeric comparisons. Older analytics routes keep their physical-column
allowlist. Discovery exposes declarations, not internal SQL or compiled dependency metadata.

## Project-specific logical schemas

`ProjectSchemas` is a list of version-1 declarations. Each entry has `ProjectId` and its own
`Dimensions`, `Measures`, `TenantAttributes`, `ComputedDimensions`, `NamedMetrics`, `EventSegments`,
`SavedReports` and `Rollups`. These lists form a complete logical schema, not an overlay inheriting
other projects' definitions. Names may overlap across projects with different logical meanings.

A request selects one immutable context from its validated JWT `project_id`, or from its pinned
write credential. Query-string project names never choose a catalog. Track/validation/tenant writes
use their existing header/query credential precedence; beacon continues to use its query key.
Configured-project writes always need a pinned credential. Unknown projects are forbidden unless
`AllowLegacyProjectSchema` explicitly permits the global schema for unconfigured projects.
With no project declarations, existing global behavior remains available.

`MaxProjectSchemas` defaults to 128 and supports 1–1024. Identifiers are trimmed, case-sensitive,
nonblank and at most 256 characters. Existing catalog bounds apply within each context. One
bounded compatible physical schema union backs all contexts; physical dimension/measure and
16-tenant-attribute bounds also apply to that union. Incompatible shared column types, dimension/
measure collisions or conflicting rollup names prevent activation before DDL.

Validation, server-only fields, unit rules and bounds stay logical. The writer uses the physical
union; project-specific rejected measure values become absent before WAL persistence, preserving
the existing accepted-request/NULL-storage contract. Dry-run validation still explains invalid
values. Hidden fields retain normal sanitize/drop diagnostics. Physical readiness fences are
shared across every logical project using the affected rollup.

Changing physical columns retains existing migration, backup, retirement and pending-delivery
schema-fingerprint requirements. Removing a logical declaration does not delete data. Changes
activate at restart. Existing permitted producer request bodies, responses and durable ACKs remain
compatible.

## Planning, source policy and accuracy

Planning is opt-in; both options default to false:

```json
{
  "TelemetryEngine": {
    "EnableAnalyticsRollupPlanning": true,
    "AnalyticsRollupSourceIsAppendOnly": true
  }
}
```

The second flag is an operator assertion, not an automatic duplicate/mutation detector. Enable it
only with exclusively governed delivery writes, unique event ordering keys, no out-of-band
UPDATE/DELETE/truncate or reused identities, and verified rebuilds after any mutation. The source
is a ReplacingMergeTree; replacement and materialized aggregate states cannot retract the same
rows automatically. Existing exclusive-writer, source-identity and finite delivery-token retry
preconditions still apply. Do not treat an enabled flag as proof that historical data meets them.

`execution=auto` chooses a verified compatible hour/day rollup; `execution=raw` forces the reference
path. One SQL query combines disjoint `[from, firstFullBucket)`, complete summarized buckets,
and `[lastFullBucket, to)` edges. It merges aggregate states before finalizing formulas, preserving
weighted averages rather than averaging averages. The response `source` is `raw`, `rollup` or
`hybrid`.

Candidates must retain event-type, filter, group, unit and calendar semantics. Widened integer
sums, unsupported quantiles, missing states/types, row-level measure filters, computed expressions,
nullable grouped/filtered dimensions, session rollups, insertion-time tenant-group rollups,
unready/backfill/ambiguous-delivery states, coarse grains and ranges without full buckets fall back
to raw data. Regular per-tenant rollups can apply current tenant-membership subqueries. Queries
that may include rows expired under the shortest raw-retention rule also fall back, using a
conservative whole-second TTL boundary. Nonpositive/unreconciled retention prevents selection.

Named metrics support `p50`, `p75`, `p90`, `p95`, `p99` with a permitted measure aggregation and
`Accuracy: exact` (default) or `approximate`. Approximate definitions require callers to explicitly
send `accuracy=approximate`; omitted/`exact` requests return 400. This request option permits
approximate definitions; it does not change an exact definition to an approximate algorithm.
Exact formulas cannot contain approximate dependencies. Approximate accuracy is limited to
quantiles/formulas; ordinary counts,
sums, averages, minima and maxima retain their existing algorithms. Quantiles currently use raw
data, with a plan explanation. `accuracy` describes algorithm choice, not a guarantee of arbitrary
precision floating-point arithmetic, identity deduplication or transactional snapshots across
concurrent writes.

## Administrative explanations

Authenticated `GET /api/v1/analytics/metrics/{name}/explain` and
`GET /api/v1/analytics/reports/{name}/explain` accept the execution endpoint's parameters. They
require a signed literal `role=admin` claim. When read scopes are enabled, they additionally need
`plans` and the corresponding `metrics` or `reports` grant (or `*`). They retain project, tenant,
segment, parameter and unit checks.

Responses show source, accuracy, selected logical rollup, candidate eligibility/reasons and
bounded range slices. They execute no analytics query and expose no SQL, parameter values,
execution statistics or workload recommendations. Planning metadata reflects readiness at
selection; it is not a linearizable snapshot across concurrent ingestion or membership changes.

## Rollout and boundaries

Start with raw execution and compare functional results for representative authorized selections.
Review source identity, rollup rebuild/completion, replay bounds, raw retention and append-only
preconditions before enabling planning. Disable planning or force `execution=raw` to restore the
reference path. Retain configuration and physical schemas needed by pending WAL/delivery manifests
when rolling back an executable or changing declarations.

The selected C scope excludes runtime scripts/SQL, arbitrary stored ingest transforms, historical
user/session cohorts, external enrichment connectors, automatic rollup creation, result caching,
live definition CRUD/deployments, hosted operations and future recommendation experiments.
Benchmarks, profiling and performance claims remain stopped under the owner's instruction.
