"""Actual DTO-shaped 15k loopback fixture for Read-QualificationCoverage.ps1."""
import argparse, json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse

p = argparse.ArgumentParser()
p.add_argument("--port", type=int, required=True)
p.add_argument("--scenario", choices=("ready", "partial-profile"), required=True)
a = p.parse_args()
count = 15000
job_id = "0123456789abcdef0123456789abcdef"
scope = "a" * 64
job = {
    "jobId": job_id, "status": "completed", "scopeFingerprint": scope,
    "sourceGeneration": "source-r1", "settingsRevision": "settings-r1",
    "policyRevision": "policy-r1", "authorityContextFingerprint": "b" * 64,
    "version": 4, "wave": 3, "pageCount": 150, "initializedPages": 150,
    "initializationCursor": 150, "admittedEligibleSensorCount": 15000,
    "initializedEligibleSensorCount": 15000, "cursor": 14999, "selected": 15000,
    "eligible": 15000, "qualified": 15000, "waiting": 0, "failed": 0,
    "attempts": 15000, "totalFailed": 0, "maximumAttempts": 1,
    "durationHours": 720, "cancelRequested": False
}

class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    def log_message(self, *_): pass
    def respond(self, data):
        raw = json.dumps({"success": True, "data": data}, separators=(",", ":")).encode()
        self.send_response(200); self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(raw))); self.end_headers(); self.wfile.write(raw)
    def do_GET(self):
        path = urlparse(self.path)
        if path.path.endswith("/qualification-jobs/current"):
            return self.respond(job)
        if "/qualification-jobs/" in path.path and path.path.endswith("/page"):
            q = parse_qs(path.query); offset = int(q["offset"][0]); limit = int(q["limit"][0])
            end = min(offset + limit, count)
            rows = [{"sensorObjid": i, "bindingRevision": 1, "bindingFingerprint": f"binding-{i}",
                     "status": "qualified", "reason": "proof-and-authority-current", "attempts": 1,
                     "waveAttempts": 1, "hasQualificationProof": True} for i in range(offset + 1, end + 1)]
            return self.respond({"status": "completed", "total": count, "offset": offset, "limit": 100,
                                 "nextOffset": end if end < count else None, "rows": rows})
        if "/profiles" in path.path:
            q = parse_qs(path.query); offset = int(q["offset"][0]); limit = int(q["limit"][0])
            end = min(offset + limit, count)
            ids = list(range(offset + 1, end + 1))
            if a.scenario == "partial-profile" and offset == 14900: ids.pop()
            rows = [{"sensorObjid": i, "status": "ready", "missingFacts": [],
                     "currentIdentityEpoch": 4, "currentChannelGeneration": "channel-generation-1",
                     "bindingRevision": 1, "bindingFingerprint": f"binding-{i}",
                     "bindingStatus": "qualified", "qualificationProofReference": f"proof-{i}"}
                    for i in ids]
            return self.respond({"settingsRevision": "settings-r1", "policyRevision": "policy-r1",
                                 "sourceGeneration": "source-r1", "authorityContextFingerprint": "b" * 64,
                                 "prtgEnabled": True, "total": count, "offset": offset, "limit": 100,
                                 "rows": rows, "nextOffset": end if end < count else None})
        self.send_response(404); self.end_headers()

ThreadingHTTPServer(("127.0.0.1", a.port), Handler).serve_forever()
