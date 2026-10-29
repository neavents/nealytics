# B6: combined ingestion qualification

Branch: feat/ingestion-performance-b6-qualification

## Required comparison

Run the original engine at f1cfe3e0c4e69bbbafc6096c02659a68986d0836 and the integrated
candidate on identical storage, durability, resource and offered-load settings. The candidate
matrix is managed/custom request execution crossed with managed/io_uring WAL. Compare each
candidate against the original and against the candidate's managed/managed control. Report the
cost of the safety changes as well as any benefit from custom execution or native I/O.

Use bench/qualify_ingestion.py on actual Linux ARM64 through Docker on this ARM64 workstation.
Each run owns a fresh database, network and persistent WAL volume with unique names and labels.
Cleanup verifies ownership. The baseline executable may be reused only after checking its recorded
revision and SHA-256. The candidate is built from an immutable Git archive and runs as a non-root,
runtime-free, single-executable deployment. ClickHouse is pinned to 25.3.14.14. io_uring permission
applies only to the isolated test container.

The engine receives one CPU and 512 MiB; ClickHouse receives two CPUs and two GiB. Both versions
use synchronous database inserts, durable WAL flushes, database batches of 1,000 records and a
one-second flush interval. Ordinary rate limits are raised explicitly for qualification. Track
offers 200 requests/events per second. Beacon offers 25 requests per second with 50 events each,
or 1,250 events per second. Eight caller workers serve both workloads. These are named offered
loads, not measurements of maximum capacity.

Warm each scenario for ten seconds at its measurement rate. Measure twenty seconds and repeat
three times, rotating the order of implementations. Save request p95/p99, scheduled-arrival
p95/p99, exact stored counts, errors and sampled visibility lag. Save engine process CPU ticks,
sampled RSS, cgroup usage and throttling separately from client statistics. CPU sampling includes
the workload and its stored-count verification; warmup is excluded. RSS is a sampled peak. Keep
all manifests and samples with the executable hashes and declared settings.

## Durability and acceptance

Force-kill the engine after 32 track records and two eight-record beacon requests have received
their existing successful responses, while database batching still retains all 48 in the WAL.
Restart against the same volume, refresh the dynamically assigned host port, and verify the exact
48 identifiers without a merge or FINAL query. Include an explicit zero event UUID. Repeat for
all four candidate modes and require a clean subsequent shutdown with no OOM termination.

The engine's kill/restart test keeps ClickHouse running. It does not establish whole-host power-loss
behavior or replace the database deployment's own durability configuration. The replay-safe
protocol retains its documented exclusive-writer and finite deduplication-window boundaries.
Real-server integration separately exercises an ambiguous database response, frozen membership
and timestamps, token eviction, partial view failure and readiness fencing.

A default switch requires qualified correctness, coverage, mutation, platform and packaging checks
plus the measured outcome for the named workloads. A backend's availability does not establish
that it improves every workload. Record regressions and any default that remains unchanged.

## Evidence status

The first thirty-run matrix against candidate 3e86f3b completed without request errors or stored
count mismatches. Its timing varied substantially while older qualification databases remained
active, including a memory-pressured baseline instance. Those instances were stopped with their
data preserved. The first matrix is exploratory evidence and must not supply release-note speed
percentages. Its restart fixture also required refreshing the ephemeral host port after restart;
the corrected fixture subsequently recovered all 48 records in the custom/managed mode.

The final matrix, platform results, combined coverage, mutation findings and default decision are
recorded with the final verification artifacts. This document does not mark an unfinished gate
as passed.


The actual x64/ARM64 run at source 81b2ba9 passed its preceding platform, coverage and artifact
stages but stopped at the final mutation gate (98.60%). Both architectures reported the same
three surviving mutations: unchecked recovery byte addition, unchecked recovery record addition,
and removing the checkpoint failure check after acquiring the file gate. New tests inject
metadata measurements that overflow each total while preserving files and directory ownership,
and queue a checkpoint behind a failing durable flush to assert that its cleanup never runs.
These focused regressions pass. Subsequent runs at 44fe741 and 12a6781 passed both actual
architectures, including the selected native-boundary mutation gate. The broad A6/A7 managed
mutation scope remains a separate unfinished gate.

The [saturation protocol](b6-saturation-protocol.md) extends the offered-load matrix to measure
throughput bounds and overload behavior. Stock-profile refusal under retained-generation
pressure prevents treating a successful short peak as sustained capacity. A larger-budget
profile is operator tuning and cannot replace continuous-traffic reclamation or justify a
default switch by itself.
