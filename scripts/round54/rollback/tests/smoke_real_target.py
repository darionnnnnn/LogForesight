#!/usr/bin/env python3
"""Run the exact clean Round-53 Web release through the real rollback CLI on a tiny owned DB."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import socket
import sqlite3
import subprocess
import sys
import tempfile
import threading
import time
import urllib.request
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import rollback_control as rollback


TARGET_COMMIT = "638c58434f0bc01fe77e62365addeafd75680fb0"
DEFAULT_RELEASE = Path(r"C:\Users\eldar\Documents\Codex\2026-10-01\c-users-eldar-claude-project-logforesight\work\root-round53-publish-638c5843")


def sha(path: Path) -> str:
    return rollback.sha256_file(path)


def dump(path: Path, value) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")


def free_port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
        sock.bind(("127.0.0.1", 0))
        return int(sock.getsockname()[1])


def wait_for_web(process: subprocess.Popen, port: int, timeout: float = 45) -> None:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if process.poll() is not None:
            raise RuntimeError(f"target Web exited during bootstrap/startup with code {process.returncode}")
        try:
            with socket.create_connection(("127.0.0.1", port), timeout=0.3):
                return
        except OSError:
            time.sleep(0.1)
    raise TimeoutError("target Web did not open its isolated loopback listener")


class TcpTrap:
    def __init__(self):
        self.socket = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self.socket.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 0)
        self.socket.bind(("127.0.0.1", 0)); self.socket.listen(8); self.socket.settimeout(0.2)
        self.port = self.socket.getsockname()[1]
        self.connections = 0
        self._stop = threading.Event()
        self._thread = threading.Thread(target=self._run, daemon=True)
        self._thread.start()

    def _run(self):
        while not self._stop.is_set():
            try:
                conn, _ = self.socket.accept()
                self.connections += 1
                conn.close()
            except socket.timeout:
                pass
            except OSError:
                return

    def close(self):
        self._stop.set(); self.socket.close(); self._thread.join(timeout=2)


def ensure_blob(connection: sqlite3.Connection, key: str, content: str):
    row = connection.execute("SELECT 1 FROM lf_blobs WHERE blob_key=?", (key,)).fetchone()
    if row:
        connection.execute("UPDATE lf_blobs SET content=?, version=version+1, updated_at=? WHERE blob_key=?", (content, "2026-10-14T00:00:00", key))
    else:
        connection.execute("INSERT INTO lf_blobs(blob_key,content,version,updated_at) VALUES(?,?,1,?)", (key, content, "2026-10-14T00:00:00"))


def insert_row(connection: sqlite3.Connection, table: str, values: dict[str, object]):
    columns = {str(row[1]): row for row in connection.execute(f'PRAGMA table_info("{table}")')}
    unknown = set(values) - columns.keys()
    if unknown:
        raise RuntimeError(f"fixture schema lacks {table} columns: {sorted(unknown)}")
    selected = [column for column in values if column in columns]
    quoted = ",".join('"' + column.replace('"', '""') + '"' for column in selected)
    placeholders = ",".join("?" for _ in selected)
    connection.execute(f'INSERT INTO "{table}" ({quoted}) VALUES ({placeholders})', [values[column] for column in selected])


def canonical_rows(connection: sqlite3.Connection, table: str, key_column: str, key: object):
    rows = connection.execute(f'SELECT * FROM "{table}" WHERE "{key_column}"=? ORDER BY "{key_column}"', (key,)).fetchall()
    names = [row[1] for row in connection.execute(f'PRAGMA table_info("{table}")')]
    return [{name: (value.hex() if isinstance(value, bytes) else value) for name, value in zip(names, row)} for row in rows]


def seed_fixture(db_path: Path, trap_port: int):
    connection = sqlite3.connect(db_path)
    connection.execute("PRAGMA foreign_keys=OFF")
    blobs = {row[0]: row[1] for row in connection.execute("SELECT blob_key,content FROM lf_blobs")}
    try:
        system = json.loads(blobs.get("system_settings", "{}"))
    except json.JSONDecodeError:
        system = {}
    for flag in rollback.MAIL_FLAGS:
        system[flag] = False
    system.update({"PrtgEnabled": False, "AutoDispatchEnabled": False, "AiBaseUrl": "", "AdAuthEnabled": False,
                   "AdServers": [], "AdSearchBase": "", "AdSearchFilter": "", "PrtgUrl": f"http://127.0.0.1:{trap_port}/prtg",
                   "SmtpServer": "127.0.0.1", "SmtpPort": trap_port, "SmtpAccount": "", "SmtpPasswordEnc": ""})
    schedule = {"Enabled": False, "LocalAnalysisEnabled": False}
    sentinel = [{"SentinelId": 991, "Name": "rollback-smoke-disabled", "BaseUrl": f"http://127.0.0.1:{trap_port}/sentinel",
                 "Username": "fixture", "PasswordEnc": "", "Active": False, "Os": "windows", "UseEsmDirectory": False}]
    mail = {"PrtgUrgentAcceptedFacts": {"rollback-smoke-mail-accepted": 1},
            "UrgentSentKeys": ["rollback-smoke-mail-sent"],
            "UrgentOutbox": {"rollback-smoke-pending-intent": {"Key": "rollback-smoke-pending-intent", "Status": "pending"}}}
    ensure_blob(connection, "system_settings", json.dumps(system, ensure_ascii=False))
    ensure_blob(connection, "schedule_options", json.dumps(schedule))
    ensure_blob(connection, "sentinels", json.dumps(sentinel, ensure_ascii=False))
    ensure_blob(connection, "mail_notify_state", json.dumps(mail, ensure_ascii=False))

    now = "2026-10-14T00:00:00"
    case_id = "rollback-smoke-case-54"
    insert_row(connection, "lf_issue_cases", {"case_id": case_id, "host_name": "fixture-host", "host_name_key": "FIXTURE-HOST",
        "issue_key": "rollback-smoke-issue", "issue_label": "fixture issue", "status": "open", "note": "preserve exact case row",
        "source_key": "",
        "first_linked_date": now, "last_linked_date": now, "created_at": now, "created_by_account": "fixture", "updated_at": now,
        "day_sync_pending": 0, "cancelled": 0})
    order_id = 9901
    insert_row(connection, "lf_work_orders", {"work_order_id": order_id, "source_name": "fixture", "source_key": "FIXTURE",
        "event_id": 991, "issue_label": "rollback smoke pending task", "handler_id": 0, "origin": "manual", "scope_kind": "all",
        "scope_group_ids": "", "auto_attach": 0, "note": "preserve exact pending task", "created_by_account": "fixture", "created_at": now, "updated_at": now})
    event_id = 9901
    insert_row(connection, "lf_work_order_events", {"event_id": event_id, "work_order_id": order_id, "action": "reply",
        "actor_account": "fixture", "member_delta": 0, "note": "preserve exact human reply", "created_at": now})
    connection.commit()
    snapshot = {
        "case": canonical_rows(connection, "lf_issue_cases", "case_id", case_id),
        "workOrder": canonical_rows(connection, "lf_work_orders", "work_order_id", order_id),
        "reply": canonical_rows(connection, "lf_work_order_events", "event_id", event_id),
        "systemSettings": connection.execute("SELECT content FROM lf_blobs WHERE blob_key='system_settings'").fetchone()[0],
        "schedule": connection.execute("SELECT content FROM lf_blobs WHERE blob_key='schedule_options'").fetchone()[0],
        "sentinels": connection.execute("SELECT content FROM lf_blobs WHERE blob_key='sentinels'").fetchone()[0],
        "mailState": connection.execute("SELECT content FROM lf_blobs WHERE blob_key='mail_notify_state'").fetchone()[0],
    }
    connection.close()
    return snapshot, case_id, order_id, event_id


def make_bundle(bundle: Path, release: Path, db_path: Path, key_path: Path, case_id: str, order_id: int, event_id: int):
    web_root = bundle / "release"
    shutil.copytree(release, web_root)
    database = bundle / "paired.db"; shutil.copy2(db_path, database)
    key = bundle / "lf-crypto.key"; shutil.copy2(key_path, key)
    config = bundle / "paired-appsettings.json"; shutil.copy2(web_root / "appsettings.json", config)
    delta_rows = []
    differences = {
        "case": (case_id, {"recordId": case_id, "payload": "case-after-backup", "status": "held-unreconciled"}),
        "humanReply": (str(event_id), {"recordId": event_id, "workOrderId": order_id, "payload": "reply-after-backup", "status": "held-unreconciled"}),
        "notificationReceipt": ("smtp-rollback-smoke", {"recordId": "smtp-rollback-smoke", "accepted": True, "status": "held-unreconciled"}),
        "pendingTask": (str(order_id), {"recordId": order_id, "payload": "task-after-backup", "status": "held-unreconciled"}),
    }
    counts = {category: 0 for category in rollback.PROTECTED_CATEGORIES}
    for category, (item_id, payload_value) in differences.items():
        rel = Path("delta") / category / (item_id + ".json")
        payload_path = bundle / rel; dump(payload_path, payload_value)
        delta_rows.append({"category": category, "id": item_id, "status": "held-unreconciled", "payloadPath": rel.as_posix(), "payloadSha256": sha(payload_path)})
        counts[category] = 1
    preservation = bundle / "preservation.json"
    dump(preservation, {"schema": "lf-round54-rollback-preservation-v1", "counts": counts, "items": delta_rows})
    meta = rollback.db_metadata(database)
    web = web_root / "LogForesight.Web.dll"; core = web_root / "LogForesight.Core.dll"
    authority = {
        "schema": rollback.AUTHORITY_SCHEMA, "authorityVersion": "authority-54.1", "schemaVersion": "lf-schema-53",
        "sourceAuthorityVersion": "source-authority-54.1", "targetCommit": TARGET_COMMIT,
        "artifacts": {"webSha256": sha(web), "coreSha256": sha(core), "assetsTreeSha256": rollback.tree_sha256(web_root / "wwwroot"), "webRootTreeSha256": rollback.tree_sha256(web_root)},
        "database": {key: meta[key] for key in ("applicationId", "userVersion", "schemaSha256")},
        "pairedHashes": {"databaseSha256": sha(database), "keySha256": sha(key), "configSha256": sha(config)},
        "keyFileName": key.name,
    }
    authority_file = bundle / "authority-manifest.json"; dump(authority_file, authority)
    files = {
        "webRoot": {"path": "release", "sha256": rollback.tree_sha256(web_root)},
        "database": {"path": database.name, "sha256": sha(database)},
        "key": {"path": key.name, "sha256": sha(key)},
        "config": {"path": config.name, "sha256": sha(config)},
        "authority": {"path": authority_file.name, "sha256": sha(authority_file)},
        "preservation": {"path": preservation.name, "sha256": sha(preservation)},
    }
    manifest = bundle / "rollback-input.json"
    dump(manifest, {"schema": rollback.SCHEMA, "versions": {"authority": "authority-54.1", "schema": "lf-schema-53", "source": "source-authority-54.1"}, "files": files})
    return manifest, authority_file


def cli(*args):
    return subprocess.run([sys.executable, str(rollback.__file__), *map(str, args)], capture_output=True, text=True, check=False, timeout=90)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--release-root", type=Path, default=DEFAULT_RELEASE)
    parser.add_argument("--report", type=Path)
    args = parser.parse_args()
    release = args.release_root.resolve(strict=True)
    if rollback.tree_sha256(release) == "": raise RuntimeError("unreachable")
    identity_path = release / "build-identity.json"
    identity = rollback.read_json(identity_path)
    stamps = {row.get("SourceStamp") for row in identity.get("Assemblies", [])}
    if len(stamps) != 1 or next(iter(stamps)).split("|")[2] != TARGET_COMMIT or next(iter(stamps)).split("|")[1] != "clean":
        raise RuntimeError("specified release is not the requested exact clean target build")
    repo = Path(__file__).resolve().parents[4]
    verifier = subprocess.run(["pwsh", "-NoProfile", "-File", str(repo / "scripts" / "Verify-BuildIdentity.ps1"), "-Directory", str(release)],
                              capture_output=True, text=True, timeout=45)
    if verifier.returncode != 0 or "PASS (BuildIdentityVerified)" not in verifier.stdout:
        raise RuntimeError(f"existing Verify-BuildIdentity.ps1 rejected target release: {verifier.stdout} {verifier.stderr}")

    dotnet = shutil.which("dotnet")
    if not dotnet: raise RuntimeError("dotnet is unavailable")
    trap = TcpTrap()
    report = {"targetRelease": str(release), "targetCommit": TARGET_COMMIT, "buildIdentityVerifier": verifier.stdout.strip(),
              "targetReleaseTreeSha256Before": rollback.tree_sha256(release), "externalTrapPort": trap.port}
    try:
        with tempfile.TemporaryDirectory(prefix="lf-r54-real-rollback-") as temp_dir:
            temp = Path(temp_dir)
            app = temp / "bootstrap-app"; shutil.copytree(release, app)
            bootstrap_root = temp / "bootstrap-data"
            bootstrap_root.mkdir()
            bootstrap_port = free_port()
            # Preserve Windows runtime identity variables (SystemRoot, USERDOMAIN, etc.) which
            # the old app's Windows principal/group provider needs. Remove inherited app knobs,
            # then set only the explicit isolated bootstrap configuration.
            env = {key: value for key, value in os.environ.items()
                   if not key.casefold().startswith(("storage__", "auth__", "mail__", "prtg__", "ai__", "ad__"))}
            env.update({"DOTNET_NOLOGO": "1", "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
                        "ASPNETCORE_ENVIRONMENT": "Development", "DOTNET_ENVIRONMENT": "Development", "Auth__Provider": "Stub",
                        "Storage__Type": "Sqlite", "Storage__DataRoot": str(bootstrap_root), "Storage__ConnectionString": ""})
            bootstrap_logs = open(temp / "bootstrap.log", "ab")
            bootstrap = subprocess.Popen([dotnet, str(app / "LogForesight.Web.dll"), "--contentRoot", str(app), "--urls", f"http://127.0.0.1:{bootstrap_port}"],
                                         cwd=app, env=env, stdin=subprocess.DEVNULL, stdout=bootstrap_logs, stderr=subprocess.STDOUT)
            bootstrap_failure = None
            try:
                wait_for_web(bootstrap, bootstrap_port)
                time.sleep(2)
            except Exception as exc:
                bootstrap_failure = exc
            finally:
                if bootstrap.poll() is None:
                    bootstrap.terminate()
                    try: bootstrap.wait(timeout=8)
                    except subprocess.TimeoutExpired: bootstrap.kill(); bootstrap.wait(timeout=5)
                bootstrap_logs.close()
            if bootstrap_failure:
                log_tail = (temp / "bootstrap.log").read_text(encoding="utf-8", errors="replace")[-8000:]
                raise RuntimeError(f"{bootstrap_failure}\nbootstrap log tail:\n{log_tail}")
            db_path = bootstrap_root / "Db" / "logforesight.db"
            key_path = bootstrap_root / "keys" / "lf-crypto.key"
            if not db_path.is_file() or not key_path.is_file():
                raise RuntimeError("actual old Web bootstrap did not create its real SQLite/key paths")
            report["productionSchemaBootstrap"] = {"databasePath": str(db_path), "keyPath": str(key_path), "databaseBytes": db_path.stat().st_size,
                                                    "keyBytes": key_path.stat().st_size, "bootstrapProcessStopped": bootstrap.poll() is not None}

            snapshot, case_id, order_id, event_id = seed_fixture(db_path, trap.port)
            report["fixture"] = {"schemaTables": rollback.db_metadata(db_path)["tables"], "rowCounts": {"case": 1, "workOrderTask": 1, "reply": 1, "mailState": 1, "sentinel": 1}, "bytes": db_path.stat().st_size}
            source_db_sha, source_key_sha = sha(db_path), sha(key_path)
            bundle = temp / "bundle"; bundle.mkdir()
            manifest, authority = make_bundle(bundle, release, db_path, key_path, case_id, order_id, event_id)
            restore = temp / "restore-root"
            prepare = cli("prepare", "--manifest", manifest, "--expected-target-commit", TARGET_COMMIT,
                          "--expected-authority-sha256", sha(authority), "--restore-root", restore)
            if prepare.returncode != 0: raise RuntimeError(f"real target prepare failed: {prepare.stderr}")
            verify = cli("verify", "--restore-root", restore, "--expected-target-commit", TARGET_COMMIT)
            if verify.returncode != 0: raise RuntimeError(f"real target verify failed: {verify.stderr}")
            report["prepare"] = json.loads(prepare.stdout)
            report["verifyBeforeLaunch"] = json.loads(verify.stdout)
            actual_db = restore / "data" / "Db" / "logforesight.db"
            actual_key = restore / "data" / "keys" / "lf-crypto.key"
            if sha(actual_db) != source_db_sha or sha(actual_key) != source_key_sha:
                raise RuntimeError("prepare did not copy the exact paired DB/key to the production DataRoot paths")
            if (restore / "data" / "logforesight.db").exists() or (restore / "data" / "lf-crypto.key").exists():
                raise RuntimeError("incorrect legacy DataRoot paths were created")
            port = free_port()
            launched = cli("launch", "--restore-root", restore, "--expected-target-commit", TARGET_COMMIT, "--port", port)
            if launched.returncode != 0:
                tails = []
                for log_path in (restore / "web.stdout.log", restore / "web.stderr.log"):
                    if log_path.exists():
                        tails.append(f"{log_path.name}:\n{log_path.read_text(encoding='utf-8', errors='replace')[-8000:]}")
                raise RuntimeError(f"real target launch failed: {launched.stderr}\n" + "\n".join(tails))
            report["launch"] = json.loads(launched.stdout)
            try:
                with urllib.request.urlopen(f"http://127.0.0.1:{port}/login", timeout=3) as response:
                    report["loginHttpStatus"] = response.status
                time.sleep(12)  # Covers old hosted-service startup delays while keeping the fixture tiny.
                connection = sqlite3.connect(f"file:{actual_db.as_posix()}?mode=ro", uri=True)
                after = {
                    "case": canonical_rows(connection, "lf_issue_cases", "case_id", case_id),
                    "workOrder": canonical_rows(connection, "lf_work_orders", "work_order_id", order_id),
                    "reply": canonical_rows(connection, "lf_work_order_events", "event_id", event_id),
                    "systemSettings": connection.execute("SELECT content FROM lf_blobs WHERE blob_key='system_settings'").fetchone()[0],
                    "schedule": connection.execute("SELECT content FROM lf_blobs WHERE blob_key='schedule_options'").fetchone()[0],
                    "sentinels": connection.execute("SELECT content FROM lf_blobs WHERE blob_key='sentinels'").fetchone()[0],
                    "mailState": connection.execute("SELECT content FROM lf_blobs WHERE blob_key='mail_notify_state'").fetchone()[0],
                }
                connection.close()
                if after != snapshot:
                    differences = {key: {"before": snapshot.get(key), "after": after.get(key)}
                                   for key in snapshot if snapshot.get(key) != after.get(key)}
                    raise RuntimeError("old Web changed one or more exact smoke fixture rows/settings/sentinels: "
                                       + json.dumps(differences, ensure_ascii=False, default=str))
                report["exactRowsAndDurableStatePreserved"] = True
                report["externalTrapConnectionsDuringWebStart"] = trap.connections
                if trap.connections != 0: raise RuntimeError("a disabled external integration attempted an outbound connection")
                report["sourceBundleDbUnchanged"] = sha(db_path) == source_db_sha
                report["sourceBundleKeyUnchanged"] = sha(key_path) == source_key_sha
            finally:
                stopped = cli("stop", "--restore-root", restore, "--expected-target-commit", TARGET_COMMIT)
                if stopped.returncode != 0: raise RuntimeError(f"exact owned-PID stop failed: {stopped.stderr}")
                report["stop"] = json.loads(stopped.stdout)
            if not report["sourceBundleDbUnchanged"] or not report["sourceBundleKeyUnchanged"]:
                raise RuntimeError("test-owned paired inputs changed during launch")
        report["targetReleaseTreeSha256After"] = rollback.tree_sha256(release)
        report["targetReleaseUnchanged"] = report["targetReleaseTreeSha256Before"] == report["targetReleaseTreeSha256After"]
        report["externalTrapConnections"] = trap.connections
        if not report["targetReleaseUnchanged"] or trap.connections != 0:
            raise RuntimeError("target release changed or an integration trap received traffic")
        report["result"] = "PASS"
    finally:
        trap.close()
    output = json.dumps(report, ensure_ascii=False, indent=2)
    print(output)
    if args.report:
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(output + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
