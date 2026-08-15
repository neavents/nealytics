#!/usr/bin/env bash
set -euo pipefail

# Adds a monthly partition key to an existing global_events.
#
# clickhouse-init.sql only runs on an empty data volume, so a deployment that already has data will
# never get the partition key from it — the same reason ClickHouseSchemaMigrator exists for columns.
# Unlike a column, a partition key cannot be ALTERed in: the table has to be rebuilt.
#
# Why bother. Every query this engine serves filters on timestamp, and the sort key leads with
# (project_id, tenant_id, event_type), so a query that does not pin an event type cannot prune by
# time through the primary index at all. Partitioning also turns retention into a metadata-only
# partition drop instead of a part rewrite.
#
# What this is NOT: a fix for retention being broken. It is not broken — that was tested. Expired
# rows are removed today. This is a performance change.
#
# The swap is EXCHANGE TABLES, which is atomic on the default Atomic database engine. Ingest keeps
# running throughout: the batch writer retries, and the WAL holds anything that fails during the
# swap. Nothing is dropped until the new table is verified to hold the same number of rows.

CH="${CLICKHOUSE_CLIENT:-docker exec -i neavents-nealytics-clickhouse clickhouse-client}"
DB="${NEALYTICS_DB:-nealytics_core}"
TABLE="${NEALYTICS_TABLE:-global_events}"
NEW="${TABLE}_repartitioned"

q() { $CH -q "$1"; }

echo "Target: ${DB}.${TABLE}"

existing="$(q "SELECT partition_key FROM system.tables WHERE database='${DB}' AND name='${TABLE}'")"

if [ -z "$existing" ]; then
  : # no partition key — this is what we are here to fix
else
  echo "Already partitioned by: ${existing}"
  echo "Nothing to do. Rebuilding a table that is already partitioned would copy every row for no gain."
  exit 0
fi

before="$(q "SELECT count() FROM ${DB}.${TABLE}" | tr -d '[:space:]')"
echo "Rows: ${before}"

# The new table's definition comes from the live one rather than from a file. Declared dimensions
# and measures are columns the engine created at boot and no file knows about; writing the DDL by
# hand here would silently drop them.
# TSVRaw, because the default format escapes the DDL for transport — quotes come back as \' and
# newlines as \n, which is not SQL. Feeding that back in is a syntax error at best.
create="$(q "SHOW CREATE TABLE ${DB}.${TABLE} FORMAT TSVRaw")"

new_create="$(printf '%s\n' "$create" \
  | sed "0,/${TABLE}/s//${NEW}/" \
  | sed "0,/^ORDER BY/s//PARTITION BY toYYYYMM(timestamp)\nORDER BY/")"

if ! printf '%s' "$new_create" | grep -q 'PARTITION BY toYYYYMM(timestamp)'; then
  echo "FAIL: could not place the partition key in the generated DDL. Nothing has changed."
  printf '%s\n' "$create"
  exit 1
fi

echo "Creating ${DB}.${NEW}..."
q "DROP TABLE IF EXISTS ${DB}.${NEW}"
printf '%s' "$new_create" | $CH --multiquery

echo "Copying..."
q "INSERT INTO ${DB}.${NEW} SELECT * FROM ${DB}.${TABLE}"

after="$(q "SELECT count() FROM ${DB}.${NEW}" | tr -d '[:space:]')"
echo "Copied: ${after}"

# Ingest continues during the copy, so the new table may legitimately hold FEWER rows than the
# source does by the time this runs — the rows written in between land in the old table and are
# picked up by the second copy after the swap. It must never hold fewer than the count taken before
# the copy started; that would mean rows were lost rather than merely arriving late.
if [ "$after" -lt "$before" ]; then
  echo "FAIL: copied ${after} of ${before} rows. Leaving both tables in place — nothing was swapped."
  exit 1
fi

echo "Swapping (atomic)..."
q "EXCHANGE TABLES ${DB}.${TABLE} AND ${DB}.${NEW}"

# Anything ingested during the copy is now in the table that was just swapped out. Copy it across
# before dropping, or a deploy-time repartition quietly loses a few minutes of traffic.
straggler_source="${DB}.${NEW}"
stragglers="$(q "SELECT count() FROM ${straggler_source} WHERE event_id NOT IN (SELECT event_id FROM ${DB}.${TABLE})" | tr -d '[:space:]')"

if [ "${stragglers:-0}" != "0" ]; then
  echo "Copying ${stragglers} row(s) that arrived during the swap..."
  q "INSERT INTO ${DB}.${TABLE} SELECT * FROM ${straggler_source} WHERE event_id NOT IN (SELECT event_id FROM ${DB}.${TABLE})"
fi

final="$(q "SELECT count() FROM ${DB}.${TABLE}" | tr -d '[:space:]')"
echo "Live table now holds ${final} row(s), partitioned by:"
q "SELECT partition_key FROM system.tables WHERE database='${DB}' AND name='${TABLE}'"

echo
echo "The old table is kept as ${DB}.${NEW}. Verify, then drop it deliberately:"
echo "  DROP TABLE ${DB}.${NEW}"
