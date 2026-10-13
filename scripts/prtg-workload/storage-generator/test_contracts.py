"""Bounded isolated storage-tool contracts. Usage: python test_contracts.py <built DLL>.

Requires Python 3 and dotnet. Does not connect to SQL Server or PRTG. Results are
functional evidence only, not N6 capacity, provider, recovery or retention passes.
"""
import json
from contextlib import closing
import os
from pathlib import Path
import shutil
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
                                  capture_output=True, text=True, timeout=180)

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

        # A real process boundary: the first process commits one production batch,
        # writes its owned DB marker and sealed checkpoint, exits, then a new
        # process resumes the remaining sensor-major prefix through MergeSampledValues.
        interrupted = home / "interrupted-owned-run"
        start = run(["--mode", "n6", "--provider", "sqlite", "--root", str(interrupted),
                     "--rows", "205", "--simulate-crash-after-db-commit"])
        assert start.returncode == 76, start.stdout + start.stderr
        manifest = json.loads((interrupted / "n6-run-manifest.json").read_text(encoding="utf-8"))
        checkpoint = json.loads((interrupted / "n6-checkpoint.json").read_text(encoding="utf-8"))
        assert manifest["requestedRows"] == 205 and checkpoint["committedRows"] == 0
        # Wrong scope and changed source/checkpoint are each tested against the
        # same untouched partial run, restoring only the external JSON after a
        # refusal. No database is fabricated or edited by these negatives.
        refusal = run(["--mode", "n6", "--provider", "sqlite", "--root", str(interrupted), "--rows", "206", "--resume"])
        assert refusal.returncode == 2

        source_manifest_path = interrupted / "n6-run-manifest.json"
        source_manifest = json.loads(source_manifest_path.read_text(encoding="utf-8"))
        source_manifest["sourceFingerprint"] = "0" * 64
        source_manifest_path.write_text(json.dumps(source_manifest), encoding="utf-8")
        refusal = run(["--mode", "n6", "--provider", "sqlite", "--root", str(interrupted), "--rows", "205", "--resume"])
        assert refusal.returncode == 2
        source_manifest["sourceFingerprint"] = manifest["sourceFingerprint"]
        source_manifest_path.write_text(json.dumps(source_manifest), encoding="utf-8")

        checkpoint_path = interrupted / "n6-checkpoint.json"
        changed = json.loads(checkpoint_path.read_text(encoding="utf-8"))
        changed["committedRows"] = 200
        checkpoint_path.write_text(json.dumps(changed), encoding="utf-8")
        refusal = run(["--mode", "n6", "--provider", "sqlite", "--root", str(interrupted), "--rows", "205", "--resume"])
        assert refusal.returncode == 2
        checkpoint_path.write_text(json.dumps(checkpoint), encoding="utf-8")

        foreign = home / "foreign-db"
        result = run(["--mode", "n6", "--provider", "sqlite", "--root", str(foreign),
                      "--rows", "205", "--simulate-crash-after-db-commit"])
        assert result.returncode == 76, result.stdout + result.stderr
        shutil.copy2(next(interrupted.glob("n6-sqlite-*.db")), next(foreign.glob("n6-sqlite-*.db")))
        refusal = run(["--mode", "n6", "--provider", "sqlite", "--root", str(foreign), "--rows", "205", "--resume"])
        assert refusal.returncode == 2

        resumed = run(["--mode", "n6", "--provider", "sqlite", "--root", str(interrupted),
                       "--rows", "205", "--resume"])
        assert resumed.returncode == 0, resumed.stdout + resumed.stderr
        continued = json.loads((interrupted / "n6-high-water-result.json").read_text(encoding="utf-8"))
        assert continued["rowsCommitted"] == continued["persistedRows"] == 205
        assert continued["interruptedBetweenProcesses"] is True and continued["resumeCount"] == 1
        assert continued["capacityAccepted"] is False and continued["fullWorkloadAccepted"] is False
        assert continued["currentProcessActiveSeconds"] > 0
        assert continued["accumulatedOwnedActiveSeconds"] >= continued["currentProcessActiveSeconds"]
        assert continued["totalElapsedSecondsComplete"] is True
        assert continued["elapsedSeconds"] >= continued["currentProcessActiveSeconds"]
        assert abs(continued["rowsPerSecond"] - 205 / continued["elapsedSeconds"]) < 1e-9
        proof = continued["typedProofUtf8Bytes"]
        assert proof["scope"] == "rows generated in current process only"
        # Recovery audits the committed 100-row batch that was ahead of the
        # ownership marker, so the resumed process generates the remaining 105.
        assert proof["rows"] == 105
        assert proof["average"] == proof["total"] / proof["rows"]
        resumed_db = Path(continued["databasePath"])
        with closing(sqlite3.connect(f"{resumed_db.as_uri()}?mode=ro", uri=True)) as connection:
            assert connection.execute("SELECT count(*) FROM lf_prtg_values").fetchone()[0] == 205
            assert connection.execute("SELECT count(*) FROM (SELECT sensor_objid, period_start FROM lf_prtg_values GROUP BY sensor_objid, period_start)").fetchone()[0] == 205
            assert connection.execute("SELECT min(id), max(id) FROM lf_prtg_values").fetchone() == (1, 205)
        print("PASS: bounded storage, process-boundary resume with exact 205-row sequence, wrong scope/source/database/checkpoint refusals; capacityAccepted=false")


if __name__ == "__main__":
    main()
