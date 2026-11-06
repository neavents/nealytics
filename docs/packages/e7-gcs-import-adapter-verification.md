# E7 GCS adapter local qualification

Status: locally qualified, 2026-11-06; deferred platform/provider checks remain explicit.
Branch: feat/integrations-e7-gcs-import. Runtime candidate: 9356629. Refinement: ced3b09.
This is separate from the completed [signing foundation](e7-gcs-import-verification.md).
See the [operator guide](../gcs-import.md) for the implemented contract.

## Implemented contract

Named project-pinned GCS XML sources use explicit HMAC interoperability credentials and private
32-byte descriptor identity keys. Inspection retains generation/metageneration, a canonical strong
ETag and matching stored/response lengths. Conditional ranges pin the retained generation and
metadata and reject transcoding, ignored ranges, inconsistent metadata and partial EOF. Public
generation fields serialize as exact decimal strings. Existing D1 intents, confirmed receipts,
uncertain-admission fencing and mapping/policy/WAL behavior are reused without persistent-format
changes. Source names, keys and fields remain domain agnostic. No provider SDK or service is added.

The configuration compiler bounds declarations/keys/credentials and rejects malformed IDN hosts
with a fixed diagnostic before decoding any private identity key. Literal object encoding moves
to a concrete shared helper with S3. All 125 pre-existing ranged mutation responsibilities retain
identical source fragments after moving offsets. GCS and S3 scopes explicitly include the moved
path encoder; no maintained responsibility or mutation threshold is removed.

## Evidence and pending gates

- Full initial unit suite: 4,175 passed; strengthened suite at ced3b09: 4,182 passed, zero skips.
- Full real-dependency integration suite at ced3b09: 323 passed, zero skips.
- Four new ClickHouse/Kestrel cases exercise managed/custom and canonical/mapped data, retained
  generations after replacement, persisted receipts across restart, exact two-row/sum-six results,
  source conflict, metadata-change/deletion fences and readable completed status.
- Final source-bound combined coverage at ced3b09: 557/557 changed executable lines, 357/357 branches.
  Whole maintained managed engine: 15,133/15,655 lines (96.67%) and 6,145/6,519 branches (94.26%). The remaining
  522 lines/374 branches are inherited gaps. No mismatched-source points were combined.
  Both final collectors match portable-PDB source hashes; no source documents were excluded.
- Published actual ARM64 qualification: all eleven canonical/mapped GCS, HTTP, S3, Kafka,
  NDJSON and CSV summaries passed at ced3b09. The initial 9356629 run is retained separately.
- The independent test-only Go origin checks V4 signatures against an independently calculated
  reference vector at build/startup, retains generations and exposes authenticated metadata/delete
  controls. The published executable passes SIGKILL/restart and credential-log-redaction checks
  with custom ingestion and io_uring. This fixture is not a Google-hosted service.
- Final Complete GCS/shared mutation: all 567 executable mutants killed; zero survivors, timeouts
  or uncovered mutants. Stryker reports 139 compile-invalid and 553 filtered/structural ignored
  mutants in the selected report. Thresholds and maintained scopes remain unchanged.
- Preserved S3/shared Complete mutation: all 519 executable mutants killed; zero survivors,
  timeouts or uncovered mutants, with 133 compile-invalid and 539 filtered/structural ignored.
  The moved path encoder and signer remain inside the maintained S3 mutation scope.
- Initial mutation runs are retained separately: 558/568 kills with ten survivors, then 566/567
  kills with one survivor. A missing assertion for the absent AWS session-token header caused the
  last survivor; the strengthened inspection test and all 83 focused protocol cases pass.
  No production code changed after ced3b09 for that assertion.

The survivors drove structural checks using correctly keyed but invalid descriptors, all-source
catalogue disposal/zeroing assertions, inclusive secret/bucket character bounds and mixed numeric
bucket-label cases. The static 32-KiB configuration writer's unneeded encoding reservation was
removed after maximal legal escaped configuration passed without it. The separate snapshot writer
retains its reservation for worst-case escaped names/ETags. Final runtime differs from 9356629 only
in that reservation choice; tests and prepared fixture/workflow checks were strengthened.

## Explicit deferrals and remaining work

Remote CI and actual x64 checks remain deferred by the owner on 2026-11-06. Live Google-service
verification is unrun without separately configured credentials/resources. Local protocol checks
must not be described as live-provider certification. Archy preflight still lacks a configured
layer rule and archy verify reports an uninitialized worktree; executable architecture guards pass.
The full goal and category E remain open. Main is untouched and measurements remain stopped.

The final solution build passes with zero warnings/errors. The later absent-session-token
assertion changes tests only; both full collectors and all eleven published summaries bind to
production ced3b09. Focused tests and the final full mutation runs include that added assertion.

Raw evidence lives in artifacts/verification/e7-gcs-import. Final collector/PDB proofs,
coverage-audit.json, mutation-adapter-final-audit.json and published-adapter-v2-audit.json identify
the source and exact gates. Initial failed mutation reports and earlier candidate checks remain
separate from final evidence.
