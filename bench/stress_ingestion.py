import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import platform
import statistics
import subprocess
import threading
import time

import qualify_ingestion as qualification


CONFIGURATIONS = {
    "baseline": ("managed", "managed"),
    "custom-native": ("io_uring", "custom"),
    "custom-managed": ("managed", "custom"),
    "managed-managed": ("managed", "managed"),
    "managed-native": ("io_uring", "managed"),
}


def reused_binary(output, label, revision):
    binary = json.loads((output / "summary.json").read_text())["binaries"][label]
    assert binary["revision"] == revision, "Reused executable revision does not match"
    assert hashlib.sha256(Path(binary["binary"]).read_bytes()).hexdigest() == binary["sha256"], "Reused executable hash does not match"
    return binary


def classify(level, counts, state, drain_seconds, duration, offered_rate=0):
    acknowledged = level["eventsSent"]
    exact = counts["stored"] == acknowledged and counts["uniqueSessions"] == acknowledged
    offered_met = not offered_rate or level["throughput"] >= offered_rate * 0.98
    sustainable = (level["errors"] == 0 and level["ok"] > 0 and exact
                   and not state["OOMKilled"] and state["Running"]
                   and drain_seconds <= 3 and level["elapsedSeconds"] <= duration * 1.10
                   and offered_met and level["visibilityTimeouts"] == 0 and level["visibilityDropped"] == 0)
    return {"allAcknowledgedEventsCounted": counts["stored"] >= acknowledged,
            "exactZeroErrorCounts": exact and level["errors"] == 0,
            "uniqueStoredSessions": counts["uniqueSessions"] == counts["stored"],
            "sustainable": sustainable,
            "p99Under100ms": sustainable and level["p99"] <= 100,
            "p99Under1000ms": sustainable and level["p99"] <= 1000,
            "offeredRateMet": bool(offered_met)}


def resource_summary(samples, elapsed, ticks):
    selected = [row for row in samples if row["time"] <= samples[0]["time"] + elapsed]
    if len(selected) < 2:
        selected = samples
    period = selected[-1]["time"] - selected[0]["time"]
    cpu = (selected[-1]["cpuTicks"] - selected[0]["cpuTicks"]) / ticks
    peaks = [(b["cpuTicks"] - a["cpuTicks"]) / ticks / (b["time"] - a["time"]) * 100
             for a, b in zip(selected, selected[1:]) if b["time"] > a["time"]]
    return {"meanCpuPercentOfOneCore": cpu / period * 100 if period else 0,
            "peakCpuPercentOfOneCore": max(peaks, default=0),
            "sampledPeakRssBytes": max(row["rssBytes"] for row in samples),
            "sampledPeakCgroupBytes": max(row["cgroupBytes"] for row in samples),
            "throttledMicros": samples[-1]["throttledMicros"] - samples[0]["throttledMicros"],
            "scope": "CPU approximates client load window; peaks include verification and drain"}


def stored_counts(target, tenant):
    assert tenant.replace("-", "").isalnum(), "Only benchmark-generated tenants are queried"
    query = ("SELECT count() AS stored, uniqExact(session_id) AS uniqueSessions "
             "FROM nealytics_core.global_events WHERE tenant_id='" + tenant + "' FORMAT JSONEachRow")
    result = json.loads(qualification.docker("exec", target["storage"], "clickhouse-client", "--query", query))
    return {key: int(value) for key, value in result.items()}


def wait_for_drain(target, seconds=30):
    started = time.monotonic()
    while time.monotonic() - started < seconds:
        state = json.loads(qualification.docker("inspect", "--format", "{{json .State}}", target["engine"]))
        if not state["Running"]:
            return False, time.monotonic() - started, state
        length = qualification.docker("exec", target["engine"], "stat", "-c", "%s", "/wal/telemetry_wal.log")
        if length == "0":
            return True, time.monotonic() - started, state
        time.sleep(0.25)
    return False, time.monotonic() - started, state


def run_level(repository, output, client, target, name, mode, concurrency, duration, rate, visibility, timeout):
    manifest = output / (name + ".json")
    command = ["dotnet", str(client), "--url", f"http://127.0.0.1:{target['port']}",
               "--key", "b6-key", "--mode", mode, "--concurrency", str(concurrency),
               "--beacon-batch", "50", "--ch", target["connection"], "--duration", str(duration),
               "--warmup", "0", "--rate", str(rate), "--verify-loss", "false",
               "--visibility-every", str(visibility), "--manifest", str(manifest),
               "--out", str(output / "client-results.md")]
    environment = os.environ.copy()
    environment.update(target["settings"])
    environment["GIT_SHA"] = target["revision"]
    with (output / (name + ".log")).open("w") as log:
        result = subprocess.run(command, cwd=repository, env=environment, stdout=log,
                                stderr=subprocess.STDOUT, timeout=timeout)
    return json.loads(manifest.read_text())["levels"][0], result.returncode


def measure(repository, output, client, binaries, args, label, mode, concurrency, duration, stage, rate=0, visibility=0):
    backend, execution = CONFIGURATIONS[label]
    binary = binaries["baseline" if label == "baseline" else "candidate"]
    name = f"{label}-{mode}-{stage}-c{concurrency}-r{rate:.1f}"
    print("Starting", name, flush=True)
    overrides = {"TelemetryEngine__MaxConcurrentIngestionRequests": str(args.admission_concurrency),
                 "TelemetryEngine__IngestionRequestByteBudget": str(args.request_byte_budget),
                 "TelemetryEngine__MaxRequestBodyBytes": str(args.max_body_bytes),
                 "TelemetryEngine__DeliveryJournalMaxBytes": str(args.delivery_journal_bytes),
                 "TelemetryEngine__DeliveryJournalMaxRecords": str(args.delivery_journal_records),
                 "TelemetryEngine__WalMaxBacklogBytes": str(args.wal_backlog_bytes)}
    with qualification.deployment(output, binary["binary"], backend, execution,
                                  settings_overrides=overrides, engine_cpus=args.engine_cpus,
                                  engine_memory=args.engine_memory) as target:
        target["revision"] = binary["revision"]
        warmup_concurrency = min(concurrency, 16)
        try:
            run_level(repository, output, client, target, name + "-warmup", mode, warmup_concurrency,
                      args.warmup_seconds, 0, 0, args.warmup_seconds + 45)
        except (subprocess.TimeoutExpired, FileNotFoundError, json.JSONDecodeError) as error:
            return {"name": name, "label": label, "mode": mode, "stage": stage, "concurrency": concurrency,
                    "offeredRate": rate, "failure": "warmup observation failed: " + type(error).__name__}
        warmup_drained, _, _ = wait_for_drain(target)
        if not warmup_drained:
            return {"name": name, "label": label, "mode": mode, "stage": stage, "concurrency": concurrency,
                    "offeredRate": rate, "failure": "warmup did not drain; measured phase was not started"}
        samples = {"engine": [qualification.sample(target["engine"])],
                   "storage": [qualification.sample(target["storage"])]}
        errors = []
        stopped = threading.Event()

        def observe():
            while not stopped.wait(1):
                for role in samples:
                    try:
                        samples[role].append(qualification.sample(target[role]))
                    except (subprocess.CalledProcessError, OSError, StopIteration) as error:
                        errors.append({"role": role, "error": str(error)})

        observer = threading.Thread(target=observe)
        observer.start()
        try:
            phase_started = time.monotonic()
            level, exit_code = run_level(repository, output, client, target, name, mode, concurrency,
                                         duration, rate, visibility, duration * 2 + 45)
            drained, drain_seconds, state = wait_for_drain(target)
            drain_seconds += max(0, time.monotonic() - phase_started - drain_seconds - level["elapsedSeconds"])
            counts = stored_counts(target, level["tenantId"])
        except (subprocess.TimeoutExpired, FileNotFoundError, json.JSONDecodeError) as error:
            state = json.loads(qualification.docker("inspect", "--format", "{{json .State}}", target["engine"]))
            result = {"name": name, "label": label, "mode": mode, "stage": stage,
                      "concurrency": concurrency, "offeredRate": rate, "state": state,
                      "failure": "client did not produce a complete bounded observation: " + type(error).__name__}
            print("Failed", name, result["failure"], flush=True)
            return result
        finally:
            stopped.set()
            observer.join()
            (output / (name + "-samples.json")).write_text(json.dumps({"samples": samples, "errors": errors}, indent=2))
        for role in samples:
            if role == "storage" or state["Running"]:
                try:
                    samples[role].append(qualification.sample(target[role]))
                except (subprocess.CalledProcessError, OSError, StopIteration) as error:
                    errors.append({"role": role, "error": str(error)})
        ticks = int(qualification.docker("exec", target["storage"], "getconf", "CLK_TCK"))
        result = {"name": name, "label": label, "mode": mode, "stage": stage,
                  "concurrency": concurrency, "duration": duration, "offeredRate": rate,
                  "level": level, "clientExit": exit_code, "counts": counts, "state": state,
                  "walDrained": drained, "drainSeconds": drain_seconds, "samplingErrors": errors,
                  "assessment": classify(level, counts, state, drain_seconds, duration, rate),
                  "resources": {role: resource_summary(rows, level["elapsedSeconds"], ticks)
                                for role, rows in samples.items()}}
        for key in ("sustainable", "p99Under100ms", "p99Under1000ms"):
            result["assessment"][key] &= drained and not errors
        qualification.docker("stop", "--time", "40", target["engine"])
        result["shutdownState"] = json.loads(qualification.docker("inspect", "--format", "{{json .State}}", target["engine"]))
        if result["shutdownState"]["ExitCode"] != 0 or result["shutdownState"]["OOMKilled"]:
            for key in ("sustainable", "p99Under100ms", "p99Under1000ms"):
                result["assessment"][key] = False
        print("Finished", name, "events/s", round(level["eventThroughput"]), "errors", level["errors"],
              "p99", level["p99"], "stored", counts["stored"], "sustainable", result["assessment"]["sustainable"], flush=True)
        return result


def ceilings(runs):
    measured = [row for row in runs if "level" in row]
    result = {}
    for label in CONFIGURATIONS:
        for mode in ("track", "beacon"):
            group = [row for row in measured if row["label"] == label and row["mode"] == mode]
            if not group:
                continue
            values = {}
            for key in ("sustainable", "p99Under100ms", "p99Under1000ms"):
                eligible = [row for row in group if row["assessment"][key]]
                values[key] = max(eligible, key=lambda row: row["level"]["eventThroughput"])["name"] if eligible else None
            values["peakAcknowledged"] = max(group, key=lambda row: row["level"]["eventThroughput"])["name"]
            confirmations = [row for row in group if row["stage"].startswith("confirm-")]
            confirmed = []
            for count in {row["concurrency"] for row in confirmations}:
                repeated = [row for row in confirmations if row["concurrency"] == count]
                if len(repeated) >= 2 and all(row["assessment"]["sustainable"] for row in repeated):
                    confirmed.append({"concurrency": count,
                                      "eventsPerSecond": statistics.median(row["level"]["eventThroughput"] for row in repeated)})
            values["confirmed"] = max(confirmed, key=lambda value: value["eventsPerSecond"]) if confirmed else None
            values["confirmationsPassed"] = bool(confirmed)
            values["concurrencyLadderExhausted"] = max(group, key=lambda row: row["concurrency"])["assessment"]["sustainable"]
            result[label + "/" + mode] = values
    return result


def save(output, summary):
    summary["ceilings"] = ceilings(summary["runs"])
    (output / "summary.json").write_text(json.dumps(summary, indent=2))
    lines = ["# Ingestion saturation experiment", "", "Measured ceilings apply only to the declared workload and hardware.", "",
             "| Configuration | Mode | Stage | Callers | Offered req/s | Ack events/s | Errors | p95 ms | p99 ms | Stored | Drain s | Eligible |",
             "| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |"]
    for row in summary["runs"]:
        if "level" not in row:
            lines.append(f"| {row['label']} | {row['mode']} | {row['stage']} | {row['concurrency']} | {row['offeredRate']} | failed | | | | | | false |")
            continue
        level = row["level"]
        lines.append(f"| {row['label']} | {row['mode']} | {row['stage']} | {row['concurrency']} | {row['offeredRate']:.1f} | "
                     f"{level['eventThroughput']:.1f} | {level['errors']} | {level['p95']:.2f} | {level['p99']:.2f} | "
                     f"{row['counts']['stored']} | {row['drainSeconds']:.2f} | {row['assessment']['sustainable']} |")
    (output / "results.md").write_text("\n".join(lines) + "\n")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--baseline", default="f1cfe3e0c4e69bbbafc6096c02659a68986d0836")
    parser.add_argument("--candidate", default="HEAD")
    parser.add_argument("--reuse-baseline-from", type=Path, required=True)
    parser.add_argument("--reuse-candidate-from", type=Path)
    parser.add_argument("--configurations", default=",".join(CONFIGURATIONS))
    parser.add_argument("--concurrency", default="1,8,16,64,256,1024")
    parser.add_argument("--duration", type=int, default=10)
    parser.add_argument("--warmup-seconds", type=int, default=5)
    parser.add_argument("--confirmation-seconds", type=int, default=30)
    parser.add_argument("--confirmation-repetitions", type=int, default=2)
    parser.add_argument("--admission-concurrency", type=int, default=64)
    parser.add_argument("--request-byte-budget", type=int, default=16_777_216)
    parser.add_argument("--max-body-bytes", type=int, default=1_048_576)
    parser.add_argument("--delivery-journal-bytes", type=int, default=268_435_456)
    parser.add_argument("--delivery-journal-records", type=int, default=100_000)
    parser.add_argument("--wal-backlog-bytes", type=int, default=1_073_741_824)
    parser.add_argument("--engine-cpus", type=float, default=1)
    parser.add_argument("--engine-memory", default="512m")
    args = parser.parse_args()
    assert platform.machine() == "arm64", "Native performance requires the actual supported architecture"
    configurations = args.configurations.split(",")
    concurrency = [int(value) for value in args.concurrency.split(",")]
    assert set(configurations) <= CONFIGURATIONS.keys() and configurations
    assert concurrency and all(value > 0 for value in concurrency)
    assert min(args.duration, args.warmup_seconds, args.confirmation_seconds, args.confirmation_repetitions) > 0
    assert args.admission_concurrency > 0 and args.max_body_bytes > 0 and args.request_byte_budget >= args.max_body_bytes
    assert min(args.delivery_journal_bytes, args.delivery_journal_records, args.wal_backlog_bytes) > 0
    assert math.isfinite(args.engine_cpus) and args.engine_cpus > 0
    repository = Path(__file__).resolve().parent.parent
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    baseline = qualification.run("git", "-C", str(repository), "rev-parse", args.baseline)
    candidate = qualification.run("git", "-C", str(repository), "rev-parse", args.candidate)
    previous = reused_binary(args.reuse_baseline_from, "baseline", baseline)
    if args.reuse_candidate_from:
        current = reused_binary(args.reuse_candidate_from, "candidate", candidate)
        source = output / "sources" / "candidate"
        source.mkdir(parents=True)
        (source / "clickhouse-init.sql").write_text(qualification.run("git", "-C", str(repository),
                                                                  "show", candidate + ":clickhouse-init.sql"))
    else:
        current = qualification.build(repository, output, candidate, "candidate", native=True)
    binaries = {"baseline": previous, "candidate": current}
    client_directory = output / "client"
    client_directory.mkdir()
    with (output / "client-build.log").open("w") as log:
        subprocess.run(["dotnet", "build", str(repository / "bench/Nealytics.Engine.Bench"), "--configuration", "Release",
                        "--output", str(client_directory)], check=True, stdout=log, stderr=subprocess.STDOUT)
    client = client_directory / "Nealytics.Engine.Bench.dll"
    summary = {"host": platform.platform(), "binaries": binaries, "settings": vars(args) | {"output": str(output),
               "reuse_baseline_from": str(args.reuse_baseline_from),
               "reuse_candidate_from": str(args.reuse_candidate_from) if args.reuse_candidate_from else None},
               "runs": [], "ceilings": {},
               "notes": ["One fresh database and WAL volume per point; durable fsync and synchronous inserts.",
                         "Eligibility requires zero errors, exact unique stored session counts and at most three seconds of conservative post-load overhead/drain.",
                         "Overload beacon failures can persist partial request prefixes; extra stored rows are not labeled duplicates or loss.",
                         "Request percentiles cover successful acknowledgments; scheduled percentiles cover offered-arrival delay.",
                         "Visibility is sampled during longer confirmations; the short ramp omits probes to reduce observer load.",
                         "Reported maxima are measured bounds within the tested ladder, not universal maximum capacity.",
                         "The client reports aggregate errors, without HTTP status classification."]}
    for label in configurations:
        for mode in ("track", "beacon"):
            group = []
            for count in concurrency:
                result = measure(repository, output, client, binaries, args, label, mode, count, args.duration, "ramp")
                group.append(result)
                summary["runs"].append(result)
                save(output, summary)
            eligible = [row for row in group if row.get("assessment", {}).get("sustainable")]
            if not eligible:
                continue
            best = None
            for candidate in sorted(eligible, key=lambda row: row["level"]["eventThroughput"], reverse=True):
                confirmed = True
                for repetition in range(args.confirmation_repetitions):
                    result = measure(repository, output, client, binaries, args, label, mode, candidate["concurrency"],
                                     args.confirmation_seconds, "confirm-" + str(repetition + 1),
                                     visibility=max(1, int(candidate["level"]["throughput"] / 5)))
                    summary["runs"].append(result)
                    save(output, summary)
                    if not result.get("assessment", {}).get("sustainable"):
                        confirmed = False
                        break
                if confirmed:
                    best = candidate
                    break
            if best is None:
                continue
            for ratio in (0.8, 1.0, 1.2):
                rate = best["level"]["throughput"] * ratio
                result = measure(repository, output, client, binaries, args, label, mode, max(64, best["concurrency"]),
                                 args.duration, "arrival-" + str(ratio), rate)
                summary["runs"].append(result)
                save(output, summary)


if __name__ == "__main__":
    main()
