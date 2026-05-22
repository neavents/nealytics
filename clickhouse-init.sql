CREATE DATABASE IF NOT EXISTS nealytics_core;

CREATE TABLE IF NOT EXISTS nealytics_core.global_events
(
    event_id UUID,
    project_id LowCardinality(String),
    tenant_id String,
    session_id String,
    event_type LowCardinality(String),
    item_id Nullable(String),
    metadata_json String CODEC(ZSTD(1)),
    timestamp DateTime64(3, 'UTC')
)
ENGINE = MergeTree()
ORDER BY (project_id, tenant_id, event_type, timestamp)
SETTINGS index_granularity = 8192;
