CREATE DATABASE IF NOT EXISTS nealytics_core;

-- Dimensions below `metadata_json` are first-class columns on purpose.
--
-- menu_id, section_id and table_id all used to live inside metadata_json, which meant no
-- aggregate could group on them: ClickHouse would have had to parse every row's JSON. That is
-- exactly why the dashboard reported "menus", "sections" and per-menu analytics as *not
-- measured* — the data was arriving and being thrown into a string nobody could query.
--
-- device_class / os / browser / country come from the edge, which sees the User-Agent and
-- Cloudflare's request metadata. The client never sends them, so they cannot be spoofed by a
-- caller and they cost the 14.3 KB document budget nothing.
CREATE TABLE IF NOT EXISTS nealytics_core.global_events
(
    event_id UUID,
    project_id LowCardinality(String),
    tenant_id String,
    session_id String,
    user_id Nullable(String),
    event_type LowCardinality(String),
    item_id Nullable(String),
    menu_id Nullable(String),
    section_id Nullable(String),
    table_id Nullable(String),
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
-- existing deployment never sees it, so the same columns are added idempotently at service
-- startup by ClickHouseSchemaMigrator. Keep the two in step.
