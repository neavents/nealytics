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

**The `~1,500 req/s` figure that looked bad is the pathological worst case:** one durable event
per request from a **closed-loop** client that waits for each `202`. That starves group-commit —
it can only amortize an `fsync` across *concurrently pending* appends, and a wait-for-each client
never lets a backlog form (observed WAL group size ≈ 5–6). Feed it concurrency (batched beacons,
or many independent clients) and it coalesces into groups of hundreds and rides the WAL up to
**~164k events/s**. Every run below is **zero-loss** (`stored == sent`).

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
