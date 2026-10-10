"""Loopback HTTP contract fixture for Invoke-QualificationJob.ps1 tests only."""
import argparse, json, time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

parser = argparse.ArgumentParser()
parser.add_argument("--port", type=int, required=True)
parser.add_argument("--scenario", required=True)
parser.add_argument("--request-log", required=True)
args = parser.parse_args()
SCOPE = "a" * 64
AUTHORITY = "b" * 64
JOB_ID = "0123456789abcdef0123456789abcdef"
job = {"jobId": JOB_ID, "status": "completed", "version": 4, "wave": 3,
       "scopeFingerprint": SCOPE, "settingsRevision": "settings-r1", "policyRevision": "policy-r1",
       "sourceGeneration": "source-r1", "authorityContextFingerprint": AUTHORITY,
       "selected": 2, "qualified": 0, "waiting": 2, "failed": 0, "cursor": 0,
       "deadlineUtc": "2026-10-11T00:00:00+00:00"}

class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    started = False
    resumed = False
    def log_message(self, *_): pass
    def send_body(self, status, value, content_type="application/json"):
        raw = value if isinstance(value, bytes) else json.dumps(value, separators=(",", ":")).encode()
        self.send_response(status); self.send_header("Content-Type", content_type); self.send_header("Content-Length", str(len(raw))); self.end_headers(); self.wfile.write(raw)
    def handle_request(self):
        length = int(self.headers.get("Content-Length", "0")); body = self.rfile.read(length) if length else b""
        with open(args.request_log, "a", encoding="utf-8") as f: f.write(json.dumps({"method": self.command, "path": self.path, "body": body.decode("utf-8", "replace")}) + "\n")
        if self.command != "GET" and self.headers.get("X-Requested-By") != "LogForesight": return self.send_body(400, {"success":False,"error":{"code":"csrf_header_required"}})
        if args.scenario == "slow": time.sleep(5)
        if args.scenario == "redirect":
            self.send_response(302); self.send_header("Location", "/elsewhere"); self.end_headers(); return
        if args.scenario == "unauthorized": return self.send_body(401, {})
        if args.scenario == "mime": return self.send_body(200, b"{}", "text/plain")
        if args.scenario == "oversize": return self.send_body(200, b" " * 270000)
        if self.path.endswith("/api/admin/settings"):
            data = {"revision":"settings-r1", "prtgUrl":f"http://127.0.0.1:{args.port}/prtg", "prtgEnabled":True}
        elif self.path.endswith("/contract"):
            data = {"settingsRevision":"settings-r1", "policyRevision":"policy-r1", "scopeFingerprint":SCOPE, "selectedSensors":15000 if args.scenario == "large-15000" else 2, "capacityPilotCurrent":args.scenario != "no-pilot"}
        elif self.path.startswith("/app/api/prtg/monitoring/trusted-sampling/profiles?"):
            from urllib.parse import parse_qs, urlparse
            query = parse_qs(urlparse(self.path).query); offset = int(query["offset"][0]); limit = int(query["limit"][0])
            rows = [
                {"sensorObjid":1001,"status":"waiting","missingFacts":["qualification:raw_channel_id_time_proof_required"],"bindingStatus":"waiting","qualificationProofReference":None,"currentIdentityEpoch":1,"currentChannelGeneration":"channel-1","bindingRevision":1,"bindingFingerprint":"c"*64,"boundChannelObjectId":"ch-1"},
                {"sensorObjid":1002,"status":"waiting","missingFacts":["qualification:raw_channel_id_time_proof_required"],"bindingStatus":"waiting","qualificationProofReference":None,"currentIdentityEpoch":1,"currentChannelGeneration":"channel-2","bindingRevision":1,"bindingFingerprint":"d"*64,"boundChannelObjectId":"ch-2"}]
            if args.scenario in ("incomplete", "unqualified"): rows[0]["status"] = "source_authority_incomplete"
            if args.scenario == "incomplete": rows[0]["missingFacts"] = []
            if args.scenario == "missing-binding": rows[0]["bindingRevision"] = 0
            if args.scenario == "context-drift": rows[0]["currentIdentityEpoch"] = 9
            if args.scenario == "duplicate-id": rows[1]["sensorObjid"] = 1001
            if args.scenario == "partial-id": rows.pop()
            if args.scenario == "matrix-ready":
                for row in rows: row.update(status="ready", missingFacts=[], bindingStatus="qualified", qualificationProofReference="fixture-raw-proof")
            total = 15000 if args.scenario == "large-15000" else 2
            if args.scenario == "large-15000":
                rows = [{"sensorObjid":i,"status":"waiting","missingFacts":["qualification:raw_channel_id_time_proof_required"],"bindingStatus":"waiting","qualificationProofReference":None,"currentIdentityEpoch":1,"currentChannelGeneration":f"channel-{i}","bindingRevision":1,"bindingFingerprint":"c"*64,"boundChannelObjectId":f"ch-{i}"} for i in range(offset+1,min(offset+limit,total)+1)]
            data = {"settingsRevision":"settings-r1","policyRevision":"policy-r1","sourceGeneration":"source-r1","authorityContextFingerprint":AUTHORITY,"prtgEnabled":True,"total":total,"offset":offset,"limit":limit,"rows":rows,"nextOffset":offset+len(rows) if offset+len(rows)<total else None}
        elif self.path.endswith("/current"):
            if args.scenario == "resume" and not type(self).resumed: data = {**job, "status":"expired", "version":3, "wave":2}
            elif args.scenario == "resume":
                type(self).pollCount = getattr(type(self), "pollCount", 0) + 1
                data = {**job, "status":"running" if type(self).pollCount == 1 else "completed", "version":4 + type(self).pollCount, "wave":3}
            elif args.scenario == "wave-drift" and type(self).started: data = {**job, "status":"running", "version":5, "wave":4}
            elif args.scenario == "active-start" and type(self).started: data = {**job, "status":"running", "version":4, "wave":3}
            elif args.scenario == "version-progress" and type(self).started:
                type(self).pollCount = getattr(type(self), "pollCount", 0) + 1
                data = {**job, "status":"running" if type(self).pollCount == 1 else "completed", "version":4 + type(self).pollCount, "wave":3}
            elif args.scenario == "job-context-drift": data = {**job, "status":"expired", "version":3, "wave":2, "sourceGeneration":"source-old"}
            elif args.scenario in ("matrix-ready", "matrix-incomplete", "large-15000"): data = {**job, "status":"completed", "selected":15000 if args.scenario == "large-15000" else 2}
            else: data = job if type(self).started else None
        elif self.path.endswith("/start"):
            type(self).started = True
            accepted = {**job, "status":"running", "version":4, "wave":3} if args.scenario in ("wave-drift", "active-start", "version-progress") else job
            data = {"status":"initializing", "reason":"durable-job-accepted", "capacity":None, "job":accepted}
        elif self.path.endswith("/resume"):
            type(self).started = True
            type(self).resumed = True
            resumed = {**job, "status":"running"}
            if args.scenario == "job-context-drift": resumed["sourceGeneration"] = "source-old"
            data = {"status":"running", "reason":"existing-job-resumed-with-preserved-pages-and-attempt-watermark", "capacity":None, "job":resumed}
        else: return self.send_body(404, {"success":False})
        status = 202 if self.path.endswith("/start") or self.path.endswith("/resume") else 200
        self.send_body(status, {"success":True,"data":data})
    do_GET = handle_request
    do_POST = handle_request

ThreadingHTTPServer(("127.0.0.1", args.port), Handler).serve_forever()
