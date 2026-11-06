# GCS protocol qualification fixture

Test-only Go standard-library origin for the engine's XML interoperability contract. It is not
Google Cloud Storage and cannot certify live-provider behavior. It requires SOURCE_ACCESS_KEY,
SOURCE_SECRET_KEY, FIXTURE_CONTROL_KEY and a read-only /data/object.ndjson bind mount.

The fixed fixture-bucket/events/file.ndjson object gets generation 17 initially. Changed file
bytes create a new generation while retaining up to four versions. HEAD/range GET requests must
carry an independently verified AWS V4 signature, identity encoding, and valid conditional fields.
Metadata begins at three. Authenticated fixture-only POST /control/metadata?generation=... and
/control/delete?generation=... change/remove retained versions using header X-Fixture-Key.
/ready is an unauthenticated liveness probe. Bodies and credentials are never logged.

A reference-signature self-test runs during the pinned Docker build and startup. The source,
toolchain and runtime are local/pinned; the fixture runs without root. It is never an engine
service or deployment dependency. Full local integration tests also use real loopback Kestrel
origins and pinned ClickHouse; live Google checks remain separately unrun.

```sh
docker build --file tests/Fixtures/GcsObjects/Dockerfile --tag nealytics-gcs-fixture:local .
```
