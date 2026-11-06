# D2 import-job discovery local qualification

Locally qualified runtime: e44d9d1 on feat/developer-operator-tools-d2-import-job-discovery, 2026-11-06.
Base: local operator prerequisite merge 5880a8715f191ce0568522285cab0c3aeada5dcd,
including locally qualified E7. Main is untouched and benchmarks remain stopped.

The optional existing import-job routes now include bounded, project-pinned read-only discovery.
Each live page returns existing public receipts in ascending UUID order, with an exclusive cursor,
default 50 and maximum 100 records. The store remains capped at 4,096 states; pages are detached
under the existing lock and serialized after it is released. No provider calls, manifest/WAL
writes, new persistent version, library, queue or service are introduced. Existing producer and
job contracts stay compatible. See the [brief](d2-import-job-discovery.md) and
[operator guide](../import-jobs.md).

## Verification evidence

- All 865 focused import-job and source/architecture/domain guard cases pass.
- Full unit suite: 4,233 passed, zero failed/skipped. The new cases include parser and page limits,
  duplicates, exact/exclusive continuation, empty/max/overflow pages, project filtering, foreign
  ordering hints, detached snapshots, concurrent state updates, restart-derived uncertainty,
  fault/disposal guards, unchanged journal bytes and real-Kestrel confirmed receipt behavior.
- Same-source unit coverage: all 52 changed maintained executable lines and 34 branch outcomes.
  Both source additions and additions to the existing store/endpoint are included. Final combined
  storage/unit coverage preserves those totals with matching PDB source hashes and no exclusions.
  Whole maintained engine: 15,185/15,707 lines and 6,179/6,553 branches; inherited gaps remain
  522 lines and 374 branches. No repository-wide 100% claim is made.
- Full real-dependency integration suite: 323 passed, zero failed/skipped. Its four existing
  managed/custom and canonical/mapped checkpoint cases now assert that ready/complete list records
  exactly match the confirmed chunk response while stored counts and aggregate sums stay intact.
- Published actual Linux ARM64: canonical and mapped NDJSON/job scenarios pass at e44d9d1 with
  custom ingestion and io_uring. Authentication, pagination, exact public receipt equality,
  restart and complete/rejected/needs_reconciliation discovery pass. These checks do not replay or
  resolve ambiguous admissions and preserve exact stored aggregate totals.
- All 129 pre-existing ranged mutation fragments are byte-for-byte preserved at their existing
  offsets. The new Complete mutation scope includes the full request parser, page, store and
  endpoint, retaining thresholds of 100%. All 123 executable mutants are killed, with zero survivors, timeouts or uncovered mutants.
  The selected report also records 13 compile-invalid and 333 filtered/structural ignored mutants.

The final solution build passes with zero warnings and errors. The bounded D2 runtime is locally
qualified; all evidence above binds to production e44d9d1. Archy remains unavailable
because no layer rule is configured and the worktree is uninitialized. Executable guards pass.
Remote CI and actual x64 remain owner-deferred after GitHub access could not be restored; they are
not passes. Raw evidence is in artifacts/verification/d2-import-job-discovery. The broader D/E goal
and category work remain open; retention, deletion, reconciliation actions, other formats, database
adapters, sinks, clients and hosted F work are not completed by this page endpoint.
