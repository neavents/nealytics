# Benchmark results

Runs produced by `./scripts/run-benchmark.sh` (HTTP layers) and the `LayeredBenchmarks`
xUnit micro-benchmarks (in-process WAL/channel layers). See [`bench/README.md`](README.md)
for methodology, the layer definitions, and how to produce a same-host before/after pair.

> **Read numbers as relative, not absolute.** Host: macOS / Apple Silicon, loopback client,
> APFS WAL (`Flush(flushToDisk:true)` ≈ 4 ms/`fsync`), ClickHouse in Docker Desktop. On Linux
> with an NVMe/tmpfs WAL and a real NIC (where LZ4 + `async_insert` pay off) the same code goes
> higher. What matters here is the **shape** across layers and the **zero-loss** guarantee.

> **The rate limiter is on by default** (`RateLimitPermitCount=1000` / `10s` = 100 req/s).
> `run-benchmark.sh` relaxes it so we measure the ingest path, not the limiter.

---

## TL;DR — the durable-ingest ceiling is ~164k events/s, not ~1k

Every measured layer on the **same Mac**, so they compose:

| Layer | Peak (this box) | Notes |
|---|---|---|
| Channel publish (in-proc) | **~856,000 events/s** | never the bottleneck |
| WAL durable append (in-proc, isolated) | **~96,000 appends/s** | group-commit coalescing |
| **Batched durable ingest** (`/beacon`, c2048×200) | **~164,000 events/s** ✅ | still climbing, 1.9M rows, zero loss |
| HTTP round-trip ceiling (`/health`) | **~32,000 req/s** | client+loopback+Kestrel saturation |
| Single-event ingest (`/track`, closed-loop) | **~1,500 req/s** | worst case for group-commit (see below) |

> **Superseded on Linux — see the re-run below.** On NVMe, closed-loop `track` reaches 25,255 req/s
> at concurrency 256 rather than plateauing at ~1.5k. The explanation in this paragraph is right
> about the mechanism and wrong about the floor: the floor was APFS's ~4 ms `fsync`.

**The `~1,500 req/s` figure that looked bad is the pathological worst case:** one durable event
per request from a **closed-loop** client that waits for each `202`. That starves group-commit —
it can only amortize an `fsync` across *concurrently pending* appends, and a wait-for-each client
never lets a backlog form (observed WAL group size ≈ 5–6). Feed it concurrency (batched beacons,
or many independent clients) and it coalesces into groups of hundreds and rides the WAL up to
**~164k events/s**. Every run below is **zero-loss** (`stored == sent`).

---

## Linux re-run, 2026-08-14 — and a correction to the headline below

Everything from "Layer 1" down was measured on **macOS / Apple Silicon**. Re-run on **Linux, 12
cores, NVMe (btrfs)**, the write path looks materially different, and the difference is `fsync`.

| Concurrency | `track` on macOS (APFS) | `track` on Linux (NVMe) | |
|---|---|---|---|
| 8 | 746 req/s | **3,131** | 4.2× |
| 64 | 1,104 req/s | **16,069** | 14.6× |
| 256 | 1,266 req/s | **25,255** | 20× |

Zero loss at every level (`stored == sent`). `noop` was **33,016 req/s**, matching the Mac's ~32k —
so the HTTP layer is not what moved.

**The "~1,500 req/s pathological worst case" is a macOS result, not an engine property.** APFS
`fsync` is ~4 ms; on NVMe it is fast enough that group-commit coalesces the way it was designed to,
and closed-loop single-event durable ingest scales with concurrency instead of flatlining. The
analysis below is still correct about *why* — it is group-commit starvation — but the floor it
describes belongs to the disk.

### Put the WAL on real storage before you believe a number

An earlier run of this same benchmark reported **18,692 req/s at concurrency 8** — six times the
number above. `BENCH_WAL_DIR` defaults to `$(mktemp -d)/wal`, and on this machine `/tmp` is
**tmpfs**. The WAL was in RAM, so `fsync` was a memcpy and the run measured the ingest path with
its durability guarantee switched off.

Use `BENCH_WAL_DIR=/var/tmp/...` or any disk-backed path. A durable-ingest benchmark on tmpfs is
measuring something else.

### What this run found: per-request logging on the hot path

The API log reached **7.7 GB in minutes** and filled a 7.8 GB tmpfs.

`appsettings.json` has always declared `"Microsoft.AspNetCore": "Warning"`, and it did nothing —
Serilog owns this pipeline outright and never read `Logging:LogLevel`. So ASP.NET Core emitted four
Information lines per request (`ExecutedEndpoint`, `WritingResultAsJson`, `SettingStatusCode`,
`RequestFinished`), each JSON-formatted and written while the ingest path was trying to work, and
shipped to the collector over OTLP as well wherever logs are exported. At 20k req/s that is ~80,000 log lines a
second.

Fixed by reading the declared levels and applying `MinimumLevel.Override("Microsoft.AspNetCore", …)`.
`LogLevelConfigurationTests` guards it, including that every level name .NET writes is mapped —
Serilog spells Trace `Verbose` and Critical `Fatal`, and an unmapped name falls back silently.

---

## Read path — was the rollup worth building

Run with `./scripts/run-benchmark.sh read` (2026-08-14, Linux, 12 cores, ~1 core occupied by an
unrelated compile, so treat the absolute numbers as a floor and the **ratios** as the finding).

Each mode preflights the response's `source` field and aborts if it is not the path it claims to
measure, so these are not two labels on the same query:

```
Preflight: mode=breakdown               source=raw                      rows=100
Preflight: mode=breakdown-rollup        source=rollup:daily_by_product  rows=100
```

Both members of a pair group by the same column, over the same rows, with ranges **one second
apart** — only day-alignment differs, so only the routing decision differs.

| Query | Concurrency | raw p50 | rollup p50 | Speedup | raw req/s | rollup req/s |
|---|---|---|---|---|---|---|
| `groupBy=product_id` | 1 | 143.70 ms | 16.78 ms | **8.6×** | 7 | 39 |
| | 4 | 237.04 ms | 20.28 ms | **11.7×** | 17 | 121 |
| | 16 | 1,092.44 ms | 50.37 ms | **21.7×** | 14 | 272 |
| `metric=sum(amount)` | 1 | 133.56 ms | 16.57 ms | **8.1×** | 7 | 37 |
| | 4 | 274.03 ms | 20.17 ms | **13.6×** | 14 | 123 |
| | 16 | 1,223.47 ms | 58.85 ms | **20.8×** | 13 | 244 |

**The shape matters more than the multiplier.** Raw does not merely get slower under load, it gets
*worse than useless*: throughput peaks at concurrency 4 and then falls (17 → 14 req/s) while p50
crosses a second. That is the point at which a dashboard firing seventeen concurrent widgets stops
rendering. The rollup path is still climbing at 16 (121 → 272 req/s) with p50 under 60 ms.

`sumMerge` over stored state costs the same as `countMerge` — the measure query is no more expensive
than the count once it is pre-aggregated, which is the whole argument for declaring measures in a
rollup rather than computing them at read time.

### What this run found

`/analytics/active` returned **400 to every request**: 122,880 errors, zero successes. The endpoint
passed nine consecutive `string?` arguments positionally and two were crossed, so `to` landed in the
`traffic` slot and `TrafficFilter` refused a timestamp. It survived a green unit suite because those
tests call the factory directly and therefore pass the arguments correctly by construction. Fixed
with named arguments; `QueryParameterWiringTests` now exercises every read endpoint over HTTP with a
value that is valid only in its own slot.

A benchmark is not only a performance tool. This one was the only thing in the project that made a
real HTTP request with a full query string.

---

## Layer 1 — HTTP round-trip ceiling (`noop` → GET /health)

Pure Kestrel + loopback + client cost. No WAL, no channel, no DB.

| Concurrency | req/s | p50 ms | p99 ms |
|---|---|---|---|
| 1 | 11,764 | 0.07 | 0.21 |
| 8 | 21,617 | 0.32 | 1.14 |
| 32 | 34,474 | 0.74 | 3.45 |
| 64 | 32,907 | 1.73 | 5.40 |
| 128 | 31,104 | 3.95 | 8.61 |
| 256 | 32,072 | 7.80 | 13.78 |
| 512 | 32,025 | 15.81 | 25.43 |

Saturates at **~32k req/s** at concurrency ~32; past that, throughput is flat and latency grows
linearly — the single-box loopback/client ceiling. The framework is not the bottleneck.

## Layer 2 — WAL append + group-commit coalescing (in-process, isolated)

`LayeredBenchmarks.Layer1` — calls `WriteAheadLogger.AppendAsync` directly, reports the
internal group-commit stats. Raw `fsync` floor (Layer0): **~4 ms** ⇒ 253/s for a group of one.

| Concurrency | appends/s | avg group | avg flush ms |
|---|---|---|---|
| 1 | 254 | 1.0 | 3.9 |
| 8 | 968 | 4.0 | 4.1 |
| 64 | 7,888 | 32.2 | 4.1 |
| 256 | 34,261 | 138.9 | 4.0 |
| 1024 | **95,924** | **618.6** | 6.1 |

This is the proof group-commit works: **avg group size grows 1 → 619** with concurrency and
throughput tracks it (**254 → 96k/s**) on the *same* ~4 ms fsync. Channel publish (Layer2)
measured **~856,000 events/s** — irrelevant to the ceiling.

## Layer 3 — single-event durable ingest (`track`, closed-loop, 1 event/request)

| Concurrency | req/s = events/s | p50 ms | p99 ms | Stored/Sent |
|---|---|---|---|---|
| 8 | 746 | 11.0 | 18.0 | 7468/7468 ✅ |
| 32 | 1,141 | 27.2 | 54.6 | 11438/11438 ✅ |
| 64 | 1,104 | 56.4 | 149.9 | 11063/11063 ✅ |
| 128 | 1,152 | 106.8 | 356.8 | 11539/11539 ✅ |
| 256 | 1,266 | 200.1 | 566.3 | 12682/12682 ✅ |
| 512 | 1,497 | 320.4 | 1,232 | 15014/15014 ✅ |

Flat ~1.1–1.5k/s with linearly growing latency = closed-loop group-commit starvation, **not** a
durability limit (the WAL alone does 96k). Zero loss at every level.

## Layer 4 — batched durable ingest (`beacon`, many events/request)

Batch 100:

| Concurrency | events/s | Stored/Sent |
|---|---|---|
| 8 | 1,014 | 10400/10400 ✅ |
| 32 | 4,080 | 41600/41600 ✅ |
| 64 | 8,072 | 83200/83200 ✅ |
| 128 | 14,723 | 158200/158200 ✅ |
| 256 | 27,045 | 281600/281600 ✅ |

Overdrive (batch 200):

| Concurrency | events/s | p99 ms | Stored/Sent |
|---|---|---|---|
| 256 | 31,388 | 1,796 | 358400/358400 ✅ |
| 512 | 58,950 | 2,046 | 614400/614400 ✅ |
| 1024 | 95,968 | 2,724 | 1024000/1024000 ✅ |
| 2048 | **164,184** | 4,178 | **1913800/1913800 ✅** |

Batched durable ingest scales **near-linearly** with concurrency (1k → 164k events/s) and still
had not plateaued at c2048, delivering **1.9M events with zero loss**. p99 grows because the
pipeline is deeply queued at that offered load, but no event is dropped and nothing errors.

---

## What this tells us

- **Durability is not the wall.** WAL ≈ 96k appends/s isolated; batched e2e ≈ 164k events/s.
- **Group-commit needs concurrent *pending* appends to shine.** A closed-loop, one-event-per-
  request client is its worst case (~1.5k/s); real telemetry (batched beacons / many clients)
  hits 100k+/s.
- **Zero-loss held in every single run** (`stored == sent`), including the 1.9M-event overdrive.
- **Next ceilings to chase**, in order: the ~32k HTTP round-trip cost (matters only for
  single-event `/track`; batch to avoid it), then the ~4 ms macOS `F_FULLFSYNC` (a Linux
  NVMe/tmpfs WAL is much faster), then ClickHouse insert/merge at sustained multi-100k/s.
## Run 2026-08-14 12:28:12 UTC — mode=`breakdown`, target=`http://127.0.0.1:5299`, git=`33467fe+dirty`

- budget: 6s/level, events/request: 1, verify-loss: False

| Concurrency | OK | Errors | req/s | events/s | p50 ms | p95 ms | p99 ms | max ms | Stored/Sent | Peak queue |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 | 44 | 0 | 7 | 7 | 144.24 | 168.91 | 178.42 | 180.6 | n/a | 0 |
| 4 | 96 | 0 | 16 | 16 | 252.31 | 333.17 | 343.98 | 345.3 | n/a | 0 |
| 16 | 82 | 0 | 12 | 12 | 1,240.29 | 1,591.65 | 1,789.08 | 1,924.8 | n/a | 0 |

## Run 2026-08-14 12:28:31 UTC — mode=`breakdown-rollup`, target=`http://127.0.0.1:5299`, git=`33467fe+dirty`

- budget: 6s/level, events/request: 1, verify-loss: False

| Concurrency | OK | Errors | req/s | events/s | p50 ms | p95 ms | p99 ms | max ms | Stored/Sent | Peak queue |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 | 233 | 0 | 39 | 39 | 16.86 | 59.27 | 62.20 | 64.1 | n/a | 0 |
| 4 | 694 | 0 | 115 | 115 | 23.17 | 63.46 | 67.96 | 77.4 | n/a | 0 |
| 16 | 1484 | 0 | 245 | 245 | 57.70 | 106.61 | 122.47 | 142.3 | n/a | 0 |

## Run 2026-08-14 12:28:52 UTC — mode=`breakdown-measure`, target=`http://127.0.0.1:5299`, git=`33467fe+dirty`

- budget: 6s/level, events/request: 1, verify-loss: False

| Concurrency | OK | Errors | req/s | events/s | p50 ms | p95 ms | p99 ms | max ms | Stored/Sent | Peak queue |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 | 46 | 0 | 8 | 8 | 126.01 | 168.42 | 198.05 | 205.9 | n/a | 0 |
| 4 | 78 | 0 | 13 | 13 | 311.61 | 396.83 | 451.93 | 456.7 | n/a | 0 |
| 16 | 77 | 0 | 11 | 11 | 1,343.89 | 1,762.60 | 2,022.38 | 2,162.9 | n/a | 0 |

## Run 2026-08-14 12:29:11 UTC — mode=`breakdown-measure-rollup`, target=`http://127.0.0.1:5299`, git=`33467fe+dirty`

- budget: 6s/level, events/request: 1, verify-loss: False

| Concurrency | OK | Errors | req/s | events/s | p50 ms | p95 ms | p99 ms | max ms | Stored/Sent | Peak queue |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 | 231 | 0 | 38 | 38 | 16.84 | 58.44 | 62.76 | 63.9 | n/a | 0 |
| 4 | 704 | 0 | 116 | 116 | 22.10 | 64.16 | 71.89 | 86.9 | n/a | 0 |
| 16 | 1459 | 0 | 241 | 241 | 58.87 | 107.67 | 127.36 | 180.5 | n/a | 0 |

## Run 2026-08-14 12:29:31 UTC — mode=`timeseries`, target=`http://127.0.0.1:5299`, git=`33467fe+dirty`

- budget: 6s/level, events/request: 1, verify-loss: False

| Concurrency | OK | Errors | req/s | events/s | p50 ms | p95 ms | p99 ms | max ms | Stored/Sent | Peak queue |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 | 160 | 0 | 27 | 27 | 25.74 | 66.07 | 68.74 | 100.5 | n/a | 0 |
| 4 | 430 | 0 | 71 | 71 | 51.26 | 91.30 | 104.01 | 122.1 | n/a | 0 |
| 16 | 393 | 0 | 64 | 64 | 245.01 | 342.72 | 399.01 | 461.7 | n/a | 0 |

## Run 2026-08-14 12:29:50 UTC — mode=`top`, target=`http://127.0.0.1:5299`, git=`33467fe+dirty`

- budget: 6s/level, events/request: 1, verify-loss: False

| Concurrency | OK | Errors | req/s | events/s | p50 ms | p95 ms | p99 ms | max ms | Stored/Sent | Peak queue |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 | 164 | 0 | 27 | 27 | 23.88 | 67.12 | 78.29 | 80.2 | n/a | 0 |
| 4 | 441 | 0 | 73 | 73 | 49.09 | 92.17 | 103.96 | 110.3 | n/a | 0 |
| 16 | 392 | 0 | 64 | 64 | 243.93 | 341.56 | 382.52 | 448.7 | n/a | 0 |

## Run 2026-08-14 12:30:10 UTC — mode=`active`, target=`http://127.0.0.1:5299`, git=`33467fe+dirty`

- budget: 6s/level, events/request: 1, verify-loss: False

| Concurrency | OK | Errors | req/s | events/s | p50 ms | p95 ms | p99 ms | max ms | Stored/Sent | Peak queue |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 | 0 | 27201 | 0 | 0 | 0.00 | 0.00 | 0.00 | 0.0 | n/a | 0 |
| 4 | 0 | 105462 | 0 | 0 | 0.00 | 0.00 | 0.00 | 0.0 | n/a | 0 |
| 16 | 0 | 122880 | 0 | 0 | 0.00 | 0.00 | 0.00 | 0.0 | n/a | 0 |

## Run 2026-08-14 12:35:49 UTC — mode=`breakdown`, target=`http://127.0.0.1:5299`, git=`33467fe+dirty`

- budget: 5s/level, events/request: 1, verify-loss: False

| Concurrency | OK | Errors | req/s | events/s | p50 ms | p95 ms | p99 ms | max ms | Stored/Sent | Peak queue |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 | 36 | 0 | 7 | 7 | 143.70 | 182.86 | 207.84 | 210.8 | n/a | 0 |
| 4 | 85 | 0 | 17 | 17 | 237.04 | 289.31 | 296.49 | 298.1 | n/a | 0 |
| 16 | 77 | 0 | 14 | 14 | 1,092.44 | 1,508.70 | 1,568.47 | 1,709.2 | n/a | 0 |

## Run 2026-08-14 12:36:06 UTC — mode=`breakdown-rollup`, target=`http://127.0.0.1:5299`, git=`33467fe+dirty`

- budget: 5s/level, events/request: 1, verify-loss: False

| Concurrency | OK | Errors | req/s | events/s | p50 ms | p95 ms | p99 ms | max ms | Stored/Sent | Peak queue |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 | 195 | 0 | 39 | 39 | 16.78 | 58.08 | 60.21 | 61.0 | n/a | 0 |
| 4 | 612 | 0 | 121 | 121 | 20.28 | 63.05 | 68.02 | 76.2 | n/a | 0 |
| 16 | 1375 | 0 | 272 | 272 | 50.37 | 97.65 | 111.86 | 141.7 | n/a | 0 |

## Run 2026-08-14 12:36:23 UTC — mode=`breakdown-measure`, target=`http://127.0.0.1:5299`, git=`33467fe+dirty`

- budget: 5s/level, events/request: 1, verify-loss: False

| Concurrency | OK | Errors | req/s | events/s | p50 ms | p95 ms | p99 ms | max ms | Stored/Sent | Peak queue |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 | 37 | 0 | 7 | 7 | 133.56 | 184.21 | 187.61 | 188.4 | n/a | 0 |
| 4 | 72 | 0 | 14 | 14 | 274.03 | 347.51 | 377.66 | 380.4 | n/a | 0 |
| 16 | 71 | 0 | 13 | 13 | 1,223.47 | 1,511.77 | 1,646.56 | 1,751.7 | n/a | 0 |

## Run 2026-08-14 12:36:40 UTC — mode=`breakdown-measure-rollup`, target=`http://127.0.0.1:5299`, git=`33467fe+dirty`

- budget: 5s/level, events/request: 1, verify-loss: False

| Concurrency | OK | Errors | req/s | events/s | p50 ms | p95 ms | p99 ms | max ms | Stored/Sent | Peak queue |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 | 189 | 0 | 37 | 37 | 16.57 | 57.97 | 60.96 | 61.8 | n/a | 0 |
| 4 | 621 | 0 | 123 | 123 | 20.17 | 61.24 | 63.61 | 67.4 | n/a | 0 |
| 16 | 1234 | 0 | 244 | 244 | 58.85 | 109.05 | 127.33 | 175.1 | n/a | 0 |

## Run 2026-08-14 12:36:56 UTC — mode=`timeseries`, target=`http://127.0.0.1:5299`, git=`33467fe+dirty`

- budget: 5s/level, events/request: 1, verify-loss: False

| Concurrency | OK | Errors | req/s | events/s | p50 ms | p95 ms | p99 ms | max ms | Stored/Sent | Peak queue |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 | 142 | 0 | 28 | 28 | 23.87 | 65.22 | 67.98 | 90.3 | n/a | 0 |
| 4 | 383 | 0 | 76 | 76 | 46.70 | 87.86 | 97.78 | 111.5 | n/a | 0 |
| 16 | 364 | 0 | 71 | 71 | 220.23 | 328.31 | 366.81 | 418.1 | n/a | 0 |

## Run 2026-08-14 12:37:13 UTC — mode=`top`, target=`http://127.0.0.1:5299`, git=`33467fe+dirty`

- budget: 5s/level, events/request: 1, verify-loss: False

| Concurrency | OK | Errors | req/s | events/s | p50 ms | p95 ms | p99 ms | max ms | Stored/Sent | Peak queue |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 | 144 | 0 | 29 | 29 | 21.96 | 64.34 | 67.26 | 87.0 | n/a | 0 |
| 4 | 384 | 0 | 76 | 76 | 44.63 | 88.34 | 102.22 | 117.7 | n/a | 0 |
| 16 | 356 | 0 | 69 | 69 | 225.32 | 321.99 | 368.50 | 385.9 | n/a | 0 |

## Run 2026-08-14 12:37:30 UTC — mode=`active`, target=`http://127.0.0.1:5299`, git=`33467fe+dirty`

- budget: 5s/level, events/request: 1, verify-loss: False

| Concurrency | OK | Errors | req/s | events/s | p50 ms | p95 ms | p99 ms | max ms | Stored/Sent | Peak queue |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 | 131 | 0 | 26 | 26 | 26.02 | 68.84 | 72.13 | 75.4 | n/a | 0 |
| 4 | 353 | 0 | 70 | 70 | 50.93 | 95.09 | 109.68 | 123.9 | n/a | 0 |
| 16 | 323 | 0 | 63 | 63 | 250.87 | 344.21 | 408.95 | 445.3 | n/a | 0 |

## Run 2026-08-14 12:37:46 UTC — mode=`timeline`, target=`http://127.0.0.1:5299`, git=`33467fe+dirty`

- budget: 5s/level, events/request: 1, verify-loss: False

| Concurrency | OK | Errors | req/s | events/s | p50 ms | p95 ms | p99 ms | max ms | Stored/Sent | Peak queue |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 | 261 | 0 | 52 | 52 | 11.98 | 53.72 | 55.08 | 57.1 | n/a | 0 |
| 4 | 842 | 0 | 167 | 167 | 17.09 | 58.31 | 63.28 | 71.5 | n/a | 0 |
| 16 | 1116 | 0 | 221 | 221 | 67.22 | 116.68 | 141.67 | 213.3 | n/a | 0 |

## Run 2026-08-14 12:52:17 UTC — mode=`breakdown`, target=`http://127.0.0.1:5299`, git=`33467fe+dirty`

- budget: 2s/level, events/request: 1, verify-loss: False

| Concurrency | OK | Errors | req/s | events/s | p50 ms | p95 ms | p99 ms | max ms | Stored/Sent | Peak queue |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 | 15 | 0 | 7 | 7 | 146.68 | 168.48 | 168.76 | 168.8 | n/a | 0 |

## Run 2026-08-14 12:52:20 UTC — mode=`breakdown-rollup`, target=`http://127.0.0.1:5299`, git=`33467fe+dirty`

- budget: 2s/level, events/request: 1, verify-loss: False

| Concurrency | OK | Errors | req/s | events/s | p50 ms | p95 ms | p99 ms | max ms | Stored/Sent | Peak queue |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 | 67 | 0 | 33 | 33 | 17.73 | 58.62 | 60.17 | 60.6 | n/a | 0 |

## Run 2026-08-14 12:52:24 UTC — mode=`breakdown-measure`, target=`http://127.0.0.1:5299`, git=`33467fe+dirty`

- budget: 2s/level, events/request: 1, verify-loss: False

| Concurrency | OK | Errors | req/s | events/s | p50 ms | p95 ms | p99 ms | max ms | Stored/Sent | Peak queue |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 | 13 | 0 | 6 | 6 | 165.74 | 189.99 | 193.95 | 194.9 | n/a | 0 |

## Run 2026-08-14 12:52:27 UTC — mode=`breakdown-measure-rollup`, target=`http://127.0.0.1:5299`, git=`33467fe+dirty`

- budget: 2s/level, events/request: 1, verify-loss: False

| Concurrency | OK | Errors | req/s | events/s | p50 ms | p95 ms | p99 ms | max ms | Stored/Sent | Peak queue |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 | 68 | 0 | 34 | 34 | 17.49 | 60.00 | 61.27 | 61.4 | n/a | 0 |

## Run 2026-08-14 12:52:30 UTC — mode=`timeseries`, target=`http://127.0.0.1:5299`, git=`33467fe+dirty`

- budget: 2s/level, events/request: 1, verify-loss: False

| Concurrency | OK | Errors | req/s | events/s | p50 ms | p95 ms | p99 ms | max ms | Stored/Sent | Peak queue |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 | 57 | 0 | 28 | 28 | 23.78 | 64.23 | 67.71 | 68.3 | n/a | 0 |

## Run 2026-08-14 12:52:34 UTC — mode=`top`, target=`http://127.0.0.1:5299`, git=`33467fe+dirty`

- budget: 2s/level, events/request: 1, verify-loss: False

| Concurrency | OK | Errors | req/s | events/s | p50 ms | p95 ms | p99 ms | max ms | Stored/Sent | Peak queue |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 | 59 | 0 | 29 | 29 | 21.44 | 62.63 | 63.88 | 64.9 | n/a | 0 |

## Run 2026-08-14 12:52:37 UTC — mode=`active`, target=`http://127.0.0.1:5299`, git=`33467fe+dirty`

- budget: 2s/level, events/request: 1, verify-loss: False

| Concurrency | OK | Errors | req/s | events/s | p50 ms | p95 ms | p99 ms | max ms | Stored/Sent | Peak queue |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 | 55 | 0 | 27 | 27 | 24.29 | 67.87 | 72.16 | 75.3 | n/a | 0 |

## Run 2026-08-14 12:52:41 UTC — mode=`timeline`, target=`http://127.0.0.1:5299`, git=`33467fe+dirty`

- budget: 2s/level, events/request: 1, verify-loss: False

| Concurrency | OK | Errors | req/s | events/s | p50 ms | p95 ms | p99 ms | max ms | Stored/Sent | Peak queue |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 | 111 | 0 | 55 | 55 | 10.61 | 51.73 | 53.00 | 53.3 | n/a | 0 |

## Run 2026-08-14 12:58:49 UTC — mode=`noop`, target=`http://127.0.0.1:5299`, git=`33467fe+dirty`

- budget: 10s/level, events/request: 1, verify-loss: False

| Concurrency | OK | Errors | req/s | events/s | p50 ms | p95 ms | p99 ms | max ms | Stored/Sent | Peak queue |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 | 98236 | 0 | 9,824 | 9,824 | 0.07 | 0.26 | 0.33 | 5.8 | n/a | 0 |
| 8 | 280106 | 0 | 28,010 | 28,010 | 0.20 | 0.56 | 1.54 | 30.9 | n/a | 0 |
| 32 | 297331 | 0 | 29,731 | 29,731 | 0.77 | 2.33 | 8.26 | 26.7 | n/a | 0 |
| 64 | 293285 | 0 | 29,324 | 29,324 | 1.60 | 5.33 | 15.04 | 39.5 | n/a | 0 |
| 128 | 321520 | 0 | 32,147 | 32,147 | 3.04 | 10.13 | 19.28 | 43.4 | n/a | 0 |
| 256 | 320004 | 0 | 31,982 | 31,982 | 6.34 | 18.66 | 27.28 | 42.0 | n/a | 0 |
| 512 | 330442 | 0 | 33,016 | 33,016 | 13.47 | 30.01 | 37.62 | 66.2 | n/a | 0 |

## Run 2026-08-14 13:04:25 UTC — mode=`track`, target=`http://127.0.0.1:5299`, git=`33467fe+dirty`

- budget: 8s/level, events/request: 1, verify-loss: True

| Concurrency | OK | Errors | req/s | events/s | p50 ms | p95 ms | p99 ms | max ms | Stored/Sent | Peak queue |
|---|---|---|---|---|---|---|---|---|---|---|
| 8 | 25051 | 0 | 3,131 | 3,131 | 2.23 | 3.46 | 6.28 | 87.9 | 25051/25051 ✅ | 0 |
| 64 | 128622 | 0 | 16,069 | 16,069 | 2.94 | 7.72 | 14.49 | 111.2 | 128622/128622 ✅ | 0 |
| 256 | 202565 | 0 | 25,255 | 25,255 | 7.83 | 21.87 | 39.69 | 118.6 | 202565/202565 ✅ | 0 |

## Run 2026-08-14 13:39:32 UTC — mode=`beacon`, target=`http://127.0.0.1:5299`, git=`33467fe+dirty`

- budget: 10s/level, events/request: 200, verify-loss: True

| Concurrency | OK | Errors | req/s | events/s | p50 ms | p95 ms | p99 ms | max ms | Stored/Sent | Peak queue |
|---|---|---|---|---|---|---|---|---|---|---|
| 256 | 2048 | 0 | 181 | 36,168 | 1,418.38 | 2,445.11 | 2,455.84 | 2,466.7 | 409600/409600 ✅ | 0 |
| 1024 | 9573 | 0 | 900 | 179,939 | 862.77 | 2,684.11 | 2,717.19 | 2,766.5 | 1914600/1914600 ✅ | 0 |
| 2048 | 16384 | 0 | 1,592 | 318,448 | 1,186.24 | 1,840.70 | 1,855.12 | 1,871.4 | 3276800/3276800 ✅ | 0 |

