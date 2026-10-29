import argparse
import http.client
import json
from pathlib import Path
import subprocess
import time
import uuid


def docker(*arguments, input_text=None, check=True):
    return subprocess.run(
        ["docker", *arguments], input=input_text, text=True,
        stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=check,
    ).stdout.strip()


def wait_until(action, timeout=90):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        try:
            value = action()
            if value:
                return value
        except (OSError, http.client.HTTPException, subprocess.CalledProcessError):
            pass
        time.sleep(0.2)
    raise AssertionError("Timed out waiting for artifact behavior")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("binary", type=Path, nargs="?")
    parser.add_argument("--engine-image")
    parser.add_argument("--backend", choices=["managed", "io_uring", "auto"], required=True)
    parser.add_argument("--permit-native", action="store_true")
    args = parser.parse_args()
    if (args.binary is None) == (args.engine_image is None):
        parser.error("Provide exactly one executable path or --engine-image")
    binary = args.binary.resolve(strict=True) if args.binary else None
    suffix = uuid.uuid4().hex
    network, storage, engine = ["nealytics-artifact-" + role + "-" + suffix for role in ("net", "ch", "engine")]
    runtime = "mcr.microsoft.com/dotnet/runtime-deps:10.0@sha256:62028293921f5bd6db3d9f9119cd29cd926dfd3c2336da23b09f425e40c3c832"
    clickhouse = "clickhouse/clickhouse-server:25.3@sha256:b627d7a9bc0e0c1bac26cdbe9d2fc6316faa29c5d8a174f28f5abd57d0fa6ba2"
    label = "nealytics.scope=artifact-qualification"
    try:
        docker("network", "create", "--label", label, network)
        docker("run", "--detach", "--name", storage, "--label", label,
               "--network", network, "--memory", "2g", "--cpus", "2",
               "--ulimit", "nofile=262144:262144", "--env", "CLICKHOUSE_SKIP_USER_SETUP=1",
               "--tmpfs", "/var/lib/clickhouse:rw,size=512m", "--tmpfs", "/var/log/clickhouse-server:rw,size=128m", clickhouse)
        wait_until(lambda: docker("exec", storage, "clickhouse-client", "--query", "SELECT 1") == "1")
        schema = Path(__file__).resolve().parent.parent / "clickhouse-init.sql"
        docker("exec", "--interactive", storage, "clickhouse-client", "--multiquery", input_text=schema.read_text())
        command = [
            "run", "--detach", "--name", engine, "--label", label, "--network", network,
            "--publish", "127.0.0.1::8080", "--read-only", "--user", "10001:10001",
            "--cap-drop", "ALL", "--security-opt", "no-new-privileges", "--memory", "512m", "--cpus", "1",
            "--tmpfs", "/tmp:rw,exec,nosuid,nodev,uid=10001,gid=10001,mode=0700,size=256m",
            "--tmpfs", "/wal:rw,nosuid,nodev,uid=10001,gid=10001,mode=0700,size=64m",
        ]
        if binary:
            command += ["--mount", "type=bind,source=" + str(binary) + ",target=/app/Nealytics.Engine,readonly"]
        if args.permit_native:
            command += ["--security-opt", "seccomp=unconfined"]
        else:
            policy = Path(__file__).resolve().parent / "deny-io-uring.json"
            command += ["--security-opt", "seccomp=" + str(policy)]
        settings = {
            "ASPNETCORE_URLS": "http://0.0.0.0:8080", "DOTNET_BUNDLE_EXTRACT_BASE_DIR": "/tmp/bundle",
            "OTEL_SDK_DISABLED": "true", "TelemetryEngine__WalIoBackend": args.backend,
            "TelemetryEngine__ClickHouseConnectionString": "Host=" + storage + ";Port=9000;Database=nealytics_core;User=default;Password=;",
            "TelemetryEngine__WriteAheadLogDirectory": "/wal", "TelemetryEngine__ForceFlushIntervalSeconds": "1",
            "TelemetryEngine__DatabaseBatchCommitSize": "32", "TelemetryEngine__AllowedProjectKeys": "artifact-key",
            "TelemetryEngine__JwtSymmetricKey": "artifact-test-only-signing-key-at-least-32-bytes",
        }
        for key, value in settings.items():
            command += ["--env", key + "=" + value]
        runtime = args.engine_image or runtime
        docker(*command, "--entrypoint", "/app/Nealytics.Engine", runtime)
        port = int(docker("port", engine, "8080/tcp").rsplit(":", 1)[1])

        def request(method, path, body=None):
            connection = http.client.HTTPConnection("127.0.0.1", port, timeout=5)
            try:
                connection.request(method, path, body, {"Content-Type": "application/json"})
                response = connection.getresponse()
                data = response.read()
                return response.status, data
            finally:
                connection.close()

        def ready():
            state = json.loads(docker("inspect", "--format", "{{json .State}}", engine))
            assert state["Running"], state
            return request("GET", "/ready")[0] == 200

        wait_until(ready)
        expected = [str(uuid.uuid4()) for _ in range(3)]
        events = [{"eventId": identity, "projectId": "artifact", "tenantId": suffix,
                   "sessionId": suffix, "eventType": "e"} for identity in expected]
        assert request("POST", "/api/v1/telemetry/track?k=wrong", json.dumps(events[0]))[0] == 401
        assert request("POST", "/api/v1/telemetry/track?k=artifact-key", json.dumps(events[0]))[0] == 202
        assert request("POST", "/api/v1/telemetry/beacon?k=artifact-key", json.dumps([events[1], None, {}, events[2]])) == (204, b"")
        query = "SELECT event_id FROM nealytics_core.global_events WHERE tenant_id='" + suffix + "' ORDER BY event_id FORMAT JSONEachRow"

        def visible():
            rows = docker("exec", storage, "clickhouse-client", "--query", query)
            actual = sorted(json.loads(row)["event_id"] for row in rows.splitlines())
            return actual == sorted(expected)

        wait_until(visible)
        logs = docker("logs", engine)
        selected = "io_uring" if args.permit_native and args.backend != "managed" else "managed"
        entries = [json.loads(line) for line in logs.splitlines() if line.startswith("{")]
        selections = [entry["Properties"] for entry in entries
                      if entry.get("MessageTemplate", "").startswith("WAL file backend ")]
        assert len(selections) == 1 and selections[0]["Backend"] == selected, logs
        if args.backend == "auto" and not args.permit_native:
            assert "Native WAL create failed with errno" in selections[0]["FallbackReason"], logs
        if args.backend != "managed":
            extracted = docker("exec", engine, "find", "/tmp/bundle", "-name", "libnealytics_wal.so", "-type", "f")
            assert extracted, "The bridge was not extracted from the single executable"
        assert docker("exec", engine, "sh", "-c", "command -v dotnet || true") == ""
        assert docker("exec", engine, "find", "/usr/lib", "/lib", "-name", "liburing.so*") == ""
        binary_bytes = int(docker("exec", engine, "stat", "--format", "%s", "/app/Nealytics.Engine"))
        docker("stop", "--time", "40", engine)
        state = json.loads(docker("inspect", "--format", "{{json .State}}", engine))
        assert state["ExitCode"] == 0 and not state["OOMKilled"], state
        print(json.dumps({"backend": selected, "requested": args.backend, "events": len(expected),
                          "single_binary_bytes": binary_bytes, "clean_shutdown": True,
                          "runtime_image": runtime, "clickhouse_image": clickhouse}))
    except BaseException:
        print(docker("logs", engine, check=False))
        raise
    finally:
        docker("rm", "--force", engine, storage, check=False)
        docker("network", "rm", network, check=False)


if __name__ == "__main__":
    main()
