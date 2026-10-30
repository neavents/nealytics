# Named metrics

Named metrics are operator declarations in the existing `TelemetryEngine` configuration.
They activate at restart and run through the existing self-hosted executable and ClickHouse.
Ingestion requests stay unchanged.

## Declare

```json
{
  "TelemetryEngine": {
    "MaxNamedMetrics": 128,
    "Measures": [
      {
        "Name": "elapsed_ms",
        "Type": "UInt64",
        "Aggregations": "sum,avg,max"
      }
    ],
    "NamedMetrics": [
      {
        "Name": "actions",
        "Version": 1,
        "Aggregation": "count",
        "EventType": "action",
        "Unit": "events"
      },
      {
        "Name": "total_elapsed",
        "Version": 1,
        "Aggregation": "sum",
        "Measure": "elapsed_ms",
        "EventType": "action",
        "Unit": "ms"
      }
    ]
  }
}
```

Use `TelemetryEngine__SchemaFile` as with the existing schema file. Environment overrides still
apply. `Version` identifies the declaration format; only version 1 is supported. It is not a
definition revision or a cache key. There is no live reload or result cache in this release.

Names match `[a-z][a-z0-9_]{0,62}` and must be unique. A metric without a measure supports `count`.
With a measure, `count`, `sum`, `avg`, `min` and `max` must also be permitted by that active measure.
Missing/retired measures, unsupported aggregations or versions, duplicate names and exceeded
limits fail before activation. The default declaration limit is 128; the supported maximum is
1024. Event types and unit annotations are limited to 256 characters.

An omitted event type includes all event types. A configured event type always constrains the
metric; callers may add typed filters. Declarations are global to the deployment; query data is
scoped to the authenticated project and tenant. Project-specific definition catalogs come later.

## Discover and execute

`GET /api/v1/schema` adds a `namedMetrics` collection. Existing `metrics`, dimensions, measures
and rollup fields retain their contracts. No declarations produces an empty collection.

```sh
curl --get "$NEALYTICS_URL/api/v1/analytics/metrics/total_elapsed" \
  --header "Authorization: Bearer $TOKEN" \
  --data-urlencode 'from=2026-10-30T00:00:00Z' \
  --data-urlencode 'to=2026-10-31T00:00:00Z' \
  --data-urlencode 'filter=country:GB'
```

The query uses JWT project/tenant claims, including existing tenant-set membership and narrowing.
If `ReadScopeClaim` is configured, execution requires `metrics` or `*`; discovery still requires
`schema` or `*`. Request parameters cannot select another project's data. Unknown names return
404; invalid ranges, filters or unit combinations return 400. Missing/insufficient credentials
retain the existing 401/403 behavior.

Filters use the existing allowlisted dimension-equality and measure-comparison syntax. At most
32 are accepted, with the existing 256-character value limit. Traffic defaults to `normal`;
`traffic=all` includes other classes explicitly.

New metric ranges use UTC `[from, to)`, with an exclusive end, and normalize bounds to the stored
millisecond precision. Years 1900 through 2299 are supported. Omitted bounds use the configured
lookback and current UTC time. Time values without an offset are interpreted as UTC. Equal or
reversed normalized bounds are rejected. Legacy endpoint time semantics remain unchanged.

```json
{
  "name": "total_elapsed",
  "version": 1,
  "from": "2026-10-30T00:00:00Z",
  "to": "2026-10-31T00:00:00Z",
  "value": "9007199254740995",
  "unit": "ms",
  "numericEncoding": "string",
  "source": "raw"
}
```

`value` is a numeric string to preserve integer and decimal results without JSON binary64
coercion. Decimal trailing zeros are not retained as a formatting contract. Integer sums widen
to Int256; other aggregations follow ClickHouse's numeric semantics. Floating values and
averages retain their native floating-point precision. Nonfinite results become explicit JSON
null. Empty counts/sums return `"0"`; empty averages/minima/maxima return null.

Counts represent stored rows, or nonnull measure values when a measure is named. They do not
introduce UUID deduplication, stitched identities or distinct-user semantics. Execution uses raw
events; it does not silently select an aggregate rollup.

## Units and changes

If a measure declares `UnitDimension`, non-count metrics require an equality filter on one unit,
such as `filter=unit_key:base`. A named metric cannot bypass the existing protection against
adding unlike units. Unit annotations describe values; the engine does not convert units.

Remove a declaration to disable its name at the next restart. Definitions themselves introduce
no physical schema migration or rewrite. Existing measure/dimension changes retain their
ordinary migration requirements. Formulas, reusable segments, reports and live deployment APIs
are separate follow-up packages.
