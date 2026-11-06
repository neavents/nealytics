# E7 generation-pinned GCS NDJSON import

Selected 2026-11-06 from the remaining object-source group. Category: feat/integrations.
Step: feat/integrations-e7-gcs-import. Base: local E6 merge 2278bf46013ef1687732b70a02ebd2f8ea7a408a.
Status: optional GCS runtime locally qualified; remote/x64 and live-provider checks remain unrun.
See the [foundation ledger](e7-gcs-import-verification.md) and [operator contract](../gcs-import.md).
See the [adapter evidence](e7-gcs-import-adapter-verification.md) for the local qualification.
Remote CI/x64 remain deferred under the owner's 2026-11-06 instruction. Do not block local work
or describe those checks as passed. Main and benchmarks remain untouched.

## Bounded deliveries

1. Preserve the existing S3 signing contract while extracting its concrete AWS V4 signer into
   Networking. Characterize a Google XML interoperability request with an independent signature
   vector. Keep S3's byte-level vectors, token handling and cleared crypto-buffer ownership.
2. Add optional named project-pinned GCS sources using explicit HMAC interoperability credentials,
   a fixed bucket/prefix, operator-controlled endpoint and bounded object selection. Implement
   provider-specific inspection and conditional exact-range reads for uncompressed UTF-8 NDJSON.
3. Compose the adapter with existing D1 jobs, NDJSON mapping, budgets and admission. Add local
   protocol/real-storage/restart/publication tests, complete changed-code coverage and mutation,
   and actual ARM64 published regression checks. Retain deferred remote/x64 and unavailable live
   Google-service checks explicitly; a local protocol fixture is not live-provider certification.

## Provider contract and authentication choice

Use the GCS XML API and HMAC interoperability credentials for this bounded delivery. This permits
reuse of the existing AWS4-HMAC-SHA256 request mechanism without introducing a provider SDK,
OAuth token exchange/cache or private RSA-key handling. Region auto is the initial interoperability
selection. This is a deliberate narrower authentication contract; ADC, workload identity, OAuth
refresh and credential discovery are separate future steps. Credential rotation remains an
operator action. Scope service-account permissions to object reads outside the engine.

Google documents HMAC credentials and the AWS-compatible authorization/header path in its
[HMAC guide](https://docs.cloud.google.com/storage/docs/authentication/hmackeys) and
[simple migration guide](https://docs.cloud.google.com/storage/docs/aws-simple-migration).
The shared signer accepts canonical object URIs/queries and engine-generated normalized headers;
it is not an arbitrary request signing API or a generic provider framework.

## Resource identity and bytes

Inspection uses HEAD and retains a positive generation, metageneration, strong ETag and exact
stored/object length. A retained generation is a provider identity, not a checksum. Pin range
requests with the generation query and generation/metageneration preconditions; verify returned
identities, exact Content-Length/Content-Range and supported encoding before admission. Replacement
must never silently move a resumed job to the current generation. If a retained generation is
removed, preserve D1 progress and return a safe failure rather than switching objects.

Reject compressed stored representations and server-side decompressive transcoding. GCS can
ignore Range in some circumstances, so full-body fallback must fail. Do not call a full-object
CRC32C/MD5 response a checksum of an individual range. D1's SHA-256 verifies retained local chunk
bytes on confirmed retries; it does not certify a remote range against a full-file checksum.
See Google's [HEAD contract](https://docs.cloud.google.com/storage/docs/xml-api/head-object),
[download contract](https://docs.cloud.google.com/storage/docs/xml-api/get-object-download) and
[headers/parameters](https://docs.cloud.google.com/storage/docs/xml-api/reference-headers).

Descriptor identity will use a per-source 32-byte deployment identity key. Bind the named project,
endpoint/bucket/prefix and retained object key, generation, metageneration, ETag and byte count in
a keyed descriptor. Do not expose raw bucket/endpoint/authentication material or a public digest
for guessing private source selection. HMAC access credentials are excluded from resource identity,
so credential rotation preserves descriptors when resource settings and the identity key stay
stable. Identity-key/resource changes fence old snapshots; persisted D1 status remains readable.
This is separate from HTTP's existing header/URL rotation fence, which stays unchanged. Reuse validated
transport, cleared pooled chunks and NDJSON boundaries; keep GCS metadata and selectors in its
feature slice. Preserve literal object paths and UTF-8 encoding, including percent signs, Unicode,
query punctuation and repeated/dot path segments, without permitting authority/prefix changes.

## Limits and acceptance

Declarations are empty by default. Keep at most 32 project-scoped named sources, explicit URL/
credential/key/header/metadata budgets, a 1–120 second whole-operation deadline and one queue-free
GCS operation combined with existing global/import/D1 admission. No scans, notifications, implicit
retry, parallel download, general scheduler, format expansion, sink, hosted service or data-format
change belongs in this package. Ordinary track/beacon callers and backend defaults remain intact.

Test authentication and signatures independently, exact generation/metageneration/ETag handling,
header duplication, unsupported encoding/transcoding, ranges/EOF, Unicode paths, redirects,
transport deadlines/caller cancellation, resource cleanup, changed/deleted generations, restart,
confirmed receipts and uncertain admission. Run focused checks during editing and broad checks
at a stable adapter milestone. Preserve moved S3 mutation scope and record every deferred gate.

## Adapter implementation checkpoint

The runtime now includes named source validation, owned private descriptor keys, strict provider
metadata, generation/metageneration preconditions, identity encoding and exact conditional ranges,
D1 checkpoint composition and optional project-pinned routes. Generation fields serialize as
exact decimal strings; requests also accept integer JSON values. Source/generated serialization,
existing caller contracts, backend defaults and persistent formats are retained. Literal path
encoding is shared with S3, and all 125 existing ranged mutation responsibilities relocate with
identical fragments. Local ARM64 published qualification passes; live-provider verification is unrun.
