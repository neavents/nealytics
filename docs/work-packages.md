# Nealytics delivery categories and work packages

Status: Implementation index 0.2

Owner update, 2026-10-30: all benchmarking and performance measurement are stopped. Continue
the A/B implementation and functional qualification using the engineering rules. Measurement
requirements in the original package descriptions are deferred by this instruction. Do not
resume them or switch defaults automatically.

Date: 2026-10-29

Purpose: A short execution index for the [detailed roadmap](product-strategy-and-roadmap.md).
The A/B functional milestone is complete and C is the current feature track; consult the
[evidence ledger](implementation-status.md) for passed and remaining gates. Categories organize scope; they are not single
implementation tasks, delivery estimates, or a commitment to implement every possible integration.

Implementation must follow the owner's [engineering rules and branch policy](engineering-rules.md):
domain-agnostic SOLID code, one executable, low memory, comment-free production source, short
comments allowed in tests, extensive coverage/mutation/integration/end-to-end checks, and separate
category/step branches with no integration into main. Creating branches does not start a package.

## 1. Six categories

| Category | What belongs here | Boundary | Priority |
| --- | --- | --- | --- |
| A. Correctness and resource safety | HTTP/WAL contracts, replay and rollup correctness, bounded queues/recovery, read/write capacity | Establish guarantees and contain failures; no new product APIs in the same change | Required foundation, with targeted fixes alongside B |
| B. Ingestion performance | Native Linux io_uring WAL, managed fallback, custom ingestion dispatcher, microbatching, body-reader/tracing tuning | Existing producers send the same requests and receive the same durable acknowledgment contract | Immediate product priority |
| C. Analytics customization | Named metrics, event segments, saved reports, typed transforms, project schemas, query planning, rollup accuracy modes | Define and execute analytics; separate definition changes from physical data migrations | Next product area after the ingestion foundation |
| D. Developer and operator tools | Schema/deployment APIs, jobs/progress, SDKs, local workflows, quarantine, scoped deletion, usage/audit information, RBAC/token lifecycle, optional admin UI/SSO | Management and background work use explicit budgets outside the hot path | Incremental; build only the piece needed by the next capability |
| E. Optional integrations | File/URL imports, Kafka, S3/GCS, database/DynamoDB adapters, exports/sinks, MCP, BI adapters | One adapter per package; optional runtime components and separately tested delivery semantics | Select from concrete customer needs |
| F. Future hosted platform | Provisioning, billing, organization/seat management, managed regions, coordinated multi-region operations | No mandatory hosted dependency in the engine | Outside current implementation scope |

Some features span categories: the jobs machinery belongs in D, while an S3 export job is an E
adapter. Shared reference sources and controlled table functions belong in C; their external
source adapters belong in E. Data branches belong in D but require their own storage design.
Self-hosted installation, backup/restore, and upgrade work remain current operator responsibilities
in D; they are not postponed with F. SOC 2 examinations and HIPAA deployment obligations are
organizational assurance work, with supporting controls in A/D, rather than an engine feature ticket.

The older P0-P5 labels are broad milestones, not executable tickets. The H/W/C/T identifiers in
the technical roadmap identify proposals. Use the packages below when selecting actual work.

## 2. First delivery track: transparent native ingestion

| Package | One outcome | Depends on | Completion evidence |
| --- | --- | --- | --- |
| A1. Latency baseline | A repeatable track/beacon workload measuring acknowledgment p95/p99, offered load, errors, CPU, and visibility | Current engine | Saved run manifest and repeatable reference results |
| A2. Compatibility and durability fixtures | Encode the existing HTTP contract and WAL persistence/dispatch behavior | Current engine | Tests exercise unchanged requests, partial beacon input, disconnects, restart, and failed flushes |
| B1. WAL I/O boundary | Separate write/flush mechanics from shared WAL serialization/accounting using the existing managed backend | A2 | Existing behavior and WAL files work unchanged; no default/backend switch |
| A3. Bounded WAL admission | Bound pending bytes/records and flush groups before raising producer capacity | A2 | Saturation stays within configured bounds and preserves acknowledged records |
| B2. Native backend | Implement Linux io_uring writes/completions behind the I/O boundary, initially outside live ingestion | B1 | Linux x64/ARM64 packaging and file-level completion/flush/error tests pass |
| B3. Native WAL integration | Use the native backend through the existing ingestion handlers with operator selection | A2, A3, B2 | Same WAL/accounting/recovery suite passes for native and managed modes; unsupported-host behavior is explicit |
| B4. Beacon microbatches | Submit bounded groups of valid beacon records through one WAL durability barrier | A2, A3, B1 | Existing accepted-prefix/rejection behavior survives; low-concurrency latency and memory bounds are measured |
| B5. Custom ingestion path | Introduce the custom dispatcher behind the existing HTTP routes, first on managed I/O | A2, A3, B4 | Captured request/response and stored-event parity; independent latency results |
| B6. Qualify and enable | Combine the custom path with native WAL and qualify default selection on supported hosts | A1, B3, B5 | Four-combination benchmark, crash parity, bounded resources, and measured p95/p99 improvement for named workloads |

Queue order: A1/A2, B1, A3, B2, B3, B4, B5, B6. A1 and A2 are separate packages. B4 can be
developed independently after its dependencies, but do not combine its implementation with the
B3 native-WAL change in one review. Likewise, keep the B5 dispatcher separate from B3/B4.

No package in this track includes new connectors, changed event schemas, arbitrary SQL, cloud
hosting, or weakened durability. Native I/O remains required; measurements qualify behavior and
default enablement rather than remove the requirement. SQPOLL and other native tuning get separate
follow-up packages after the base backend works.

Replay/rollup defects discovered by A2 require their own A-category fixes before a release relies
on the affected totals. They must not be hidden inside a throughput patch. Likewise, reserved
database capacity and streaming recovery are separate A packages, not extra scope attached to B3.

A4 now has a separate [bounded streaming-recovery brief](packages/a4-streaming-recovery.md).
It bounds the recovery reader and batch owner while retaining the current record format. Durable
segment rotation/delivery identities and reserved capacity remain separate A requirements.

A3b has an [encoding reservation brief](packages/a3b-encoding-reservations.md) after B4 exposed a
valid-record rejection at tight byte limits. It keeps actual output bounded while separating the
JSON writer's scratch reservation from committed bytes.

A5 has a [rollup readiness brief](packages/a5-rollup-readiness.md) after a real memory-pressure
failure exposed routing to partial backfill results. It establishes persisted completion before
routing; stable delivery identity and resumable rebuild semantics remain separate A6 work.

B5 has a [custom ingestion brief](packages/b5-custom-ingestion.md). Implementation retains managed
execution as the default and adds direct bounded pipe reading and caller-independent durable
handoff in custom mode. Broad qualification is consolidated at implementation milestones.

## 3. How to split the later work

C1 is functionally qualified on `feat/analytics-customization-c1-named-metrics`; see the
[named metrics brief](packages/c1-named-metrics.md) and
[verification evidence](packages/c1-verification.md). The category branch includes the qualified
A/B prerequisites through a normal feature merge. The owner authorized continuing beyond A/B;
C remains the current implementation track, with D/E following bounded customer workflows.

C2 is active on `feat/analytics-customization-c2-event-segments`; see the
[reusable event-segment brief](packages/c2-event-segments.md). It adds one bounded AND selection
to named metrics without changing ingestion or introducing user/session cohort semantics.

The [C1 timestamp review](packages/c1-time-boundary-review.md) records an inherited DateTime
parameter-inference limitation in legacy reads. The new metric slice binds timestamps explicitly;
broader legacy corrections remain a separate compatibility review.

Deliver an end-to-end slice per package, rather than a whole subsystem:

- C: one named metric through declaration, validation, execution, and schema discovery. Add formula
  composition, reusable segments, project overrides, and hybrid plans in separate follow-ups.
- D: one resumable backfill job with persisted status. Add scheduling, exports, deletion, and
  general job APIs only as distinct workflows become necessary.
- E: one NDJSON file import before multiple formats and connectors. Treat each connector's
  credentials, checkpointing, retries, and compatibility as its own delivery scope.

Each package needs a named owner/selected scope when work starts, but categories do not imply
separate services, teams, or permanent plugin frameworks. Preserve the simple self-hosted topology.

## 4. Package rules

Before implementation, write a short package brief containing:

1. User-visible outcome and explicit exclusions.
2. Current behavior and compatibility requirements.
3. Affected code/data boundaries and dependencies.
4. Main failure modes, including data/authorization effects where applicable.
5. Acceptance checks appropriate to the changed behavior and a focused performance check if needed.
6. Rollout/disable path and whether persistent data can safely be read by the previous version.

Keep changes independent: backend extraction, a new backend, a new dispatcher, and default
enablement are different changes. If a package introduces an unrelated failure mode, split it.
An operator switch is useful rollout control, but it does not reverse DDL or guarantee rollback
of changed persistent formats. Do not promise a fixed duration before the brief is understood.

Work proceeds sequentially through bounded packages. C-E are not simultaneous projects.
Review one package's evidence before building on its changed contract; retain the longer roadmap
as design context rather than a checklist to execute in one turn.
