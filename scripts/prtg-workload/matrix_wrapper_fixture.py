"""Public-API-only fixture for exercising the delivered Check -> six-cell Matrix wrapper."""
import json
import threading
import time
from datetime import datetime, timezone, timedelta
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlsplit

import argparse
parser = argparse.ArgumentParser()
parser.add_argument('--port', type=int, default=0)
parser.add_argument('--port-file', required=True)
args = parser.parse_args()

JOB_ID = 'a' * 32
POLICY = 'policy-wrapper-1'
SOURCE = 'source-wrapper-1'
SCOPE = 'scope-wrapper-1'
AUTHORITY = 'authority-wrapper-1'
BINDING = 'b' * 64
state = {'enabled': True, 'revision': 1, 'run_id': 42, 'run': None, 'fail_next_settings_put': False, 'drift_revision_on_next_route': False, 'clock_offset_seconds': 2}

def now(offset_seconds=None):
    # The fixture models API-host timestamps ahead of this client's clock so the
    # public consumer can verify the explicit trigger-to-run boundary under skew.
    if offset_seconds is None:
        offset_seconds = state['clock_offset_seconds']
    return (datetime.now(timezone.utc) + timedelta(seconds=offset_seconds)).isoformat(timespec='microseconds').replace('+00:00', 'Z')

def job(status='completed'):
    return {
        'jobId': JOB_ID, 'status': status, 'scopeFingerprint': SCOPE,
        'settingsRevision': '1', 'policyRevision': POLICY, 'sourceGeneration': SOURCE,
        'authorityContextFingerprint': AUTHORITY, 'version': 1, 'wave': 1,
        'selected': 15000, 'qualified': 15000, 'waiting': 0, 'failed': 0,
        'pageCount': 150, 'initializedPages': 150, 'initializationCursor': 150,
        'eligible': 15000, 'attempts': 15000, 'totalFailed': 0, 'maximumAttempts': 1,
        'admittedEligibleSensorCount': 15000, 'initializedEligibleSensorCount': 15000,
        'cursor': 14999, 'durationHours': 720, 'cancelRequested': False,
    }

class Handler(BaseHTTPRequestHandler):
    protocol_version = 'HTTP/1.1'

    def handle(self):
        try:
            super().handle()
        except (ConnectionResetError, BrokenPipeError):
            pass

    def reply(self, data, status=200):
        raw = json.dumps({'success': status < 400, 'data': data}, separators=(',', ':')).encode()
        self.send_response(status)
        self.send_header('Content-Type', 'application/json')
        self.send_header('Content-Length', str(len(raw)))
        self.end_headers()
        offset = 0
        while offset < len(raw):
            written = self.wfile.write(raw[offset:])
            if not written:
                raise ConnectionError('fixture failed to finish its response body')
            offset += written
        self.wfile.flush()

    def body(self):
        size = int(self.headers.get('Content-Length', '0'))
        return json.loads(self.rfile.read(size) or b'{}')

    def page(self, offset, rows, base):
        limit = min(100, 15000 - offset)
        return {'offset': offset, 'limit': limit, 'total': 15000,
                'nextOffset': offset + limit if offset + limit < 15000 else None, **base, 'rows': rows}

    def do_GET(self):
        split = urlsplit(self.path)
        path = split.path
        query = parse_qs(split.query)
        if path == '/health': return self.reply({'ok': True})
        if path == '/__fixture/fail-next-settings-put':
            state['fail_next_settings_put'] = True
            return self.reply({'armed': True})
        if path == '/__fixture/drift-revision-on-next-route':
            state['drift_revision_on_next_route'] = True
            return self.reply({'armed': True, 'fixtureOnly': True})
        if path == '/__fixture/set-clock-offset':
            offset = int(query.get('seconds', ['2'])[0])
            if offset < -10 or offset > 10: return self.reply({'error': 'offset outside fixture bound'}, 400)
            state['clock_offset_seconds'] = offset
            return self.reply({'clockOffsetSeconds': offset, 'fixtureOnly': True})
        if path == '/api/admin/settings':
            return self.reply({'revision': str(state['revision']), 'prtgEnabled': state['enabled'],
                               'prtgUrl': f'http://127.0.0.1:{server.server_port}/prtg'})
        if path == '/api/admin/hosts/all':
            rows = [{'hostId': i, 'hostName': f'host-{i}', 'ipAddress': f'10.33.{i // 256}.{i % 256}'} for i in range(1, 3001)]
            return self.reply(rows)
        prefix = '/api/prtg/monitoring/trusted-sampling/'
        if path == prefix + 'qualification-jobs/contract':
            return self.reply({'settingsRevision': str(state['revision']), 'policyRevision': POLICY,
                               'scopeFingerprint': SCOPE, 'selectedSensors': 15000,
                               'prtgEnabled': state['enabled'], 'capacityPilotCurrent': state['enabled']})
        if path == prefix + 'qualification-jobs/current':
            return self.reply(job() if state.get('job_started', True) else None)
        if path == prefix + 'profiles':
            offset = int(query.get('offset', ['0'])[0]); limit = min(100, int(query.get('limit', ['100'])[0]))
            rows = []
            for sensor_id in range(offset + 1, offset + limit + 1):
                rows.append({'sensorObjid': sensor_id, 'status': 'ready', 'bindingStatus': 'qualified',
                             'bindingRevision': 1, 'bindingFingerprint': BINDING,
                             'qualificationProofReference': f'proof-{sensor_id}', 'currentIdentityEpoch': 1,
                             'currentChannelGeneration': f'channel-{sensor_id}', 'boundChannelObjectId': f'channel-{sensor_id}',
                             'missingFacts': []})
            base = {'settingsRevision': str(state['revision']), 'policyRevision': POLICY,
                    'sourceGeneration': SOURCE, 'authorityContextFingerprint': AUTHORITY,
                    'prtgEnabled': state['enabled']}
            return self.reply(self.page(offset, rows, base))
        if path.startswith(prefix + f'qualification-jobs/{JOB_ID}/page'):
            offset = int(query.get('offset', ['0'])[0]); limit = min(100, int(query.get('limit', ['100'])[0]))
            rows = [{'sensorObjid': sensor_id, 'status': 'qualified', 'hasQualificationProof': True,
                     'bindingRevision': 1, 'bindingFingerprint': BINDING}
                    for sensor_id in range(offset + 1, offset + limit + 1)]
            return self.reply(self.page(offset, rows, {'status': 'completed'}))
        if path.startswith('/api/admin/schedule/run-preview'):
            return self.reply({'hostCount': 3000})
        if path == '/api/admin/schedule/options':
            return self.reply({'enabled': True, 'windows': [{'start': '00:00', 'end': '12:00'}]})
        if path == '/api/admin/schedule/status':
            if state['run'] is None:
                stamp = now()
                timing = {'runId': 41, 'status': 'complete', 'expectedHostDays': 15000,
                          'observedHostDays': 15000, 'committedHostDays': 15000,
                          'sourceFailedHostDays': 0, 'unknownHostDays': 0, 'duplicateHostDays': 0,
                          'durationSampleCount': 15000, 'overflow': False, 'p95Milliseconds': 100.0,
                          'startedAtUtc': stamp, 'completedAtUtc': stamp}
                return self.reply({'isRunning': False, 'lastRunEndedAt': '2026-10-01T00:00:00Z',
                                   'lastRunBatchRunId': 41, 'lastRunSuccess': True,
                                   'lastRunTriggerText': 'previous', 'netiqHostDayTiming': timing})
            return self.reply({'isRunning': False, 'lastRunEndedAt': state['run']['ended'],
                               'lastRunBatchRunId': state['run']['run_id'], 'lastRunSuccess': True,
                               'lastRunTriggerText': 'manual', 'netiqHostDayTiming': state['run']['timing']})
        if path == '/api/hosts' or path.startswith('/api/host-detail/'):
            # Real server work in this functional fixture gives a deliberately
            # separated positive pair. Client timings remain unmodified and the
            # production 10%/2-second gates still evaluate every actual request.
            # This fixture proves orchestration, not physical capacity.
            time.sleep(0.20 if not state['enabled'] else 0.01)
            if state['drift_revision_on_next_route']:
                state['drift_revision_on_next_route'] = False
                state['revision'] += 2  # model an intervening settings change and restore
            if path == '/api/hosts':
                return self.reply({'total': 3000, 'truncated': False, 'items': [{'hostId': i, 'hostName': f'host-{i}', 'roleDesc': '', 'lastReportAt': None} for i in range(1, 3001)]})
            requested_id = int(path.rsplit('/', 1)[1])
            return self.reply({'hostId': requested_id, 'caseGrantOnly': False, 'hostName': f'host-{requested_id}'})
        return self.reply({'error': 'not found'}, 404)

    def do_PUT(self):
        if self.path != '/api/admin/settings/prtg' or self.headers.get('X-Requested-By') != 'LogForesight':
            return self.reply({'error': 'not found'}, 404)
        body = self.body()
        if state['fail_next_settings_put']:
            state['fail_next_settings_put'] = False
            return self.reply({'error': 'controlled fixture failure'}, 503)
        if str(body.get('expectedRevision')) != str(state['revision']): return self.reply({'error': 'conflict'}, 409)
        state['enabled'] = bool(body['prtgEnabled']); state['revision'] += 1
        return self.reply({'revision': str(state['revision']), 'prtgEnabled': state['enabled']})

    def do_POST(self):
        if self.path != '/api/admin/schedule/run' or self.headers.get('X-Requested-By') != 'LogForesight':
            return self.reply({'error': 'not found'}, 404)
        self.body()
        run_id = state['run_id'] + 1
        started = now(); ended = now()
        timing = {'runId': run_id, 'status': 'complete', 'expectedHostDays': 15000,
                  'observedHostDays': 15000, 'committedHostDays': 15000,
                  'sourceFailedHostDays': 0, 'unknownHostDays': 0, 'duplicateHostDays': 0,
                  'durationSampleCount': 15000, 'overflow': False, 'p95Milliseconds': 100.0,
                  'startedAtUtc': started, 'completedAtUtc': ended}
        state['run_id'] = run_id; state['run'] = {'run_id': run_id, 'ended': ended, 'timing': timing}
        return self.reply({'started': True, 'message': 'started'})

server = ThreadingHTTPServer(('127.0.0.1', args.port), Handler)
server.daemon_threads = True
with open(args.port_file, 'w', encoding='ascii') as port_file:
    port_file.write(str(server.server_port))
server.serve_forever(poll_interval=0.05)
