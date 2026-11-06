# E8 Kafka TLS and SASL qualification

Selected from the remaining Kafka operational group, 2026-11-06. Branch:
feat/integrations-e8-kafka-security; base 1c1a83537108afb3362745b1fc45d1c47bd5b74a.
Status: locally qualified; see the [evidence ledger](e8-kafka-security-verification.md). Main stays untouched; no benchmarks.

Outcome: qualify the existing single-source Kafka connector against real TLS and SASL/TLS
brokers, including PLAIN, SCRAM-SHA-256 and SCRAM-SHA-512. Preserve certificate-chain and
hostname verification, durable handoff before manual offsets, bounded queues, mapping and
restart/poison-prefix behavior. The engine remains one executable with bundled native assets.

Use ephemeral private CAs/certificates and fixture credentials, a digest-pinned Kafka 4.1.1 and
ClickHouse 25.3.14.14. A separate plaintext administration listener is confined to the isolated
fixture network for topic/credential setup and authoritative offset inspection. Engine reads
use the selected encrypted listener; no plaintext fallback or verification-disable switch is added.
Fixture keys and broker administration are test-only dependencies, not deployment requirements.

Acceptance: real managed/custom and canonical/mapped ingestion with all four secure settings;
wrong trust root, wrong hostname and wrong SASL password cannot admit source records or advance
source offsets. Verify authentication failures without relying only on elapsed waiting. Check
credential/payload redaction, shutdown ownership, persisted offsets and poison-prefix stops.
Exercise the published Linux ARM64 executable with io_uring/custom and scoped actual broker
checks. Keep remote CI/actual x64 explicitly owner-deferred. Use focused tests during editing,
then appropriate milestone build/unit/integration/coverage and mutation gates. Preserve existing
mutation scopes and report inherited coverage gaps; test-only changes cannot establish a new
runtime performance or broad broker certification claim.

No OAuth, credential discovery/refresh, client certificates, ACL provisioning, additional topics,
multi-broker failover, independent connector health, poison-record repair, hosted service or
delivery-semantics change belongs in this security package. If a runtime defect is exposed,
fix it in the existing cohesive boundary and cover its changed lines/branches and mutants.

References: Apache Kafka 4.1 [SSL configuration](https://kafka.apache.org/41/security/encryption-and-authentication-using-ssl/)
and [SASL configuration](https://kafka.apache.org/41/security/authentication-using-sasl/), plus
Confluent/librdkafka 2.15.1 already pinned by E1. Local fixtures do not certify every managed
Kafka vendor, network topology or enterprise identity deployment.
