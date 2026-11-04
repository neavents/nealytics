import argparse
import base64
import csv
import gzip
import hmac
import http.client
import io
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
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=False)
    architecture = run(['docker', 'image', 'inspect', '--format', '{{.Architecture}}', args.image])
    host = run(['docker', 'info', '--format', '{{.Architecture}}'])
    assert architecture == args.architecture
    assert host in ({'arm64', 'aarch64'} if architecture == 'arm64' else {'amd64', 'x86_64'})
    scope = 'csv-smoke-' + uuid.uuid4().hex[:12]
    network, storage, engine = (scope + suffix for suffix in ['-network', '-storage', '-engine'])
    jwt_secret = 'csv-functional-test-signing-key-at-least-32-bytes'
    names = []
    run(['docker', 'network', 'create', '--label', 'nealytics.scope=' + scope, network])

    def start(name, image, options):
        run(['docker', 'run', '--detach', '--name', name, '--label', 'nealytics.scope=' + scope,
             '--network', network, *options, image])
        names.append(name)

    def token():
        def encode(data):
            return base64.urlsafe_b64encode(data).decode().rstrip('=')
        unsigned = encode(b'{"alg":"HS256","typ":"JWT"}') + '.' + encode(json.dumps({
            'project_id': 'p', 'tenant_id': 't', 'scope': 'metrics', 'exp': int(time.time()) + 900}).encode())
        return unsigned + '.' + encode(hmac.digest(jwt_secret.encode(), unsigned.encode(), 'sha256'))

    def request(method, path, body=None, key='source-key', compressed=False, chunked=False):
        port = int(run(['docker', 'port', engine, '5000/tcp']).split(':')[-1])
        connection = http.client.HTTPConnection('127.0.0.1', port, timeout=10)
        headers = {'Content-Type': 'text/csv; charset=utf-8; header=present',
                   'X-Project-Key': key, 'Authorization': 'Bearer ' + token()}
        if compressed:
            headers['Content-Encoding'] = 'gzip'
            body = gzip.compress(body)
        if chunked:
            headers['Transfer-Encoding'] = 'chunked'
            encoded = body
            body = (encoded[index:index + 7] for index in range(0, len(encoded), 7))
        try:
            connection.request(method, path, body=body, headers=headers, encode_chunked=chunked)
            response = connection.getresponse()
            payload = response.read()
            return response.status, json.loads(payload) if payload else None
        finally:
            connection.close()

    def ready():
        assert run(['docker', 'inspect', '--format', '{{.State.Running}}', engine]) == 'true', 'Engine exited; see retained logs'
        try:
            return request('GET', '/ready')[0] == 200
        except (OSError, http.client.HTTPException):
            return False

    def total(value):
        status, result = request('GET', '/api/v1/analytics/metrics/total')
        return status == 200 and result['value'] == str(value)

    def record(values):
        buffer = io.StringIO(newline='')
        csv.writer(buffer, lineterminator='\r\n').writerow(values)
        return buffer.getvalue().encode()

    def event(value, details='日本語, "quoted"\r\nprivate-csv-secret'):
        return record([str(uuid.uuid4()), datetime.now(timezone.utc).isoformat().replace('+00:00', 'Z'), 't', str(value), details])

    try:
        start(storage, CLICKHOUSE, ['--network-alias', 'storage', '--memory', '2g', '--cpus', '2', '--env',
              'CLICKHOUSE_SKIP_USER_SETUP=1', '--mount', f'type=bind,source={Path.cwd()}/clickhouse-init.sql,target=/docker-entrypoint-initdb.d/init.sql,readonly'])
        wait_for(lambda: subprocess.run(['docker', 'exec', storage, 'clickhouse-client', '--host', 'storage', '-q',
                 'SELECT count() FROM nealytics_core.global_events'], capture_output=True).returncode == 0, 'network storage initialization')
        settings = {
            'TelemetryEngine__ClickHouseConnectionString': 'Host=storage;Port=9000;Database=nealytics_core;User=default;Password=;',
            'TelemetryEngine__JwtSymmetricKey': jwt_secret,
            'TelemetryEngine__IngestionKeys__0__Key': 'source-key', 'TelemetryEngine__IngestionKeys__0__Scope': 'server',
            'TelemetryEngine__IngestionKeys__0__ProjectId': 'p',
            'TelemetryEngine__IngestionKeys__1__Key': 'public-key', 'TelemetryEngine__IngestionKeys__1__Scope': 'public',
            'TelemetryEngine__IngestionKeys__1__ProjectId': 'p',
            'TelemetryEngine__ProjectSchemas__0__ProjectId': 'p',
            'TelemetryEngine__ProjectSchemas__0__Measures__0__Name': 'amount',
            'TelemetryEngine__ProjectSchemas__0__Measures__0__Type': 'Int64',
            'TelemetryEngine__ProjectSchemas__0__NamedMetrics__0__Name': 'total',
            'TelemetryEngine__ProjectSchemas__0__NamedMetrics__0__Aggregation': 'sum',
            'TelemetryEngine__ProjectSchemas__0__NamedMetrics__0__Measure': 'amount',
            'TelemetryEngine__EnableCsvImport': 'true', 'TelemetryEngine__EnableNdjsonImport': 'false',
            'TelemetryEngine__ImportMaxLineBytes': '512', 'TelemetryEngine__ImportMaxRecords': '2',
            'TelemetryEngine__ImportMaxConcurrency': '1',
            'TelemetryEngine__MaxRequestBodyBytes': '4096', 'TelemetryEngine__ForceFlushIntervalSeconds': '1',
            'TelemetryEngine__IngestionExecutionPath': 'custom', 'TelemetryEngine__WalIoBackend': 'io_uring'
        }
        prefix = 'TelemetryEngine__CsvSourceMappings__p__Fields__'
        for target, name in {'eventId': 'event,id', 'timestamp': 'time', 'tenantId': 'tenant',
                             'measures.amount': 'value', 'metadataJson': 'details'}.items():
            settings[prefix + target + '__Path__0'] = name
        settings[prefix + 'projectId__Default'] = 'p'
        settings[prefix + 'eventType__Default'] = 'action'
        options = ['--memory', '512m', '--cpus', '2', '--security-opt', 'seccomp=unconfined', '--publish', '127.0.0.1::5000']
        for key, value in settings.items():
            options.extend(['--env', key + '=' + value])
        start(engine, args.image, options)
        wait_for(ready, 'published CSV-only engine readiness')
        route = '/api/v1/telemetry/import/csv'
        header = b'\xef\xbb\xbf' + record(['event,id', 'time', 'tenant', 'value', 'details'])
        assert request('POST', route, header, key='unknown')[0] == 401
        assert request('POST', route, header, key='public-key')[0] == 403
        assert request('POST', route, b'wrong,header\n')[0] == 400
        first = header + event(2) + event(4)
        status, receipt = request('POST', route, first + b'wrong,row\n', chunked=True)
        assert status == 400 and receipt['error'] == 'record_limit' and receipt['acceptedRecords'] == 2, (status, receipt)
        assert receipt['nextByteOffset'] == len(first) and receipt['nextRecord'] == 3 and not receipt['complete']
        wait_for(lambda: total(6), 'mapped multiline CSV prefix becomes queryable')
        final = header + event(1)[:-2]
        status, receipt = request('POST', route, final, compressed=True)
        assert status == 202 and receipt['complete'] and receipt['acceptedRecords'] == 1 and receipt['nextByteOffset'] == len(final), (status, receipt)
        wait_for(lambda: total(7), 'compressed final unterminated record becomes queryable')
        prefix = header + event(2)
        status, receipt = request('POST', route, prefix + b'bad,row\n' + event(100))
        assert status == 400 and receipt['error'] == 'column_count' and receipt['acceptedRecords'] == 1, (status, receipt)
        assert receipt['nextByteOffset'] == len(prefix)
        wait_for(lambda: total(9), 'invalid row preserves only the durable prefix')
        status, receipt = request('POST', route, header + event(100, 'x' * 600))
        assert status == 413 and receipt['error'] == 'record_too_large' and receipt['acceptedRecords'] == 0, (status, receipt)
        assert receipt['nextByteOffset'] == len(header)
        status, receipt = request('POST', route, b'x' * 4097)
        assert status == 413 and receipt['error'] == 'body_too_large' and receipt['nextByteOffset'] == 0, (status, receipt)
        status, receipt = request('POST', route, header)
        assert status == 202 and receipt['acceptedRecords'] == 0 and receipt['nextByteOffset'] == len(header)
        run(['docker', 'kill', '--signal', 'KILL', engine])
        run(['docker', 'start', engine])
        wait_for(ready, 'CSV engine restart after confirmed receipts')
        assert total(9), 'Restart changed the acknowledged CSV event set'
        logs = run(['docker', 'logs', engine])
        assert any(json.loads(line).get('Properties', {}).get('Backend') == 'io_uring' for line in logs.splitlines() if line.startswith('{'))
        assert 'private-csv-secret' not in logs
        (args.output / 'summary.json').write_text(json.dumps({
            'source': run(['git', 'rev-parse', 'HEAD']), 'architecture': architecture, 'hostArchitecture': host,
            'image': args.image, 'checks': ['csv-only-startup', 'custom-io_uring', 'authentication',
                'literal-header-mapping', 'quoted-multiline-utf8', 'bom-wire-offset', 'chunked-prefix-receipt',
                'exact-record-limit', 'compressed-input', 'final-unterminated-record', 'invalid-row-prefix',
                'record-body-limits', 'header-only', 'confirmed-receipt-kill-restart', 'exact-query-total', 'payload-log-redaction'],
            'result': 'passed', 'measurements': 'disabled'
        }, indent=2) + '\n')
        print('CSV published-artifact functional checks passed on ' + architecture + '.')
    finally:
        for name in reversed(names):
            assert run(['docker', 'inspect', '--format', '{{index .Config.Labels "nealytics.scope"}}', name]) == scope
            logs = subprocess.run(['docker', 'logs', name], capture_output=True, text=True)
            (args.output / (name + '.log')).write_text(logs.stdout + logs.stderr)
            run(['docker', 'rm', '--force', '--volumes', name])
        assert run(['docker', 'network', 'inspect', '--format', '{{index .Labels "nealytics.scope"}}', network]) == scope
        run(['docker', 'network', 'rm', network])


if __name__ == '__main__':
    main()
