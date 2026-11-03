# Correctness and ingestion sweep: measured checkpoint

Updated 2026-10-30. This is an unfinished qualification checkpoint, not release evidence.
The engine remains on feature branches; nothing was merged into main.

The owner stopped all benchmarks and measurements on 2026-10-30. No measurement process or
benchmark container remains active. Development and correctness qualification continue. This
document preserves historical observations and does not authorize another measurement run.

The owner reports that the earlier measurements ran with Battery Saver enabled. Their power
state was not captured per point. The timing tables and earlier saturation numbers below are
historical observations and must not supply a performance comparison or speedup claim. The
replacement measurement used only custom ingestion with io_uring and recorded Low Power Mode
throughout each point. That measurement work is also stopped. Correctness counts and test results remain
separate evidence.

## Preliminary latency measurements

Values are milliseconds, each the median of three per-run percentiles. They are request
acknowledgment latencies, not query visibility latencies. Do not treat a median of run
percentiles as a percentile of the combined sample population.

| Configuration | Track p95 | Track p99 | Beacon p95 | Beacon p99 |
| --- | ---: | ---: | ---: | ---: |
| Original baseline | 22.72 | 59.78 | 323.58 | 460.80 |
| Managed ingestion / managed WAL | 39.17 | 132.61 | 26.94 | 194.56 |
| Custom ingestion / managed WAL | 30.46 | 91.39 | 29.44 | 168.45 |
| Managed ingestion / io_uring WAL | 42.62 | 120.06 | 26.94 | 108.54 |
| Custom ingestion / io_uring WAL | 24.32 | 84.74 | 38.53 | 142.34 |

All thirty measured runs together sent 435,000 events and stored exactly 435,000, with zero
request errors and no stored-count mismatches. Track offered 200 requests/events per second.
Beacon offered 25 requests per second with 50 events per request, or 1,250 events per second.
These are tested offered loads, not maximum throughput measurements. Eight workers served each
workload. Runs lasted twenty seconds, after the original fixture's 100-request warmup.

The engine had one CPU and 512 MiB. ClickHouse had two CPUs and two GiB. Sampled engine
process peak RSS ranged from 181.38 to 198.90 MiB across the runs; these figures exclude the
database and are not whole-deployment memory. The candidate single executable was 50,897,816
bytes. Both versions used durable WAL fsync, synchronous database inserts, a 1,000-record
batch setting and a one-second flush interval on actual Linux ARM64 in Docker.

Beacon acknowledgment tails improved in this experiment. Track p99 regressed in every
candidate configuration. io_uring did not establish a consistent win across these workloads.
Older qualification databases were active during this matrix, including a memory-pressured
baseline instance. The timing is noisy and must not supply release-note speed percentages or
claims of superiority to Tinybird. No Tinybird benchmark was performed.

Baseline source: f1cfe3e0c4e69bbbafc6096c02659a68986d0836.
Candidate source: 3e86f3be64d757fe01dfd59b6d120ea06342698b.
Raw local evidence: /tmp/nealytics-b6-matrix-20261029-v2/summary.json.
The candidate has received further correctness fixes since that exploratory measurement.
The final matrix uses a ten-second workload warmup and requires the heavy test processes to
finish first, with older owned databases stopped and their data preserved.

## What the implementation now provides

- A5: persisted rollup readiness and raw-query fallback when readiness cannot be established.
- A6: durable delivery manifests freeze batch membership, record identities, schema, database
  table identity and ingestion timestamp before insertion. Retries reuse the same insert token.
  This has explicit exclusive-writer and finite deduplication-window requirements; it is not
  an unlimited exactly-once guarantee.
- A7: separate bounded database workloads, request concurrency and body admission, channel and
  database batch byte limits, and bounded retained WAL and delivery-journal capacity.
- B5: configurable managed or custom request execution with the existing caller contract.
- B6: a repeatable four-combination ingestion/WAL comparison and published-artifact crash fixture.

The defaults still use managed ingestion and managed WAL. Backend availability alone does not
justify enabling it by default.

## Qualification state

| Package | State |
| --- | --- |
| A5 | Scoped correctness, coverage and mutation gates passed |
| A6 | Implemented; combined coverage and mutation qualification open |
| A7 | Implemented; combined coverage and mutation qualification open |
| B5 | Implemented; mutation gate passed at 977978a, combined artifact qualification open |
| B6 | Exploratory matrix complete; final clean matrix, all-mode crash checks and default decision open |

General CI at source 12a6781 passed build, unit, integration, benchmark and smoke jobs:
https://github.com/neavents/nealytics/actions/runs/40960013642.
Both actual Linux x64 and ARM64 jobs passed their platform, native coverage/sanitizer,
published-artifact and selected native-boundary mutation gates:
https://github.com/neavents/nealytics/actions/runs/40960013745.
The earlier three native-boundary survivors are closed by overflow and queued-checkpoint
failure regressions. This selected native gate does not replace broad A6/A7 mutation qualification.

A full isolated real-ClickHouse suite passed 184 cases at source 1d63a4b, with zero skips.
The preceding full portable unit run passed 2,037 cases; later focused regressions passed.
Further source changes still require final same-source qualification. The corrected standalone
custom/managed crash fixture recovered all 48 accepted identifiers after a forced process kill;
all four modes remain to be qualified together. This does not establish whole-host power-loss
behavior. The B5 mutation rerun at source 977978a detected all 65 executable mutants: 57 assertion
kills and eight timeout detections, with zero survivors or uncovered mutants. Seven selected
mutants could not compile. The broad A6/A7 mutation run has surviving mutants under investigation.
A later unit-only sweep reached 98.79%: 403 assertion kills, five timeout detections, five survivors,
zero uncovered mutants and fifty selected compile errors. Pool integration qualification remains
separate. Follow-up regressions and a redundant flush removal are awaiting verification.
The sweep is not 100% complete.

## Throughput stress qualification

The new saturation experiment reuses the hash-verified original executable and candidate
12a6781. Its [protocol](packages/b6-saturation-protocol.md) ramps to 1,024 callers, repeats
eligible rates twice for thirty seconds, verifies unique stored sessions and tests fixed
arrivals above the selected short peak. Fresh databases and WAL volumes isolate every point.

Stock-profile stress exposed request admission rejection and exhaustion of retained WAL
generations under continuous traffic. A short successful acknowledgment peak cannot establish
sustained capacity when the longer run returns errors. Increasing finite retention budgets
is a separately labeled hardware-probing experiment and does not close live generation
reclamation. Keep all overload failures visible and leave the default-selection gate open.
