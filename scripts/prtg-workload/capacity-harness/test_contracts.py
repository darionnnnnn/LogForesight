#!/usr/bin/env python3
"""Fail-closed contract tests for the isolated E07 capacity-preparation executable."""

import hashlib
import json
import os
import pathlib
import shutil
import sqlite3
import subprocess
import tempfile
import unittest


PROJECT = pathlib.Path(__file__).resolve().parent
DLL = PROJECT / "bin" / "Debug" / "net8.0-windows" / "CapacityHarness.dll"
FIXED_ROWS = 64_800_000


class PreparationContracts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if not DLL.is_file():
            raise unittest.SkipTest("Build capacity-harness/CapacityHarness.csproj before running these contracts.")

    def invoke(self, *arguments):
        return subprocess.run(
            ["dotnet", str(DLL), *map(str, arguments)],
            cwd=PROJECT,
            capture_output=True,
            text=True,
            timeout=180,
            check=False,
        )

    def test_small_selftest_is_labeled_and_cannot_claim_e07(self):
        with tempfile.TemporaryDirectory(prefix="lf-e07-self-test-") as temporary:
            root = pathlib.Path(temporary) / "owned-db"
            result = self.invoke("--self-test-root", root)
            self.assertEqual(0, result.returncode, result.stderr)
            report = json.loads((root / "self-test-receipt.json").read_text(encoding="utf-8"))
            self.assertEqual("self-test-only-not-e07-evidence", report["status"])
            self.assertEqual(3, report["hosts"])
            self.assertFalse(report["fullShape"])
            self.assertEqual("passed", report["mapDateBoundaryCheck"])
            self.assertRegex(report["sourceHostMapDateForBoundaryCheck"], r"^\d{4}-\d{2}-\d{2}$")
            self.assertEqual("absent", report["qualificationJobState"])
            for flag in ("nativeSourceVerified", "fullWorkloadAccepted", "capacityAccepted",
                         "retentionAccepted", "recoveryAccepted", "wholeRoundAccepted"):
                self.assertIs(report[flag], False)
            self.assertEqual(0, report["nativeQualificationProofs"])
            self.assertNotIn("proofReferences", report)
            output = report["output"]
            self.assertEqual(str(root.resolve()), output["dataRoot"])
            self.assertEqual(str((root / "Db" / "self-test.db").resolve()), output["sqlitePath"])
            self.assertEqual(str((root / "start-development-web.ps1").resolve()), output["launchInstructions"])
            self.assertTrue(pathlib.Path(output["sqlitePath"]).is_file())
            launch = pathlib.Path(output["launchInstructions"]).read_text(encoding="utf-8")
            self.assertIn(output["sqlitePath"], launch)
            self.assertIn(output["dataRoot"], launch)
            self.assertNotIn(".stage-", json.dumps(output) + launch)

    def test_requires_explicit_quiescence_acknowledgement_before_source_access(self):
        with tempfile.TemporaryDirectory(prefix="lf-e07-quiescence-") as temporary:
            root = pathlib.Path(temporary)
            source = root / "missing-generator-root"
            output = root / "new-output"
            result = self.invoke("--generator-root", source, "--output-root", output)
            self.assertNotEqual(0, result.returncode)
            self.assertIn("confirm-source-quiescent", result.stderr)
            self.assertFalse(output.exists())
            self.assertFalse(source.exists())

    def test_fake_full_shape_receipt_cannot_promote_an_incomplete_database(self):
        with tempfile.TemporaryDirectory(prefix="lf-e07-fake-source-") as temporary:
            root = pathlib.Path(temporary)
            source = root / "generator"
            source.mkdir()
            seed_root = root / "owned-schema-seed"
            seed_result = self.invoke("--self-test-root", seed_root)
            self.assertEqual(0, seed_result.returncode, seed_result.stderr)
            db = source / "n6-sqlite-fake.db"
            shutil.copy2(seed_root / "Db" / "self-test.db", db)
            connection = sqlite3.connect(db)
            try:
                connection.execute("DELETE FROM lf_blobs WHERE blob_key = 'hosts'")
                connection.commit()
            finally:
                connection.close()
            receipt = {
                "schema": "LogForesight.N6GeneratedHighWaterResult",
                "provider": "sqlite",
                "input": {"hosts": 3000, "sensors": 15000, "days": 180,
                          "rowsPerSensor": 4320, "requestedRows": FIXED_ROWS},
                "databasePath": str(db),
                "rowsCommitted": FIXED_ROWS,
                "persistedRows": FIXED_ROWS,
                "fullFixedShape": True,
                "typedSlotsPerRow": 4,
                "trustVersionOneRows": FIXED_ROWS,
                "fullWorkloadAccepted": False,
                "capacityAccepted": False,
                "retentionAccepted": False,
                "recoveryAccepted": False,
                "wholeRoundAccepted": False,
            }
            receipt_path = source / "n6-high-water-result.json"
            receipt_path.write_text(json.dumps(receipt), encoding="utf-8")
            source_files = [db, pathlib.Path(str(db) + "-wal"), pathlib.Path(str(db) + "-shm")]
            input_hashes = {path.name: hashlib.sha256(path.read_bytes()).hexdigest()
                            for path in source_files if path.exists()}
            output = root / "prepared"
            result = self.invoke("--generator-root", source, "--output-root", output,
                                 "--confirm-source-quiescent")
            self.assertNotEqual(0, result.returncode)
            self.assertIn("Copied SQLite shape differs", result.stderr)
            self.assertFalse(output.exists())
            self.assertEqual(input_hashes, {path.name: hashlib.sha256(path.read_bytes()).hexdigest()
                                           for path in source_files if path.exists()})

if __name__ == "__main__":
    unittest.main(verbosity=2)
