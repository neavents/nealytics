# D1 checkpoint qualification ledger

Status: qualification in progress; do not treat D1 or the full roadmap as complete.
Category: feat/developer-operator-tools. Step: feat/developer-operator-tools-d1-import-checkpoints.
Base: 1aa755b61961e7c857d8cfd4370bf2880a207c0b (qualified E3).

## Implemented contract

Opt-in caller-driven bounded NDJSON chunks with persisted confirmed counters and latest retry
receipts. Processing intent is synchronized before ingestion; receipt publication follows durable
WAL/handoff. Unconfirmed processing markers recover as needs_reconciliation and reject replay.
Project scope, source/chunk hashes, contract fingerprints, limits and retained history fence writes.
Ordinary ingestion and its default backend remain compatible. See the [brief](d1-import-checkpoints.md)
and [operator guide](../import-jobs.md).

## Evidence collected so far

- Initial consolidated unit collector: 3,355 passing cases. All 468 new feature-file executable
  lines are covered. Final same-source branch/changed-code audit remains pending.
- Focused HTTP/component run: 127 passing cases before the final contract vectors and bounded
  length-read refinement. It includes real Kestrel/WAL for managed/custom execution, credentials,
  prefix receipts, retries, body/decompression bounds and project isolation.
- File faults exercise prepare/final open/write/flush/publish/directory-sync failures with real
  manifests, fault latching, lease ownership, torn replacement recovery and ambiguous admissions.
- Local actual Linux ARM64 published executable checks pass with io_uring/custom for canonical
  and mapped NDJSON, confirmed job restart/retry and terminal prefix receipts. The mapped run also
  restores an unconfirmed marker fixture while stopped and verifies recovery refuses replay.
  These local images precede the final single-read manifest-length refinement; final source
  qualification remains required on both architectures.
- All 109 existing ranged mutation responsibilities preserve identical source fragments after
  offset rebasing; 26 move with Program additions. D1 has a separate Complete mutation scope
  with thresholds 100, not yet qualified.
- An initial integration attempt could not start ClickHouse from an old incompatible test volume.
  A first fresh-run launcher had an invalid log-based readiness check and ran no tests. The corrected
  launcher uses an isolated fresh pinned server and external-port readiness. Its full suite is running.

Raw local evidence: artifacts/verification/d1-import-checkpoints. Keep failed/preliminary evidence
separate from the final qualified source. No benchmark, capacity, throughput or latency claims.

## Remaining gates

Final warning-free build; focused and consolidated same-source coverage audit; real ClickHouse
suite; Complete mutation with every survivor/no-coverage/timeout investigated; actual published
Linux x64/ARM64 checks; source/evidence review and feature-category integration. Archy preflight
currently reports no configured layer rule; archy verify reports an uninitialized workspace.
Executable architecture/style/domain guards passed in the initial full unit run; Archy did not pass.
