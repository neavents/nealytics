CREATE DATABASE IF NOT EXISTS nealytics_core;

CREATE TABLE IF NOT EXISTS nealytics_core.global_events
(
    event_id UUID,
    project_id LowCardinality(String),
    tenant_id String,
    session_id String,
    user_id Nullable(String),
    event_type LowCardinality(String),
    object_id Nullable(String),
    seq UInt32 DEFAULT 0,
    traffic_class LowCardinality(String) DEFAULT 'normal',
    page_path String DEFAULT '',
    referrer LowCardinality(String) DEFAULT '',
    device_class LowCardinality(String) DEFAULT '',
    os LowCardinality(String) DEFAULT '',
    browser LowCardinality(String) DEFAULT '',
    country LowCardinality(String) DEFAULT '',
    metadata_json String CODEC(ZSTD(1)),
    timestamp DateTime64(3, 'UTC') CODEC(Delta, ZSTD(1)),
    ingested_at DateTime64(3, 'UTC') DEFAULT now64(3) CODEC(Delta, ZSTD(1)),
    INDEX idx_object_id object_id TYPE bloom_filter(0.01) GRANULARITY 4
)
ENGINE = ReplacingMergeTree()
PARTITION BY toYYYYMM(timestamp)
ORDER BY (project_id, tenant_id, event_type, timestamp, event_id)
TTL toDateTime(timestamp) + INTERVAL 90 DAY DELETE
SETTINGS index_granularity = 8192, ttl_only_drop_parts = 1;
