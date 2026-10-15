import json
import hashlib
import sqlite3
import tempfile
import unittest
from pathlib import Path

from e2_preflight import _manifest_hash, validate_source


class E2PreflightContractTests(unittest.TestCase):
    def test_manifest_checksum_matches_system_text_json_windows_bytes(self):
        manifest = {
            "schema": "LogForesight.N6StorageRunManifest", "schemaVersion": 1,
            "runId": "test-run", "rootPath": r"C:\owned\root",
            "databasePath": r"C:\owned\root\store.db", "provider": "sqlite",
            "hosts": 1, "sensors": 2, "days": 3, "requestedRows": 6,
            "rowOrder": "sensor-major", "typedSlotsPerRow": 4,
            "sourceFingerprint": "ABC", "originalProvenance": "",
            "createdUtc": "2026-10-14T04:41:17.4981252+00:00", "runDate": "2026-10-14",
            "originalProgressElapsedSeconds": 1.0176521,
        }
        self.assertEqual(
            "591BEA53D70D12307A912C29BB6133AE3DE708F6D5D9FB36DE62401B4C79B6C1",
            _manifest_hash(manifest),
        )

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="e2-contract-")
        self.root = Path(self.temp.name) / "owned"
        self.root.mkdir()
        self.db = self.root / "tiny.db"
        conn = sqlite3.connect(self.db)
        try:
            conn.executescript("""
                CREATE TABLE lf_prtg_devices(id INTEGER);
                CREATE TABLE lf_prtg_sensors(id INTEGER);
                CREATE TABLE lf_prtg_host_map(id INTEGER);
                CREATE TABLE lf_prtg_values(id INTEGER);
                CREATE TABLE lf_n6_generation_control(run_id TEXT, manifest_sha256 TEXT, committed_rows INTEGER);
            """)
            conn.executemany("INSERT INTO lf_prtg_devices VALUES(?)", [(1,), (2,)])
            conn.executemany("INSERT INTO lf_prtg_sensors VALUES(?)", [(11,), (12,)])
            conn.execute("INSERT INTO lf_prtg_host_map VALUES(1)")
            conn.executemany("INSERT INTO lf_prtg_values VALUES(?)", [(1,), (2,)])
            conn.commit()
        finally:
            conn.close()
        self.manifest = {
            "schema": "LogForesight.N6StorageRunManifest", "schemaVersion": 1,
            "rootPath": str(self.root), "databasePath": str(self.db), "provider": "sqlite",
            "runId": "run-a", "manifestSha256": "a" * 64, "requestedRows": 2,
            "hosts": 2, "sensors": 2, "days": 180, "typedSlotsPerRow": 4,
            "rowOrder": "sensor-major, one row per sensor-hour", "sourceFingerprint": "source",
            "originalProvenance": "generated-with-current-binary", "createdUtc": "2026-10-14T00:00:00+00:00",
            "runDate": "2026-10-14", "originalSourceFingerprint": "", "originalProgressSha256": "",
            "originalLogSha256": "", "originalProgressRows": 0, "originalAuditedRows": 0,
            "originalUnloggedTailRows": 0, "originalProgressElapsedSeconds": 0,
            "originalProgressSampledDbBytes": 0, "databaseBytesAtAdoption": 0, "walBytesAtAdoption": 0,
            "shmBytesAtAdoption": 0,
        }
        self.manifest["manifestSha256"] = _manifest_hash(self.manifest)
        conn = sqlite3.connect(self.db)
        try:
            conn.execute("INSERT INTO lf_n6_generation_control VALUES(?,?,?)", ("run-a", self.manifest["manifestSha256"], 2))
            conn.commit()
        finally:
            conn.close()
        self.result = {
            "schema": "LogForesight.N6GeneratedHighWaterResult", "schemaVersion": 2,
            "runId": "run-a", "manifestSha256": self.manifest["manifestSha256"], "persistedRows": 2,
            "rowsCommitted": 2, "fullFixedShape": True, "fullWorkloadAccepted": False,
            "capacityAccepted": False, "retentionAccepted": False, "recoveryAccepted": False,
            "wholeRoundAccepted": False,
        }
        self.write(self.root / "n6-run-manifest.json", self.manifest)
        self.write(self.root / "n6-high-water-result.json", self.result)
        self.descriptor = self.root / "provider.json"
        self.write(self.descriptor, {"schema": "e2-owned-provider-v1", "provider": "sqlite",
                                     "dataRoot": str(self.root), "databaseFile": str(self.db), "retentionDays": 180,
                                     "prtgUrl": "http://127.0.0.1:5101/"})

    def tearDown(self):
        self.temp.cleanup()

    @staticmethod
    def write(path, value):
        path.write_text(json.dumps(value), encoding="utf-8")

    def test_exact_owned_receipt_and_catalog_pass_read_only(self):
        before = self.db.read_bytes()
        counts = {"devices": 2, "sensors": 2, "hostMaps": 1, "values": 2}
        result = validate_source(self.root, self.descriptor, counts)
        self.assertEqual("complete-owned-source", result["status"])
        self.assertFalse(result["capacityAccepted"])
        self.assertEqual(hashlib.sha256(self.descriptor.read_bytes()).hexdigest().upper(), result["descriptorSha256"])
        self.assertEqual(64, len(result["sourceIdentitySha256"]))
        self.assertEqual(before, self.db.read_bytes())

    def test_source_identity_stays_stable_as_web_settings_and_bindings_grow_database(self):
        counts = {"devices": 2, "sensors": 2, "hostMaps": 1, "values": 2}
        first = validate_source(self.root, self.descriptor, counts)
        conn = sqlite3.connect(self.db)
        try:
            conn.execute("CREATE TABLE runtime_settings_and_bindings(payload BLOB)")
            conn.execute("INSERT INTO runtime_settings_and_bindings VALUES(?)", (b"x" * 4096,))
            conn.commit()
        finally:
            conn.close()
        second = validate_source(self.root, self.descriptor, counts)
        self.assertGreater(second["databaseBytes"], first["databaseBytes"])
        self.assertEqual(first["sourceIdentitySha256"], second["sourceIdentitySha256"])

    def test_partial_catalog_is_rejected(self):
        conn = sqlite3.connect(self.db)
        try:
            conn.execute("DELETE FROM lf_prtg_values WHERE id=2")
            conn.commit()
        finally:
            conn.close()
        with self.assertRaisesRegex(ValueError, "catalog counts"):
            validate_source(self.root, self.descriptor, {"devices": 2, "sensors": 2, "hostMaps": 1, "values": 2})

    def test_stale_or_promoted_receipt_is_rejected(self):
        self.result["manifestSha256"] = "b" * 64
        self.write(self.root / "n6-high-water-result.json", self.result)
        with self.assertRaisesRegex(ValueError, "exact manifest"):
            validate_source(self.root, self.descriptor, {"devices": 2, "sensors": 2, "hostMaps": 1, "values": 2})

        self.result["manifestSha256"] = self.manifest["manifestSha256"]
        self.result["capacityAccepted"] = True
        self.write(self.root / "n6-high-water-result.json", self.result)
        with self.assertRaisesRegex(ValueError, "non-promotion"):
            validate_source(self.root, self.descriptor, {"devices": 2, "sensors": 2, "hostMaps": 1, "values": 2})

    def test_pending_checkpoint_and_foreign_marker_are_rejected(self):
        (self.root / "n6-checkpoint.json").write_text("{}", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "checkpoint"):
            validate_source(self.root, self.descriptor, {"devices": 2, "sensors": 2, "hostMaps": 1, "values": 2})
        (self.root / "n6-checkpoint.json").unlink()
        conn = sqlite3.connect(self.db)
        try:
            conn.execute("UPDATE lf_n6_generation_control SET run_id='foreign'")
            conn.commit()
        finally:
            conn.close()
        with self.assertRaisesRegex(ValueError, "ownership marker"):
            validate_source(self.root, self.descriptor, {"devices": 2, "sensors": 2, "hostMaps": 1, "values": 2})


if __name__ == "__main__":
    unittest.main()
