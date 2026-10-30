# A5 verification review

Persisted rollup readiness is implemented at 62d1247. Declared rollups initially use raw data;
successful completion is tied to the source, target and view UUIDs, the declared definition,
the stored definitions and whether historical backfill completed. An existing unmarked table
or view never proves completion, and partial aggregates are not backfilled again automatically.

The final portable unit run passed 1,680 cases. The full integration run passed all 168 cases on
a fresh, isolated ClickHouse 25.3.14.14 instance with two CPUs and a 2-GiB limit. The twenty new
database cases include committed-prefix failure, restart, drift, replaced objects, concurrent
creators, cancellation, missing metadata privileges and first-start schema creation. The cold-start
case owns an additional disposable server, so a pre-existing metadata table cannot mask missing
DDL. Cleanup removes only uniquely owned rollups, rows, users and completion records.

The immutable-source complete-level mutation run killed all 99 executable mutants, with no
survivors, uncovered mutants or timeout detections. Its selected scopes cover the new readiness
files, startup and rollup reconciliation methods, initial routing state and rejection-reason
lookup. Character spans in the existing large files must be regenerated when those source
positions change. Combined same-source unit/integration collectors cover all 172 maintained
executable lines and 40 branch outcomes in this scope. Generated logging code is outside those
maintained-code totals; this is not repository-wide coverage.

The first mutation run scored 66.94%. It exposed missing first-start and diagnostic assertions,
redundant control flow and unnecessary ConfigureAwait policy. These were corrected without
reducing thresholds or excluding maintained changed logic. A failed fresh-server test then
exposed cleanup assuming the metadata table already existed; cleanup now checks existence.
The suite's source guard also required the explicit owned-rollup prefix in a replacement test.
Both issues were fixed before the clean full run.

An aged test database accumulated enough resident memory for ClickHouse to reject backfill and
cleanup work at its cap. Those failures remain evidence of required resource-budget work. The
new routing gate correctly kept incomplete rollups unavailable. Normal qualification now uses
an isolated database per phase and derives its published port after startup. No developer data
or unrelated container was removed, and the aged database remains available for pressure work.

This gate does not provide resumable rebuilds, an online backfill/live-insert fence, persisted
delivery units or unlimited duplicate protection. A6 and the remaining resource/durability
packages must establish those guarantees before category A can be complete. Existing unmarked
rollups use raw queries until a supported rebuild establishes completion. Older binaries ignore
the proof and therefore do not provide an equivalent rollback guarantee.

Archy preflight remains unavailable without configured layer rules. Existing executable style,
architecture and domain-neutrality guards pass in the full unit suite. Main is untouched.
