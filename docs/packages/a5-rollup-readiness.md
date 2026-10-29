# A5: fail-closed rollup readiness

Branch: feat/correctness-and-safety-a5-rollup-readiness

Outcome: queries use raw data until the current physical rollup and view have a persisted,
positive completion record. A failed or canceled backfill, an unavailable schema reconciliation,
or another instance creating a view must never make incomplete aggregates routable. Keep the
existing query and ingestion APIs, single executable and ClickHouse-only dependency.

The A4 integration run exposed a real inherited failure: ClickHouse exhausted its memory limit
during a session-backfill slice, startup caught the exception, and the partially populated table
remained routable. A later restart also trusted the existing view without checking completion.
The isolated passing rerun does not resolve this defect.

Use a small ClickHouse readiness table keyed by physical table/view identity and the exact
declared definition. Write a completion record only after all required backfill statements
succeed, or after explicit forward-only creation when backfill is disabled. An old marker must
not authorize a dropped/recreated table, replaced view or changed definition. Missing metadata
and failed metadata reads mean raw fallback. Registry construction starts with no verified
rollups; a startup error before rollup reconciliation therefore stays safe.

The instance that wins view creation owns its initial backfill. Losing creators inspect persisted
completion rather than infer it from view existence. Never automatically rerun a partially
completed aggregate insert: that would duplicate counts and sums. Persisted delivery identity,
safe resumable/rebuild semantics and the live-insert/backfill boundary are separate A6 work.
This package is a safety gate, not a claim that those delivery problems are solved. An incomplete
rollup remains available for diagnosis while reads use the source table.

Acceptance: deterministic failure after a committed backfill prefix; cancellation; failure while
writing readiness; fresh-registry restart; an existing unmarked view; table/view recreation;
definition drift; concurrent creators; successful historical and explicit forward-only creation;
raw/routed query parity and scoped access on pinned ClickHouse 25.3. New state/decision logic
needs full maintained line/branch coverage and complete-level mutation checks. Repeat existing
integration and real HTTP checks. Test discovery must not manufacture ready production state.

Rollout: existing rollups without proof of completion initially use raw queries. Do not infer a
marker from their contents or run additive backfill to fabricate one. A6 supplies a supported
safe rebuild/resume path. The metadata table is additive; older binaries ignore it and do not
provide the new routing guarantee, so binary rollback cannot be advertised as safety equivalent.
