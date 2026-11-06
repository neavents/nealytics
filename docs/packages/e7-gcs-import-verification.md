# E7 GCS signing foundation evidence

Status: foundation locally verified, 2026-11-06. The GCS adapter is still in progress.
Branch: feat/integrations-e7-gcs-import. Base: local E6 merge 2278bf4.
Foundation source: 83113c919b18ba0a3fea401beb489c83242fa3c7.
No GCS source declarations, routes or Google-provider qualification are claimed here.

## Change and preservation

The existing S3 AWS V4 algorithm moves to Infrastructure/Networking/AwsV4RequestSigner, accepting
explicit credential, region, session-token, clock and pool values. S3 retains its existing wrapper
and byte-level signature tests. The extraction audit compares the entire original file with only
namespace/type/signature/source-field-to-parameter substitutions; no algorithm-body edits remain.
This avoids a second signing implementation or a provider SDK. Existing requests and defaults
are unaffected. See the [bounded plan and provider references](e7-gcs-import.md).

A Python hashlib/hmac calculation independently derives a GCS XML interoperability request
signature for region auto, a UTF-8/escaped object path, a generation query and generation/
metageneration preconditions. The C# test checks the full authorization value and cleared pooled
crypto-buffer return. Existing AWS vectors still cover time-zone normalization, session tokens,
version queries, ranges and the minimum configured secret. The GCS vector verifies local signing,
not acceptance by a live Google service.

## Local gates

| Gate | Result |
| --- | --- |
| Focused S3, architecture, style and domain tests | 847 passed, zero skipped |
| New/changed maintained production coverage | 49/49 executable lines, 6/6 branch outcomes |
| Source proof | Focused portable-PDB document hashes match maintained source; no mismatches excluded |
| Real-dependency S3 regression | Four ClickHouse/protocol-fixture restart cases passed, managed/custom and canonical/mapped |
| Published actual ARM64 regression | Canonical and mapped S3 passed with custom ingestion and io_uring at 83113c9 |
| Complete Stryker.NET S3/shared scope | 519/519 executable mutants killed, no survivors/uncovered mutants/timeouts, unchanged 100% threshold |
| Solution build | Passed, zero warnings/errors |
| Archy | Preflight has no configured layer rule; verify reports an uninitialized worktree. Executable guards passed separately |

The moved signer remains explicitly included in the committed S3 mutation scope; no previous
responsibility is dropped. The report separately records 131 compile-invalid mutants and ignored
outside-scope/block-covered entries. Raw evidence lives in artifacts/verification/e7-gcs-import.
The 847-test collector is focused: its whole-engine execution totals are not full-suite coverage.
The last consolidated full-suite maintained coverage snapshot remains the explicitly versioned
[E6 ledger](e6-http-import-verification.md); a new broad snapshot belongs to the stable GCS adapter
milestone. Do not substitute focused totals for whole-engine qualification.

## Remaining adapter work

Named source/key declarations and private descriptor ownership, provider-specific HEAD metadata,
conditional generation/metageneration range reads, stored-encoding/transcoding rejection, safe
failure diagnostics, D1 composition, protocol fixture and full local qualification remain.
Remote CI and actual x64 checks remain deferred by the owner's 2026-11-06 instruction. A live
Google-service check requires separately configured credentials/resources and is unrun. Keep
these boundaries explicit while local development continues. No benchmarks or main integration.
