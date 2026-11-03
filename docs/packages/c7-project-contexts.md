# C7 authenticated project schemas

Branch: `feat/analytics-customization-c7-project-contexts`. Selected 2026-11-03 after C6 focused
contracts passed. Package mutation gates still run from their own immutable snapshots.

Compile version-1 ProjectSchemas into bounded immutable contexts. Each context owns logical
dimensions, measures, computed dimensions, named metrics, segments, reports, tenant attributes
and permitted rollup declarations. Resolve one context per request from authenticated project
claims or a validated pinned ingestion key. Reuse it for discovery, query parsing/planning,
validation, sanitization and server-only field checks. Never select definitions by caller query
parameters or fall through to another configured project's catalog.

Keep one physical event table and a bounded union of compatible physical columns. Reject
incompatible shared column types and rollup-name definitions before starting DDL. Logical
constraints remain project-specific; the writer uses the compatible physical union and broad
numeric bounds so one project's bounds cannot erase another project's valid values. Physical
rollup readiness is shared with logical subsets. No table per tenant, per-request DDL or live
schema editing is introduced.

MaxProjectSchemas defaults to 128, supported range 1–1024. Existing field/catalog caps still
apply to each logical context and the physical union. Unknown configured-mode projects are
forbidden by default. AllowLegacyProjectSchema explicitly permits the global legacy schema for
unconfigured projects; it never makes an unpinned key valid for a configured project. With no
ProjectSchemas, existing global behavior and service overrides remain compatible.

Track, beacon, validation and tenant-attribute writes share the credential project policy.
Existing producer bodies, credentials, responses and durability remain unchanged for permitted
requests. Hidden fields retain the existing sanitize/drop diagnostics; invalid project/key
combinations reject before persistence. Readers cannot discover or filter/group by another
project's fields or execute another project's definition with the same name.

Self-hosted migration/backup and WAL schema-drift rules continue to apply. Adding physical union
columns uses the existing migrator; removing a logical declaration does not erase stored data.
Activate changes at restart and account for pending WAL manifests before physical schema changes.

Acceptance: two projects with overlapping definition names and distinct fields/types/constraints;
cross-project discovery/query/ingestion/tenant-attribute isolation; pinned/unpinned/unknown policy;
compatible union and conflict rejection before DDL; contextual sanitizers/server-only measures;
shared readiness; unchanged legacy mode; real-server and published executable checks; changed
coverage and Complete-level mutation, followed by category milestone suites. No measurements.
