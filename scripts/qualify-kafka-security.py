import argparse
import base64
import hashlib
import hmac
import http.client
import json
import secrets
import subprocess
import tempfile
import time
import uuid
from datetime import datetime, timezone
from pathlib import Path


KAFKA = 'apache/kafka:4.1.1@sha256:0bc1bb2478f45b6cea78864df86acdc11e8df2c5172477819a4d12942cbe5d40'
CLICKHOUSE = 'clickhouse/clickhouse-server:25.3.14.14@sha256:b627d7a9bc0e0c1bac26cdbe9d2fc6316faa29c5d8a174f28f5abd57d0fa6ba2'
MODES = ['ssl', 'plain', 'scram-sha-256', 'scram-sha-512']


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
    parser.add_argument('--security', choices=MODES, action='append')
    parser.add_argument('--canonical-only', action='store_true')
    parser.add_argument('--skip-failures', action='store_true')
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=False)
    architecture = run(['docker', 'image', 'inspect', '--format', '{{.Architecture}}', args.image])
    host = run(['docker', 'info', '--format', '{{.Architecture}}'])
    assert architecture == args.architecture
    assert host in ({'aarch64', 'arm64'} if architecture == 'arm64' else {'x86_64', 'amd64'})
    source = run(['git', 'rev-parse', 'HEAD'])
    image_id = run(['docker', 'image', 'inspect', '--format', '{{.Id}}', args.image])
    scope = 'kafka-security-' + uuid.uuid4().hex[:12]
    network, broker, storage = scope + '-network', scope + '-broker', scope + '-storage'
    password = 'fixture-' + secrets.token_hex(24)
    signing = 'fixture-jwt-' + secrets.token_hex(24)
    names = []
    summaries = {}
    run(['docker', 'network', 'create', '--label', 'nealytics.scope=' + scope, network])

    def start(name, image, options, command=None):
        run(['docker', 'run', '--detach', '--name', name, '--label', 'nealytics.scope=' + scope,
             '--network', network, *options, image, *(command or [])])
        names.append(name)

    def remove(name):
        assert run(['docker', 'inspect', '--format', '{{index .Config.Labels "nealytics.scope"}}', name]) == scope
        run(['docker', 'rm', '--force', '--volumes', name])
        names.remove(name)

    def sql(query):
        return run(['docker', 'exec', storage, 'clickhouse-client', '--query', query])

    def broker_ready():
        assert run(['docker', 'inspect', '--format', '{{.State.Running}}', broker]) == 'true'
        result = subprocess.run(['docker', 'exec', broker, '/opt/kafka/bin/kafka-topics.sh',
                                 '--bootstrap-server', 'broker:9092', '--list'], capture_output=True)
        return result.returncode == 0

    def container_log(name):
        result = subprocess.run(['docker', 'logs', name], check=True, capture_output=True, text=True)
        return result.stdout + result.stderr

    def broker_log():
        return container_log(broker)

    def offset(group, topic):
        result = subprocess.run(['docker', 'exec', broker, '/opt/kafka/bin/kafka-consumer-groups.sh',
                                 '--bootstrap-server', 'broker:9092', '--describe', '--group', group],
                                capture_output=True, text=True)
        if result.returncode:
            return None
        for line in result.stdout.splitlines():
            fields = line.split()
            if len(fields) >= 4 and fields[:3] == [group, topic, '0']:
                return int(fields[3]) if fields[3].isdigit() else None
        return None

    def topic_create(topic):
        run(['docker', 'exec', broker, '/opt/kafka/bin/kafka-topics.sh', '--bootstrap-server', 'broker:9092',
             '--create', '--topic', topic, '--partitions', '1', '--replication-factor', '1'])

    def produce(topic, values):
        run(['docker', 'exec', '-i', broker, '/opt/kafka/bin/kafka-console-producer.sh',
             '--bootstrap-server', 'broker:9092', '--topic', topic], input='\n'.join(values) + '\n')

    def event(project, mapped):
        identity = str(uuid.uuid4())
        timestamp = datetime.now(timezone.utc).isoformat().replace('+00:00', 'Z')
        if mapped:
            return json.dumps({'source': {'id': identity, 'time': timestamp, 'tenant': 't'}})
        return json.dumps({'eventId': identity, 'timestamp': timestamp, 'projectId': project,
                           'tenantId': 't', 'eventType': 'action'})

    def logs_safe(engine, output):
        logs = container_log(engine)
        assert password not in logs and 'wrong-' + password not in logs
        assert signing not in logs and 'source-key' not in logs and 'private-secure-payload' not in logs
        assert 'io_uring' in logs
        output.write_text(logs)

    try:
        start(storage, CLICKHOUSE, ['--network-alias', 'storage', '--memory', '2g', '--cpus', '2',
              '--env', 'CLICKHOUSE_SKIP_USER_SETUP=1', '--mount',
              f'type=bind,source={Path.cwd()}/clickhouse-init.sql,target=/docker-entrypoint-initdb.d/init.sql,readonly'])
        wait_for(lambda: subprocess.run(['docker', 'exec', storage, 'clickhouse-client', '--host', 'storage', '--query',
                 'SELECT count() FROM nealytics_core.global_events'], capture_output=True).returncode == 0, 'storage initialization')
        start(broker, KAFKA, ['--network-alias', 'broker', '--memory', '1g', '--cpus', '2',
              '--env', 'KAFKA_HEAP_OPTS=-Xms256m -Xmx512m', '--env', 'FIXTURE_SASL_PASSWORD=' + password,
              '--mount', f'type=bind,source={Path.cwd()}/tests/Fixtures/KafkaSecurity/prepare.sh,target=/tmp/prepare.sh,readonly',
              '--entrypoint', '/bin/sh'], ['/tmp/prepare.sh'])
        wait_for(broker_ready, 'secured broker initialization')
        with tempfile.TemporaryDirectory(prefix='nealytics-kafka-public-ca-') as directory:
            directory = Path(directory)
            for name in ['ca', 'wrong-ca']:
                run(['docker', 'cp', broker + ':/tmp/kafka-security/' + name + '.pem', str(directory / (name + '.pem'))])
                (directory / (name + '.pem')).chmod(0o644)

            def exercise(mode, mapped=False, fault=None):
                name = mode + ('-mapped' if mapped else '') + ('-' + fault if fault else '')
                output = args.output / name
                output.mkdir()
                engine = scope + '-' + name
                project = 'p_' + name.replace('-', '_')
                topic, group = 't_' + name.replace('-', '_'), 'g_' + name.replace('-', '_')
                topic_create(topic)
                if fault:
                    produce(topic, [event(project, False)])
                    run(['docker', 'exec', broker, '/opt/kafka/bin/kafka-consumer-groups.sh', '--bootstrap-server',
                         'broker:9092', '--reset-offsets', '--group', group, '--topic', topic, '--to-offset', '0', '--execute'])
                    assert offset(group, topic) == 0
                endpoint = 'broker:9096' if fault == 'hostname' else 'broker:9094' if mode == 'ssl' else 'broker:9095'
                settings = {
                    'TelemetryEngine__ClickHouseConnectionString': 'Host=storage;Port=9000;Database=nealytics_core;User=default;Password=;',
                    'TelemetryEngine__JwtSymmetricKey': signing,
                    'TelemetryEngine__IngestionKeys__0__Key': 'source-key',
                    'TelemetryEngine__IngestionKeys__0__Scope': 'server',
                    'TelemetryEngine__IngestionKeys__0__ProjectId': project,
                    'TelemetryEngine__IngestionKeys__1__Key': 'public-key',
                    'TelemetryEngine__IngestionKeys__1__Scope': 'public',
                    'TelemetryEngine__IngestionKeys__1__ProjectId': project,
                    'TelemetryEngine__IngestionKeys__2__Key': 'other-key',
                    'TelemetryEngine__IngestionKeys__2__Scope': 'server',
                    'TelemetryEngine__IngestionKeys__2__ProjectId': 'other',
                    'TelemetryEngine__NamedMetrics__0__Name': 'activity',
                    'TelemetryEngine__NamedMetrics__0__EventType': 'action',
                    'TelemetryEngine__Kafka__Enabled': 'true',
                    'TelemetryEngine__Kafka__BootstrapServers': endpoint,
                    'TelemetryEngine__Kafka__Topic': topic,
                    'TelemetryEngine__Kafka__GroupId': group,
                    'TelemetryEngine__Kafka__ProjectKey': 'source-key',
                    'TelemetryEngine__Kafka__SecurityProtocol': 'ssl' if mode == 'ssl' else 'sasl_ssl',
                    'TelemetryEngine__Kafka__SslCaLocation': '/fixture/' + ('wrong-ca.pem' if fault == 'trust' else 'ca.pem'),
                    'TelemetryEngine__Kafka__InitialOffset': 'earliest',
                    'TelemetryEngine__IngestionExecutionPath': 'custom',
                    'TelemetryEngine__WalIoBackend': 'io_uring',
                    'TelemetryEngine__ForceFlushIntervalSeconds': '1'
                }
                if mode != 'ssl':
                    settings['TelemetryEngine__Kafka__SaslMechanism'] = mode
                    settings['TelemetryEngine__Kafka__SaslUsername'] = 'fixture'
                    settings['TelemetryEngine__Kafka__SaslPassword'] = 'wrong-' + password if fault == 'password' else password
                if mapped:
                    prefix = 'TelemetryEngine__Kafka__SourceMapping__Fields__'
                    for target, field in {'eventId': 'id', 'timestamp': 'time', 'tenantId': 'tenant'}.items():
                        settings[prefix + target + '__Path__0'] = 'source'
                        settings[prefix + target + '__Path__1'] = field
                    settings[prefix + 'projectId__Default'] = project
                    settings[prefix + 'eventType__Default'] = 'action'
                options = ['--memory', '512m', '--cpus', '2', '--security-opt', 'seccomp=unconfined',
                           '--publish', '127.0.0.1::5000']
                for cert in ['ca', 'wrong-ca']:
                    options.extend(['--mount', f'type=bind,source={directory}/{cert}.pem,target=/fixture/{cert}.pem,readonly'])
                for key, value in settings.items():
                    options.extend(['--env', key + '=' + value])
                previous_log = broker_log()
                start(engine, args.image, options)
                address = run(['docker', 'inspect', '--format', '{{(index .NetworkSettings.Networks "' + network + '").IPAddress}}', engine])
                assert address
                port = int(run(['docker', 'port', engine, '5000/tcp']).split(':')[-1])

                def encode(value):
                    return base64.urlsafe_b64encode(value).decode().rstrip('=')

                def request(path, key='source-key'):
                    header = encode(b'{"alg":"HS256","typ":"JWT"}')
                    claims = encode(json.dumps({'project_id': project, 'tenant_id': 't', 'scope': 'metrics',
                                                'exp': int(time.time()) + 900}).encode())
                    unsigned = header + '.' + claims
                    token = unsigned + '.' + encode(hmac.new(signing.encode(), unsigned.encode(), hashlib.sha256).digest())
                    connection = http.client.HTTPConnection('127.0.0.1', port, timeout=2)
                    try:
                        connection.request('GET', path, headers={'Authorization': 'Bearer ' + token, 'X-Project-Key': key})
                        response = connection.getresponse()
                        return response.status, response.read()
                    except (OSError, http.client.HTTPException):
                        return None, b''
                    finally:
                        connection.close()

                def count():
                    return int(sql("SELECT count() FROM nealytics_core.global_events WHERE project_id='" + project + "'"))

                health_route = '/api/v1/telemetry/sources/kafka/default'
                def health_is(state, issue=None):
                    status, body = request(health_route)
                    if status != 200:
                        return False
                    value = json.loads(body)
                    assert value['name'] == 'default' and value['changedAt'].endswith('Z')
                    assert set(value) <= {'name', 'state', 'issue', 'changedAt'}
                    return value['state'] == state and (issue is None or value.get('issue') == issue)

                if fault:
                    def rejected_connection():
                        fresh = broker_log()[len(previous_log):]
                        return any(address in line and ('failed authentication' in line.lower() or 'ssl handshake failed' in line.lower())
                                   for line in fresh.splitlines())
                    wait_for(rejected_connection, 'broker-observed rejected ' + fault + ' connection from engine')
                    wait_for(lambda: health_is('unavailable', 'tls_rejected' if fault in ['trust', 'hostname'] else 'authentication_failed'),
                             'observable security rejection without leaking native diagnostics')
                    assert offset(group, topic) == 0 and count() == 0
                    if run(['docker', 'inspect', '--format', '{{.State.Running}}', engine]) == 'true':
                        run(['docker', 'stop', '--time', '30', engine])
                    assert offset(group, topic) == 0 and count() == 0
                    checks = ['custom-io_uring', 'broker-observed-security-rejection', 'no-offset-advance',
                              'no-ingestion-before-authentication', 'credential-log-redaction', 'security-source-health']
                else:
                    wait_for(lambda: request('/ready')[0] == 200, 'published application readiness')
                    assert request(health_route, 'unknown')[0] == 401
                    assert request(health_route, 'public-key')[0] == 403
                    assert request(health_route, 'other-key')[0] == 404
                    wait_for(lambda: health_is('assigned'), 'idle encrypted source health')
                    if mode == 'ssl' and not mapped:
                        run(['docker', 'pause', broker])
                        try:
                            wait_for(lambda: health_is('unavailable'), 'paused broker is visible independently from process readiness')
                            wait_for(lambda: health_is('unavailable', 'assignment_lost'), 'lost native assignment source health')
                            assert request('/ready')[0] == 200 and count() == 0
                        finally:
                            run(['docker', 'unpause', broker])
                    produce(topic, [event(project, mapped)])
                    wait_for(lambda: offset(group, topic) == 1, 'durable encrypted first offset')
                    def visible():
                        status, body = request('/api/v1/analytics/metrics/activity')
                        return status == 200 and json.loads(body)['value'] == '1'
                    wait_for(visible, 'encrypted named metric visibility')
                    wait_for(lambda: health_is('assigned'), 'source recovery after broker assignment/commit')
                    run(['docker', 'kill', '--signal', 'KILL', engine])
                    produce(topic, [event(project, mapped)])
                    run(['docker', 'start', engine])
                    port = int(run(['docker', 'port', engine, '5000/tcp']).split(':')[-1])
                    wait_for(lambda: request('/ready')[0] == 200, 'crash restart readiness')
                    wait_for(lambda: offset(group, topic) == 2 and count() == 2, 'encrypted committed offset restart')
                    wait_for(lambda: health_is('assigned'), 'encrypted restart source health')
                    produce(topic, ['{private-secure-payload}', event(project, mapped)])
                    wait_for(lambda: run(['docker', 'inspect', '--format', '{{.State.Running}}', engine]) == 'false',
                             'encrypted poison prefix stops host')
                    assert offset(group, topic) == 2 and count() == 2
                    assert 'during validate on topic ' + topic + ', partition 0, offset 2' in container_log(engine)
                    checks = ['custom-io_uring', 'secure-native-single-file-client', 'encrypted-ingestion', 'named-metric-visibility',
                              'committed-offset-kill-restart', 'poison-prefix-stop', 'credential-payload-log-redaction',
                              'project-scoped-source-health', 'idle-and-restart-assignment-health']
                    if mode == 'ssl' and not mapped:
                        checks.append('broker-outage-lost-assignment-and-recovery-health')
                logs_safe(engine, output / 'application.log')
                summary = {'source': source, 'image': args.image, 'imageId': image_id, 'architecture': architecture,
                           'hostArchitecture': host, 'security': mode, 'mapped': mapped, 'fault': fault,
                           'checks': checks, 'result': 'passed', 'measurements': 'disabled'}
                (output / 'summary.json').write_text(json.dumps(summary, indent=2) + '\n')
                summaries[name] = summary
                remove(engine)
                print(name + ': passed', flush=True)

            for mode in args.security or MODES:
                for mapped in ([False] if args.canonical_only else [False, True]):
                    exercise(mode, mapped)
            if not args.skip_failures:
                for mode, fault in [('ssl', 'trust'), ('ssl', 'hostname'), ('plain', 'password'),
                                    ('scram-sha-256', 'password'), ('scram-sha-512', 'password')]:
                    exercise(mode, fault=fault)
        (args.output / 'summary.json').write_text(json.dumps({'source': source, 'architecture': architecture,
            'hostArchitecture': host, 'scenarios': list(summaries), 'result': 'passed', 'measurements': 'disabled'}, indent=2) + '\n')
    finally:
        for name in reversed(names[:]):
            inspection = subprocess.run(['docker', 'inspect', '--format', '{{index .Config.Labels "nealytics.scope"}}', name],
                                        capture_output=True, text=True)
            if inspection.returncode == 0 and inspection.stdout.strip() == scope:
                logs = subprocess.run(['docker', 'logs', name], capture_output=True, text=True)
                text = (logs.stdout + logs.stderr).replace(password, '[redacted]').replace(signing, '[redacted]')
                (args.output / (name + '.log')).write_text(text)
                remove(name)
        assert run(['docker', 'network', 'inspect', '--format', '{{index .Labels "nealytics.scope"}}', network]) == scope
        run(['docker', 'network', 'rm', network])


if __name__ == '__main__':
    main()
