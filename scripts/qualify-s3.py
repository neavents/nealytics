import argparse
import base64
import hashlib
import hmac
import http.client
import json
import subprocess
import time
import urllib.parse
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
    parser.add_argument('--store-image', default='nealytics-s3-fixture:9e49d5e')
    parser.add_argument('--architecture', choices=['amd64', 'arm64'], required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--mapped', action='store_true')
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=False)
    architecture = run(['docker', 'image', 'inspect', '--format', '{{.Architecture}}', args.image])
    host = run(['docker', 'info', '--format', '{{.Architecture}}'])
    assert architecture == args.architecture
    assert host in ({'arm64', 'aarch64'} if architecture == 'arm64' else {'amd64', 'x86_64'})
    scope = 's3-smoke-' + uuid.uuid4().hex[:12]
    network, storage, objects, engine = (scope + suffix for suffix in ['-network', '-storage', '-objects', '-engine'])
    access, secret = 'fixture' + uuid.uuid4().hex[:12], uuid.uuid4().hex
    jwt_secret = 's3-functional-test-signing-key-at-least-32-bytes'
    names = []
    run(['docker', 'network', 'create', '--label', 'nealytics.scope=' + scope, network])

    def start(name, image, options):
        run(['docker', 'run', '--detach', '--name', name, '--label', 'nealytics.scope=' + scope,
             '--network', network, *options, image])
        names.append(name)

    def port(name, internal):
        return int(run(['docker', 'port', name, str(internal) + '/tcp']).split(':')[-1])

    def seed(path, body=b'', content_type='application/octet-stream'):
        connection = http.client.HTTPConnection('127.0.0.1', port(objects, 9000), timeout=10)
        now = datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ')
        digest = hashlib.sha256(body).hexdigest()
        headers = {'host': '127.0.0.1:' + str(port(objects, 9000)), 'x-amz-date': now, 'x-amz-content-sha256': digest}
        if content_type == 'application/xml':
            headers['content-md5'] = base64.b64encode(hashlib.md5(body).digest()).decode()
        signed = ';'.join(sorted(headers))
        target, _, query = path.partition('?')
        canonical = 'PUT\n' + target + '\n' + query + '\n' + ''.join(key + ':' + headers[key] + '\n' for key in sorted(headers)) + '\n' + signed + '\n' + digest
        credential_scope = now[:8] + '/us-east-1/s3/aws4_request'
        message = 'AWS4-HMAC-SHA256\n' + now + '\n' + credential_scope + '\n' + hashlib.sha256(canonical.encode()).hexdigest()
        signing = ('AWS4' + secret).encode()
        for component in [now[:8], 'us-east-1', 's3', 'aws4_request']:
            signing = hmac.digest(signing, component.encode(), 'sha256')
        signature = hmac.new(signing, message.encode(), 'sha256').hexdigest()
        headers['Authorization'] = 'AWS4-HMAC-SHA256 Credential=' + access + '/' + credential_scope + ',SignedHeaders=' + signed + ',Signature=' + signature
        headers['Content-Type'] = content_type
        try:
            connection.request('PUT', path, body=body, headers=headers)
            response = connection.getresponse()
            assert response.status in (200, 204), (response.status, response.read().decode())
            response.read()
        finally:
            connection.close()

    def token():
        def encode(data):
            return base64.urlsafe_b64encode(data).decode().rstrip('=')
        unsigned = encode(b'{"alg":"HS256","typ":"JWT"}') + '.' + encode(json.dumps({
            'project_id': 'p', 'tenant_id': 't', 'scope': 'metrics', 'exp': int(time.time()) + 900}).encode())
        return unsigned + '.' + encode(hmac.digest(jwt_secret.encode(), unsigned.encode(), 'sha256'))

    def request(method, path, body=None, key='source-key'):
        connection = http.client.HTTPConnection('127.0.0.1', port(engine, 5000), timeout=10)
        headers = {'Content-Type': 'application/json', 'X-Project-Key': key, 'Authorization': 'Bearer ' + token()}
        try:
            connection.request(method, path, body=None if body is None else json.dumps(body).encode(), headers=headers)
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

    def event(value):
        fields = {'id': str(uuid.uuid4()), 'time': datetime.now(timezone.utc).isoformat().replace('+00:00', 'Z'), 'tenant': 't', 'amount': value}
        if args.mapped:
            return json.dumps({'source': fields}, separators=(',', ':')).encode()
        return json.dumps({'eventId': fields['id'], 'timestamp': fields['time'], 'projectId': 'p', 'tenantId': 't',
                           'eventType': 'action', 'measures': {'amount': str(value)}}, separators=(',', ':')).encode()

    try:
        start(storage, CLICKHOUSE, ['--network-alias', 'storage', '--memory', '2g', '--cpus', '2', '--env',
              'CLICKHOUSE_SKIP_USER_SETUP=1', '--mount', f'type=bind,source={Path.cwd()}/clickhouse-init.sql,target=/docker-entrypoint-initdb.d/init.sql,readonly'])
        start(objects, args.store_image, ['--network-alias', 'objects', '--memory', '1g', '--cpus', '2', '--publish', '127.0.0.1::9000',
              '--env', 'MINIO_ROOT_USER=' + access, '--env', 'MINIO_ROOT_PASSWORD=' + secret, '--env', 'MINIO_BROWSER=off'])
        wait_for(lambda: subprocess.run(['docker', 'exec', storage, 'clickhouse-client', '--host', 'storage', '-q', 'SELECT count() FROM nealytics_core.global_events'], capture_output=True).returncode == 0, 'storage initialization')
        def object_ready():
            try:
                connection = http.client.HTTPConnection('127.0.0.1', port(objects, 9000), timeout=2)
                try:
                    connection.request('GET', '/minio/health/ready')
                    response = connection.getresponse()
                    response.read()
                    return response.status == 200
                finally:
                    connection.close()
            except (OSError, http.client.HTTPException):
                return False
        wait_for(object_ready, 'S3 fixture readiness')
        seed('/events')
        seed('/events?versioning=', b'<VersioningConfiguration xmlns="http://s3.amazonaws.com/doc/2006-03-01/"><Status>Enabled</Status></VersioningConfiguration>', 'application/xml')
        object_key = 'events/日本語 +%.ndjson'
        object_path = '/events/' + urllib.parse.quote(object_key, safe='/')
        first, second = event(2) + b'\r\n', event(4)
        seed(object_path, first + second, 'application/x-ndjson')
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
            'TelemetryEngine__EnableNdjsonImport': 'true', 'TelemetryEngine__EnableImportJobs': 'true',
            'TelemetryEngine__ImportMaxLineBytes': '512', 'TelemetryEngine__ImportMaxRecords': '1',
            'TelemetryEngine__MaxRequestBodyBytes': '4096', 'TelemetryEngine__ForceFlushIntervalSeconds': '1',
            'TelemetryEngine__IngestionExecutionPath': 'custom', 'TelemetryEngine__WalIoBackend': 'io_uring'
        }
        source = {'Name': 'source', 'ProjectId': 'p', 'Endpoint': 'http://objects:9000', 'Bucket': 'events',
                  'Region': 'us-east-1', 'Prefix': 'events/', 'AccessKey': access, 'SecretKey': secret, 'AllowInsecureHttp': 'true'}
        settings.update({'TelemetryEngine__S3Sources__0__' + key: value for key, value in source.items()})
        if args.mapped:
            prefix = 'TelemetryEngine__NdjsonSourceMappings__p__Fields__'
            for target, name in {'eventId': 'id', 'timestamp': 'time', 'tenantId': 'tenant', 'measures.amount': 'amount'}.items():
                settings[prefix + target + '__Path__0'] = 'source'
                settings[prefix + target + '__Path__1'] = name
            settings[prefix + 'projectId__Default'] = 'p'
            settings[prefix + 'eventType__Default'] = 'action'
        options = ['--memory', '512m', '--cpus', '2', '--security-opt', 'seccomp=unconfined', '--publish', '127.0.0.1::5000']
        for key, value in settings.items():
            options.extend(['--env', key + '=' + value])
        start(engine, args.image, options)
        wait_for(ready, 'published engine readiness')
        route = '/api/v1/telemetry/import/s3/source'
        assert request('POST', route + '/inspect', {'key': object_key}, key='unknown')[0] == 401
        assert request('POST', route + '/inspect', {'key': object_key}, key='public-key')[0] == 403
        status, snapshot = request('POST', route + '/inspect', {'key': object_key})
        assert status == 200 and snapshot['bytes'] == len(first + second) and snapshot['versionId'], (status, snapshot)
        seed(object_path, event(100), 'application/x-ndjson')
        job, first_id = str(uuid.uuid4()), str(uuid.uuid4())
        first_route = route + '/jobs/' + job + '/chunks/' + first_id + '?offset=0'
        status, receipt = request('POST', first_route, snapshot)
        assert status == 202 and receipt['state'] == 'ready' and receipt['acceptedRecords'] == 1 and receipt['nextByteOffset'] == len(first), (status, receipt)
        wait_for(lambda: total(2), 'pinned first version becomes queryable')
        run(['docker', 'kill', '--signal', 'KILL', engine])
        run(['docker', 'start', engine])
        wait_for(ready, 'restart after confirmed S3 checkpoint')
        assert request('GET', '/api/v1/telemetry/import/jobs/' + job) == (200, receipt)
        assert request('POST', first_route, snapshot) == (202, receipt)
        assert total(2), 'Confirmed retry duplicated the first chunk'
        final_route = route + '/jobs/' + job + '/chunks/' + str(uuid.uuid4()) + '?offset=' + str(len(first))
        status, finished = request('POST', final_route, snapshot)
        assert status == 202 and finished['state'] == 'complete' and finished['acceptedRecords'] == 2, (status, finished)
        assert finished['nextByteOffset'] == len(first + second)
        assert request('POST', final_route, snapshot) == (202, finished)
        wait_for(lambda: total(6), 'original version totals remain exact')
        status, latest = request('POST', route + '/inspect', {'key': object_key})
        assert status == 200 and latest['sourceId'] != snapshot['sourceId']
        status, error = request('POST', route + '/jobs/' + job + '/chunks/' + str(uuid.uuid4()) + '?offset=0', latest)
        assert status == 409 and error['error'] == 'source_conflict', (status, error)
        assert total(6), 'Changed object altered the completed import'
        logs = run(['docker', 'logs', engine])
        assert any(json.loads(line).get('Properties', {}).get('Backend') == 'io_uring' for line in logs.splitlines() if line.startswith('{'))
        assert secret not in logs and access not in logs and object_key not in logs
        (args.output / 'summary.json').write_text(json.dumps({
            'source': run(['git', 'rev-parse', 'HEAD']), 'architecture': architecture, 'hostArchitecture': host,
            'image': args.image, 'storeImage': args.store_image, 'sourceMapping': args.mapped,
            'checks': ['custom-io_uring', 'authentication', 'sigv4-s3-read', 'unicode-object-key', 'version-overwrite-isolation',
                       'bounded-source-framing', 'confirmed-checkpoint-kill-restart', 'confirmed-retry', 'exact-query-total', 'source-conflict', 'credential-log-redaction'],
            'result': 'passed', 'measurements': 'disabled'
        }, indent=2) + '\n')
        print('S3 published-artifact functional checks passed on ' + architecture + '.')
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
