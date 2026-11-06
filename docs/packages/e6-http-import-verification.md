# E6 named HTTP import qualification ledger

Status: implementation and local qualification complete.
Remote CI and actual x64 qualification are deferred at the owner's request, 2026-11-06.
This is not completion of category E or the remaining roadmap.
Step: feat/integrations-e6-http-import. Base: 07e0cd68fb197ba1ba6de637ba7e9782a6234667.
Runtime and tests: 118da517c68e9769933d252339310576ad574c79. Main remains untouched; measurements remain stopped.

## Delivered behavior

Optional operator-selected HTTP/HTTPS NDJSON objects use project-pinned server keys, strong
ETags, exact conditional ranges and existing D1 checkpoints. A per-source deployment secret
keys the private descriptor; URLs, queries and headers never appear in descriptors or manifests.
Stable configuration preserves descriptors across restart; changing URL/header/key selection
fences old descriptors. Confirmed progress stays readable, and uncertain admission is never
automatically replayed. No provider SDK, new engine service or persistent format is introduced.
See the [brief](e6-http-import.md) and [operator contract](../http-import.md).

Bounded helpers for transport, response ownership, exact reads, NDJSON framing and control-body
policy are shared with S3. Signing, provider metadata and source selection remain in their feature
slices. All 120 prior ranged mutation responsibilities retain identical fragments at their new
positions; the moved S3 responsibilities remain explicit in its mutation configuration.

## Consolidated local evidence

- Warning-free solution build passed: zero warnings/errors.
- Full local unit suite: 3,933 passed, zero skips, including source/style/domain/architecture guards.
- Full real-dependency integration suite: 319 passed, zero skips.
- Four new real ClickHouse/Kestrel cases passed across managed/custom and canonical/mapped inputs.
- All nine published actual ARM64 checks passed with custom ingestion and io_uring: canonical/mapped
  HTTP, S3, Kafka and NDJSON, plus CSV. Every summary identifies runtime 118da51.
- Changed maintained coverage: 517/517 executable lines and 303/303 branch outcomes.
  Both portable-PDB source audits match all 260 maintained source documents; no source exclusions.
  Whole maintained engine: 14,643/15,165 lines (96.56%), 5,820/6,194 branches (93.96%).
  The remaining 522 lines and 374 branches are inherited gaps, not HTTP gaps. Generated code
  is explicitly excluded.
- Initial Complete mutation: 380 killed, 37 survivors among 417 executable mutants, no timeouts.
  Stronger diagnostic, boundary, identity-vector and ownership assertions precede the final rerun.
- Archy preflight is unavailable because no layer rule is configured; archy verify reports that
  the worktree is uninitialized. Executable architecture guards pass separately.

Maximum escaped ETags exercise the JSON writer's encoding reservation. Removing that reservation
fails valid boundary input, so it is retained. The BCL rejects wildcard ETags during parsing;
the separate wildcard comparison was redundant and has been removed. Header-order identity has
an independent two-header HMAC vector, rather than only comparing two executions of the same code.
Failed body reads assert actual rentals and cleared return ownership, not empty count equality.

Final review found that invariant lowercasing maps the Kelvin sign to ASCII k. Header validation
now checks the original declaration's token alphabet before using the normalized name. A regression
case rejects both a bare Kelvin sign and its occurrence inside a header name. Full local unit,
integration and nine published ARM64 cases were rerun against the hardened revision 118da51.
Both final PDB audits match current maintained source, with no stale-source exclusions.

## Deferred platform gates

The final full HTTP/shared gate detects all 415 executable mutants: 414 assertion kills and one
timeout when mutation removes the source deadline. The separate S3/shared gate kills all 519
executable mutants with no survivors, uncovered mutants or timeouts. Both keep Complete mutation
and unchanged 100% thresholds; the scopes overlap and must not be added as unique mutants.
The HTTP report records 103 compile-invalid mutants, 82 block-covered duplicates and 444 outside
its ranged scope. The S3 report records 131 compile-invalid mutants separately. Original scopes
remain intact. The initial 380/417 result and intermediate 415-kill report are retained as history.

The deadline's independent three-second caller safeguard now converts removal of the one-second
source deadline into a finite type/diagnostic assertion failure. All 3,933 unit cases pass with
that safeguard. The focused deadline recheck kills that single mutant by assertion, with no timeout.
Its temporary evidence scope does not replace or shrink the committed full mutation configuration.
Runtime C# remains byte-identical to 118da51; only the test safeguard and documentation change.

Actual x64 and final-source remote CI remain unrun. The prepared workflow includes canonical/mapped
HTTP checks alongside the seven existing connector checks on each architecture.

The GitHub push was rejected because the signed-in OAuth credential lacks workflow scope.
The existing SSH identity is also rejected. Runtime/tests/workflow changes are committed locally;
remote E6 still points to the earlier documentation-only base. On 2026-11-06 the owner explicitly
requested continued local development without restoring that access. Keep remote/platform evidence
deferred and resume it when access becomes available; no credential or repository policy was changed.
Local category integration may continue without treating deferred checks as passed.

Raw evidence lives in artifacts/verification/e6-http-import. Preliminary runs and failed mutation
reports are retained separately. Native/platform results are functional checks, not measurements.
Additional formats, GCS, database adapters, sinks, broader Kafka operation and operator workflows
remain separate bounded packages. Existing ingestion contracts and backend defaults stay unchanged.
