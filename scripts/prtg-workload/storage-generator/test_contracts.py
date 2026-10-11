"""Bounded isolated storage-tool contracts. Usage: python test_contracts.py <built DLL>.

Requires Python 3 and dotnet. Does not connect to SQL Server or PRTG. Results are
functional evidence only, not N6 capacity, provider, recovery or retention passes.
"""
import json
from contextlib import closing
import os
from pathlib import Path
import sqlite3
import subprocess
import sys
import tempfile


def main():
    dll = Path(sys.argv[1]).resolve(strict=True)
    with tempfile.TemporaryDirectory(prefix="lf-storage-contracts-") as folder:
        home = Path(folder)

        def run(extra, connection=None):
            env = dict(os.environ)
            env.pop("LF_CAPACITY_SQLSERVER_CONNECTION", None)
            if connection:
                env["LF_CAPACITY_SQLSERVER_CONNECTION"] = connection
            return subprocess.run(["dotnet", str(dll), *extra], env=env,
                                  capture_output=True, text=True, timeout=60)

        prefix = ["--mode", "n6", "--root", str(home / "missing-rows")]
        invalid = [prefix, prefix + ["--rows", "0"], prefix + ["--rows", "64800001"],
                   prefix + ["--rows", "x"], prefix + ["--rows", "1", "--rows", "2"],
                   prefix + ["--rows", "1", "--unexpected", "1"],
                   prefix + ["--rows", "1", "--provider", "unknown"]]
        for args in invalid:
            result = run(args)
            assert result.returncode == 2, result.stdout + result.stderr
            assert not (home / "missing-rows").exists(), "Invalid input created an output directory"

        existing = home / "existing"
        existing.mkdir()
        sentinel = existing / "keep.txt"
        sentinel.write_text("preserve original data", encoding="utf-8")
        result = run(["--mode", "n6", "--root", str(existing), "--rows", "1"])
        assert result.returncode == 2
        assert sentinel.read_text(encoding="utf-8") == "preserve original data"
        assert list(existing.iterdir()) == [sentinel]

        # Numeric loopback only: aliases, named pipes, remote hosts and bad ports
        # are rejected before SQL Open. The marker must never reach either stream.
        marker = "credential-must-never-be-printed"
        for index, server in enumerate(["db.example.invalid", "localhost", ".", "np:localhost",
                                       "tcp:127.0.0.1,0", "tcp:[::1],65536"]):
            root = home / f"rejected-sql-{index}"
            result = run(["--mode", "n6", "--root", str(root), "--rows", "1", "--provider", "sqlserver"],
                         f"Server={server};User ID=test;Password={marker};TrustServerCertificate=True")
            assert result.returncode == 2
            assert marker not in result.stdout + result.stderr
            assert not (root / "n6-high-water-result.json").exists()
            assert not list(root.glob("*.db"))

        root = home / "bounded-positive"
        result = run(["--mode", "n6", "--root", str(root), "--rows", "4"])
        assert result.returncode == 0, result.stdout + result.stderr
        report = json.loads((root / "n6-high-water-result.json").read_text(encoding="utf-8"))
        assert report["devicesWritten"] == report["hostMapsWritten"] == 3000
        assert report["sensorsWritten"] == 15000
        assert report["rowsCommitted"] == report["persistedRows"] == report["trustVersionOneRows"] == 4
        assert report["typedSlotsPerRow"] == 4
        for field in ["fullFixedShape", "fullWorkloadAccepted", "capacityAccepted", "retentionAccepted",
                      "recoveryAccepted", "wholeRoundAccepted"]:
            assert report[field] is False
        database = Path(report["databasePath"])
        assert database.parent == root
        with closing(sqlite3.connect(f"{database.as_uri()}?mode=ro", uri=True)) as connection:
            rows = connection.execute('SELECT trust_version, trusted_proof, avg_value FROM lf_prtg_values').fetchall()
            assert len(rows) == 4
            for version, proof, average in rows:
                assert version == 1 and 0 <= average <= 100
                parsed = json.loads(proof)
                slots = parsed["s"]
                assert len(slots) == 4 and [slot["s"] for slot in slots] == [0, 1, 2, 3]
                assert len({slot["p"] for slot in slots}) == 4
                assert all(slot["v"] == average for slot in slots)
                assert parsed["sg"] == "capacity-synthetic-source-v1"
            assert sum(len(proof.encode("utf-8")) for _, proof, _ in rows) == report["typedProofUtf8Bytes"]["total"]
            assert connection.execute('SELECT count(*) FROM lf_prtg_devices').fetchone()[0] == 3000
            assert connection.execute('SELECT count(*) FROM lf_prtg_sensors').fetchone()[0] == 15000
            assert connection.execute('SELECT count(*) FROM lf_prtg_host_map').fetchone()[0] == 3000
        assert report["sqliteDbWalShmHighWaterBytes"] >= report["sqliteDatabaseBytes"] > 0
        print("PASS: 7 invalid inputs, existing-data preservation, 6 SQL pre-connect/redaction refusals, actual typed SQLite storage; capacityAccepted=false")


if __name__ == "__main__":
    main()
