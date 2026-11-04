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
    parser.add_argument('--jobs', action='store_true')
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

    def request(method, path, body=None, key='source-key', content_type='application/x-ndjson', compressed=False, chunked=False, metadata=None):
        connection = http.client.HTTPConnection('127.0.0.1', port(), timeout=5)
        headers = {'X-Project-Key': key, 'Content-Type': content_type, 'Authorization': 'Bearer ' + token()}
        if metadata:
            headers.update(metadata)
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
        wait_for(lambda: subprocess.run(['docker', 'exec', storage, 'clickhouse-client', '--host', 'storage', '--query',
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
        if args.jobs:
            settings['TelemetryEngine__EnableImportJobs'] = 'true'
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
        if args.jobs:
            job = str(uuid.uuid4())
            first_id, second_id = str(uuid.uuid4()), str(uuid.uuid4())
            first, second = event(2) + b'\r\n', event(4)
            whole = first + second
            metadata = {'X-Import-Source': hashlib.sha256(whole).hexdigest(), 'X-Import-Source-Bytes': str(len(whole))}
            job_route = '/api/v1/telemetry/import/jobs/' + job
            first_route = job_route + '/chunks/' + first_id + '?offset=0'
            second_route = job_route + '/chunks/' + second_id + '?offset=' + str(len(first))
            assert request('GET', job_route, key='unknown')[0] == 401
            assert request('GET', job_route, key='public-key')[0] == 403
            assert request('GET', job_route)[0] == 404
            status, receipt = request('POST', first_route, first, metadata=metadata, chunked=True)
            assert status == 202 and receipt['state'] == 'ready', (status, receipt)
            assert receipt['acceptedRecords'] == 1 and receipt['nextByteOffset'] == len(first), receipt
            assert request('GET', job_route) == (200, receipt)
            assert request('POST', first_route, first, metadata=metadata, compressed=True) == (202, receipt)
            wait_for(lambda: total(15), 'job first chunk is visible without duplicate aggregate')
            run(['docker', 'stop', '--time', '30', engine])
            run(['docker', 'start', engine])
            wait_for(ready, 'job checkpoint restart readiness')
            assert request('GET', job_route) == (200, receipt)
            assert request('POST', first_route, first, metadata=metadata) == (202, receipt)
            assert total(15), 'Confirmed retry changed the aggregate after restart'
            status, finished = request('POST', second_route, second, metadata=metadata, chunked=True)
            assert status == 202 and finished['state'] == 'complete', (status, finished)
            assert finished['acceptedRecords'] == 2 and finished['nextByteOffset'] == len(whole), finished
            assert request('POST', second_route, second, metadata=metadata) == (202, finished)
            wait_for(lambda: total(19), 'job second chunk is visible exactly once')
            assert request('POST', first_route, first, metadata=metadata) == (409, {'error': 'job_terminal'})
            rejected_route = '/api/v1/telemetry/import/jobs/' + str(uuid.uuid4()) + '/chunks/' + str(uuid.uuid4()) + '?offset=0'
            rejected_body = event(5) + b'\n{\n'
            rejected_metadata = {'X-Import-Source': hashlib.sha256(rejected_body).hexdigest(), 'X-Import-Source-Bytes': str(len(rejected_body))}
            status, rejected = request('POST', rejected_route, rejected_body, metadata=rejected_metadata)
            assert status == 400 and rejected['state'] == 'rejected' and rejected['acceptedRecords'] == 1, rejected
            assert rejected['receipt']['error'] == 'invalid_json', rejected
            assert request('POST', rejected_route, rejected_body, metadata=rejected_metadata) == (400, rejected)
            wait_for(lambda: total(24), 'rejected job preserves only the durable prefix')
            run(['docker', 'stop', '--time', '30', engine])
            run(['docker', 'start', engine])
            wait_for(ready, 'terminal job checkpoint restart readiness')
            assert request('GET', job_route) == (200, finished)
            assert request('POST', second_route, second, metadata=metadata) == (202, finished)
            assert request('POST', rejected_route, rejected_body, metadata=rejected_metadata) == (400, rejected)
            assert total(24), 'Terminal job retries changed stored aggregates'
            run(['docker', 'stop', '--time', '30', engine])
            manifest = args.output / 'processing-recovery-fixture.json'
            run(['docker', 'cp', engine + ':/app/logs/import-jobs/' + uuid.UUID(job).hex + '.json', str(manifest)])
            confirmed = json.loads(manifest.read_bytes()[65:])
            uncertain_job, uncertain_chunk = str(uuid.uuid4()), str(uuid.uuid4())
            confirmed.update({'id': uncertain_job, 'status': 'processing', 'nextByteOffset': 0,
                'acceptedRecords': 0, 'chunkId': uncertain_chunk, 'chunkOffset': 0,
                'chunkBytes': len(first), 'chunkHash': hashlib.sha256(first).hexdigest().upper(), 'receipt': None})
            body = json.dumps(confirmed, separators=(',', ':')).encode()
            manifest.write_bytes(hashlib.sha256(body).hexdigest().upper().encode() + b'\n' + body)
            run(['docker', 'cp', str(manifest), engine + ':/app/logs/import-jobs/' + uuid.UUID(uncertain_job).hex + '.json'])
            run(['docker', 'start', engine])
            wait_for(ready, 'processing marker fixture recovery readiness')
            uncertain_route = '/api/v1/telemetry/import/jobs/' + uncertain_job
            status, uncertain = request('GET', uncertain_route)
            assert status == 200 and uncertain['state'] == 'needs_reconciliation', (status, uncertain)
            assert uncertain['acceptedRecords'] == 0 and uncertain['nextByteOffset'] == 0 and uncertain.get('receipt') is None
            assert request('POST', uncertain_route + '/chunks/' + uncertain_chunk + '?offset=0', first, metadata=metadata) == (409, {'error': 'needs_reconciliation'})
            assert total(24), 'Ambiguous processing fixture was replayed'
        logs = run(['docker', 'logs', engine])
        assert any(json.loads(line).get('Properties', {}).get('Backend') == 'io_uring'
                   for line in logs.splitlines() if line.startswith('{'))
        (args.output / 'summary.json').write_text(json.dumps({
            'source': run(['git', 'rev-parse', 'HEAD']), 'image': args.image,
            'architecture': architecture, 'hostArchitecture': host, 'sourceMapping': args.mapped, 'importJobs': args.jobs,
            'checks': ['custom-io_uring', 'authentication', 'streamed-utf8-prefix-receipt', 'pinned-project-schema',
                       'sanitization', 'exact-record-limit', 'final-line', 'line-body-limits', 'compression', 'restart-visibility']
                      + (['job-checkpoint-restart', 'confirmed-chunk-retry', 'job-terminal-retry', 'job-prefix-rejection', 'processing-manifest-recovery-fixture'] if args.jobs else []),
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
