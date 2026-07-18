# Contributing to Nealytics

Thanks for your interest in improving Nealytics. This guide covers how to build, test, and keep changes consistent with the engine's design.

## Prerequisites

- .NET 10 SDK
- Docker (only required for the integration test suite)

## Building

```bash
dotnet build
```

The engine (`src/Nealytics.Engine`) compiles with **Native AOT** enabled and full trimming. Keep that in mind: no runtime reflection, no dynamic IL, and no reflection-based serialization. JSON goes through the source-generated `TelemetryAotContext`.

## Running tests

### Unit tests (no external dependencies)

```bash
dotnet test tests/Nealytics.Engine.Tests.Unit
```

These are fast and self-contained. They cover all pure logic (request parsing, SQL builders, WAL, batch-processor orchestration via a fake writer, validators) and boot the API in-memory with `WebApplicationFactory` for the endpoint paths that don't touch the database.

### Integration tests (require ClickHouse)

The integration suite talks to a real ClickHouse. The connection string is read from
`TelemetryEngine__ClickHouseConnectionString` and defaults to
`Host=127.0.0.1;Port=9000;Database=nealytics_core;User=default;Password=;`.

The simplest way to run them locally:

```bash
./scripts/run-integration-tests.sh
```

That script starts ClickHouse via `docker-compose.test.yml`, waits for it to be healthy, runs the suite, and tears the container down afterwards. Set `KEEP_CLICKHOUSE=1` to leave it running between runs.

To point the tests at an existing ClickHouse instead, just export `TelemetryEngine__ClickHouseConnectionString` and run `dotnet test tests/Nealytics.Engine.Tests.Integration` directly. The tests never assume a specific container name or port.

### Coverage

```bash
dotnet test --collect:"XPlat Code Coverage"
```

## Code style

The engine follows a deliberately strict style. Please match it in `src/`:

- **No `var`.** Every local is written with its explicit type. This keeps value-type boxing and implicit allocation conversions visible.
- **No comments** in engine source, SQL, or configuration files. Names should carry the intent.
- **Vertical Slice Architecture.** Each feature lives in its own `Features/<Name>` folder with its endpoint, query/command, and DTOs. `Infrastructure` must never depend on `Features` (enforced by `ArchitectureTests`).
- **Sealed by default.** Response models, payloads, and infrastructure classes are sealed (also enforced by `ArchitectureTests`).
- Keep the parsing/validation logic out of endpoint lambdas and in a testable request factory, and keep I/O behind an interface where it helps testing (see `ITelemetryBatchWriter`).

Test projects may use `var` and comments freely.

## Pull requests

- Add or update tests for any behavioral change. If a test fails, fix the implementation rather than weakening the test.
- Run `dotnet build` (must be warning-free) and both test suites before opening a PR.
- CI runs the build, unit tests, and integration tests against a ClickHouse service on every push and PR.
