# S3 protocol fixture

This optional test service is built from upstream MinIO commit
9e49d5e7a648f00e26f2246f4dc28e6b07f8c84a (RELEASE.2025-10-15T17-29-55Z). The source archive,
Go toolchain image and runtime image are pinned and verified in Dockerfile. The image retains
the upstream license, runs without root and is used only for isolated functional tests.

```sh
docker build --file tests/Fixtures/S3/Dockerfile --tag nealytics-s3-fixture:9e49d5e .
```

Build once before directly running the integration project; scripts/run-integration-tests.sh
and CI build it automatically. An explicit NEALYTICS_S3_FIXTURE_IMAGE can select a prebuilt copy
for the C# tests. The engine has no dependency on this image or its build toolchain.

Testcontainers assigns independent ports/storage and ephemeral credentials. The tests create
versioned objects using a separate test uploader, then exercise the engine's signed HEAD/range
reads against the real service and verify ClickHouse totals across overwrite/restart/retry.
Published qualification uses the same fixture alongside the SDK-free engine executable.
