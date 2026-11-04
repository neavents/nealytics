# Bounded CSV upload

Implementation and qualification are in progress on feat/integrations-e5-csv-import. This guide
specifies the implemented contract; consult the package ledger before treating it as qualified.
The route is disabled by default. Existing track/beacon, Kafka, NDJSON and S3 contracts stay intact.

## Enable and declare columns

EnableCsvImport enables POST /api/v1/telemetry/import/csv independently of EnableNdjsonImport.
Declare CsvSourceMappings by project, using the existing source-mapping target vocabulary:

```json
{
  "TelemetryEngine": {
    "EnableCsvImport": true,
    "CsvSourceMappings": {
      "example-project": {
        "Fields": {
          "eventId": { "Path": ["event,id"] },
          "timestamp": { "Path": ["time"] },
          "projectId": { "Default": "example-project" },
          "tenantId": { "Path": ["tenant"] },
          "eventType": { "Default": "action" },
          "measures.amount": { "Path": ["value"] }
        }
      }
    }
  }
}
```

Declare each measure/dimension in the project's normal schema. Fields accepts 5–64 bindings,
including required identity/time/project/tenant/event targets. Each path is one literal header
name, including punctuation. Empty paths use explicit defaults. Identity and timestamp require
input columns and cannot have defaults. Nested object paths do not describe CSV columns.
Mappings and routes are frozen at startup; activating a changed declaration requires restart.

Use a server ingestion key pinned to the same project. Public/unpinned keys cannot import.
X-Project-Key takes precedence over k. A project without a configured CSV mapping returns 404.
Policy restrictions and normal sanitization apply after mapping, before durable admission.

## Format and request

Submit text/csv with UTF-8 charset or no charset. An optional header parameter must be present;
header=absent is rejected. A header row is always required. Delimiters are commas; quotes are
escaped by doubling. CRLF/LF separators, quoted embedded CR/LF and a final unterminated record are
supported. Spaces and empty cells remain literal strings. An optional UTF-8 BOM is consumed only
at the start of the header, and remains part of the reported physical source-byte position.

For the declaration above:

```csv
"event,id",time,tenant,value
8f461621-bc4a-482e-a84a-73bdc139e560,2026-11-04T00:00:00Z,example-tenant,2
```

```sh
curl --fail-with-body -sS http://localhost:5000/api/v1/telemetry/import/csv \
  -H "X-Project-Key: $NEALYTICS_SERVER_KEY" \
  -H 'Content-Type: text/csv; charset=utf-8; header=present' \
  --data-binary @events.csv
```

Each record must have the header's column count. Header names are nonempty, unique ordinal
strings up to 128 UTF-16 code units; case differs. Every referenced column must exist even if
its binding has a fallback. Extra columns are allowed and ignored after bounded parsing. Invalid
UTF-8, unescaped/incomplete quotes, unexpected characters after a closing quote and bare CR
outside quotes fail. No delimiter guessing, legacy transcoding or spreadsheet evaluation occurs.
CSV values use the same explicit identity/UTC-Z timestamp and text/measure rules as source mapping.

## Receipts and resource limits

A receipt contains acceptedRecords, nextRecord (one-based data-record position), nextByteOffset,
complete, and error on rejection. Offsets count decoded original UTF-8 source bytes, including the
header, BOM and record separators. Quoted newlines do not increment data-record positions. Header
validation admits no telemetry event, but a valid header advances the parsed source position.
A valid header-only body completes with zero accepted events. An empty body returns header_required.

Stop on the first invalid data record, retaining only the durable accepted prefix. Success uses
202; invalid input uses 400, logical-record/body byte limits use 413, and unsupported media uses
415. Authentication uses 401/403, missing mappings use 404, and queue-free admission can use 429.
Errors include invalid_header, invalid_csv, invalid_utf8, column_limit, column_count,
invalid_mapping, record_limit, record_too_large and body_too_large, plus shared policy rejections.

| Resource | Bound |
| --- | --- |
| Project declarations | 128 |
| Fields per mapping | 5–64 |
| Columns per record/header | 256 |
| Header name | 1–128 UTF-16 code units, ordinal unique |
| Logical record bytes | ImportMaxLineBytes, default 65,536, allowed 1–1,048,576 and within MaxRequestBodyBytes |
| Data records/request | ImportMaxRecords, default 1,000, allowed 1–10,000 |
| Concurrent import requests | Shared ImportMaxConcurrency with NDJSON/jobs/S3; default 2, allowed 1–64, no queue |
| Whole decoded body | Existing MaxRequestBodyBytes |

The logical-record byte budget includes delimiters, quote syntax and terminating CRLF/LF, even
inside multiline fields. It also caps mapped text, including repeated bindings and defaults.
One cleared pooled raw-record buffer plus bounded header/cell state is held per request; the
adapter does not materialize a whole file or encode an intermediate JSON row. Existing global
write/byte/WAL/backlog bounds also apply. Supported request compression uses the decoded budget.

A confirmed receipt establishes WAL persistence and dispatch handoff, not immediate database
visibility or exactly-once ingestion. An admitted event remains engine-owned after caller
disconnect. Missing receipts, storage faults or shutdown can leave an uncertain prefix; do not
automatically replay it. EventId alone does not deduplicate aggregate delivery.

CSV uploads have no D1 job/checkpoint identity or cross-chunk header state. S3 continues to accept
NDJSON only: its LF framing is not a valid CSV multiline framing rule. Durable CSV jobs, S3 CSV,
GCS/URL sources, other delimiters/encodings and Parquet remain separate packages. Disabling CSV
removes its route and leaves acknowledged WAL recovery intact. No DDL/persistent-format/default
backend change is introduced. Benchmarks remain disabled.
