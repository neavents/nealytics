# Correctness and ingestion sweep: measured checkpoint

Recorded 2026-10-29. This is an unfinished qualification checkpoint, not release evidence.
The engine remains on feature branches; nothing was merged into main.

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

The latest general CI at source 81b2ba9 passed build, unit, integration, benchmark and smoke
jobs: https://github.com/neavents/nealytics/actions/runs/40951681940.
The separate actual Linux x64 and ARM64 run was still running when this checkpoint was written:
https://github.com/neavents/nealytics/actions/runs/40951682055.
The preceding platform run passed 2,013 unit and 24 native cases on both architectures but
failed its coverage gate on three checkpoint-failure lines. A new fault regression covers
that path; its platform rerun is not assumed successful until the jobs complete.

A full isolated real-ClickHouse suite passed 182 cases at source 71e2657. Later production
changes and new tests still require final same-source qualification. The corrected standalone
custom/managed crash fixture recovered all 48 accepted identifiers after a forced process kill;
all four modes remain to be qualified together. This does not establish whole-host power-loss
behavior. The B5 mutation rerun at source 977978a detected all 65 executable mutants: 57 assertion
kills and eight timeout detections, with zero survivors or uncovered mutants. Seven selected
mutants could not compile. The broad A6/A7 mutation run has surviving mutants under investigation. The sweep is
not 100% complete.
