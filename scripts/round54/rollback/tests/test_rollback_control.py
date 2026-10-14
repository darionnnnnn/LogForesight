import hashlib
import importlib.util
import json
import socket
import sqlite3
import subprocess
import sys
import tempfile
import unittest
from argparse import Namespace
from pathlib import Path
from unittest.mock import patch


MODULE_PATH = Path(__file__).resolve().parents[1] / "rollback_control.py"
spec = importlib.util.spec_from_file_location("rollback_control", MODULE_PATH)
rollback = importlib.util.module_from_spec(spec)
spec.loader.exec_module(rollback)


COMMIT = "a69fedd7" + "1" * 32


def sha(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def dump(path: Path, value) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")


def create_bundle(base: Path):
    bundle = base / "input"
    app = bundle / "release"
    app.mkdir(parents=True)
    (app / "wwwroot").mkdir()
    (app / "wwwroot" / "site.js").write_text("const rollbackFixture=true;", encoding="utf-8")
    web = app / "LogForesight.Web.dll"
    core = app / "LogForesight.Core.dll"
    web.write_bytes(b"fixture-web-binary")
    core.write_bytes(b"fixture-core-binary")
    settings = {"Storage": {"Type": "Sqlite", "DataRoot": "", "ConnectionString": ""}, "Auth": {"Provider": "Stub"}}
    dump(app / "appsettings.json", settings)
    stamp = f"lfbuild-v1|clean|{COMMIT}|" + "b" * 64
    dump(app / "build-identity.json", {"Schema": "logforesight-build-identity-v1", "Assemblies": [
        {"Name": "LogForesight.Web", "SourceStamp": stamp, "FileSha256": sha(web)},
        {"Name": "LogForesight.Core", "SourceStamp": stamp, "FileSha256": sha(core)},
    ]})
    database = bundle / "paired.db"
    connection = sqlite3.connect(database)
    connection.execute("PRAGMA application_id=1279679566")
    connection.execute("PRAGMA user_version=54")
    connection.execute("CREATE TABLE lf_blobs (blob_key TEXT PRIMARY KEY, content TEXT NOT NULL)")
    connection.execute("INSERT INTO lf_blobs VALUES (?, ?)", ("schedule_options", json.dumps({"Enabled": False, "LocalAnalysisEnabled": False})))
    system = {name: False for name in rollback.MAIL_FLAGS}
    system.update({"PrtgEnabled": False, "AutoDispatchEnabled": False, "AiBaseUrl": "", "AdAuthEnabled": False, "AdServers": []})
    connection.execute("INSERT INTO lf_blobs VALUES (?, ?)", ("system_settings", json.dumps(system)))
    connection.execute("INSERT INTO lf_blobs VALUES (?, ?)", ("sentinels", json.dumps([])))
    connection.commit(); connection.close()
    key = bundle / "key.bin"; key.write_bytes(b"paired encryption key fixture")
    config = bundle / "paired-appsettings.json"; config.write_bytes((app / "appsettings.json").read_bytes())
    preservation = bundle / "preservation.json"
    rows = []
    categories = {"case": 0, "humanReply": 0, "notificationReceipt": 0, "pendingTask": 0}
    payloads = {
        "case": ("case-71", b"case row exact bytes"),
        "humanReply": ("reply-8", b"operator reply exact bytes"),
        "notificationReceipt": ("smtp-5", b"smtp accepted receipt exact bytes"),
        "pendingTask": ("task-19", b"pending task exact bytes"),
    }
    for category, (item_id, content) in payloads.items():
        rel = Path("delta") / category / item_id
        payload = bundle / rel; payload.parent.mkdir(parents=True, exist_ok=True); payload.write_bytes(content)
        rows.append({"category": category, "id": item_id, "status": "held-unreconciled", "payloadPath": rel.as_posix(), "payloadSha256": hashlib.sha256(content).hexdigest()})
        categories[category] = 1
    dump(preservation, {"schema": "lf-round54-rollback-preservation-v1", "counts": categories, "items": rows})
    metadata = rollback.db_metadata(database)
    authority = {
        "schema": rollback.AUTHORITY_SCHEMA,
        "authorityVersion": "authority-54.1",
        "schemaVersion": "lf-schema-54",
        "sourceAuthorityVersion": "source-authority-54.1",
        "targetCommit": COMMIT,
        "artifacts": {"webSha256": sha(web), "coreSha256": sha(core), "assetsTreeSha256": rollback.tree_sha256(app / "wwwroot"), "webRootTreeSha256": rollback.tree_sha256(app)},
        "database": {k: metadata[k] for k in ("applicationId", "userVersion", "schemaSha256")},
        "pairedHashes": {"databaseSha256": sha(database), "keySha256": sha(key), "configSha256": sha(config)},
        "keyFileName": key.name,
    }
    authority_path = bundle / "authority.json"; dump(authority_path, authority)
    files = {
        "webRoot": {"path": "release", "sha256": rollback.tree_sha256(app)},
        "database": {"path": "paired.db", "sha256": sha(database)},
        "key": {"path": "key.bin", "sha256": sha(key)},
        "config": {"path": "paired-appsettings.json", "sha256": sha(config)},
        "authority": {"path": "authority.json", "sha256": sha(authority_path)},
        "preservation": {"path": "preservation.json", "sha256": sha(preservation)},
    }
    manifest = bundle / "rollback-input.json"
    dump(manifest, {"schema": rollback.SCHEMA, "versions": {"authority": "authority-54.1", "schema": "lf-schema-54", "source": "source-authority-54.1"}, "files": files})
    return {"bundle": bundle, "app": app, "database": database, "manifest": manifest, "authority": authority_path, "system": system, "preservation": preservation}


def prepare_args(bundle, root):
    return Namespace(manifest=str(bundle["manifest"]), expected_target_commit=COMMIT,
                     expected_authority_sha256=sha(bundle["authority"]), restore_root=str(root))


def repin_database_hash(bundle):
    authority = json.loads(bundle["authority"].read_text(encoding="utf-8"))
    authority["pairedHashes"]["databaseSha256"] = sha(bundle["database"])
    dump(bundle["authority"], authority)
    manifest = json.loads(bundle["manifest"].read_text(encoding="utf-8"))
    manifest["files"]["database"]["sha256"] = sha(bundle["database"])
    manifest["files"]["authority"]["sha256"] = sha(bundle["authority"])
    dump(bundle["manifest"], manifest)


class RollbackControlTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="r54-rollback-")
        self.base = Path(self.temp.name)
        self.bundle = create_bundle(self.base)
        self.root = self.base / "restore-owned"

    def tearDown(self):
        self.temp.cleanup()

    def test_paired_restore_preserves_exact_files_and_rows_before_launch(self):
        result = rollback.prepare(prepare_args(self.bundle, self.root))
        state, meta = rollback.verify_restore_root(self.root, COMMIT)
        self.assertEqual("prepared-isolated", result["result"])
        self.assertEqual({"case": 1, "humanReply": 1, "notificationReceipt": 1, "pendingTask": 1}, result["preservationCounts"])
        self.assertEqual(sha(self.bundle["database"]), sha(self.root / "data" / "Db" / "logforesight.db"))
        self.assertEqual(b"paired encryption key fixture", (self.root / "data" / "keys" / "lf-crypto.key").read_bytes())
        self.assertEqual(b"operator reply exact bytes", (self.root / "held-differences" / "humanReply" / "reply-8").read_bytes())
        self.assertEqual(b"smtp accepted receipt exact bytes", (self.root / "held-differences" / "notificationReceipt" / "smtp-5").read_bytes())
        self.assertEqual(b"pending task exact bytes", (self.root / "held-differences" / "pendingTask" / "task-19").read_bytes())
        self.assertEqual(54, meta["userVersion"])
        self.assertEqual("held; dispatch disabled until separately deduplicated", state["externalMail"])

    def test_cli_prepare_then_verify_is_executable(self):
        command = [sys.executable, str(MODULE_PATH), "prepare", "--manifest", str(self.bundle["manifest"]),
                   "--expected-target-commit", COMMIT, "--expected-authority-sha256", sha(self.bundle["authority"]),
                   "--restore-root", str(self.root)]
        prepared = subprocess.run(command, capture_output=True, text=True, check=False)
        self.assertEqual(0, prepared.returncode, prepared.stderr)
        verified = subprocess.run([sys.executable, str(MODULE_PATH), "verify", "--restore-root", str(self.root),
                                   "--expected-target-commit", COMMIT], capture_output=True, text=True, check=False)
        self.assertEqual(0, verified.returncode, verified.stderr)
        self.assertEqual("verified-closed", json.loads(verified.stdout)["result"])

    def test_identity_mismatch_is_rejected_before_root_creation(self):
        web = self.bundle["app"] / "LogForesight.Web.dll"
        web.write_bytes(b"foreign release")
        with self.assertRaisesRegex(rollback.Refusal, "input hash mismatch|Web/Core/assets"):
            rollback.prepare(prepare_args(self.bundle, self.root))
        self.assertFalse(self.root.exists())

    def test_schedule_or_mail_enabled_fails_closed_without_side_effects(self):
        connection = sqlite3.connect(self.bundle["database"])
        enabled = json.dumps({"Enabled": True})
        connection.execute("UPDATE lf_blobs SET content=? WHERE blob_key='schedule_options'", (enabled,))
        connection.commit(); connection.close()
        repin_database_hash(self.bundle)
        self.assertEqual(b"paired encryption key fixture", (self.bundle["bundle"] / "key.bin").read_bytes())
        with self.assertRaisesRegex(rollback.Refusal, "schedule_options.Enabled"):
            rollback.prepare(prepare_args(self.bundle, self.root))
        self.assertFalse(self.root.exists())

    def test_unknown_same_database_schema_is_rejected(self):
        connection = sqlite3.connect(self.bundle["database"])
        connection.execute("PRAGMA user_version=55"); connection.commit(); connection.close()
        repin_database_hash(self.bundle)
        with self.assertRaisesRegex(rollback.Refusal, "input hash mismatch|application id, user_version"):
            rollback.prepare(prepare_args(self.bundle, self.root))
        self.assertFalse(self.root.exists())

    def test_uncheckpointed_sqlite_sidecar_is_rejected_before_restore(self):
        Path(str(self.bundle["database"]) + "-wal").write_bytes(b"pending committed frames")
        repin_database_hash(self.bundle)
        with self.assertRaisesRegex(rollback.Refusal, "sidecar present"):
            rollback.prepare(prepare_args(self.bundle, self.root))
        self.assertFalse(self.root.exists())

    def test_foreign_listener_refuses_before_dotnet_or_process_start(self):
        rollback.prepare(prepare_args(self.bundle, self.root))
        listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 0)
        listener.bind(("127.0.0.1", 51971)); listener.listen(1)
        try:
            args = Namespace(restore_root=str(self.root), expected_target_commit=COMMIT, port=51971)
            with patch.object(rollback.shutil, "which", side_effect=AssertionError("dotnet must not be resolved")):
                with self.assertRaisesRegex(rollback.Refusal, "already owned or unavailable"):
                    rollback.launch(args)
        finally:
            listener.close()
        state = json.loads((self.root / "rollback-state.json").read_text(encoding="utf-8"))
        self.assertEqual("prepared-isolated", state["phase"])

    def test_changed_preserved_delta_and_seal_fail_closed(self):
        rollback.prepare(prepare_args(self.bundle, self.root))
        payload = self.root / "held-differences" / "case" / "case-71"
        payload.write_bytes(b"silently changed case")
        with self.assertRaisesRegex(rollback.Refusal, "held recovery difference"):
            rollback.verify_restore_root(self.root, COMMIT)
        state_path = self.root / "rollback-state.json"
        state = json.loads(state_path.read_text(encoding="utf-8")); state["targetCommit"] = "f" * 40
        state_path.write_text(json.dumps(state), encoding="utf-8")
        with self.assertRaisesRegex(rollback.Refusal, "sealed operation state was modified"):
            rollback.verify_restore_root(self.root, COMMIT)

    def test_unknown_preservation_status_is_rejected(self):
        value = json.loads(self.bundle["preservation"].read_text(encoding="utf-8"))
        value["items"][0]["status"] = "replayed"
        dump(self.bundle["preservation"], value)
        with self.assertRaisesRegex(rollback.Refusal, "input hash mismatch|must remain held"):
            rollback.prepare(prepare_args(self.bundle, self.root))
        self.assertFalse(self.root.exists())

    def test_unknown_authority_version_is_rejected_even_when_hash_is_pinned(self):
        authority = json.loads(self.bundle["authority"].read_text(encoding="utf-8"))
        authority["authorityVersion"] = "authority-unknown"
        dump(self.bundle["authority"], authority)
        manifest = json.loads(self.bundle["manifest"].read_text(encoding="utf-8"))
        manifest["files"]["authority"]["sha256"] = sha(self.bundle["authority"])
        manifest["versions"]["authority"] = "authority-unknown"
        dump(self.bundle["manifest"], manifest)
        with self.assertRaisesRegex(rollback.Refusal, "unknown authority version"):
            rollback.prepare(prepare_args(self.bundle, self.root))
        self.assertFalse(self.root.exists())

    def test_preservation_identity_cannot_escape_restore_root(self):
        value = json.loads(self.bundle["preservation"].read_text(encoding="utf-8"))
        value["items"][0]["id"] = "..\\outside"
        dump(self.bundle["preservation"], value)
        with self.assertRaisesRegex(rollback.Refusal, "input hash mismatch|unknown preservation category"):
            rollback.prepare(prepare_args(self.bundle, self.root))
        self.assertFalse(self.root.exists())


class ExactLaunchIdentityTests(unittest.TestCase):
    def test_exact_arguments_accept_spaces_and_reject_substrings_or_extra_switches(self):
        exe = str(Path("runtime/dotnet.exe").resolve())
        dll = Path("restore root/app/LogForesight.Web.dll")
        app = dll.parent
        url = "http://127.0.0.1:51973"
        good = [exe, str(dll.resolve()), "--contentRoot", str(app.resolve()), "--urls", url]
        rollback.require_launch_tokens(good, exe, dll, app, url)
        for index in (1, 3, 5):
            bad = good.copy(); bad[index] += "-foreign"
            with self.subTest(index=index), self.assertRaises(rollback.Refusal):
                rollback.require_launch_tokens(bad, exe, dll, app, url)
        with self.assertRaises(rollback.Refusal):
            rollback.require_launch_tokens(good + ["--urls", "http://0.0.0.0:51973"], exe, dll, app, url)

    @unittest.skipUnless(sys.platform == "win32", "actual Windows argv parser")
    def test_actual_windows_parser_roundtrips_exact_launcher_tokens(self):
        tokens = [r"C:\Program Files\dotnet\dotnet.exe", r"C:\restore root\app\LogForesight.Web.dll",
                  "--contentRoot", r"C:\restore root\app", "--urls", "http://127.0.0.1:51973"]
        self.assertEqual(tokens, rollback.windows_command_tokens(subprocess.list2cmdline(tokens)))


if __name__ == "__main__":
    unittest.main()
