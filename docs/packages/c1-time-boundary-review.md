# C1 timestamp binding review

Real calendar-edge tests exposed that the driver inferred DateTime for untyped DateTime values,
even with a DateTime64 SQL placeholder. That wire type loses fractional seconds and rejects dates
outside 1970 through 2105. Parser-only boundary tests did not establish the actual wire contract.

The named metric query now explicitly uses the driver's DateTime2 type for timestamp parameters.
Its documented representation is DateTime64 with 100-nanosecond precision. The request factory
normalizes values to stored milliseconds first, preserving the public contract while retaining
the full supported 1900–2299 calendar range. Values carry UTC kind.

This change belongs only to the new query slice. Existing endpoints retain their legacy binding
behavior; broader timestamp binding corrections require their own compatibility review. Real
server tests cover both calendar extremes and a millisecond selection that second-precision
binding cannot reproduce. The reference is the pinned client's shipped XML documentation under
ClickHouseDbType.DateTime2 and ClickHouseDbType.DateTime64.
