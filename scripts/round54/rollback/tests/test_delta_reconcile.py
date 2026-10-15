import hashlib
import importlib.util
import json
import shutil
import sqlite3
import subprocess
import sys
import tempfile
import unittest
from datetime import datetime, timedelta
from argparse import Namespace
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
sys.path.insert(0, str(Path(__file__).resolve().parent))
import delta_reconcile as delta
from test_rollback_control import COMMIT, create_bundle, prepare_args, repin_database_hash, rollback, sha
sys.modules["rollback_control"] = rollback


def add_business_tables(database: Path) -> None:
    connection = sqlite3.connect(database)
    connection.executescript("""
        CREATE TABLE lf_issue_cases(case_id TEXT PRIMARY KEY, host_name TEXT NOT NULL, issue_key TEXT NOT NULL, status TEXT NOT NULL, note TEXT);
        CREATE TABLE lf_work_orders(work_order_id INTEGER PRIMARY KEY, issue_label TEXT NOT NULL, closed_at TEXT NULL, note TEXT);
        CREATE TABLE lf_work_order_events(event_id INTEGER PRIMARY KEY, work_order_id INTEGER NOT NULL, action TEXT NOT NULL, actor_account TEXT NOT NULL, note TEXT);
    """)
    connection.execute("INSERT INTO lf_work_orders VALUES(12,'disk',NULL,'before')")
    connection.execute("INSERT INTO lf_blobs VALUES('mail_notify_state', ?)", (json.dumps({"UrgentOutbox": {}}, separators=(",", ":")),))
    connection.commit(); connection.close()


def repin_full(bundle):
    authority = json.loads(bundle["authority"].read_text(encoding="utf-8"))
    metadata = rollback.db_metadata(bundle["database"])
    authority["database"] = {key: metadata[key] for key in ("applicationId", "userVersion", "schemaSha256")}
    authority["pairedHashes"]["databaseSha256"] = sha(bundle["database"])
    bundle["authority"].write_text(json.dumps(authority, separators=(",", ":")), encoding="utf-8")
    manifest = json.loads(bundle["manifest"].read_text(encoding="utf-8"))
    manifest["files"]["database"]["sha256"] = sha(bundle["database"])
    manifest["files"]["authority"]["sha256"] = sha(bundle["authority"])
    bundle["manifest"].write_text(json.dumps(manifest, separators=(",", ":")), encoding="utf-8")


def native_pending_reply_fixture(database: Path):
    """Tiny predicate fixture; native EF producer/consumer is separately exercised by the C# seam test."""
    issue_key = "System|disk|153|1"
    local_base = datetime.now().replace(microsecond=0)
    aware = local_base.astimezone()
    offset = aware.strftime("%z")
    zone = f"{offset[:3]}:{offset[3:]}"
    prefix = local_base.strftime("%Y-%m-%dT%H:%M:%S")
    naive_stamp = f"{prefix.replace('T', ' ')}.1234567"
    aware_stamp = f"{prefix}.1234567{zone}"
    connection = sqlite3.connect(database)
    connection.executescript("""
        CREATE TABLE lf_blobs(blob_key TEXT PRIMARY KEY, content TEXT NOT NULL);
        CREATE TABLE lf_top_issues(host_id INTEGER, record_date TEXT, log_name TEXT, source_name TEXT,
            event_id INTEGER, entry_type INTEGER, event_key TEXT);
        CREATE TABLE lf_issue_handling(id INTEGER PRIMARY KEY, host_name TEXT, issue_key TEXT,
            record_date TEXT, status TEXT);
        CREATE TABLE lf_issue_cases(case_id TEXT PRIMARY KEY, work_order_id INTEGER, closed_at TEXT);
    """)
    connection.execute("INSERT INTO lf_blobs VALUES('hosts', ?)", (json.dumps([
        {"HostId": 77, "HostName": "host-a", "MergedInto": None}
    ], separators=(",", ":")),))
    for day in ("2026-10-12", "2026-10-13", "2026-10-14"):
        connection.execute("INSERT INTO lf_top_issues VALUES(77, ?, 'System', 'disk', 153, 1, '')", (day,))
    connection.execute("INSERT INTO lf_issue_cases VALUES('case-1', 12, NULL)")
    connection.commit(); connection.close()

    intent = {
        "Mode": "sync", "Status": "in_progress", "Note": "pending seam", "DueDate": None,
        "Clearing": False, "ActorId": 7, "ActorAccount": "handler",
        "OccurredAt": aware_stamp, "TriggerDate": None,
    }
    before_case = {
        "case_id": "case-1", "work_order_id": 12, "closed_at": None, "status": "open",
        "note": None, "due_date": None, "updated_at": naive_stamp, "day_sync_pending": False,
        "day_sync_intent": None, "host_name": "host-a", "issue_key": issue_key,
        "issue_label": "disk 153",
    }
    after_case = dict(before_case, status="in_progress", note="pending seam", updated_at=naive_stamp,
                      day_sync_pending=True, day_sync_intent=json.dumps(intent, separators=(",", ":")))
    before_order = {"work_order_id": 12, "closed_at": None, "closed_reason": None, "last_reply_at": None,
                    "updated_at": "2026-10-01 00:00:00", "note": "order"}
    after_order = dict(before_order, last_reply_at=naive_stamp, updated_at=naive_stamp)
    items = [
        {"category": "pendingTask", "table": "lf_work_orders", "id": "12", "before": before_order, "after": after_order},
        {"category": "case", "table": "lf_issue_cases", "id": "case-1", "before": before_case, "after": after_case},
        {"category": "humanReply", "table": "lf_work_order_events", "id": "22", "before": None,
         "after": {"event_id": 22, "work_order_id": 12, "action": "replied", "member_delta": 0,
                   "actor_id": 7, "actor_account": "handler", "created_at": naive_stamp,
                   "note": "in_progress：pending seam（1 台）"}},
    ]
    return issue_key, intent, items


class DeltaReconcileTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="r54-delta-reconcile-")
        self.base = Path(self.temp.name)
        self.bundle = create_bundle(self.base)
        add_business_tables(self.bundle["database"])
        repin_full(self.bundle)
        self.current = self.base / "current.db"
        shutil.copy2(self.bundle["database"], self.current)
        self.root = self.base / "restore-owned"

    def tearDown(self):
        self.temp.cleanup()

    def _clear_preservation(self):
        counts = {category: 0 for category in rollback.PROTECTED_CATEGORIES}
        self.bundle["preservation"].write_text(json.dumps({"schema": "lf-round54-rollback-preservation-v1", "counts": counts, "items": []}, separators=(",", ":")), encoding="utf-8")
        manifest = json.loads(self.bundle["manifest"].read_text(encoding="utf-8"))
        manifest["files"]["preservation"]["sha256"] = sha(self.bundle["preservation"])
        self.bundle["manifest"].write_text(json.dumps(manifest, separators=(",", ":")), encoding="utf-8")

    def _mutate_current(self, unknown_mail=False):
        connection = sqlite3.connect(self.current)
        accepted_state = {
            "UrgentOutbox": {
                "intent-accepted": {"Key": "intent-accepted", "Status": "smtp-accepted", "Recipients": {"a@example.test": "smtp-accepted"}, "SmtpAcceptedAtUtc": {"a@example.test": "2026-10-14T01:00:00Z"}},
                "intent-pending": {"Key": "intent-pending", "Status": "pending", "Recipients": {"b@example.test": "pending"}, "SmtpAcceptedAtUtc": {}},
            }
        }
        if unknown_mail:
            accepted_state["UrgentOutbox"]["intent-unknown"] = {"Key": "intent-unknown", "Status": "sending-result-unknown", "Recipients": {"c@example.test": "sending-result-unknown"}, "SmtpAcceptedAtUtc": {}}
        connection.execute("UPDATE lf_blobs SET content=? WHERE blob_key='mail_notify_state'", (json.dumps(accepted_state, separators=(",", ":")),))
        connection.commit(); connection.close()

    def _capture_prepare_reconcile(self, unknown_mail=False):
        self._mutate_current(unknown_mail)
        delta_path = self.base / "delta.json"
        delta.capture_delta(self.bundle["database"], self.current, delta_path)
        self._clear_preservation()
        rollback.prepare(prepare_args(self.bundle, self.root))
        result = delta.reconcile(self.root, COMMIT, delta_path, sha(delta_path))
        return delta_path, result

    def test_same_schema_delta_cas_backup_and_controlled_release(self):
        delta_path, result = self._capture_prepare_reconcile()
        self.assertTrue(result["dataRecoveryEligible"])
        self.assertTrue(result["mailReleaseEligible"])
        self.assertTrue((self.root / "reconcile-backup" / "logforesight.after-reconcile.db").is_file())
        connection = sqlite3.connect(self.root / "data" / "Db" / "logforesight.db")
        self.assertEqual("before", connection.execute("SELECT note FROM lf_work_orders WHERE work_order_id=12").fetchone()[0])
        self.assertEqual(0, connection.execute("SELECT COUNT(*) FROM lf_issue_cases WHERE case_id='case-1'").fetchone()[0])
        self.assertEqual(0, connection.execute("SELECT COUNT(*) FROM lf_work_order_events WHERE event_id=22").fetchone()[0])
        mail = json.loads(connection.execute("SELECT content FROM lf_blobs WHERE blob_key='mail_notify_state'").fetchone()[0])
        self.assertEqual("smtp-accepted", mail["UrgentOutbox"]["intent-accepted"]["Recipients"]["a@example.test"])
        self.assertEqual("pending", mail["UrgentOutbox"]["intent-pending"]["Recipients"]["b@example.test"])
        connection.close()
        state, _ = rollback.verify_restore_root(self.root, COMMIT)
        self.assertEqual(sha(delta_path), json.loads((self.root / "delta-reconciliation.json").read_text())["deltaManifestSha256"])
        self.assertEqual(0, rollback.main(["release", "--restore-root", str(self.root), "--expected-target-commit", COMMIT]))
        state, _ = rollback.verify_restore_root(self.root, COMMIT)
        self.assertEqual("released", state["releaseDecision"]["dataRecovery"])
        self.assertEqual("dedup-reviewed-but-disabled", state["releaseDecision"]["mailDispatch"])
        resumed = delta.reconcile(self.root, COMMIT, delta_path, sha(delta_path))
        self.assertEqual("reconciled-isolated", resumed["result"])
        connection = sqlite3.connect(self.root / "data" / "Db" / "logforesight.db")
        self.assertEqual(0, connection.execute("SELECT COUNT(*) FROM lf_work_order_events WHERE event_id=22").fetchone()[0])
        connection.close()

    def test_human_reply_event_is_held_without_coordinator_side_effect_replay(self):
        connection = sqlite3.connect(self.current)
        connection.execute("INSERT INTO lf_work_order_events VALUES(22,12,'reply','operator','reviewed')")
        connection.commit(); connection.close()
        delta_path = self.base / "reply-held.json"
        delta.capture_delta(self.bundle["database"], self.current, delta_path)
        entry = json.loads(delta_path.read_text(encoding="utf-8"))["items"][0]
        self.assertEqual("humanReply", entry["category"])
        self.assertFalse(entry["replayable"])
        self._clear_preservation()
        rollback.prepare(prepare_args(self.bundle, self.root))
        result = delta.reconcile(self.root, COMMIT, delta_path, sha(delta_path))
        self.assertFalse(result["dataRecoveryEligible"])
        connection = sqlite3.connect(self.root / "data" / "Db" / "logforesight.db")
        self.assertEqual(0, connection.execute("SELECT COUNT(*) FROM lf_work_order_events WHERE event_id=22").fetchone()[0])
        connection.close()

    def test_native_reply_graph_rejects_stale_closed_at_on_nonclosed_case(self):
        occurred = "2026-10-14T03:04:05.000000"
        items = [
            {"category": "humanReply", "table": "lf_work_order_events", "id": "22", "before": None,
             "after": {"event_id": 22, "work_order_id": 12, "action": "replied", "member_delta": 0,
                       "actor_id": 7, "actor_account": "operator", "created_at": occurred,
                       "note": "in_progress：reviewed（1 台）"}},
            {"category": "pendingTask", "table": "lf_work_orders", "id": "12",
             "before": {"work_order_id": 12, "closed_at": None, "closed_reason": None,
                        "last_reply_at": None, "updated_at": occurred},
             "after": {"work_order_id": 12, "closed_at": None, "closed_reason": None,
                       "last_reply_at": occurred, "updated_at": occurred}},
            {"category": "case", "table": "lf_issue_cases", "id": "case-1",
             "before": {"case_id": "case-1", "work_order_id": 12, "closed_at": None,
                        "status": "open", "note": None, "due_date": None, "updated_at": occurred,
                        "day_sync_pending": False, "day_sync_intent": None,
                        "host_name": "host-a", "issue_key": "issue-a"},
             "after": {"case_id": "case-1", "work_order_id": 12, "closed_at": "2026-10-13T00:00:00",
                       "status": "in_progress", "note": "reviewed", "due_date": None,
                       "updated_at": occurred, "day_sync_pending": False, "day_sync_intent": None,
                       "host_name": "host-a", "issue_key": "issue-a"}},
        ]

        self.assertEqual(set(), delta._native_reply_graph_items(items, self.bundle["database"]))

    def test_sqlite_local_timestamp_matches_only_same_local_wall_time(self):
        sqlite_local = datetime.now().replace(microsecond=0)
        serialized_local = sqlite_local.astimezone().isoformat()

        self.assertTrue(delta._same_sqlite_local_datetime(serialized_local, sqlite_local.isoformat()))
        self.assertFalse(delta._same_sqlite_local_datetime(serialized_local, (sqlite_local + timedelta(seconds=1)).isoformat()))

    def test_datetime_comparison_preserves_dotnet_100ns_ticks_and_normalizes_offsets(self):
        self.assertFalse(delta._same_datetime(
            "2026-10-15T09:10:31.6555971+08:00",
            "2026-10-15T09:10:31.6555972+08:00"))
        self.assertTrue(delta._same_datetime(
            "2026-10-15T09:10:31.6555972+08:00",
            "2026-10-15T01:10:31.6555972Z"))
        # .NET DateTime stores seven fractional digits (100ns); finer unknown precision fails closed.
        self.assertFalse(delta._same_datetime(
            "2026-10-15T09:10:31.65559720+08:00",
            "2026-10-15T09:10:31.65559720+08:00"))

    def test_sqlite_local_bridge_preserves_the_seventh_fractional_digit(self):
        local_base = datetime.now().replace(microsecond=0)
        aware = local_base.astimezone()
        offset = aware.strftime("%z")
        zone = f"{offset[:3]}:{offset[3:]}"
        prefix = local_base.strftime("%Y-%m-%dT%H:%M:%S")

        self.assertTrue(delta._same_sqlite_local_datetime(
            f"{prefix}.1234567{zone}", f"{prefix.replace('T', ' ')}.1234567"))
        self.assertFalse(delta._same_sqlite_local_datetime(
            f"{prefix}.1234567{zone}", f"{prefix.replace('T', ' ')}.1234568"))

    def test_native_pending_reply_uses_private_small_threshold_and_local_intent_clock(self):
        database = self.base / "pending-reply-base.db"
        _, intent, items = native_pending_reply_fixture(database)
        expected = {
            ("pendingTask", "lf_work_orders", "12"),
            ("case", "lf_issue_cases", "case-1"),
            ("humanReply", "lf_work_order_events", "22"),
        }
        self.assertEqual(expected, delta._native_reply_graph_items(items, database, inline_row_limit=2))
        self.assertEqual(set(), delta._native_reply_graph_items(items, database))

        bad_tick = json.loads(json.dumps(items))
        bad_intent = json.loads(bad_tick[1]["after"]["day_sync_intent"])
        bad_intent["OccurredAt"] = bad_intent["OccurredAt"].replace(".1234567", ".1234568")
        bad_tick[1]["after"]["day_sync_intent"] = json.dumps(bad_intent, separators=(",", ":"))
        self.assertEqual(set(), delta._native_reply_graph_items(bad_tick, database, inline_row_limit=2))

        bad_actor = json.loads(json.dumps(items))
        bad_intent = json.loads(bad_actor[1]["after"]["day_sync_intent"])
        bad_intent["ActorId"] = 8
        bad_actor[1]["after"]["day_sync_intent"] = json.dumps(bad_intent, separators=(",", ":"))
        self.assertEqual(set(), delta._native_reply_graph_items(bad_actor, database, inline_row_limit=2))

        self.assertTrue(delta._same_native_actor(7, "handler", 7, "handler"))
        self.assertFalse(delta._same_native_actor(7, "handler", 8, "handler"))
        self.assertFalse(delta._same_native_actor(True, "handler", 1, "handler"))
        self.assertFalse(delta._same_native_actor(7, "handler", 7, "other"))

    def test_unknown_smtp_outcome_keeps_mail_hold_but_data_lane_releasable(self):
        _, result = self._capture_prepare_reconcile(unknown_mail=True)
        self.assertTrue(result["dataRecoveryEligible"])
        self.assertFalse(result["mailReleaseEligible"])
        receipt = json.loads((self.root / "delta-reconciliation.json").read_text(encoding="utf-8"))
        self.assertTrue(any("unknown-recipient-outcome" in item for item in receipt["mailUnknownOutcomes"]))
        self.assertEqual(0, rollback.main(["release", "--restore-root", str(self.root), "--expected-target-commit", COMMIT]))
        state, _ = rollback.verify_restore_root(self.root, COMMIT)
        self.assertEqual("held", state["releaseDecision"]["mailDispatch"])

    def test_preexisting_unknown_smtp_without_mail_delta_still_keeps_mail_hold(self):
        unknown_state = {"UrgentOutbox": {"intent-unknown": {
            "Key": "intent-unknown", "Status": "sending-result-unknown",
            "Recipients": {"c@example.test": "sending-result-unknown"}, "SmtpAcceptedAtUtc": {}}}}
        connection = sqlite3.connect(self.bundle["database"])
        connection.execute("UPDATE lf_blobs SET content=? WHERE blob_key='mail_notify_state'", (json.dumps(unknown_state, separators=(",", ":")),))
        connection.commit(); connection.close()
        repin_full(self.bundle)
        shutil.copy2(self.bundle["database"], self.current)
        delta_path = self.base / "no-mail-delta.json"
        delta.capture_delta(self.bundle["database"], self.current, delta_path)
        self.assertEqual([], json.loads(delta_path.read_text(encoding="utf-8"))["items"])
        self._clear_preservation()
        rollback.prepare(prepare_args(self.bundle, self.root))
        result = delta.reconcile(self.root, COMMIT, delta_path, sha(delta_path))
        self.assertTrue(result["dataRecoveryEligible"])
        self.assertFalse(result["mailReleaseEligible"])
        self.assertEqual(0, rollback.main(["release", "--restore-root", str(self.root), "--expected-target-commit", COMMIT]))
        state, _ = rollback.verify_restore_root(self.root, COMMIT)
        self.assertEqual("held", state["releaseDecision"]["mailDispatch"])

    def test_nonempty_new_contribution_ledgers_preserve_raw_state_and_hold_only_mail(self):
        state = {
            "UrgentOutbox": {},
            "AcceptedMailContributionReceipts": {"disk|recipient-a|host-1|2026-10-14": "2026-10-14T01:00:00Z"},
            "HeldLegacyMailContributionReceipts": {"disk|recipient-b|host-2|2026-10-13": "2026-10-13T01:00:00Z"},
            "MailContributionReservations": {"opaque-key": {"ReservationId": "owned-attempt", "Status": "unknown"}},
        }
        raw = json.dumps(state, separators=(",", ":"))
        connection = sqlite3.connect(self.current)
        connection.execute("UPDATE lf_blobs SET content=? WHERE blob_key='mail_notify_state'", (raw,))
        connection.commit(); connection.close()
        delta_path = self.base / "opaque-contribution-ledgers.json"
        delta.capture_delta(self.bundle["database"], self.current, delta_path)
        self._clear_preservation()
        rollback.prepare(prepare_args(self.bundle, self.root))

        result = delta.reconcile(self.root, COMMIT, delta_path, sha(delta_path))

        self.assertTrue(result["dataRecoveryEligible"])
        self.assertFalse(result["mailReleaseEligible"])
        reconciliation = json.loads((self.root / "delta-reconciliation.json").read_text(encoding="utf-8"))
        self.assertTrue(any("AcceptedMailContributionReceipts:unsupported-legacy-ledger" in item
                            for item in reconciliation["mailUnknownOutcomes"]))
        self.assertTrue(any("HeldLegacyMailContributionReceipts:unsupported-legacy-ledger" in item
                            for item in reconciliation["mailUnknownOutcomes"]))
        self.assertTrue(any("MailContributionReservations:unsupported-legacy-ledger" in item
                            for item in reconciliation["mailUnknownOutcomes"]))
        restored_db = self.root / "data" / "Db" / "logforesight.db"
        backup_db = self.root / "reconcile-backup" / "logforesight.after-reconcile.db"
        for database in (restored_db, backup_db):
            connection = sqlite3.connect(database)
            try:
                persisted = connection.execute(
                    "SELECT content FROM lf_blobs WHERE blob_key='mail_notify_state'").fetchone()[0]
            finally:
                connection.close()
            self.assertEqual(raw, persisted)

        self.assertEqual(0, rollback.main(["release", "--restore-root", str(self.root),
                                           "--expected-target-commit", COMMIT]))
        receipt, _ = rollback.verify_restore_root(self.root, COMMIT)
        self.assertEqual("released", receipt["releaseDecision"]["dataRecovery"])
        self.assertEqual("held", receipt["releaseDecision"]["mailDispatch"])

    def test_previously_accepted_recipient_identity_and_timestamp_cannot_change(self):
        connection = sqlite3.connect(self.bundle["database"])
        old = {"UrgentOutbox": {"intent-accepted": {"Key": "intent-accepted", "Status": "smtp-accepted",
              "Recipients": {"a@example.test": "smtp-accepted"}, "SmtpAcceptedAtUtc": {"a@example.test": "2026-10-14T00:30:00Z"}}}}
        connection.execute("UPDATE lf_blobs SET content=? WHERE blob_key='mail_notify_state'", (json.dumps(old, separators=(",", ":")),))
        connection.commit(); connection.close()
        repin_full(self.bundle)
        shutil.copy2(self.bundle["database"], self.current)
        self._mutate_current()
        delta_path = self.base / "accepted-regression.json"
        delta.capture_delta(self.bundle["database"], self.current, delta_path)
        self._clear_preservation()
        rollback.prepare(prepare_args(self.bundle, self.root))
        with self.assertRaisesRegex(rollback.Refusal, "discard a previously accepted"):
            delta.reconcile(self.root, COMMIT, delta_path, sha(delta_path))

    def test_delta_manifest_pin_and_compare_and_swap_conflict_refuse(self):
        self._mutate_current()
        delta_path = self.base / "delta.json"
        delta.capture_delta(self.bundle["database"], self.current, delta_path)
        self._clear_preservation()
        rollback.prepare(prepare_args(self.bundle, self.root))
        with self.assertRaisesRegex(rollback.Refusal, "operator-pinned"):
            delta.reconcile(self.root, COMMIT, delta_path, "0" * 64)
        database = self.root / "data" / "Db" / "logforesight.db"
        connection = sqlite3.connect(database)
        connection.execute("UPDATE lf_work_orders SET note='foreign edit' WHERE work_order_id=12")
        connection.commit(); connection.close()
        with self.assertRaisesRegex(rollback.Refusal, "changed after preparation"):
            delta.reconcile(self.root, COMMIT, delta_path, sha(delta_path))
        self.assertFalse((self.root / "delta-reconciliation.json").exists())

    def test_delta_from_different_host_timezone_authority_is_refused(self):
        self._mutate_current()
        delta_path = self.base / "foreign-clock-delta.json"
        delta.capture_delta(self.bundle["database"], self.current, delta_path)
        manifest = json.loads(delta_path.read_text(encoding="utf-8"))
        manifest["clockAuthority"]["timezoneId"] = "Foreign/Timezone"
        delta_path.write_text(json.dumps(manifest, ensure_ascii=False, sort_keys=True, separators=(",", ":")) + "\n", encoding="utf-8")
        self._clear_preservation()
        rollback.prepare(prepare_args(self.bundle, self.root))

        with self.assertRaisesRegex(rollback.Refusal, "same host and timezone offset"):
            delta.reconcile(self.root, COMMIT, delta_path, sha(delta_path))
        self.assertFalse((self.root / "delta-reconciliation.json").exists())

    def test_unknown_table_or_column_delta_is_preserved_held_and_blocks_data_release(self):
        connection = sqlite3.connect(self.current)
        connection.execute("ALTER TABLE lf_issue_cases ADD COLUMN new_authority TEXT")
        connection.execute("INSERT INTO lf_issue_cases(case_id,host_name,issue_key,status,note,new_authority) VALUES('case-1','host-a','cpu','open','new case','current-only')")
        connection.commit(); connection.close()
        delta_path = self.base / "incompatible-delta.json"
        delta.capture_delta(self.bundle["database"], self.current, delta_path)
        delta_value = json.loads(delta_path.read_text(encoding="utf-8"))
        self.assertTrue(any(row["category"] == "case" and row["replayable"] is False for row in delta_value["items"]))
        self._clear_preservation()
        rollback.prepare(prepare_args(self.bundle, self.root))
        result = delta.reconcile(self.root, COMMIT, delta_path, sha(delta_path))
        self.assertFalse(result["dataRecoveryEligible"])
        self.assertEqual(2, rollback.main(["release", "--restore-root", str(self.root), "--expected-target-commit", COMMIT]))

    def test_case_and_mutable_pending_task_remain_held_without_native_consumer(self):
        connection = sqlite3.connect(self.current)
        connection.execute("INSERT INTO lf_issue_cases VALUES('case-2','host-b','memory','open','coordinator-owned state')")
        connection.execute("INSERT INTO lf_work_orders VALUES(13,'memory',NULL,'new mutable task')")
        connection.execute("CREATE TABLE lf_prtg_scope_purge_jobs(job_id TEXT PRIMARY KEY,status TEXT NOT NULL,scope_fence_json TEXT)")
        connection.execute("INSERT INTO lf_prtg_scope_purge_jobs VALUES('job-1','paused','{}')")
        connection.commit(); connection.close()
        delta_path = self.base / "business-holds.json"
        delta.capture_delta(self.bundle["database"], self.current, delta_path)
        delta_value = json.loads(delta_path.read_text(encoding="utf-8"))
        self.assertTrue(all(row["replayable"] is False for row in delta_value["items"]))
        self.assertTrue(any(row["table"] == "lf_prtg_scope_purge_jobs" and row["id"] == "job-1" for row in delta_value["items"]))
        self._clear_preservation()
        rollback.prepare(prepare_args(self.bundle, self.root))
        result = delta.reconcile(self.root, COMMIT, delta_path, sha(delta_path))
        self.assertFalse(result["dataRecoveryEligible"])
        connection = sqlite3.connect(self.root / "data" / "Db" / "logforesight.db")
        self.assertEqual(0, connection.execute("SELECT COUNT(*) FROM lf_issue_cases WHERE case_id='case-2'").fetchone()[0])
        self.assertEqual(0, connection.execute("SELECT COUNT(*) FROM lf_work_orders WHERE work_order_id=13").fetchone()[0])
        connection.close()

    def test_public_cli_delta_consumer_and_release_gate(self):
        self._mutate_current()
        delta_path = self.base / "cli-delta.json"
        self._clear_preservation()
        captured = subprocess.run([sys.executable, str(rollback.__file__), "capture-delta", "--before-db", str(self.bundle["database"]),
                                   "--current-db", str(self.current), "--output", str(delta_path)], capture_output=True, text=True)
        self.assertEqual(0, captured.returncode, captured.stderr)
        capture_result = json.loads(captured.stdout)
        self.assertEqual("delta-captured", capture_result["result"])
        prepared = subprocess.run([sys.executable, str(rollback.__file__), "prepare", "--manifest", str(self.bundle["manifest"]),
                                   "--expected-target-commit", COMMIT, "--expected-authority-sha256", sha(self.bundle["authority"]),
                                   "--restore-root", str(self.root)], capture_output=True, text=True)
        self.assertEqual(0, prepared.returncode, prepared.stderr)
        reconciled = subprocess.run([sys.executable, str(rollback.__file__), "reconcile", "--restore-root", str(self.root),
                                     "--expected-target-commit", COMMIT, "--delta-manifest", str(delta_path),
                                     "--expected-delta-sha256", sha(delta_path)], capture_output=True, text=True)
        self.assertEqual(0, reconciled.returncode, reconciled.stderr)
        self.assertEqual("reconciled-isolated", json.loads(reconciled.stdout)["result"])
        released = subprocess.run([sys.executable, str(rollback.__file__), "release", "--restore-root", str(self.root),
                                   "--expected-target-commit", COMMIT], capture_output=True, text=True)
        self.assertEqual(0, released.returncode, released.stderr)
        self.assertEqual("controlled-restore-released", json.loads(released.stdout)["result"])
        verified = subprocess.run([sys.executable, str(rollback.__file__), "verify", "--restore-root", str(self.root),
                                   "--expected-target-commit", COMMIT], capture_output=True, text=True)
        self.assertEqual(0, verified.returncode, verified.stderr)
        self.assertEqual("verified-closed", json.loads(verified.stdout)["result"])

    def test_capture_delta_cli_refuses_existing_output_without_changing_inputs(self):
        output = self.base / "existing-owned-output.json"
        original_output = b"owned output must remain byte-identical\n"
        output.write_bytes(original_output)
        before_hash = sha(self.bundle["database"])
        current_hash = sha(self.current)

        rejected = subprocess.run([
            sys.executable, str(rollback.__file__), "capture-delta",
            "--before-db", str(self.bundle["database"]),
            "--current-db", str(self.current),
            "--output", str(output),
        ], capture_output=True, text=True, check=False)

        self.assertEqual(2, rejected.returncode, rejected.stdout + rejected.stderr)
        receipt = json.loads(rejected.stderr)
        self.assertEqual("refused-closed", receipt["result"])
        self.assertIn("delta output must be a new path", receipt["reason"])
        self.assertEqual(original_output, output.read_bytes())
        self.assertEqual(before_hash, sha(self.bundle["database"]))
        self.assertEqual(current_hash, sha(self.current))

    def test_capture_delta_cli_refuses_wrong_sqlite_identity_as_structured_result(self):
        wrong_current = self.base / "wrong-application-id.db"
        shutil.copy2(self.current, wrong_current)
        connection = sqlite3.connect(wrong_current)
        connection.execute("PRAGMA application_id=1279679567")
        connection.commit(); connection.close()
        output = self.base / "should-not-be-created.json"
        before_hash = sha(self.bundle["database"])
        current_hash = sha(self.current)
        wrong_hash = sha(wrong_current)

        rejected = subprocess.run([
            sys.executable, str(rollback.__file__), "capture-delta",
            "--before-db", str(self.bundle["database"]),
            "--current-db", str(wrong_current),
            "--output", str(output),
        ], capture_output=True, text=True, check=False)

        self.assertEqual(2, rejected.returncode, rejected.stdout + rejected.stderr)
        receipt = json.loads(rejected.stderr)
        self.assertEqual("refused-closed", receipt["result"])
        self.assertIn("SQLite application identity differs", receipt["reason"])
        self.assertFalse(output.exists())
        self.assertEqual(before_hash, sha(self.bundle["database"]))
        self.assertEqual(current_hash, sha(self.current))
        self.assertEqual(wrong_hash, sha(wrong_current))

    def test_unmatched_original_preservation_items_block_release(self):
        self._mutate_current()
        delta_path = self.base / "delta-without-original-identities.json"
        delta.capture_delta(self.bundle["database"], self.current, delta_path)
        rollback.prepare(prepare_args(self.bundle, self.root))
        delta.reconcile(self.root, COMMIT, delta_path, sha(delta_path))
        result = rollback.main(["release", "--restore-root", str(self.root), "--expected-target-commit", COMMIT])
        self.assertEqual(2, result)


if __name__ == "__main__":
    unittest.main()
