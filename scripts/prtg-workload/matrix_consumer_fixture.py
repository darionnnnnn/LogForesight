import argparse
import json
import threading
from datetime import datetime, timezone, timedelta
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

parser = argparse.ArgumentParser()
parser.add_argument('--port', type=int, default=0)
parser.add_argument('--port-file', required=True)
parser.add_argument('--mode', default='ok')
args = parser.parse_args()
state = {'schedule_posts': 0, 'status_reads': 0, 'enabled': True, 'revision': 1, 'started': None}
def api_now():
    # Model a bounded API-host/client clock offset; the production check remains strict.
    return (datetime.now(timezone.utc) + timedelta(seconds=2)).isoformat()
prior = api_now()

class Handler(BaseHTTPRequestHandler):
    protocol_version = 'HTTP/1.1'
    def handle(self):
        try:
            super().handle()
        except ConnectionResetError:
            pass
    def log_message(self, *unused):
        super().log_message(*unused)
    def reply(self, value, status=200, content_type='application/json'):
        raw = value if isinstance(value, bytes) else json.dumps(value, separators=(',', ':')).encode()
        self.send_response(status)
        self.send_header('Content-Type', content_type)
        self.send_header('Content-Length', str(len(raw)))
        self.end_headers()
        offset = 0
        while offset < len(raw):
            written = self.wfile.write(raw[offset:])
            if written is None or written <= 0:
                raise ConnectionError('fixture could not finish writing its declared response body')
            offset += written
        self.wfile.flush()
        self.log_message('wrote %d of %d response bytes', offset, len(raw))
    def json(self):
        n = int(self.headers.get('Content-Length', '0'))
        return json.loads(self.rfile.read(n) or b'{}')
    def do_GET(self):
        path = self.path.split('?', 1)[0]
        if path == '/health': return self.reply({'ok': True})
        if path == '/api/admin/settings':
            if args.mode == 'redirect': return self.reply({}, 302)
            if args.mode == 'unauthorized': return self.reply({'error': 'no'}, 401)
            if args.mode == 'mime': return self.reply('{}', 200, 'text/plain')
            if args.mode == 'oversize': return self.reply({'data': {'padding': 'x' * 140000}}, 200)
            prtg_url = f'http://127.0.0.1:{server.server_port}/prtg'
            if args.mode == 'wrong-prtg-url': prtg_url = f'http://127.0.0.1:{server.server_port}/other'
            return self.reply({'data': {'revision': str(state['revision']), 'prtgEnabled': state['enabled'], 'prtgUrl': prtg_url}})
        if path == '/api/admin/hosts/all':
            hosts = []
            host_limit = 2999 if args.mode == 'wrong-host-count' else 3000
            for i in range(1, host_limit + 1):
                value = i
                ip = f'10.33.{value // 256}.{value % 256}'
                hosts.append({'hostId': i, 'hostName': f'fixture-{i}', 'ipAddress': ip})
            return self.reply({'data': hosts})
        if path.startswith('/api/admin/schedule/run-preview'):
            return self.reply({'data': {'hostCount': 2999 if args.mode == 'wrong-preview-count' else 3000}})
        if path == '/api/admin/schedule/status':
            state['status_reads'] += 1
            running = state['schedule_posts'] > 0 and state['status_reads'] < 2
            completed = api_now()
            stale = args.mode == 'stale-run-timing' and state['schedule_posts'] > 0
            current_start = state['started'] or prior
            return self.reply({'data': {
                'isRunning': running,
                'lastRunEndedAt': None if running else (prior if state['schedule_posts'] == 0 else completed),
                'lastRunBatchRunId': None if running else (42 if stale or state['schedule_posts'] == 0 else 43),
                'lastRunSuccess': None if running else True,
                'lastRunTriggerText': None if running else 'manual',
                'netiqHostDayTiming': None if running else {
                    'runId': 42 if stale or state['schedule_posts'] == 0 else 43, 'status': 'complete', 'expectedHostDays': 15000,
                    'observedHostDays': 15000, 'committedHostDays': 15000,
                    'sourceFailedHostDays': 0, 'unknownHostDays': 0,
                    'duplicateHostDays': 0, 'durationSampleCount': 15000,
                    'overflow': False, 'p95Milliseconds': 117.5,
                    'startedAtUtc': prior if stale or state['schedule_posts'] == 0 else current_start,
                    'completedAtUtc': prior if stale or state['schedule_posts'] == 0 else completed
                }
            }})
        if path == '/api/admin/schedule/options':
            return self.reply({'data': {'enabled': True, 'windows': [{'start': '00:00', 'end': '12:00'}]}})
        if path in ('/api/hosts',) or path.startswith('/api/host-detail/'):
            if args.mode == 'route-fail': return self.reply({'error': 'fixture route failure'}, 500)
            if args.mode == 'route-domain-fail': return self.reply({'success': False, 'data': None, 'error': {'code': 'fixture-failed'}})
            if args.mode == 'route-empty': return self.reply({'success': True, 'data': {}})
            if path == '/api/hosts':
                return self.reply({'success': True, 'data': {'total': 3000, 'truncated': False, 'items': [{'hostId': i, 'hostName': f'host-{i}', 'roleDesc': '', 'lastReportAt': None} for i in range(1, 3001)]}})
            requested_id = int(path.rsplit('/', 1)[1])
            return self.reply({'success': True, 'data': {'hostId': requested_id, 'caseGrantOnly': False, 'hostName': f'host-{requested_id}'}})
        return self.reply({'error': 'not found'}, 404)
    def do_PUT(self):
        if self.path != '/api/admin/settings/prtg': return self.reply({'error':'not found'},404)
        body = self.json()
        if str(body.get('expectedRevision')) != str(state['revision']): return self.reply({'error':'conflict'},409)
        state['enabled'] = bool(body.get('prtgEnabled'))
        state['revision'] += 1
        return self.reply({'data': {'revision': str(state['revision']), 'prtgEnabled': state['enabled']}})
    def do_POST(self):
        if self.path != '/api/admin/schedule/run': return self.reply({'error':'not found'},404)
        self.json()
        state['schedule_posts'] += 1
        state['started'] = api_now()
        return self.reply({'data': {'started': True, 'message': 'started'}})

server = ThreadingHTTPServer(('127.0.0.1', args.port), Handler)
server.daemon_threads = True
with open(args.port_file, 'w', encoding='ascii') as port_file:
    port_file.write(str(server.server_port))
server.serve_forever(poll_interval=0.1)
