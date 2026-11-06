# E8 Kafka security local qualification

Locally qualified candidate: 2aa2f1c423de4604006a1cdaca66ff3e34ef989f on
feat/integrations-e8-kafka-security, 2026-11-06. Base: local D2 integration merge
1c1a83537108afb3362745b1fc45d1c47bd5b74a. Main remains untouched; measurements remain stopped.
See the [contract](e8-kafka-security.md) and [fixture guide](../../tests/Fixtures/KafkaSecurity/README.md).

This closes the previously unqualified real-broker security path of the existing connector.
Engine source/configuration, native assets, licenses and executable packaging are unchanged from
that base; the work adds real secured-broker fixtures, functional checks, prepared CI and operator
evidence. It does not add another service or runtime dependency to deployments. The single-file
executable retains existing mappings, producer contracts, durability and backend defaults.

## Verification evidence

- All 25 initial focused cases pass: four native-client secure-mode probes; five explicit
  trust/hostname/password rejections; sixteen engine-to-ClickHouse cases spanning TLS, PLAIN/TLS,
  both SCRAM/TLS modes, managed/custom execution and canonical/mapped records.
- The later rejection assertion seeds an existing group offset at zero, observes the explicit
  native SSL/authentication error using the engine's compiled settings and checks no offset advance.
  The final integration run includes it; the earlier focused run predates it.
- All 4,233 full unit cases pass with zero failed/skipped. Existing source/style/domain guards pass.
- All 348 full real-dependency integration cases pass, zero failed/skipped, including all 25 new
  secured-broker cases with the strengthened rejection assertions. Full unit/integration collectors
  match current maintained source hashes, with no mismatched-source exclusions. Whole maintained
  engine coverage is 15,185/15,707 lines and 6,179/6,553 branches. The inherited gaps remain 522
  lines/374 branches. Changed-engine coverage is not applicable: no maintained runtime code changed.
- All thirteen published actual Linux ARM64 scenarios pass: eight secure-mode/mapping combinations
  and five trust/hostname/password rejection cases. The real packaged executable uses io_uring and
  custom ingestion, not the test host. Each positive case checks metric visibility, manual offsets,
  SIGKILL/restart, exact stored counts and poison-prefix fencing. Rejections must be observed by the
  broker from the exact engine container address and leave a seeded offset at zero and no records
  stored, including after shutdown. Waiting without an observed rejection is not passing evidence.
- Both application log streams are checked for source/SASL/JWT credentials and poison payloads.
  Retained fixture cleanup logs redact generated secrets. Certificates and passwords are ephemeral;
  private fixture files use owner-only permissions. Only public CA files reach the engine.
- The C# fixture creates certificates through BCL and alters SCRAM credentials through the pinned
  client administration API. The published fixture independently uses the broker image's OpenSSL
  and storage formatter. Both use digest-pinned Kafka 4.1.1 and ClickHouse 25.3.14.14.

- The unchanged Complete Kafka mutation scope kills all 185 executable mutants, with zero
  survivors, uncovered mutants or timeouts. Its selected report also records 22 compile-invalid
  and 375 filtered/structural ignored mutants. Scope and 100% thresholds remain unchanged.
- Fixture-only refinement 6bbd360 binds the four host-side listener ports to loopback. All 25
  affected security cases pass again; four additional native-client probes pass with actual Docker
  inspection proving all four bindings use 127.0.0.1 and the broker retains its 1-GiB/two-CPU caps.
  The full collectors and thirteen published summaries identify 2aa2f1c; runtime trees remain
  identical through the fixture refinement, which changes no engine behavior or native script.
- The final solution build passes with zero warnings/errors. No new maintained engine lines or
  branches are introduced; zero changed lines are not presented as a new runtime coverage
  achievement. All existing ranged mutation scopes and thresholds remain intact.

## Preliminary evidence retained separately

Early focused runs failed during fixture setup: storage initialization arguments, a random
cluster identifier parsed as a flag, a CLI using externally advertised addresses inside the broker,
and duplicate same-user SCRAM updates in one administration request. The fixture now uses explicit
cluster-id=value syntax, host-side administration and sequential user updates. A nullable container
configuration warning was fixed before the final milestone build. Those runs are not passing gates.
The initial published SSL-only smoke is separate from the full thirteen-scenario evidence.

## Boundaries

The fixture administration/controller path is plaintext and isolated; ingestion uses encrypted
listeners with certificate/hostname verification. Client certificates, OAuth, ACL provisioning,
multi-broker failover, vendor identity services, credential discovery/rotation and independent
source health remain separate capabilities/qualification. A transport rejection can leave the
native consumer retrying without ingesting; E8 does not claim that HTTP readiness exposes source
health or implement poison repair. Existing consume/validation/persistence/commit failure handling
is unchanged. At-least-once replay can duplicate events outside these controlled offset cases.

Remote CI/actual x64 remain owner-deferred; the prepared workflow has not been pushed or run.
Archy preflight remains unavailable without a configured layer rule; archy verify reports an
uninitialized worktree. The broader D/E goal remains
open. Raw evidence is under artifacts/verification/e8-kafka-security, including source-bound PDBs,
full TRX/collectors, coverage-audit.json, mutation-audit.json, published-audit.json,
source-equivalence.json, loopback-bindings.json and all preliminary/final run logs.
