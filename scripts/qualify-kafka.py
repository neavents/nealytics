import argparse
import base64
import hashlib
import hmac
import http.client
import json
import subprocess
import tempfile
import time
import uuid
from datetime import datetime, timezone
from pathlib import Path


KAFKA = 'apache/kafka:4.1.1@sha256:0bc1bb2478f45b6cea78864df86acdc11e8df2c5172477819a4d12942cbe5d40'
CLICKHOUSE = 'clickhouse/clickhouse-server:25.3.14.14@sha256:b627d7a9bc0e0c1bac26cdbe9d2fc6316faa29c5d8a174f28f5abd57d0fa6ba2'


def run(arguments, **kwargs):
    return subprocess.run(arguments, check=True, text=True, capture_output=True, **kwargs).stdout.strip()


def wait_for(predicate, description):
    deadline = time.monotonic() + 90
    while time.monotonic() < deadline:
        if predicate():
            return
        time.sleep(0.2)
    raise AssertionError('Functional check did not complete: ' + description)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--image', required=True)
    parser.add_argument('--architecture', choices=['amd64', 'arm64'], required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--mapped', action='store_true')
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=False)
    architecture = run(['docker', 'image', 'inspect', '--format', '{{.Architecture}}', args.image])
    assert architecture == args.architecture, (architecture, args.architecture)
    host_architecture = run(['docker', 'info', '--format', '{{.Architecture}}'])
    assert host_architecture in ({'arm64', 'aarch64'} if architecture == 'arm64' else {'amd64', 'x86_64'}), host_architecture
    scope = 'kafka-smoke-' + uuid.uuid4().hex[:12]
    network = scope + '-network'
    broker = scope + '-broker'
    storage = scope + '-storage'
    engine = scope + '-engine'
    names = []
    secret = 'kafka-functional-test-signing-key-at-least-32-bytes'
    label = 'nealytics.scope=' + scope
    run(['docker', 'network', 'create', '--label', label, network])

    def start(name, image, options):
        result = run(['docker', 'run', '--detach', '--name', name, '--label', label,
                      '--network', network, *options, image])
        names.append(name)
        return result

    def sql(query):
        return run(['docker', 'exec', storage, 'clickhouse-client', '--query', query])

    def kafka_ready():
        if run(['docker', 'inspect', '--format', '{{.State.Running}}', broker]) != 'true':
            raise AssertionError('Broker exited before readiness; see retained logs.')
        result = subprocess.run(['docker', 'exec', broker, '/opt/kafka/bin/kafka-topics.sh',
                                 '--bootstrap-server', 'broker:9092', '--list'], capture_output=True)
        return result.returncode == 0

    def offset():
        result = subprocess.run(['docker', 'exec', broker, '/opt/kafka/bin/kafka-consumer-groups.sh',
                                 '--bootstrap-server', 'broker:9092', '--describe', '--group', 'source-group'],
                                capture_output=True, text=True)
        if result.returncode:
            return None
        for line in result.stdout.splitlines():
            columns = line.split()
            if len(columns) >= 4 and columns[:3] == ['source-group', 'events', '0']:
                return int(columns[3]) if columns[3].isdigit() else None
        return None

    def produce(values):
        run(['docker', 'exec', '-i', broker, '/opt/kafka/bin/kafka-console-producer.sh',
             '--bootstrap-server', 'broker:9092', '--topic', 'events'], input='\n'.join(values) + '\n')

    def event(sequence):
        if args.mapped:
            return json.dumps({'source': {'id': str(uuid.uuid4()),
                'time': datetime.now(timezone.utc).isoformat().replace('+00:00', 'Z'), 'tenant': 't'}})
        return json.dumps({'eventId': str(uuid.uuid4()), 'timestamp': datetime.now(timezone.utc).isoformat().replace('+00:00', 'Z'),
                           'projectId': 'p', 'tenantId': 't', 'eventType': 'action', 'seq': sequence})

    def encode(value):
        return base64.urlsafe_b64encode(value).decode().rstrip('=')

    def token():
        header = encode(b'{"alg":"HS256","typ":"JWT"}')
        claims = encode(json.dumps({'project_id': 'p', 'tenant_id': 't', 'scope': 'metrics', 'exp': int(time.time()) + 900}).encode())
        unsigned = header + '.' + claims
        return unsigned + '.' + encode(hmac.new(secret.encode(), unsigned.encode(), hashlib.sha256).digest())

    port = None

    def request(path):
        connection = http.client.HTTPConnection('127.0.0.1', port, timeout=2)
        try:
            connection.request('GET', path, headers={'Authorization': 'Bearer ' + token()})
            response = connection.getresponse()
            return response.status, response.read()
        except (OSError, http.client.HTTPException):
            return None, b''
        finally:
            connection.close()

    try:
        start(storage, CLICKHOUSE, ['--network-alias', 'storage', '--memory', '2g', '--cpus', '2',
              '--env', 'CLICKHOUSE_SKIP_USER_SETUP=1', '--mount',
              f'type=bind,source={Path.cwd()}/clickhouse-init.sql,target=/docker-entrypoint-initdb.d/init.sql,readonly'])
        wait_for(lambda: subprocess.run(['docker', 'exec', storage, 'clickhouse-client', '--host', 'storage', '--query',
                 'SELECT count() FROM nealytics_core.global_events'], capture_output=True).returncode == 0, 'storage initialization')
        start(broker, KAFKA, ['--network-alias', 'broker', '--memory', '1g', '--cpus', '2',
              '--env', 'KAFKA_ADVERTISED_LISTENERS=PLAINTEXT://broker:9092',
              '--env', 'KAFKA_HEAP_OPTS=-Xms256m -Xmx512m', '--env', 'KAFKA_GROUP_INITIAL_REBALANCE_DELAY_MS=0',
              '--env', 'KAFKA_NODE_ID=1', '--env', 'KAFKA_PROCESS_ROLES=broker,controller',
              '--env', 'KAFKA_LISTENERS=PLAINTEXT://0.0.0.0:9092,CONTROLLER://0.0.0.0:9093',
              '--env', 'KAFKA_LISTENER_SECURITY_PROTOCOL_MAP=CONTROLLER:PLAINTEXT,PLAINTEXT:PLAINTEXT',
              '--env', 'KAFKA_CONTROLLER_LISTENER_NAMES=CONTROLLER',
              '--env', 'KAFKA_INTER_BROKER_LISTENER_NAME=PLAINTEXT',
              '--env', 'KAFKA_CONTROLLER_QUORUM_VOTERS=1@broker:9093',
              '--env', 'KAFKA_OFFSETS_TOPIC_REPLICATION_FACTOR=1'])
        wait_for(kafka_ready, 'broker initialization')
        run(['docker', 'exec', broker, '/opt/kafka/bin/kafka-topics.sh', '--bootstrap-server', 'broker:9092',
             '--create', '--topic', 'events', '--partitions', '1', '--replication-factor', '1'])
        settings = {
            'TelemetryEngine__ClickHouseConnectionString': 'Host=storage;Port=9000;Database=nealytics_core;User=default;Password=;',
            'TelemetryEngine__JwtSymmetricKey': secret,
            'TelemetryEngine__IngestionKeys__0__Key': 'source-key',
            'TelemetryEngine__IngestionKeys__0__Scope': 'server',
            'TelemetryEngine__IngestionKeys__0__ProjectId': 'p',
            'TelemetryEngine__NamedMetrics__0__Name': 'activity',
            'TelemetryEngine__NamedMetrics__0__EventType': 'action',
            'TelemetryEngine__Kafka__Enabled': 'true',
            'TelemetryEngine__Kafka__BootstrapServers': 'broker:9092',
            'TelemetryEngine__Kafka__Topic': 'events',
            'TelemetryEngine__Kafka__GroupId': 'source-group',
            'TelemetryEngine__Kafka__ProjectKey': 'source-key',
            'TelemetryEngine__Kafka__SecurityProtocol': 'plaintext',
            'TelemetryEngine__Kafka__InitialOffset': 'earliest',
            'TelemetryEngine__IngestionExecutionPath': 'custom',
            'TelemetryEngine__WalIoBackend': 'io_uring',
            'TelemetryEngine__ForceFlushIntervalSeconds': '1'
        }
        if args.mapped:
            prefix = 'TelemetryEngine__Kafka__SourceMapping__Fields__'
            for target, source in {'eventId': 'id', 'timestamp': 'time', 'tenantId': 'tenant'}.items():
                settings[prefix + target + '__Path__0'] = 'source'
                settings[prefix + target + '__Path__1'] = source
            settings[prefix + 'projectId__Default'] = 'p'
            settings[prefix + 'eventType__Default'] = 'action'
        options = ['--memory', '512m', '--cpus', '2', '--security-opt', 'seccomp=unconfined', '--publish', '127.0.0.1::5000']
        for key, value in settings.items():
            options.extend(['--env', key + '=' + value])
        start(engine, args.image, options)
        port = int(run(['docker', 'port', engine, '5000/tcp']).split(':')[-1])
        wait_for(lambda: request('/ready')[0] == 200, 'published application readiness')
        with tempfile.TemporaryDirectory(prefix='nealytics-kafka-binary-') as directory:
            binary = Path(directory) / 'Nealytics.Engine'
            run(['docker', 'cp', engine + ':/app/Nealytics.Engine', str(binary)])
            digest = hashlib.sha256(binary.read_bytes()).hexdigest()
        produce([event(1)])
        wait_for(lambda: offset() == 1, 'durable first offset')
        def metric_visible():
            status, body = request('/api/v1/analytics/metrics/activity')
            return status == 200 and json.loads(body)['value'] == '1'
        wait_for(metric_visible, 'named metric visibility')
        run(['docker', 'stop', '--time', '30', engine])
        produce([event(2)])
        run(['docker', 'start', engine])
        port = int(run(['docker', 'port', engine, '5000/tcp']).split(':')[-1])
        wait_for(lambda: request('/ready')[0] == 200, 'restart readiness')
        wait_for(lambda: offset() == 2, 'restart resumes from committed offset')
        wait_for(lambda: sql("SELECT count() FROM nealytics_core.global_events WHERE project_id='p'") == '2', 'stored restart count')
        produce(['{"invalid-secret-payload":true}' if args.mapped else '{invalid-secret-payload}', event(3)])
        wait_for(lambda: run(['docker', 'inspect', '--format', '{{.State.Running}}', engine]) == 'false', 'poison input stops host')
        assert offset() == 2, 'Poison input advanced the group offset'
        assert sql("SELECT count() FROM nealytics_core.global_events WHERE project_id='p'") == '2'
        logs = run(['docker', 'logs', engine])
        assert 'invalid-secret-payload' not in logs
        assert 'during validate on topic events, partition 0, offset 2' in logs
        assert 'io_uring' in logs
        (args.output / 'application.log').write_text(logs)
        (args.output / 'summary.json').write_text(json.dumps({
            'source': run(['git', 'rev-parse', 'HEAD']), 'image': args.image, 'architecture': architecture,
            'hostArchitecture': host_architecture, 'sha256': digest, 'sourceMapping': args.mapped,
            'checks': ['native-single-file-loading', 'custom-io_uring', 'kafka-to-wal-to-clickhouse',
                       'named-metric-visibility', 'committed-offset-restart', 'poison-prefix-stop', 'payload-log-redaction'],
            'result': 'passed', 'measurements': 'disabled'
        }, indent=2) + '\n')
        print('Kafka published-artifact functional checks passed on ' + architecture + '.')
    finally:
        for name in reversed(names):
            inspection = subprocess.run(['docker', 'inspect', '--format', '{{index .Config.Labels "nealytics.scope"}}', name], capture_output=True, text=True)
            if inspection.returncode == 0 and inspection.stdout.strip() == scope:
                logs = subprocess.run(['docker', 'logs', name], capture_output=True, text=True)
                (args.output / (name + '.log')).write_text(logs.stdout + logs.stderr)
                subprocess.run(['docker', 'rm', '--force', '--volumes', name], check=True, capture_output=True)
        inspection = run(['docker', 'network', 'inspect', '--format', '{{index .Labels "nealytics.scope"}}', network])
        assert inspection == scope
        run(['docker', 'network', 'rm', network])


if __name__ == '__main__':
    main()
