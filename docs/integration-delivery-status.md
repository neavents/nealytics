# Self-hosted integration delivery status

Snapshot: 2026-11-04. This checklist uses the capabilities in the owner's supplied Tinybird
screenshots as planning input. Each delivered entry has a bounded contract and its own evidence;
category E and the broader product roadmap remain open. Hosting is outside current scope.

## Delivered slices

| Capability | Qualified scope | Important boundary |
| --- | --- | --- |
| Events ingestion | Existing track/beacon contracts, WAL persistence, optional custom dispatcher and Linux io_uring | Backend defaults stay unchanged; no current speed claims |
| Kafka | One configured project/topic/group; manual offsets after durable handoff | At-least-once replay; poison/failure stops the host; broader topology/security qualification remains |
| File upload | Streamed UTF-8 NDJSON and headered CSV, accepted-prefix receipts and bounded source mapping | CSV checkpoints, object-store CSV, Parquet and automatic source retry are separate |
| Durable import progress | Caller-driven chunk jobs, persisted latest receipt, status and contract fencing | Uncertain admissions require reconciliation; no automatic resolution, scheduling or retention |
| S3 objects | Named project-pinned sources, signed inspection/ranges, version/ETag fencing, bounded NDJSON chunks and checkpoint retries | No scans, notifications, credential discovery/refresh or other formats |
| Custom source declarations | Nested JSON bindings, defaults, dimensions/measures and existing policy validation | No arbitrary scripts, generated identities/times or externally supplied internal delivery identities |

Use the [delivery index](work-packages.md), [CSV guide](csv-import.md), [S3 guide](s3-import.md),
[Kafka guide](kafka-connector.md), [NDJSON guide](ndjson-import.md),
[mapping guide](source-mapping.md) and [job contract](import-jobs.md) for precise scopes.

## Remaining integration groups

| Group | Separate implementation slices | Acceptance boundary |
| --- | --- | --- |
| Additional file formats | CSV checkpoint/object-store framing; Parquet separately | Explicit schema mapping, bounded decoding and format-specific accepted-prefix/checkpoint rules |
| Additional object/HTTP sources | GCS; then named operator-controlled HTTP sources | Credentials, immutable validators, exact range semantics, redirect/endpoint policy and real protocol qualification per adapter |
| Kafka operational breadth | Secured broker fixtures, independent source health, operator-controlled poison recovery, additional topic/group declarations | Resource/assignment limits and replay semantics must stay explicit |
| Database changes | One PostgreSQL or MySQL snapshot/CDC workflow; DynamoDB separately | Snapshot-to-stream boundary, offsets, deletes/schema changes and restart semantics |
| Exports and sinks | One bounded S3 export, then Kafka/GCS separately | Stable output manifests/identities, retry behavior and partial-publication handling |
| Client integrations | Optional query clients, BI protocol adapters and scoped read-only MCP | Authentication, parameter/schema discovery and query budgets; no mandatory client service |

CSV upload is qualified with an explicit header, strict UTF-8, quoted commas/quotes/newlines,
literal source bindings and bounded accepted-prefix ingestion. Object-store CSV framing and durable
cross-chunk CSV resume still need separate header/record-state contracts; S3's NDJSON LF framing
cannot be reused for quoted multiline CSV unchanged. Named HTTP sources and GCS remain the next
source-expansion group; they need provider-specific validators, credentials and transport tests.

## Operator prerequisites that stay in D

Checkpoint retention, explicit reconciliation decisions, source/job visibility, token lifecycle,
RBAC/audit information, deployment validation, backup/restore and upgrade workflows are operator
features. Build them as bounded workflows alongside the connector that needs them. Import caps
must not imply silent eviction or automatic ambiguous replay. Data branches and general background
scheduling require their own persistent-storage design.

Cloud provisioning, billing, managed regions and seat/organization administration remain future F
work. Existing self-hosted deployments can consume external Kafka/object stores without a hosted
Nealytics control plane. Compliance attestations require organizational assurance work alongside
engine controls; they cannot be completed by adding an API option.

No package in this checklist authorizes benchmarks, changes existing producer contracts or merges
feature work into main. Keep one category branch and a separate bounded-step branch per delivery.
