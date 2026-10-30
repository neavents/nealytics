# Reusable event segments

An event segment is a named conjunction of existing typed filters. Configure it alongside
[named metrics](named-metrics.md) in `TelemetryEngine`, including through the existing SchemaFile
provider. Declarations validate and compile at startup; restart to activate edits.

```json
{
  "TelemetryEngine": {
    "Dimensions": [{ "Name": "unit_key" }],
    "Measures": [{ "Name": "amount", "Type": "Int64", "UnitDimension": "unit_key" }],
    "NamedMetrics": [{ "Name": "total", "Aggregation": "sum", "Measure": "amount" }],
    "EventSegments": [
      { "Name": "selected", "Version": 1, "Filters": ["unit_key:u", "amount>10"] }
    ]
  }
}
```

Query `GET /api/v1/analytics/metrics/total?segment=selected&from=...&to=...` with the existing
read token. The response's `segment` is `selected`; its `value` follows the named-metric numeric
string contract. No selector, or an empty selector, preserves the unsegmented request. Names
are case sensitive; unknown names return 404 before storage. The configured read-scope claim
still requires `metrics` or `*`, and authenticated schema discovery requires its existing scope.
`GET /api/v1/schema` adds `eventSegments` with name, format version and declared filters.
Internal compiled predicates are not serialized. `version: 1` denotes declaration format, not a
revision, immutable snapshot identifier or cache key.

Dimension conditions use `column:value` exact equality, with values treated as literal bound
parameters. Core event columns and active declared event dimensions are allowed. An empty string
value is valid and differs from SQL NULL. Numeric measure conditions support `=`, `!=`, `<`,
`<=`, `>` and `>=`, against a finite Float64 value. These predicates use the existing filter
semantics: large integer and decimal boundaries can round, and SQL NULL does not satisfy a
comparison. The exact string encoding of the aggregate result does not make predicates exact.
Use an explicit equality filter for a measure's declared unit when summing, averaging or
finding minima/maxima; a segment can provide that filter. No unit conversion occurs.

All segment conditions AND with the caller's filters, authenticated project/tenant or tenant-set
membership, metric event type, traffic selection and UTC `[from,to)` range. Conditions cannot
override them. Contradictory conditions select no rows. These are event selections, with no
cross-event user/session matching or historical cohort snapshots. `tenant.<attribute>` membership
conditions are rejected as segment declarations; existing authenticated tenant-set scope remains
available independently.

`MaxEventSegments` defaults to 128, with a supported range of 1 to 1024. Each declaration needs
1 to 32 nonblank filters; the request independently permits at most 32 additional filters,
giving at most 64 combined conditions. Existing filter values are limited to 256 characters.
Names match `[a-z][a-z0-9_]{0,62}` after configuration whitespace trimming. Duplicate names,
unsupported versions, inactive/unknown columns and nonfinite numbers fail startup.

There is no DDL, ingestion-path processing, arbitrary SQL, OR tree, regex, nested segment,
project override or live-edit API in this package. The engine remains one executable. Remove a
definition and stop selecting its name to disable it; stored data is unchanged. Before rolling
back to C1, update clients to stop sending `segment`: C1 ignores that unknown parameter and
would execute the unsegmented query.
