# HTTP object qualification fixture

Test-only standard-library Go server for named HTTP source qualification. It requires a read-only
/data/object.ndjson bind mount and SOURCE_TOKEN. /object?selection=private-query requires the matching
Bearer header and uses strong SHA-256 ETags with standard conditional HEAD/single-range GET behavior.
/ready is a liveness probe; /redirect exercises redirect refusal. No payload or credential logging.

The Go toolchain and final runtime images use pinned multiarchitecture digests. Build per actual
architecture with the normal Dockerfile; this fixture is neither an engine dependency nor a
production service. The integration tests use their own real loopback Kestrel origin.

```sh
docker build --file tests/Fixtures/HttpObjects/Dockerfile --tag nealytics-http-fixture:local .
```
