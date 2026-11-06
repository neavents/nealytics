import argparse
import base64
import os
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
    parser.add_argument('--origin-image', default='nealytics-gcs-fixture:local')
    parser.add_argument('--architecture', choices=['amd64', 'arm64'], required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--mapped', action='store_true')
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=False)
    architecture = run(['docker', 'image', 'inspect', '--format', '{{.Architecture}}', args.image])
    host = run(['docker', 'info', '--format', '{{.Architecture}}'])
    assert architecture == args.architecture
    assert host in ({'arm64', 'aarch64'} if architecture == 'arm64' else {'amd64', 'x86_64'})
    scope = 'gcs-smoke-' + uuid.uuid4().hex[:12]
    network, storage, origin, engine = (scope + suffix for suffix in ['-network', '-storage', '-origin', '-engine'])
    access_key = 'GOOG' + uuid.uuid4().hex
    secret_key = uuid.uuid4().hex
    control_key = uuid.uuid4().hex
    identity_key = base64.b64encode(os.urandom(32)).decode()
    jwt_secret = 'gcs-functional-test-signing-key-at-least-32-bytes'
    names = []
    run(['docker', 'network', 'create', '--label', 'nealytics.scope=' + scope, network])

    def start(name, image, options):
        run(['docker', 'run', '--detach', '--name', name, '--label', 'nealytics.scope=' + scope,
             '--network', network, *options, image])
        names.append(name)

    def port(name, internal):
        return int(run(['docker', 'port', name, str(internal) + '/tcp']).split(':')[-1])

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
        first, second = event(2) + b'\r\n', event(4)
        object_file = args.output.resolve() / 'object.ndjson'
        object_file.write_bytes(first + second)
        start(origin, args.origin_image, ['--network-alias', 'origin', '--memory', '128m', '--cpus', '1', '--publish', '127.0.0.1::9000',
              '--env', 'SOURCE_ACCESS_KEY=' + access_key, '--env', 'SOURCE_SECRET_KEY=' + secret_key,
              '--env', 'FIXTURE_CONTROL_KEY=' + control_key, '--mount', f'type=bind,source={object_file},target=/data/object.ndjson,readonly'])
        wait_for(lambda: subprocess.run(['docker', 'exec', storage, 'clickhouse-client', '--host', 'storage', '-q',
                 'SELECT count() FROM nealytics_core.global_events'], capture_output=True).returncode == 0, 'network storage initialization')
        def origin_ready():
            try:
                connection = http.client.HTTPConnection('127.0.0.1', port(origin, 9000), timeout=2)
                try:
                    connection.request('GET', '/ready')
                    response = connection.getresponse()
                    response.read()
                    return response.status == 200
                finally:
                    connection.close()
            except (OSError, http.client.HTTPException):
                return False
        wait_for(origin_ready, 'HTTP origin readiness')
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
        source = {'Name': 'source', 'ProjectId': 'p', 'Endpoint': 'http://origin:9000',
                  'Bucket': 'fixture-bucket', 'Prefix': 'events/', 'AccessKey': access_key, 'SecretKey': secret_key,
                  'IdentityKey': identity_key, 'AllowInsecureHttp': 'true'}
        settings.update({'TelemetryEngine__GcsSources__0__' + key: value for key, value in source.items()})
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
        wait_for(ready, 'published GCS engine readiness')
        route = '/api/v1/telemetry/import/gcs/source'
        selector = {'key': 'events/file.ndjson'}
        assert request('POST', route + '/inspect', key='unknown')[0] == 401
        assert request('POST', route + '/inspect', key='public-key')[0] == 403
        status, snapshot = request('POST', route + '/inspect', selector)
        assert status == 200 and snapshot['bytes'] == len(first + second) and len(snapshot['sourceId']) == 64, (status, snapshot)
        assert snapshot['generation'] == '17' and snapshot['metageneration'] == '3'
        job, first_id = str(uuid.uuid4()), str(uuid.uuid4())
        first_route = route + '/jobs/' + job + '/chunks/' + first_id + '?offset=0'
        status, receipt = request('POST', first_route, snapshot)
        assert status == 202 and receipt['state'] == 'ready' and receipt['acceptedRecords'] == 1 and receipt['nextByteOffset'] == len(first), (status, receipt)
        wait_for(lambda: total(2), 'first chunk becomes queryable')
        object_file.write_bytes(event(100))
        assert request('POST', first_route, snapshot) == (202, receipt), 'Replacement must retain the selected generation'
        assert total(2), 'Retained-generation retry duplicated the first chunk'
        status, latest = request('POST', route + '/inspect', selector)
        assert status == 200 and latest['generation'] == '18' and latest['sourceId'] != snapshot['sourceId']
        status, error = request('POST', route + '/jobs/' + job + '/chunks/' + str(uuid.uuid4()) + '?offset=0', latest)
        assert status == 409 and error['error'] == 'source_conflict', (status, error)
        assert request('GET', '/api/v1/telemetry/import/jobs/' + job) == (200, receipt)
        run(['docker', 'kill', '--signal', 'KILL', engine])
        run(['docker', 'start', engine])
        wait_for(ready, 'restart after confirmed retained-generation checkpoint')
        assert request('GET', '/api/v1/telemetry/import/jobs/' + job) == (200, receipt)
        assert request('POST', first_route, snapshot) == (202, receipt)
        assert total(2), 'Confirmed retry duplicated the first chunk'
        final_route = route + '/jobs/' + job + '/chunks/' + str(uuid.uuid4()) + '?offset=' + str(len(first))
        status, finished = request('POST', final_route, snapshot)
        assert status == 202 and finished['state'] == 'complete' and finished['acceptedRecords'] == 2, (status, finished)
        assert finished['nextByteOffset'] == len(first + second)
        assert request('POST', final_route, snapshot) == (202, finished)
        wait_for(lambda: total(6), 'retained representation totals remain exact')
        def control(action):
            connection = http.client.HTTPConnection('127.0.0.1', port(origin, 9000), timeout=10)
            try:
                connection.request('POST', '/control/' + action + '?generation=17', headers={'X-Fixture-Key': control_key})
                response = connection.getresponse()
                response.read()
                assert response.status == 204, response.status
            finally:
                connection.close()
        control('metadata')
        status, error = request('POST', final_route, snapshot)
        assert status == 409 and error['error'] == 'source_changed', (status, error)
        assert request('GET', '/api/v1/telemetry/import/jobs/' + job) == (200, finished)
        control('delete')
        status, error = request('POST', final_route, snapshot)
        assert status == 404 and error['error'] == 'source_object_not_found', (status, error)
        assert request('GET', '/api/v1/telemetry/import/jobs/' + job) == (200, finished)
        assert total(6), 'Unavailable retained generation altered completed totals'
        logs = run(['docker', 'logs', engine])
        assert any(json.loads(line).get('Properties', {}).get('Backend') == 'io_uring' for line in logs.splitlines() if line.startswith('{'))
        assert all(value not in logs for value in [access_key, secret_key, identity_key, control_key])
        (args.output / 'summary.json').write_text(json.dumps({
            'source': run(['git', 'rev-parse', 'HEAD']), 'architecture': architecture, 'hostArchitecture': host,
            'image': args.image, 'originImage': args.origin_image, 'sourceMapping': args.mapped,
            'checks': ['custom-io_uring', 'authentication', 'independent-v4-request-verification', 'keyed-private-snapshot', 'retained-generation-after-replacement',
                       'bounded-source-framing', 'confirmed-checkpoint-kill-restart', 'confirmed-retry', 'exact-query-total', 'source-conflict', 'metadata-fence', 'deleted-generation-fence', 'credential-log-redaction'],
            'result': 'passed', 'measurements': 'disabled', 'provider': 'local-independent-protocol-fixture', 'liveGoogleService': 'unrun'
        }, indent=2) + '\n')
        print('GCS protocol published-artifact functional checks passed on ' + architecture + '.')
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
