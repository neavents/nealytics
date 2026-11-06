# Factory-reset development handoff

Snapshot: 2026-11-06. This is a recovery checkpoint, not a release or a claim that
the current D3 implementation is qualified. Never merge this checkpoint into main
without completing its remaining verification.

## Resume here

- Current step: `feat/developer-operator-tools-d3-kafka-source-health`.
- Last qualified prerequisite: `5629470e0a8e2eb89fc709a1c98f8cc2698e1114`.
- Integrations category: `3c6555c237bc3ffe6293b58f996ca8fc3e457688`.
- Main must remain `cc8623a7d5f01fbe9834a493cc98e623bf03059e`.
- Goal: continue the remaining bounded roadmap packages. D3 is the current package;
  broader D and E work remains. Hosted services are outside current scope.
- Authoritative planning: [delivery index](work-packages.md),
  [implementation status](implementation-status.md),
  [integration status](integration-delivery-status.md),
  [engineering rules](engineering-rules.md).

The original checkout was in an unfinished rebase before this work. Do not abort,
continue, reset, clean or overwrite that checkout as part of restoring this branch.
Its loose planning documents and rebase state require the separate private backup.

## Non-negotiable owner requirements

- Single self-contained executable/container, low memory footprint, domain agnostic.
- Customizability, speed and efficiency guide design. SOLID, cohesive feature slices,
  readable multiline formatting, explicit production local types and sealed types
  where appropriate. Match the existing source style.
- No production source, SQL or configuration comments. Short test comments are allowed.
- Existing producer routes, keys, payloads, responses and durable acknowledgement
  semantics must remain unchanged. Optional native/custom paths remain optional;
  current backend defaults stay unchanged.
- All benchmarks, throughput ceilings, profiling and performance measurements are
  stopped by owner instruction. Do not resume them or publish new speed claims.
- Focused tests during development; broad milestone gates before qualification.
  Extensive unit, integration, HTTP, durability, coverage, mutation and applicable
  actual native/published-platform checks are required. Report inherited gaps.
- Do not run Stryker alongside local .NET builds/tests sharing build outputs.
- Keep category and bounded step branches. Never merge or push feature work to main.
- Do not weaken checks or mark an incomplete package qualified to unblock delivery.

## Last completed packages

A correctness, B native/custom ingestion and selected C1-C8 customization slices have
their recorded bounded qualification. This does not mean every broader roadmap item
is complete. Historical performance results are not current performance claims.

Recent integration/operator deliveries:

| Package | Result | Qualification ledger |
| --- | --- | --- |
| E6 HTTP | Named NDJSON objects, strong validators, exact ranges, private keyed snapshots and durable chunk receipts | [E6](packages/e6-http-import-verification.md) |
| E7 GCS | Generation-pinned XML HMAC objects, metadata fencing and checkpoint retries | [E7](packages/e7-gcs-import-adapter-verification.md) |
| D2 jobs | Project-pinned keyset-paginated import-job discovery | [D2](packages/d2-import-job-discovery-verification.md) |
| E8 Kafka | TLS, PLAIN and SCRAM over TLS; rejection, restart/offset and poison-prefix checks | [E8](packages/e8-kafka-security-verification.md) |

The last E8 milestone passed 4,233 unit and 348 integration tests, detected all 185
executable Kafka mutants and passed thirteen actual ARM64 published scenarios.
Whole maintained coverage was 15,185/15,707 lines and 6,179/6,553 branches; the
522-line/374-branch gaps were inherited. These are E8 results, not D3 results.
Remote CI and actual x64 checks were deferred by the owner because GitHub credentials
could not publish workflow changes. Live Google-service qualification remains unrun.

## D3 checkpoint: implemented, qualification unfinished

Brief: [D3 source health](packages/d3-kafka-source-health.md).

Optional Kafka registers `GET /api/v1/telemetry/sources/kafka/default`, using existing
server keys pinned to the configured project. The public snapshot contains only
`name`, `state`, `issue` and UTC `changedAt`, with no-store responses. It exposes no
broker/topic/group, credentials, payloads or native error text. Disabled Kafka has
no route or service. Global health/readiness semantics stay unchanged.

This reports observed connector state, not an active probe, broker lag, freshness
or delivery guarantee. Assignment/revocation/loss/error callbacks observe state only;
they never assign partitions or commit offsets. Successful manual commit confirms
progress. Terminal failure/stop states are fenced. Steady assigned confirmation
uses a fast volatile read without allocating snapshots or reading the clock.

The latest fix gives assignment loss higher priority than subsequent transport
errors, while security errors take precedence over both. Without that ordering,
one recovery test could lose its `assignment_lost` observation.

Evidence at checkpoint:

- Focused Kafka unit run: 140 passed before the final priority fix and added
  regression assertion. Re-run on the checkpoint before claiming final results.
- First expanded integration run: 35 passed, one failed waiting for assignment
  loss. Retain this failed evidence; it led to the priority fix.
- Recovery-only re-run after the fix: both real broker-stop/restart cases passed.
- The D3 ARM64 container image built successfully as
  `nealytics-d3-source-health:local`; Docker images are rebuildable, not Git content.
- Updated published qualification scripts compile, but D3 published scenarios,
  full suites, final coverage and Stryker have not run.
- Existing mutation configuration offsets were rebased once, preserving 129
  ranged responsibilities. Do not blindly rerun the rebase helper using an old
  HEAD against already rebased configuration offsets.
- Archy preflight is unavailable because no layer rule is configured; CLI
  verification is also unavailable in the worktree. Do not initialize or claim it
  passed. Run the repository's executable architecture/source guards.

Private raw evidence is under `artifacts/verification/d3-kafka-source-health`, with
`focused-integration`, `focused-recovery`, logs, scope-preservation audit and helper
scripts. Other package evidence is under neighboring verification directories.
Those ignored files are not included in an ordinary Git push.

## D3 next actions

1. Restore the checkpoint in a clean development checkout. Review the source-health
   state machine, authorization tests, callback wiring and broker outage helpers.
2. Re-run focused Kafka units and all expanded secured integration cases after the
   priority fix. Investigate failures without relaxing the intended observations.
3. Capture matching source/PDB hashes and run the full unit/integration milestone
   with coverage collectors. Target all new maintained lines and branches; retain
   preliminary/failed evidence separately from final evidence.
4. Run complete Kafka Stryker scope after local builds/tests finish. Investigate
   every survivor, uncovered mutant and timeout; retain mutation scope coverage.
5. Rebuild the image if source changes. Run plaintext canonical/mapped published
   Kafka checks and all thirteen secured scenarios with the new health assertions,
   including idle state, broker outage/recovery and certificate/credential rejection.
6. Complete warning-free build, executable guards and an attempted `archy verify`.
   Write a D3 verification ledger and operator documentation with actual results.
7. Only then merge the qualified step into its category branch. Keep main unchanged.

Further bounded work includes CSV durable checkpoints/object framing, Parquet,
database snapshot/CDC, exports/sinks, client/BI/MCP integrations, explicit uncertain
import reconciliation, retention, operator recovery, broader source declarations,
token lifecycle/RBAC/audit and deployment/backup/upgrade workflows. Define a narrow
contract and acceptance evidence before implementing each next package.

## Reset and chat safety

Do not rely on signing in on a new desktop to restore this local Codex chat.
The actual rollout and local state database need a separate private copy, and
manual file restoration is a recovery technique rather than a guaranteed supported
cross-version migration. The original thread ID is
`01a1832a-0dbf-7d23-a757-6b8c2cc9afc7`.

The project repository is public: never commit chat transcripts, authentication
files, private test evidence or machine state into it. Keep private backup archives
outside the repository and verify them on another device before wiping this one.

If the original chat cannot be reopened, open a new chat in the restored checkout
and say: "Read docs/reset-handoff.md, docs/engineering-rules.md and the D3 package
brief. Continue D3 qualification from the recovery checkpoint. No benchmarks, no
main merge, and do not treat the checkpoint as qualified."
