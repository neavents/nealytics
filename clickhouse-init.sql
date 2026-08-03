CREATE DATABASE IF NOT EXISTS nealytics_core;

-- The core columns, and only the core columns.
--
-- This table used to name three of one customer's dimensions — a menu, a section, a table id —
-- compiled into an open-source engine. They are gone from here and from the source: a deployment
-- declares its own dimensions in configuration (TelemetryEngine:Dimensions), and
-- ClickHouseSchemaMigrator creates the matching columns at startup. The names still land in this
-- table as real typed columns; what changed is only where the names come from.
--
-- Real columns rather than a JSON bag, deliberately: ClickHouse cannot GROUP BY a field inside a
-- JSON string without parsing every row, which is exactly why per-dimension analytics once
-- reported itself *not measured* while the data was arriving and being thrown into a string
-- nobody could query.
--
-- object_id is "the thing this event is about" — generic, and every analytics engine has one.
--
-- device_class / os / browser / country come from the edge, which sees the User-Agent and
-- Cloudflare's request metadata. The client never sends them, so they cannot be spoofed by a
-- caller and they cost the source document's byte budget nothing.
CREATE TABLE IF NOT EXISTS nealytics_core.global_events
(
    event_id UUID,
    project_id LowCardinality(String),
    tenant_id String,
    session_id String,
    user_id Nullable(String),
    event_type LowCardinality(String),
    object_id Nullable(String),
    device_class LowCardinality(String) DEFAULT '',
    os LowCardinality(String) DEFAULT '',
    browser LowCardinality(String) DEFAULT '',
    country LowCardinality(String) DEFAULT '',
    metadata_json String CODEC(ZSTD(1)),
    timestamp DateTime64(3, 'UTC')
)
ENGINE = ReplacingMergeTree()
ORDER BY (project_id, tenant_id, event_type, timestamp, event_id)
TTL toDateTime(timestamp) + INTERVAL 90 DAY DELETE
SETTINGS index_granularity = 8192, ttl_only_drop_parts = 1;

-- This file only runs via docker-entrypoint-initdb.d, i.e. once, on an empty data volume. An
-- existing deployment never sees it, so the core columns above are added idempotently at service
-- startup by ClickHouseSchemaMigrator. Keep the two in step. Declared dimensions are never listed
-- here — the engine does not know them at build time, and the reconciler is the only thing that
-- creates them.
