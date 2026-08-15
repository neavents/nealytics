# Nealytics load-test harness

A layered load generator for the Nealytics ingest and read paths. It isolates each stage —
HTTP round-trip, WAL group-commit, channel, single-event ingest, batched ingest — so you can
see **where** time goes instead of only the end-to-end number. It records **throughput,
p50/p95/p99/max latency, error rate**, verifies the **zero-loss invariant** (events accepted ==
rows stored) for write modes, and appends a Markdown block to [`RESULTS.md`](RESULTS.md).

Two pieces:
- **`bench/Nealytics.Engine.Bench/`** — an HTTP load generator (console app) for a running pod.
- **`tests/…/LayeredBenchmarks.cs`** — in-process micro-benchmarks that call the WAL and channel
  **directly** (no HTTP), and expose the WAL's internal group-commit stats.

Both are developer tools, exempt from the engine's no-`var` / no-comment / AOT rules.

## The layers (why several)

The end-to-end number alone is misleading — a slow e2e result can be the client, the HTTP stack,
the WAL, or the DB. Measure them separately, on the same host, so they compose:

| Layer | How | What it isolates |
|---|---|---|
| `noop` | `run-benchmark.sh noop` (GET /health) | Kestrel + loopback + client ceiling |
| WAL append | `LayeredBenchmarks.Layer1` | durable append + group-commit coalescing |
| channel | `LayeredBenchmarks.Layer2` | in-memory backpressure stage |
| raw fsync | `LayeredBenchmarks.Layer0` | the physical `fsync` floor |
| `track` | `run-benchmark.sh track` | single-event durable ingest (closed-loop) |
| `beacon` | `run-benchmark.sh beacon` | batched durable ingest (the realistic path) |
| `read` | `run-benchmark.sh read` | every read endpoint, plus the same breakdown answered from raw and from a rollup |

See [`RESULTS.md`](RESULTS.md) for a worked example and the key finding: **group-commit only
coalesces when appends are concurrently pending, so a closed-loop one-event-per-request client
is its worst case (~1.5k/s), while batched/concurrent load reaches 100k+ events/s.**

## Quick start

```bash
# Boots ClickHouse + the API, runs a sweep, tears down. mode: noop|track|beacon|<read>|all
./scripts/run-benchmark.sh all          # noop, then track, then beacon
./scripts/run-benchmark.sh beacon

# Overdrive: crank concurrency + batch to find the limit
OVERDRIVE=1 ./scripts/run-benchmark.sh beacon

# Read path: seeds one tenant, then sweeps every read endpoint against it
./scripts/run-benchmark.sh read

# On its own ClickHouse (ports 9100/8223) so it does not fight anything else for the defaults
BENCH_ISOLATED=1 ./scripts/run-benchmark.sh all
```

### Run it isolated when anything else is using ClickHouse

`BENCH_ISOLATED=1` switches to [`docker-compose.bench.yml`](../docker-compose.bench.yml) on ports
**9100/8223**. `scripts/run-integration-tests.sh` takes `INTEGRATION_ISOLATED=1` for the same
container.

This is not tidiness. Both the benchmark and the integration suite finish with `docker compose
down -v`, and `scripts/aot-smoke.sh` uses the same default ports — so running a benchmark during an
AOT publish destroys it, and an AOT publish takes long enough that you will not be watching.

## The read run, and why the pair of breakdown modes exists

`run-benchmark.sh read` is the only mode that answers *"was the rollup worth building"*, so it is
built to be hard to fool.

It boots the engine against [`bench-schema.json`](bench-schema.json) — three dimensions, one
measure, and one daily rollup covering all three dimensions — seeds a single tenant through
`/beacon` for 30 seconds, waits for the batch writer to drain, and then runs:

| Mode | Answers from | Why it lands there |
|---|---|---|
| `breakdown` | raw | range is `00:00:01`–`00:00:01`, so `RollupPlanner.IsAligned` refuses |
| `breakdown-rollup` | `rollup:daily_by_product` | same query, range on the day boundary |
| `breakdown-measure` | raw | `sum(amount)`, unaligned range |
| `breakdown-measure-rollup` | `rollup:daily_by_product` | `sumMerge` over stored state |

The two members of each pair group by the same column, over the same rows, with ranges one second
apart. Nothing differs but the routing decision, which is the point: a comparison between two
different questions would prove nothing about the rollup.

**Each read mode preflights the `source` field and aborts if it is not the one the mode claims to
measure.** Both paths return `200` and identical rows by design, so a silent fallback to raw would
otherwise be recorded in `RESULTS.md` as a rollup timing — a false number, arrived at honestly,
which is the worst kind. The preflight also aborts on an empty result set, because a benchmark over
zero rows measures the absence of work.

A read run resets the ClickHouse volume first. The reconciler refuses to boot when the table holds
a column that no declaration mentions and that column has rows, and `aot-smoke.sh` uses the same
container — without the reset the benchmark dies at startup complaining about another script's
columns.

Tunables: `BENCH_SEED_SECONDS=30`, `BENCH_SEED_CONCURRENCY=64`, `BENCH_READ_CONCURRENCY=1,4,16,64`,
`BENCH_READ_DURATION=10`. Read concurrency defaults low on purpose — these are analytical scans,
and 512 concurrent ones measure ClickHouse's queue rather than the query.

Tunables (env): `OVERDRIVE=1`, `BENCH_DURATION=10` (seconds/level; `0` ⇒ fixed `BENCH_REQUESTS`),
`BENCH_CONCURRENCY=8,64,512,2048`, `BENCH_BEACON_BATCH=200`, `BENCH_WAL_DIR=/path` (tmpfs vs SSD
vs PVC), `BENCH_RATE_LIMIT`, `KEEP_CLICKHOUSE=1`. The script also sets Server GC, a 2M channel,
a 20k batch, and an effectively-unlimited rate limiter so you measure the engine, not a knob.

## In-process micro-benchmarks (WAL / channel / fsync)

These run in the normal suite at a small size (fast + a real zero-loss assertion). Crank + print:

```bash
NEALYTICS_BENCH=1 NEALYTICS_BENCH_APPENDS=100000 \
  dotnet test tests/Nealytics.Engine.Tests.Unit \
  --filter FullyQualifiedName~LayeredBenchmarks -l "console;verbosity=detailed"
```

`Layer1` prints per-concurrency **appends/s, latency percentiles, average group size, and
average flush ms** — the average group size is the direct proof that group-commit is coalescing.

## Running the HTTP tool directly (any running pod)

```bash
dotnet run -c Release --project bench/Nealytics.Engine.Bench -- \
  --url http://localhost:5199 --mode beacon --key test-key-1 \
  --concurrency 256,512,1024,2048 --duration 10 --beacon-batch 200 \
  --out bench/RESULTS.md
```

Options: `--url`, `--mode` (`noop|track|beacon|timeline|timeseries|active|top`), `--key`,
`--jwt-key` (read modes), `--concurrency`, `--duration` (seconds/level; `0` ⇒ `--requests`),
`--requests`, `--warmup`, `--beacon-batch`, `--verify-loss true|false`, `--ch`,
`--metrics-url` (scrape `nealytics_queue_depth_current` for peak backpressure — needs
`EnablePrometheusScrape=true`), `--out`.

## Interpreting the columns

| Column | Meaning |
|---|---|
| `req/s` / `events/s` | requests, and `req/s × events-per-request` (beacon batches) |
| `p50/p95/p99/max ms` | client-observed latency over successful requests |
| `Stored/Sent` | rows in ClickHouse for the run's tenant vs events accepted — must be `✅` |
| `Peak queue` | max `nealytics_queue_depth_current` (backpressure), if `--metrics-url` set |

## Measuring before → after (same host)

```bash
GIT_SHA=$(git rev-parse --short HEAD) ./scripts/run-benchmark.sh beacon   # AFTER (current)
git stash -u && git checkout b1e6607                                      # BEFORE (pre-opt)
./scripts/run-benchmark.sh beacon
git checkout - && git stash pop
```

Copy the `bench/` dir aside before the checkout (it is newer than `b1e6607`). The delta is
largest on non-loopback networks and higher-latency WAL storage, where the old per-event `fsync`
hurt most.

## Zero-loss under crash

Steady-state zero-loss is asserted every run (`Stored/Sent`). For crash durability:

```bash
KEEP_CLICKHOUSE=1 OVERDRIVE=1 ./scripts/run-benchmark.sh beacon &
sleep 4 && kill -9 $(pgrep -f Nealytics.Engine)   # hard-kill mid-load
# restart the API; the batch processor replays the sealed WAL on boot; count must not drop.
```
