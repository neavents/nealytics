# Ephemeral Kafka security fixture

This test-only setup uses the existing digest-pinned Apache Kafka 4.1.1 image. It generates a new
private CA, an unrelated trust root, matching/mismatched server certificates and PLAIN/SCRAM
credentials. No private keys or passwords are committed. Leaf certificates expire after one day
and are recreated on every run. Private fixture files use owner-only permissions; only public CA
certificates are made readable to the separately packaged engine. The engine itself adds no
service, identity provider, client SDK or certificate generator.

The listener names ADMIN and CONTROLLER use plaintext inside the isolated fixture environment;
TLS and MISMATCH use SSL, while AUTH uses SASL_SSL. Host-side test port bindings use loopback. ADMIN is used for fixture records, topic setup
and authoritative group-offset inspection. Engine ingestion selects an encrypted listener. The
MISMATCH certificate is signed by the correct CA but names a different host. The separate wrong
CA is valid and unrelated, so trust and hostname rejection are tested independently.

The C# fixture uses BCL certificate generation and the pinned Confluent administration API; the
published fixture uses the image's OpenSSL and storage-format tool. Both enable PLAIN, SHA-256
SCRAM and SHA-512 SCRAM with TLS. CLI cluster IDs use the --cluster-id=value form because a random
URL-safe identifier may begin with a dash. Dynamic credential updates for the same user are
sequential: Kafka rejects altering one user twice within the same administration request.

Run the published ARM64 suite from the repository root after building the normal engine image:

```sh
python3 scripts/qualify-kafka-security.py --image nealytics-engine:local --architecture arm64 --output artifacts/kafka-security/arm64
```

The default command runs canonical/mapped ingestion for all four encrypted settings, forced
crash/restart, poison-prefix fencing and five broker-observed rejection scenarios. A rejection
must be observed from the exact engine container address and preserve a seeded group offset of
zero and zero stored records. Waiting alone is insufficient. Actual host/image architecture is
checked; emulation is not reported as native execution. Credentials/payloads are checked against
both application log streams. Containers/networks use unique ownership labels and only their
own resources are removed. Measurements remain disabled.

The optional security/canonical-only/skip-failures switches select smaller debugging runs; those
are not substitutes for the complete default qualification. Remote CI/actual x64 remain deferred
by the owner. A local fixture does not certify every Kafka vendor, enterprise PKI or topology.
