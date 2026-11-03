# Saved reports

Combine up to 16 [named metrics](named-metrics.md) into one ordered response. Reports activate
at restart through the existing self-hosted executable. They use one parameterized aggregate
query and one read connection lease, with no work added to ingestion.

## Declare

```json
{
  "TelemetryEngine": {
    "NamedMetrics": [
      { "Name": "actions", "Aggregation": "count", "EventType": "action", "Unit": "events" },
      { "Name": "other_actions", "Aggregation": "count", "EventType": "other" }
    ],
    "EventSegments": [
      { "Name": "selected", "Filters": ["country:GB"] }
    ],
    "SavedReports": [
      {
        "Name": "overview",
        "Version": 1,
        "Segment": "selected",
        "Fields": [
          { "Name": "primary", "Metric": "actions" },
          { "Name": "secondary", "Metric": "other_actions" }
        ]
      }
    ],
    "MaxSavedReports": 128
  }
}
```

Use the existing schema-file/configuration providers and environment overrides. Names and field
aliases match `[a-z][a-z0-9_]{0,62}`. Configuration names/references are trimmed; lookup is case
sensitive. Every report needs 1–16 fields with unique aliases. Reusing a metric under distinct
aliases is allowed. Missing metric/segment references, duplicate names, unsupported versions,
and invalid limits prevent activation. `MaxSavedReports` supports 1–1024 and defaults to 128.
Version 1 identifies the declaration format, not a result revision or cache key.

The optional segment applies to every field. Each metric also retains its own event-type
restriction. A report with two different event types therefore computes each metric from its
matching rows within the shared authorized selection. An unrestricted metric includes all
event types in that selection.

## Execute and discover

```sh
curl --get "$NEALYTICS_URL/api/v1/analytics/reports/overview" \
  --header "Authorization: Bearer $TOKEN" \
  --data-urlencode 'from=2026-11-03T00:00:00Z' \
  --data-urlencode 'to=2026-11-04T00:00:00Z' \
  --data-urlencode 'filter=country:GB'
```

Accepted query parameters are `from`, `to`, `traffic`, repeated `filter`, tenant-set narrowing
via `tenant`, declared `param.<name>` values, `execution` and `accuracy`. Other parameters return
400, including `segment`: callers cannot replace the
configured segment. Unknown reports return 404. Invalid identity/input checks precede storage
access. When both the report name and parameters are invalid, identity checks run first, then
report lookup, then input validation.

Existing JWT project/tenant and tenant-set membership constraints apply to all fields. If
`ReadScopeClaim` is configured, execution requires `reports` or `*`; discovery requires `schema`
or `*`. A `metrics` token alone does not grant report access. Missing credentials return 401
and insufficient scope returns 403.

Ranges use UTC `[from, to)`, normalize to stored milliseconds, and support years 1900–2299.
Omitted bounds use the configured lookback and current UTC time. Traffic defaults to `normal`;
`all` must be explicit. Typed filters retain the existing allowlist and 256-character value
bound. At most 32 caller filters combine with up to 32 segment filters using AND.

```json
{
  "name": "overview",
  "version": 1,
  "from": "2026-11-03T00:00:00Z",
  "to": "2026-11-04T00:00:00Z",
  "segment": "selected",
  "fields": [
    { "name": "primary", "metric": "actions", "value": "3", "unit": "events" },
    { "name": "secondary", "metric": "other_actions", "value": "1" }
  ],
  "numericEncoding": "string",
  "source": "raw"
}
```

Fields preserve declaration order and aliases. Values use the named-metric numeric contract:
integer sums widen to Int256, numeric strings preserve exact integer/decimal results, empty
counts/sums return `"0"`, and empty minima/maxima/averages return explicit null. Nonfinite
floating results become null; floating arithmetic retains its native precision. Counts mean
stored rows or nonnull measure values, without adding deduplication or user identity semantics.

Every field's unit safety check runs before query execution. A non-count metric with a unit
dimension requires an equality constraint on that dimension from the shared segment or caller
filters. A report either validates and returns all fields or returns an error. Units are labels;
reports do not perform conversions. Conflicting equality filters produce an empty selection.

Authenticated `GET /api/v1/schema` publishes `savedReports` alongside existing definitions,
without compiled metric references. An empty configuration publishes an empty collection.

## Boundaries and rollout

Reports support typed formulas, fixed grouping/time series, declared parameters, computed
dimensions and authenticated project catalogs. Compatible rollup/hybrid planning is explicitly
opt-in; raw execution remains available. See [the customization guide](analytics-customization.md)
for configuration, limits, accuracy, source-policy requirements and administrative explain.
Per-field segments, arbitrary SQL, caching, live editing and hosted services remain outside scope.
The fixed field bound caps query/response shape; the selected time range still determines the
amount of event data read. One query is a structural property, not a measured speed claim.

Definitions require a restart. Remove a report declaration to disable its name. No DDL or data
rewrite is introduced. Report clients must stop using the new endpoint before rolling back to
an older executable. Existing ingestion callers and named-metric endpoints keep their contracts.
