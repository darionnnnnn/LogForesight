import argparse
import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse

parser = argparse.ArgumentParser()
parser.add_argument('--port', type=int, required=True)
parser.add_argument('--mode', choices=['valid', 'proof-change', 'settings-drift-final', 'source-drift-final'], default='valid')
args = parser.parse_args()
state = {'profile_pages': 0, 'settings_reads': 0, 'contract_reads': 0, 'current_reads': 0, 'settings_changed': False}
ids = list(range(1, 15001))
revision = 'settings-r1'
policy = 'policy-r1'
scope = 'scope-r1'
authority = 'authority-r1'
source = 'source-r1'

class Handler(BaseHTTPRequestHandler):
    protocol_version = 'HTTP/1.1'
    def log_message(self, *_): pass
    def reply(self, data):
        payload = json.dumps({'success': True, 'data': data}, separators=(',', ':')).encode()
        self.send_response(200); self.send_header('Content-Type', 'application/json')
        self.send_header('Content-Length', str(len(payload))); self.end_headers(); self.wfile.write(payload)
    def do_GET(self):
        if self.headers.get('X-Requested-By') != 'LogForesight':
            self.send_error(400); return
        path = urlparse(self.path).path
        query = parse_qs(urlparse(self.path).query)
        if path == '/api/admin/settings':
            state['settings_reads'] += 1
            if args.mode == 'settings-drift-final' and state['profile_pages'] >= 150: state['settings_changed'] = True
            rev = 'settings-r2' if state['settings_changed'] else revision
            return self.reply({'revision': rev, 'prtgEnabled': False, 'prtgUrl': 'http://127.0.0.1:9999'})
        if path.endswith('/qualification-jobs/contract'):
            state['contract_reads'] += 1
            pol = 'policy-r2' if args.mode == 'source-drift-final' and state['contract_reads'] > 1 else policy
            return self.reply({'settingsRevision': revision, 'policyRevision': pol, 'scopeFingerprint': scope,
                               'selectedSensors': 15000, 'capacityPilotCurrent': False, 'prtgEnabled': False})
        if path.endswith('/qualification-jobs/current'):
            state['current_reads'] += 1
            src = 'source-r2' if args.mode == 'source-drift-final' and state['current_reads'] > 1 else source
            return self.reply({'jobId':'a'*32,'version':1,'wave':1,'status':'completed','scopeFingerprint':scope,
                               'policyRevision':policy,'sourceGeneration':src,'settingsRevision':revision,
                               'authorityContextFingerprint':authority})
        if path.endswith('/page') and '/qualification-jobs/' in path:
            offset = int(query['offset'][0]); limit = int(query['limit'][0]); rows=[]
            for sensor in ids[offset:offset+limit]:
                rows.append({'sensorObjid':sensor,'status':'qualified','hasQualificationProof':True,
                             'bindingRevision':1,'bindingFingerprint':'b'*64})
            return self.reply({'status':'completed','offset':offset,'limit':limit,'total':15000,'rows':rows,
                               'nextOffset':offset+limit if offset+limit < 15000 else None})
        if path.endswith('/profiles'):
            offset = int(query['offset'][0]); limit = int(query['limit'][0]); state['profile_pages'] += 1
            rows=[]
            for sensor in ids[offset:offset+limit]:
                proof = f'proof-{sensor}'
                if args.mode == 'proof-change' and sensor == 15000: proof = 'proof-changed'
                rows.append({'sensorObjid':sensor,'currentIdentityEpoch':1,'currentChannelGeneration':f'channel-{sensor}',
                             'bindingRevision':1,'bindingFingerprint':'b'*64,'bindingStatus':'qualified',
                             'qualificationProofReference':proof,'status':'waiting'})
            if args.mode == 'settings-drift-final' and offset == 14900: state['settings_changed'] = True
            return self.reply({'offset':offset,'limit':limit,'total':15000,'settingsRevision':revision,
                               'policyRevision':policy,'sourceGeneration':source,'authorityContextFingerprint':authority,
                               'prtgEnabled':False,'rows':rows,'nextOffset':offset+limit if offset+limit < 15000 else None})
        self.send_error(404)

ThreadingHTTPServer(('127.0.0.1', args.port), Handler).serve_forever()
