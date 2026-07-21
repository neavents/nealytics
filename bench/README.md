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
```

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
