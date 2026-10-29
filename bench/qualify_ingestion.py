import argparse
import contextlib
import hashlib
import http.client
import io
import json
import os
from pathlib import Path
import platform
import subprocess
import tarfile
import threading
import time
import uuid


BUILD_IMAGE = "nealytics-native-build-tools:ab"
RUNTIME_IMAGE = "mcr.microsoft.com/dotnet/runtime-deps:10.0@sha256:62028293921f5bd6db3d9f9119cd29cd926dfd3c2336da23b09f425e40c3c832"
STORAGE_IMAGE = "clickhouse/clickhouse-server:25.3.14.14@sha256:b627d7a9bc0e0c1bac26cdbe9d2fc6316faa29c5d8a174f28f5abd57d0fa6ba2"
LABEL = "nealytics.scope=b6-qualification"


def run(*command, **kwargs):
    return subprocess.run(command, check=True, text=True, capture_output=True, **kwargs).stdout.strip()


def docker(*arguments):
    return run("docker", *arguments)


def request(port, method, path, body=None):
    connection = http.client.HTTPConnection("127.0.0.1", port, timeout=10)
    try:
        connection.request(method, path, body, {"Content-Type": "application/json"})
        response = connection.getresponse()
        return response.status, response.read().decode()
    finally:
        connection.close()


def wait_for(action, seconds=90):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        try:
            if result := action():
                return result
        except (OSError, http.client.HTTPException, subprocess.CalledProcessError):
            pass
        time.sleep(0.2)
    raise TimeoutError("The isolated qualification operation did not become ready")


def snapshot(repository, revision, destination):
    destination.mkdir(parents=True, exist_ok=False)
    data = subprocess.check_output(["git", "archive", revision], cwd=repository)
    with tarfile.open(fileobj=io.BytesIO(data)) as archive:
        archive.extractall(destination)


def build(repository, output, revision, name, native):
    print("Building", name, revision, flush=True)
    source = output / "sources" / name
    snapshot(repository, revision, source)
    binary_directory = output / "binaries" / name
    binary_directory.mkdir(parents=True)
    arguments = ["docker", "run", "--rm", "--cpus", "2", "--memory", "3g", "--cap-drop", "ALL",
                 "--env", "NUGET_PACKAGES=/cache", "--env", "DOTNET_CLI_HOME=/tmp/dotnet",
                 "--mount", f"type=bind,source={Path.home()}/.nuget/packages,target=/cache",
                 "--mount", f"type=bind,source={source},target=/workspace",
                 "--mount", f"type=bind,source={binary_directory},target=/output", BUILD_IMAGE,
                 "dotnet", "publish", "src/Nealytics.Engine", "--configuration", "Release", "--runtime",
                 "linux-arm64", "--self-contained", "true", "--output", "/output", "-p:DebugType=None"]
    if native:
        arguments.append("-p:NealyticsNativeLibrary=/native-output/libnealytics_wal.so")
    with (output / (name + "-build.log")).open("w") as log:
        subprocess.run(arguments, check=True, stdout=log, stderr=subprocess.STDOUT)
    binary = binary_directory / "Nealytics.Engine"
    return {"revision": revision, "binary": str(binary), "bytes": binary.stat().st_size,
            "sha256": hashlib.sha256(binary.read_bytes()).hexdigest()}


@contextlib.contextmanager
def deployment(output, binary, backend, execution, interval=1):
    suffix = uuid.uuid4().hex[:12]
    network, storage, engine, volume = ["nealytics-b6-" + role + "-" + suffix
                                        for role in ("net", "ch", "engine", "wal")]
    owned = {"network": False, "volume": False, "storage": False, "engine": False}
    try:
        docker("network", "create", "--label", LABEL, network)
        owned["network"] = True
        docker("volume", "create", "--label", LABEL, volume)
        owned["volume"] = True
        docker("run", "--rm", "--label", LABEL, "--mount", f"type=volume,source={volume},target=/wal",
               "--entrypoint", "/bin/chown", RUNTIME_IMAGE, "10001:10001", "/wal")
        docker("run", "--detach", "--name", storage, "--label", LABEL, "--network", network,
               "--memory", "2g", "--cpus", "2", "--publish", "127.0.0.1::9000",
               "--env", "CLICKHOUSE_SKIP_USER_SETUP=1", "--ulimit", "nofile=262144:262144", STORAGE_IMAGE)
        owned["storage"] = True
        wait_for(lambda: docker("exec", storage, "clickhouse-client", "--query", "SELECT 1") == "1")
        schema = output / "sources" / "candidate" / "clickhouse-init.sql"
        subprocess.run(["docker", "exec", "--interactive", storage, "clickhouse-client", "--multiquery"],
                       input=schema.read_text(), text=True, check=True, capture_output=True)
        storage_port = int(docker("port", storage, "9000/tcp").rsplit(":", 1)[1])
        settings = {
            "TelemetryEngine__ClickHouseConnectionString": f"Host={storage};Port=9000;Database=nealytics_core;User=default;Password=;",
            "TelemetryEngine__WriteAheadLogDirectory": "/wal", "TelemetryEngine__AllowedProjectKeys": "b6-key",
            "TelemetryEngine__JwtSymmetricKey": "b6-local-test-signing-key-at-least-32-bytes",
            "TelemetryEngine__DatabaseBatchCommitSize": "1000", "TelemetryEngine__ForceFlushIntervalSeconds": str(interval),
            "TelemetryEngine__MemoryChannelCapacity": "10000", "TelemetryEngine__MemoryChannelByteCapacity": "67108864",
            "TelemetryEngine__DatabaseBatchByteCapacity": "16777216", "TelemetryEngine__EnableAsyncInsert": "false",
            "TelemetryEngine__RateLimitPermitCount": "1000000", "TelemetryEngine__RateLimitWindowSeconds": "1",
            "TelemetryEngine__RateLimitQueueSize": "0", "TelemetryEngine__ConnectionPoolSize": "16",
            "TelemetryEngine__MaxConcurrentIngestionRequests": "64", "TelemetryEngine__IngestionRequestByteBudget": "16777216",
            "TelemetryEngine__MaxRequestBodyBytes": "1048576", "TelemetryEngine__WalIoBackend": backend,
            "TelemetryEngine__IngestionExecutionPath": execution, "TelemetryEngine__BeaconWalBatchRecords": "64",
            "TelemetryEngine__WalRingEntries": "8", "TelemetryEngine__WalMaxPendingRecords": "4096",
            "TelemetryEngine__WalMaxPendingBytes": "33554432", "TelemetryEngine__WalMaxRecordBytes": "16777216",
            "TelemetryEngine__WalMaxFlushRecords": "256", "TelemetryEngine__WalMaxFlushBytes": "16777216",
            "TelemetryEngine__WalMaxBacklogBytes": "1073741824", "TelemetryEngine__EnablePrometheusScrape": "true",
            "ASPNETCORE_URLS": "http://0.0.0.0:8080", "DOTNET_BUNDLE_EXTRACT_BASE_DIR": "/tmp/bundle",
            "OTEL_SDK_DISABLED": "true", "Logging__LogLevel__Default": "Warning"
        }
        arguments = ["run", "--detach", "--name", engine, "--label", LABEL, "--network", network,
                     "--read-only", "--user", "10001:10001", "--cap-drop", "ALL", "--cpus", "1", "--memory", "512m",
                     "--security-opt", "no-new-privileges", "--security-opt", "seccomp=unconfined",
                     "--publish", "127.0.0.1::8080", "--tmpfs", "/tmp:rw,exec,nosuid,nodev,uid=10001,gid=10001,mode=0700,size=256m",
                     "--mount", f"type=volume,source={volume},target=/wal",
                     "--mount", f"type=bind,source={binary},target=/app/Nealytics.Engine,readonly"]
        for key, value in settings.items():
            arguments += ["--env", key + "=" + value]
        docker(*arguments, "--entrypoint", "/app/Nealytics.Engine", RUNTIME_IMAGE)
        owned["engine"] = True
        port = int(docker("port", engine, "8080/tcp").rsplit(":", 1)[1])
        wait_for(lambda: request(port, "GET", "/ready")[0] == 200)
        yield {"engine": engine, "storage": storage, "port": port, "settings": settings,
               "connection": f"Host=127.0.0.1;Port={storage_port};Database=nealytics_core;User=default;Password=;"}
    finally:
        if owned["engine"]:
            (output / (engine + ".log")).write_text(docker("logs", engine))
        for kind in ("engine", "storage"):
            if owned[kind]:
                name = engine if kind == "engine" else storage
                assert docker("inspect", "--format", '{{index .Config.Labels "nealytics.scope"}}', name) == "b6-qualification"
                docker("rm", "--force", "--volumes", name)
        if owned["volume"]:
            assert docker("volume", "inspect", "--format", '{{index .Labels "nealytics.scope"}}', volume) == "b6-qualification"
            docker("volume", "rm", volume)
        if owned["network"]:
            docker("network", "rm", network)


def sample(engine):
    text = docker("exec", engine, "/bin/sh", "-c", "cat /proc/1/stat /proc/1/status /sys/fs/cgroup/memory.current")
    rows = text.splitlines()
    fields = rows[0][rows[0].rindex(")") + 2:].split()
    rss = int(next(line for line in rows if line.startswith("VmRSS:")).split()[1]) * 1024
    return {"time": time.monotonic(), "cpuTicks": int(fields[11]) + int(fields[12]),
            "rssBytes": rss, "cgroupBytes": int(rows[-1])}


def measure(repository, output, client, binary, revision, label, backend, execution, mode, repetition, duration):
    name = f"{label}-{mode}-{repetition}"
    print("Starting", name, flush=True)
    with deployment(output, binary, backend, execution) as target:
        environment = os.environ.copy()
        environment.update(target["settings"])
        environment["GIT_SHA"] = revision
        common = ["dotnet", str(client), "--url", f"http://127.0.0.1:{target['port']}", "--key", "b6-key",
                  "--mode", mode, "--concurrency", "8", "--beacon-batch", "50", "--ch", target["connection"],
                  "--warmup", "0", "--verify-loss", "true", "--out", str(output / "results.md")]
        with (output / (name + "-warmup.log")).open("w") as log:
            subprocess.run(common + ["--requests", "100"], check=True, env=environment, cwd=repository,
                           stdout=log, stderr=subprocess.STDOUT)
        samples = [sample(target["engine"])]
        done = threading.Event()
        errors = []
        def observe():
            while not done.wait(0.5):
                try:
                    samples.append(sample(target["engine"]))
                except BaseException as error:
                    errors.append(str(error))
                    return
        observer = threading.Thread(target=observe)
        observer.start()
        try:
            with (output / (name + ".log")).open("w") as log:
                subprocess.run(common + ["--duration", str(duration), "--rate", "200" if mode == "track" else "25",
                                        "--visibility-every", "20", "--manifest", str(output / (name + ".json"))],
                               check=True, env=environment, cwd=repository, stdout=log, stderr=subprocess.STDOUT)
        finally:
            done.set()
            observer.join()
        samples.append(sample(target["engine"]))
        assert not errors, errors
        ticks = int(docker("exec", target["engine"], "getconf", "CLK_TCK"))
        elapsed = samples[-1]["time"] - samples[0]["time"]
        cpu = (samples[-1]["cpuTicks"] - samples[0]["cpuTicks"]) / ticks
        resource = {"scope": "workload and stored-count verification; warmup excluded", "seconds": elapsed,
                    "cpuSeconds": cpu, "meanCpuPercentOfOneCore": cpu / elapsed * 100,
                    "peakRssBytes": max(value["rssBytes"] for value in samples),
                    "peakCgroupBytes": max(value["cgroupBytes"] for value in samples), "samples": samples}
        (output / (name + "-resources.json")).write_text(json.dumps(resource, indent=2))
        manifest = json.loads((output / (name + ".json")).read_text())
        level = manifest["levels"][0]
        assert level["errors"] == 0 and level["stored"] == level["eventsSent"], level
        assert level["visibilityTimeouts"] == 0 and level["visibilityDropped"] == 0, level
        wait_for(lambda: docker("exec", target["engine"], "stat", "-c", "%s", "/wal/telemetry_wal.log") == "0")
        docker("stop", "--time", "40", target["engine"])
        state = json.loads(docker("inspect", "--format", "{{json .State}}", target["engine"]))
        assert state["ExitCode"] == 0 and not state["OOMKilled"], state
        print("Finished", name, "p95", level["p95"], "p99", level["p99"], "stored", level["stored"], flush=True)
        return {"name": name, "label": label, "mode": mode, "repetition": repetition, "level": level,
                "resources": {key: value for key, value in resource.items() if key != "samples"}}


def crash_check(output, binary, backend, execution):
    with deployment(output, binary, backend, execution, interval=300) as target:
        tenant = uuid.uuid4().hex
        events = [{"eventId": str(uuid.uuid4()), "projectId": "crash", "tenantId": tenant,
                   "sessionId": "s", "eventType": "e"} for _ in range(48)]
        for event in events[:32]:
            assert request(target["port"], "POST", "/api/v1/telemetry/track?k=b6-key", json.dumps(event))[0] == 202
        for start in (32, 40):
            assert request(target["port"], "POST", "/api/v1/telemetry/beacon?k=b6-key", json.dumps(events[start:start+8])) == (204, "")
        def stored():
            rows = docker("exec", target["storage"], "clickhouse-client", "--query",
                          f"SELECT event_id FROM nealytics_core.global_events WHERE tenant_id='{tenant}' FORMAT JSONEachRow")
            return sorted(json.loads(row)["event_id"] for row in rows.splitlines())
        assert stored() == [], "Crash qualification requires all accepted events to remain in the WAL"
        docker("kill", "--signal", "KILL", target["engine"])
        docker("start", target["engine"])
        wait_for(lambda: request(target["port"], "GET", "/ready")[0] == 200)
        expected = sorted(event["eventId"] for event in events)
        wait_for(lambda: stored() == expected)
        docker("stop", "--time", "40", target["engine"])
        state = json.loads(docker("inspect", "--format", "{{json .State}}", target["engine"]))
        assert state["ExitCode"] == 0 and not state["OOMKilled"], state
        result = {"backend": backend, "execution": execution, "accepted": len(events),
                  "storedBeforeKill": 0, "recoveredExactIds": len(expected), "cleanRestartShutdown": True}
        print("Crash parity", json.dumps(result), flush=True)
        return result


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--baseline", default="f1cfe3e0c4e69bbbafc6096c02659a68986d0836")
    parser.add_argument("--candidate", default="HEAD")
    parser.add_argument("--duration", type=int, default=20)
    parser.add_argument("--repetitions", type=int, default=3)
    args = parser.parse_args()
    assert platform.machine() == "arm64", "Run native x64 qualification on an actual x64 host separately"
    assert args.duration > 0 and args.repetitions > 0
    repository = Path(__file__).resolve().parent.parent
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    candidate = run("git", "-C", str(repository), "rev-parse", args.candidate)
    baseline = run("git", "-C", str(repository), "rev-parse", args.baseline)
    binaries = {name: build(repository, output, revision, name, native=name == "candidate")
                for name, revision in [("baseline", baseline), ("candidate", candidate)]}
    client_directory = output / "client"
    client_directory.mkdir()
    subprocess.run(["dotnet", "build", str(repository / "bench/Nealytics.Engine.Bench"), "--configuration", "Release",
                    "--output", str(client_directory)], check=True, stdout=(output / "client-build.log").open("w"), stderr=subprocess.STDOUT)
    client = client_directory / "Nealytics.Engine.Bench.dll"
    summary = {"host": platform.platform(), "docker": json.loads(docker("info", "--format", '{{json .}}'))["KernelVersion"],
               "engineCpus": 1, "engineMemoryLimitBytes": 536870912, "storageCpus": 2, "storageMemoryLimitBytes": 2147483648,
               "runtime": RUNTIME_IMAGE, "storage": STORAGE_IMAGE, "walFilesystem": "Docker named volume, durable fsync",
               "binaries": binaries, "runs": [], "crashParity": []}
    modes = [("baseline", "managed", "managed"), ("managed-managed", "managed", "managed"),
             ("custom-managed", "managed", "custom"), ("managed-native", "io_uring", "managed"),
             ("custom-native", "io_uring", "custom")]
    for repetition in range(1, args.repetitions + 1):
        order = modes[repetition - 1:] + modes[:repetition - 1]
        for label, backend, execution in order:
            binary = binaries["baseline" if label == "baseline" else "candidate"]
            for mode in ("track", "beacon"):
                result = measure(repository, output, client, binary["binary"], binary["revision"], label, backend,
                                 execution, mode, repetition, args.duration)
                summary["runs"].append(result)
                (output / "summary.json").write_text(json.dumps(summary, indent=2))
    for _, backend, execution in modes[1:]:
        summary["crashParity"].append(crash_check(output, binaries["candidate"]["binary"], backend, execution))
        (output / "summary.json").write_text(json.dumps(summary, indent=2))


if __name__ == "__main__":
    main()
