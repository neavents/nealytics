# B6 saturation and throughput ceilings

Compare the original f1cfe3e engine with the integrated candidate, including managed/custom
request execution and managed/io_uring WAL. Every point owns a fresh database and WAL volume.
Keep durable fsync, synchronous inserts, database schema, client payload, CPU and memory limits
matched. Default limits are one CPU/512 MiB for the engine and two CPUs/two GiB for ClickHouse.
No developer or production services are stopped, resized or used as test storage.

Ramp closed-loop callers through 1, 8, 16, 64, 256 and 1,024. Track sends one event per request;
beacon sends fifty. Warm each point for five seconds, drain warmup WAL, and measure ten seconds.
Report acknowledgment events/sec and request p95/p99 alongside errors, unique stored session
counts, WAL drain, engine/database CPU, sampled RSS/cgroup peaks, throttling and OOM state.
Request percentiles cover successful responses; their interpretation always includes the error
count. An overloaded point with failed beacon requests may persist a valid request prefix, so
extra stored rows are not described as duplication or loss without individual receipts.

A short point is eligible only with no errors, exact stored/unique-session counts, a running
non-OOM engine and no more than three seconds of conservative post-load overhead/drain. Repeat
eligible candidates for two thirty-second confirmations, adding sampled visibility checks.
If a faster short point fails confirmation, try a lower eligible rate. Report the highest
repeated result separately from a short peak and from throughput meeting p99 budgets of
100 ms and one second. These are measured bounds within this workload and ladder, not universal
maximum-capacity claims. An exhausted healthy ladder is a lower bound, not a discovered ceiling.

Finally offer fixed-arrival rates at 80%, 100% and 120% of the confirmed candidate's short
request rate. Require at least 98% of the offered rate, with no errors or significant extension
of the measurement interval, before calling an offered rate sustained. Report scheduled-arrival
p95/p99 so client waiting cannot hide behind a low acknowledgment percentile.

The stock profile preserves the previous request settings: 64 configured concurrent requests,
a 16-MiB request budget and a one-MiB maximum body, yielding sixteen effective reservations.
A separately labeled tuning experiment may use a 64-KiB body limit for these known small
payloads and 256 configured requests, keeping the same sixteen-MiB budget. Apply matching
supported settings to both versions and record settings that the original does not implement.
This is operator tuning; producer endpoints, payloads and acknowledgment durability do not change.

Run bench/stress_ingestion.py with a fresh output directory and the hash-verified original
binary from the preliminary matrix. Save manifests, samples, logs, binary hashes and partial
results after every point. Heavy qualification processes must finish before timed stress starts.
The isolated server may be deliberately overloaded; observation windows and cleanup stay bounded.
