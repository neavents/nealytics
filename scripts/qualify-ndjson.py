import argparse
import base64
import gzip
import hashlib
import hmac
import http.client
import json
import subprocess
import time
import uuid
from datetime import datetime, timezone
from pathlib import Path


CLICKHOUSE = 'clickhouse/clickhouse-server:25.3.14.14@sha256:b627d7a9bc0e0c1bac26cdbe9d2fc6316faa29c5d8a174f28f5abd57d0fa6ba2'


def run(arguments):
    return subprocess.run(arguments, check=True, text=True, capture_output=True).stdout.strip()


def wait_for(predicate, description):
    deadline = time.monotonic() + 60
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
    host = run(['docker', 'info', '--format', '{{.Architecture}}'])
    assert architecture == args.architecture
    assert host in ({'aarch64', 'arm64'} if architecture == 'arm64' else {'x86_64', 'amd64'})
    scope = 'ndjson-smoke-' + uuid.uuid4().hex[:12]
    network = scope + '-network'
    storage = scope + '-storage'
    engine = scope + '-engine'
    route = '/api/v1/telemetry/import/ndjson'
    secret = 'ndjson-functional-test-signing-key-at-least-32-bytes'
    names = []
    run(['docker', 'network', 'create', '--label', 'nealytics.scope=' + scope, network])

    def start(name, image, options):
        run(['docker', 'run', '--detach', '--name', name, '--label', 'nealytics.scope=' + scope,
             '--network', network, *options, image])
        names.append(name)

    def port():
        return int(run(['docker', 'port', engine, '5000/tcp']).split(':')[-1])

    def encode(value):
        return base64.urlsafe_b64encode(value).decode().rstrip('=')

    def token():
        unsigned = encode(b'{"alg":"HS256","typ":"JWT"}') + '.' + encode(json.dumps({
            'project_id': 'p', 'tenant_id': 't', 'scope': 'metrics', 'exp': int(time.time()) + 900}).encode())
        return unsigned + '.' + encode(hmac.new(secret.encode(), unsigned.encode(), hashlib.sha256).digest())

    def request(method, path, body=None, key='source-key', content_type='application/x-ndjson', compressed=False, chunked=False):
        connection = http.client.HTTPConnection('127.0.0.1', port(), timeout=5)
        headers = {'X-Project-Key': key, 'Content-Type': content_type, 'Authorization': 'Bearer ' + token()}
        if compressed:
            headers['Content-Encoding'] = 'gzip'
            body = gzip.compress(body)
        if chunked:
            data = body
            body = (data[index:index + 17] for index in range(0, len(data), 17))
        try:
            connection.request(method, path, body=body, headers=headers, encode_chunked=chunked)
            response = connection.getresponse()
            payload = response.read()
            return response.status, json.loads(payload) if payload else None
        finally:
            connection.close()

    def ready():
        if run(['docker', 'inspect', '--format', '{{.State.Running}}', engine]) != 'true':
            raise AssertionError('Application exited before readiness; see retained logs.')
        try:
            return request('GET', '/ready')[0] == 200
        except (OSError, http.client.HTTPException):
            return False

    def event(value):
        if args.mapped:
            return json.dumps({'source': {'id': str(uuid.uuid4()),
                'time': datetime.now(timezone.utc).isoformat().replace('+00:00', 'Z'),
                'tenant': 't', 'tag': '日本語 é', 'amount': value}},
                ensure_ascii=False, separators=(',', ':')).encode()
        return json.dumps({'eventId': str(uuid.uuid4()), 'timestamp': datetime.now(timezone.utc).isoformat().replace('+00:00', 'Z'),
                          'projectId': 'p', 'tenantId': 't', 'eventType': 'action',
                          'dimensions': {'tag': '日本語 é', 'unknown': 'drop'},
                          'measures': {'amount': str(value), 'unknown': '999'}, '_walRecordId': 'untrusted'},
                         ensure_ascii=False, separators=(',', ':')).encode()

    def total(expected):
        status, payload = request('GET', '/api/v1/analytics/metrics/total')
        return status == 200 and payload['value'] == str(expected)

    try:
        start(storage, CLICKHOUSE, ['--network-alias', 'storage', '--memory', '2g', '--cpus', '2', '--env',
              'CLICKHOUSE_SKIP_USER_SETUP=1', '--mount',
              f'type=bind,source={Path.cwd()}/clickhouse-init.sql,target=/docker-entrypoint-initdb.d/init.sql,readonly'])
        wait_for(lambda: subprocess.run(['docker', 'exec', storage, 'clickhouse-client', '--query',
                 'SELECT count() FROM nealytics_core.global_events'], capture_output=True).returncode == 0, 'storage initialization')
        settings = {
            'TelemetryEngine__ClickHouseConnectionString': 'Host=storage;Port=9000;Database=nealytics_core;User=default;Password=;',
            'TelemetryEngine__JwtSymmetricKey': secret,
            'TelemetryEngine__IngestionKeys__0__Key': 'source-key', 'TelemetryEngine__IngestionKeys__0__Scope': 'server',
            'TelemetryEngine__IngestionKeys__0__ProjectId': 'p', 'TelemetryEngine__IngestionKeys__0__EventTypes': 'action',
            'TelemetryEngine__IngestionKeys__1__Key': 'public-key', 'TelemetryEngine__IngestionKeys__1__Scope': 'public',
            'TelemetryEngine__IngestionKeys__1__ProjectId': 'p',
            'TelemetryEngine__ProjectSchemas__0__ProjectId': 'p',
            'TelemetryEngine__ProjectSchemas__0__Dimensions__0__Name': 'tag',
            'TelemetryEngine__ProjectSchemas__0__Measures__0__Name': 'amount',
            'TelemetryEngine__ProjectSchemas__0__Measures__0__Type': 'Int64',
            'TelemetryEngine__ProjectSchemas__0__NamedMetrics__0__Name': 'total',
            'TelemetryEngine__ProjectSchemas__0__NamedMetrics__0__Aggregation': 'sum',
            'TelemetryEngine__ProjectSchemas__0__NamedMetrics__0__Measure': 'amount',
            'TelemetryEngine__EnableNdjsonImport': 'true', 'TelemetryEngine__ImportMaxLineBytes': '512',
            'TelemetryEngine__ImportMaxRecords': '3', 'TelemetryEngine__ImportMaxConcurrency': '1',
            'TelemetryEngine__MaxRequestBodyBytes': '4096', 'TelemetryEngine__EnableRequestDecompression': 'true',
            'TelemetryEngine__IngestionExecutionPath': 'custom', 'TelemetryEngine__WalIoBackend': 'io_uring',
            'TelemetryEngine__ForceFlushIntervalSeconds': '1'
        }
        if args.mapped:
            prefix = 'TelemetryEngine__NdjsonSourceMappings__p__Fields__'
            for target, source in {'eventId': 'id', 'timestamp': 'time', 'tenantId': 'tenant',
                    'dimensions.tag': 'tag', 'measures.amount': 'amount'}.items():
                settings[prefix + target + '__Path__0'] = 'source'
                settings[prefix + target + '__Path__1'] = source
            for target, value in {'projectId': 'p', 'eventType': 'action',
                    'dimensions.unknown': 'drop', 'measures.unknown': '999'}.items():
                settings[prefix + target + '__Default'] = value
        options = ['--memory', '512m', '--cpus', '2', '--security-opt', 'seccomp=unconfined', '--publish', '127.0.0.1::5000']
        for key, value in settings.items():
            options.extend(['--env', key + '=' + value])
        start(engine, args.image, options)
        wait_for(ready, 'published application readiness')
        assert request('POST', route, b'', key='unknown')[0] == 401
        assert request('POST', route, b'', key='public-key')[0] == 403
        assert request('POST', route, b'', content_type='application/json')[0] == 415
        assert request('POST', route, b'') == (202, {'acceptedRecords': 0, 'nextByteOffset': 0, 'nextLine': 1, 'complete': True})
        if args.mapped:
            invalid = event(100).replace(b'"tenant":"t"', b'"tenant":"\\ud800"')
            status, receipt = request('POST', route, invalid, chunked=True)
            assert status == 400 and receipt == {'acceptedRecords': 0, 'nextByteOffset': 0,
                'error': 'invalid_mapping', 'nextLine': 1, 'complete': False}, receipt
        prefix = event(2) + b'\r\n' + event(4) + b'\n'
        status, receipt = request('POST', route, prefix + b'{\n' + event(100), chunked=True)
        assert status == 400, (status, receipt)
        assert receipt == {'acceptedRecords': 2, 'nextByteOffset': len(prefix), 'error': 'invalid_json', 'nextLine': 3, 'complete': False}, receipt
        wait_for(lambda: total(6), 'accepted prefix becomes queryable')
        complete = b'\n'.join(event(1) for _ in range(3))
        status, receipt = request('POST', route, complete, chunked=True)
        assert status == 202 and receipt == {'acceptedRecords': 3, 'nextByteOffset': len(complete), 'nextLine': 4, 'complete': True}, receipt
        wait_for(lambda: total(9), 'exact record limit and final unterminated line')
        prefix = b''.join(event(1) + b'\n' for _ in range(3))
        status, receipt = request('POST', route, prefix + event(100))
        assert status == 400 and receipt == {'acceptedRecords': 3, 'nextByteOffset': len(prefix), 'error': 'record_limit', 'nextLine': 4, 'complete': False}, receipt
        wait_for(lambda: total(12), 'record-limit prefix is retained')
        status, receipt = request('POST', route, b'x' * 513 + b'\n', chunked=True)
        assert status == 413 and receipt['acceptedRecords'] == 0 and receipt['error'] == 'line_too_large', receipt
        status, receipt = request('POST', route, b'x' * 4097)
        assert status == 413 and receipt['acceptedRecords'] == 0 and receipt['error'] == 'body_too_large', receipt
        body = event(1) + b'\n'
        status, receipt = request('POST', route, body, compressed=True)
        assert status == 202 and receipt['nextByteOffset'] == len(body), receipt
        wait_for(lambda: total(13), 'compressed input uses decoded offsets')
        run(['docker', 'stop', '--time', '30', engine])
        run(['docker', 'start', engine])
        wait_for(ready, 'restart readiness')
        assert total(13), 'Restart changed the accepted event set'
        logs = run(['docker', 'logs', engine])
        assert any(json.loads(line).get('Properties', {}).get('Backend') == 'io_uring'
                   for line in logs.splitlines() if line.startswith('{'))
        (args.output / 'summary.json').write_text(json.dumps({
            'source': run(['git', 'rev-parse', 'HEAD']), 'image': args.image,
            'architecture': architecture, 'hostArchitecture': host, 'sourceMapping': args.mapped,
            'checks': ['custom-io_uring', 'authentication', 'streamed-utf8-prefix-receipt', 'pinned-project-schema',
                       'sanitization', 'exact-record-limit', 'final-line', 'line-body-limits', 'compression', 'restart-visibility'],
            'result': 'passed', 'measurements': 'disabled'
        }, indent=2) + '\n')
        print('NDJSON published-artifact functional checks passed on ' + architecture + '.')
    finally:
        for name in reversed(names):
            label = run(['docker', 'inspect', '--format', '{{index .Config.Labels "nealytics.scope"}}', name])
            assert label == scope
            logs = subprocess.run(['docker', 'logs', name], capture_output=True, text=True)
            (args.output / (name + '.log')).write_text(logs.stdout + logs.stderr)
            run(['docker', 'rm', '--force', '--volumes', name])
        assert run(['docker', 'network', 'inspect', '--format', '{{index .Labels "nealytics.scope"}}', network]) == scope
        run(['docker', 'network', 'rm', network])


if __name__ == '__main__':
    main()
