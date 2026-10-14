"""Bounded isolated storage-tool contracts. Usage: python test_contracts.py <built DLL>.

Requires Python 3 and dotnet. Does not connect to SQL Server or PRTG. Results are
functional evidence only, not N6 capacity, provider, recovery or retention passes.
"""
import hashlib
import json
from contextlib import closing, contextmanager
import os
from pathlib import Path
import re
import shutil
import sqlite3
import subprocess
import sys
import tempfile
import time


@contextmanager
def retained_fixture(path):
    try:
        yield path
    except BaseException:
        print(f"E1 fixture retained after failure at {path}", file=sys.stderr)
        raise


def exercise_marker_without_checkpoint(home, approved_source, executable):
    """Use an explicit synthetic crash fixture without creating another seed."""
    source_db = next((home / "single-owned-seed").glob("n6-sqlite-*.db"))
    root = home / "init-marker-no-checkpoint"
    receipt_path = root / "e1-synthetic-crash-receipt.json"
    if root.exists():
        raise SystemExit(f"Refusing to overwrite retained initialization fixture: {root}")
    env = dict(os.environ)
    env.pop("LF_CAPACITY_SQLSERVER_CONNECTION", None)

    def run(args):
        return subprocess.run(["dotnet", str(executable), *args], cwd=executable.parent,
                              env=env, capture_output=True, text=True, encoding="utf-8",
                              errors="replace", timeout=180)

    manifest_crash = run(["--mode", "n6", "--root", str(root), "--rows", "205",
                          "--simulate-init-crash-after", "manifest"])
    assert manifest_crash.returncode == 76, manifest_crash.stdout + manifest_crash.stderr
    manifest_path = root / "n6-run-manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    database = Path(manifest["databasePath"])
    assert database.parent == root and not database.exists()
    shutil.copy2(source_db, database)

    def structure_hashes(path):
        hashes = {}
        with closing(sqlite3.connect(f"file:{path}?mode=ro", uri=True)) as connection:
            for table, order in (("lf_prtg_devices", "objid"), ("lf_prtg_sensors", "objid"),
                                 ("lf_prtg_host_map", "device_objid")):
                rows = connection.execute(f"SELECT * FROM {table} ORDER BY {order}").fetchall()
                raw = json.dumps(rows, separators=(",", ":"), ensure_ascii=False, default=str).encode("utf-8")
                hashes[table] = hashlib.sha256(raw).hexdigest().upper()
        return hashes

    source_hashes = structure_hashes(database)
    with closing(sqlite3.connect(database)) as connection:
        connection.execute("BEGIN IMMEDIATE")
        prior_sequence = connection.execute("SELECT seq FROM sqlite_sequence WHERE name='lf_prtg_values'").fetchall()
        deleted = connection.execute("DELETE FROM lf_prtg_values").rowcount
        connection.execute("DELETE FROM sqlite_sequence WHERE name='lf_prtg_values'")
        changed = connection.execute(
            "UPDATE lf_n6_generation_control SET run_id=?, manifest_sha256=?, committed_rows=0",
            (manifest["runId"], manifest["manifestSha256"])).rowcount
        connection.commit()
    assert deleted == 205 and changed == 1
    with closing(sqlite3.connect(f"file:{database}?mode=ro", uri=True)) as connection:
        marker = connection.execute("SELECT run_id, manifest_sha256, committed_rows FROM lf_n6_generation_control").fetchall()
        assert marker == [(manifest["runId"], manifest["manifestSha256"], 0)]
        assert connection.execute("SELECT COUNT(*) FROM lf_prtg_values").fetchone()[0] == 0
    assert structure_hashes(database) == source_hashes
    receipt = {
        "kind": "synthetic bounded E1 interruption fixture; not historical incident evidence",
        "manifestCrashInjectedByTool": True,
        "nextWriteBoundarySimulatedInFixture": "owned database structure and marker committed; initial checkpoint absent",
        "sourceDatabase": str(source_db),
        "fixtureRoot": str(root),
        "manifestRunId": manifest["runId"],
        "manifestSha256": manifest["manifestSha256"],
        "deletedValues": deleted,
        "syntheticFixturePriorValueSequence": prior_sequence,
        "syntheticFixtureSequenceReset": True,
        "remainingValuesBeforeResume": 0,
        "sourceStructureTableSha256": source_hashes,
        "automaticCleanup": "disabled"
    }
    receipt_path.write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
    assert not (root / "n6-checkpoint.json").exists()

    checkpoint_crash = run(["--mode", "n6", "--root", str(root), "--rows", "205", "--resume",
                            "--simulate-init-crash-after", "checkpoint"])
    assert checkpoint_crash.returncode == 76, checkpoint_crash.stdout + checkpoint_crash.stderr
    assert structure_hashes(database) == source_hashes, "marker recovery rewrote fixed structure rows/timestamps"
    with closing(sqlite3.connect(f"file:{database}?mode=ro", uri=True)) as connection:
        assert connection.execute("SELECT COUNT(*) FROM lf_prtg_values").fetchone()[0] == 0
        assert connection.execute("SELECT committed_rows FROM lf_n6_generation_control").fetchone()[0] == 0
    checkpoint = json.loads((root / "n6-checkpoint.json").read_text(encoding="utf-8"))
    assert checkpoint["committedRows"] == 0 and checkpoint["manifestSha256"] == manifest["manifestSha256"]
    final = run(["--mode", "n6", "--root", str(root), "--rows", "205", "--resume"])
    assert final.returncode == 0, final.stdout + final.stderr
    assert structure_hashes(database) == source_hashes, "generation resume rewrote fixed structure rows/timestamps"
    with closing(sqlite3.connect(f"file:{database}?mode=ro", uri=True)) as connection:
        assert connection.execute("SELECT COUNT(*) FROM lf_prtg_values").fetchone()[0] == 205
    receipt["checkpointBoundaryExitCode"] = checkpoint_crash.returncode
    receipt["finalResumeExitCode"] = final.returncode
    receipt["structureHashesAfterMarkerRecoveryAndGeneration"] = structure_hashes(database)
    shutil.copy2(root / "n6-high-water-result.json", home / "init-marker-no-checkpoint-result-evidence.json")
    with closing(sqlite3.connect(database)) as connection:
        connection.execute("BEGIN IMMEDIATE")
        reset_values = connection.execute("DELETE FROM lf_prtg_values").rowcount
        connection.execute("DELETE FROM sqlite_sequence WHERE name='lf_prtg_values'")
        reset_marker = connection.execute(
            "UPDATE lf_n6_generation_control SET committed_rows=0 WHERE run_id=? AND manifest_sha256=?",
            (manifest["runId"], manifest["manifestSha256"])).rowcount
        connection.commit()
    assert reset_values == 205 and reset_marker == 1
    (root / "n6-high-water-result.json").unlink()
    (root / "n6-progress.json").unlink()
    with closing(sqlite3.connect(f"file:{database}?mode=ro", uri=True)) as connection:
        assert connection.execute("SELECT COUNT(*) FROM lf_prtg_values").fetchone()[0] == 0
        assert connection.execute("SELECT run_id, manifest_sha256, committed_rows FROM lf_n6_generation_control").fetchall() == [
            (manifest["runId"], manifest["manifestSha256"], 0)]
    assert not (root / "n6-checkpoint.json").exists()
    assert structure_hashes(database) == source_hashes
    receipt["reusableSyntheticFixtureRestored"] = True
    receipt["reusableState"] = "tool-generated valid manifest; matching owned DB marker at 0 rows; 0 values; no checkpoint/result/progress"
    receipt_path.write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
    return database.stat().st_size


def run_bounded_e1(dll, approved_source):
    """One 205-row approved-Core seed reused for every E1 recovery contract."""
    folder = os.environ.get("LF_E1_BOUNDED_HOME") or tempfile.mkdtemp(prefix="lf-storage-e1-bounded-")
    home = Path(folder).resolve()
    home.mkdir(parents=True, exist_ok=True)
    if any(home.iterdir()):
        if os.environ.get("LF_E1_BOUNDED_CONTINUE") == "1":
            return continue_bounded_e1(home, approved_source)
        raise SystemExit(f"Refusing to reuse non-empty bounded E1 directory automatically: {home}")
    print(f"E1 bounded fixture: {home}", flush=True)
    with retained_fixture(home):
        compat_root = home / "approved-core-fixture"
        shutil.copytree(approved_source / "LogForesight.Core", compat_root / "LogForesight.Core",
                        ignore=shutil.ignore_patterns("bin", "obj"))
        for relative in ["Directory.Build.props", "Directory.Build.targets", "global.json", "NuGet.Config"]:
            shutil.copy2(approved_source / relative, compat_root / relative)
        build_stamp = compat_root / "scripts/Write-BuildIdentityStamp.ps1"
        build_stamp.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(approved_source / "scripts/Write-BuildIdentityStamp.ps1", build_stamp)
        compat_generator = compat_root / "scripts/prtg-workload/storage-generator"
        shutil.copytree(Path(__file__).resolve().parent, compat_generator,
                        ignore=shutil.ignore_patterns("bin", "obj"))
        program = compat_generator / "Program.cs"
        program_text = program.read_text(encoding="utf-8")
        generic_failure = 'Console.Error.WriteLine($"FAILED: {ex.GetType().Name}. Check input, provider availability and dedicated output path; connection details are intentionally omitted.");'
        diagnostic_failure = r'var detail = System.Text.RegularExpressions.Regex.Replace(ex.Message, @"[A-Za-z]:\\.*", "[local path]"); Console.Error.WriteLine($"CONTRACT FAILED: {ex.GetType().Name}: {detail}");'
        assert generic_failure in program_text
        program.write_text(program_text.replace(generic_failure, diagnostic_failure, 1), encoding="utf-8")
        project = compat_generator / "PrtgStorageGenerator.csproj"
        project_text = project.read_text(encoding="utf-8")
        project.write_text(project_text.replace("</PropertyGroup>",
                            "  <AssemblyVersion>1.0.0.0</AssemblyVersion>\n  </PropertyGroup>", 1),
                            encoding="utf-8")

        def build():
            result = subprocess.run(["dotnet", "build", str(project), "-v:q", "-p:LfBuildIdentityEnabled=false"], cwd=compat_root,
                                    capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=240)
            assert result.returncode == 0, (result.stdout or "") + (result.stderr or "")

        build()
        version_a_dir = compat_root / "tool-versions/tool-version-a"
        version_a_dir.parent.mkdir(parents=True, exist_ok=True)
        shutil.copytree(compat_generator / "bin/Debug/net8.0-windows", version_a_dir)
        dll_a = version_a_dir / "PrtgStorageGenerator.dll"
        dll_b = compat_generator / "bin/Debug/net8.0-windows/PrtgStorageGenerator.dll"

        env = dict(os.environ)
        env.pop("LF_CAPACITY_SQLSERVER_CONNECTION", None)

        def run(executable, args):
            return subprocess.run(["dotnet", str(executable), *args], cwd=compat_root, env=env,
                                  capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=180)

        root = home / "single-owned-seed"
        started = time.perf_counter()
        interrupted = run(dll_a, ["--mode", "n6", "--root", str(root), "--rows", "205",
                                  "--simulate-init-crash-after", "manifest"])
        assert interrupted.returncode == 76, interrupted.stdout + interrupted.stderr
        manifest_path = root / "n6-run-manifest.json"
        manifest_bytes = manifest_path.read_bytes()
        manifest = json.loads(manifest_bytes)
        assert not Path(manifest["databasePath"]).exists()
        marker_crash = run(dll_a, ["--mode", "n6", "--root", str(root), "--rows", "205", "--resume",
                                   "--simulate-init-crash-after", "db-marker"])
        assert marker_crash.returncode == 76, marker_crash.stdout + marker_crash.stderr
        assert manifest_path.read_bytes() == manifest_bytes, "DB-marker recovery drifted the manifest"
        db = Path(manifest["databasePath"])
        with closing(sqlite3.connect(f"file:{db}?mode=ro", uri=True)) as connection:
            marker = connection.execute("select run_id, manifest_sha256, committed_rows from lf_n6_generation_control").fetchall()
            assert marker == [(manifest["runId"], manifest["manifestSha256"], 0)]
            shape_timestamps = connection.execute(
                "select (select min(created_at) from lf_prtg_devices), "
                "(select max(synced_at) from lf_prtg_devices), "
                "(select max(created_at) from lf_prtg_host_map)").fetchone()
        checkpoint_crash = run(dll_a, ["--mode", "n6", "--root", str(root), "--rows", "205", "--resume",
                                       "--simulate-init-crash-after", "checkpoint"])
        assert checkpoint_crash.returncode == 76, checkpoint_crash.stdout + checkpoint_crash.stderr
        assert manifest_path.read_bytes() == manifest_bytes, "checkpoint recovery drifted the manifest"
        checkpoint_path = root / "n6-checkpoint.json"
        init_checkpoint = json.loads(checkpoint_path.read_text(encoding="utf-8"))
        assert init_checkpoint["manifestSha256"] == manifest["manifestSha256"] and init_checkpoint["committedRows"] == 0
        with closing(sqlite3.connect(f"file:{db}?mode=ro", uri=True)) as connection:
            after_timestamps = connection.execute(
                "select (select min(created_at) from lf_prtg_devices), "
                "(select max(synced_at) from lf_prtg_devices), "
                "(select max(created_at) from lf_prtg_host_map)").fetchone()
        assert after_timestamps == shape_timestamps, "completed structure was reseeded after its ownership marker"
        seeded = run(dll_a, ["--mode", "n6", "--root", str(root), "--rows", "205", "--resume"])
        assert seeded.returncode == 0, seeded.stdout + seeded.stderr
        seed_seconds = time.perf_counter() - started
        assert manifest_path.read_bytes() == manifest_bytes, "generation drifted the immutable manifest"
        seed_db_bytes = db.stat().st_size
        progress = json.loads((root / "n6-progress.json").read_text(encoding="utf-8"))
        assert progress["committedRows"] == 205 and progress["requestedRows"] == 205
        with closing(sqlite3.connect(f"file:{db}?mode=ro", uri=True)) as connection:
            assert connection.execute("select count(*) from lf_prtg_values").fetchone()[0] == 205
            assert connection.execute("select count(*) from lf_prtg_devices").fetchone()[0] == 3000
            assert connection.execute("select count(*) from lf_prtg_sensors").fetchone()[0] == 15000
            assert connection.execute("select count(*) from lf_prtg_host_map").fetchone()[0] == 3000

        # A second assembly version proves this is a real identity upgrade. It
        # uses exactly the same database and storage dependency as version A.
        project_text = project.read_text(encoding="utf-8")
        project.write_text(project_text.replace("<AssemblyVersion>1.0.0.0</AssemblyVersion>",
                            "<AssemblyVersion>1.0.0.1</AssemblyVersion>", 1), encoding="utf-8")
        build()
        mismatch = run(dll_b, ["--mode", "n6", "--root", str(root), "--rows", "205", "--resume"])
        assert mismatch.returncode == 2, mismatch.stdout + mismatch.stderr
        # Alternate the two assembly identities while recovering every atomic
        # upgrade write boundary. This never creates another storage root.
        for executable, phase in ((dll_b, "intent"), (dll_a, "db-marker"),
                                  (dll_b, "manifest"), (dll_a, "checkpoint")):
            upgrade_args = ["--mode", "n6", "--root", str(root), "--rows", "205", "--resume",
                            "--upgrade-owned-run"]
            interrupted_upgrade = run(executable, upgrade_args + ["--simulate-upgrade-crash-after", phase])
            assert interrupted_upgrade.returncode == 76, f"upgrade {phase}: {interrupted_upgrade.stdout}{interrupted_upgrade.stderr}"
            resumed_upgrade = run(executable, upgrade_args)
            assert resumed_upgrade.returncode == 0, f"upgrade retry {phase}: {resumed_upgrade.stdout}{resumed_upgrade.stderr}"

        base_db = db.read_bytes()
        for corruption in ("prefix", "mapping", "timestamps"):
            db.write_bytes(base_db)
            with closing(sqlite3.connect(db)) as connection:
                if corruption == "prefix":
                    connection.execute("UPDATE lf_prtg_values SET avg_value=avg_value+1 WHERE id=1")
                elif corruption == "mapping":
                    connection.execute("UPDATE lf_prtg_devices SET name='foreign-map' WHERE objid=1")
                else:
                    connection.execute("UPDATE lf_prtg_devices SET synced_at=datetime(created_at, '-1 second') WHERE objid=1")
                connection.commit()
            refusal = run(dll_a, ["--mode", "n6", "--root", str(root), "--rows", "205", "--resume"])
            assert refusal.returncode == 2, f"{corruption}: {refusal.stdout}{refusal.stderr}"
        db.write_bytes(base_db)

        old_log = home / "bounded-legacy.log"
        old_log.write_text(f"rows=205/205 elapsed_s={progress['elapsedSeconds']:.1f} "
                           f"db_bytes={progress['dbHighWaterBytes']} process_ws_peak_bytes=1\n", encoding="ascii")

        def make_legacy(name):
            legacy = home / name
            legacy.mkdir()
            legacy_db = legacy / db.name
            shutil.copy2(db, legacy_db)
            shutil.copy2(root / "n6-progress.json", legacy / "n6-progress.json")
            for artifact in ("n6-high-water-result.json", "n6-run-manifest.json", "n6-checkpoint.json"):
                (legacy / artifact).unlink(missing_ok=True)
            with closing(sqlite3.connect(legacy_db)) as connection:
                connection.execute("DROP TABLE lf_n6_generation_control")
                connection.commit()
            return legacy, legacy_db

        def adoption_args(legacy):
            return ["--mode", "n6", "--root", str(legacy), "--rows", "205", "--provider", "sqlite",
                    "--adopt-legacy", "--source-root", str(approved_source), "--source-log", str(old_log)]

        # Normal adoption proves a manifest, DB marker, checkpoint, evidence
        # copy, and complete 205-row deterministic prefix are all sealed.
        normal, normal_db = make_legacy("legacy-normal")
        before_bytes = normal_db.stat().st_size
        normal_started = time.perf_counter()
        adopted = run(dll_b, adoption_args(normal))
        normal_seconds = time.perf_counter() - normal_started
        assert adopted.returncode == 0, adopted.stdout + adopted.stderr
        checkpoint = json.loads((normal / "n6-checkpoint.json").read_text(encoding="utf-8"))
        assert checkpoint["committedRows"] == 205
        resumed = run(dll_b, ["--mode", "n6", "--root", str(normal), "--rows", "205", "--resume"])
        assert resumed.returncode == 0, resumed.stdout + resumed.stderr
        assert normal_db.stat().st_size >= before_bytes

        # Each cross-file write boundary is interrupted, then retried from the
        # same immutable database copy. The manifest-only retry also exercises
        # the formerly missing-marker recovery edge.
        for phase in ("manifest", "db-marker", "checkpoint"):
            legacy, legacy_db = make_legacy(f"legacy-crash-{phase}")
            interrupted_adoption = run(dll_b, adoption_args(legacy) + ["--simulate-adopt-crash-after", phase])
            assert interrupted_adoption.returncode == 76, interrupted_adoption.stdout + interrupted_adoption.stderr
            retried = run(dll_b, adoption_args(legacy))
            assert retried.returncode == 0, f"{phase}: {retried.stdout}{retried.stderr}"
            assert json.loads((legacy / "n6-checkpoint.json").read_text(encoding="utf-8"))["committedRows"] == 205
            assert legacy_db.stat().st_size >= seed_db_bytes

        foreign, foreign_db = make_legacy("legacy-foreign-marker")
        manifest_crash = run(dll_b, adoption_args(foreign) + ["--simulate-adopt-crash-after", "manifest"])
        assert manifest_crash.returncode == 76, manifest_crash.stdout + manifest_crash.stderr
        with closing(sqlite3.connect(foreign_db)) as connection:
            connection.execute("INSERT INTO lf_n6_generation_control VALUES ('foreign-run', 'foreign-hash', 205)")
            connection.commit()
        foreign_before = foreign_db.read_bytes()
        refused_foreign = run(dll_b, adoption_args(foreign))
        assert refused_foreign.returncode == 2, refused_foreign.stdout + refused_foreign.stderr
        assert foreign_db.read_bytes() == foreign_before, "Foreign ownership refusal modified its database"

        long_log = home / "bounded-long-line.log"
        final = b"rows=4/4 elapsed_s=12.3 db_bytes=456 process_ws_peak_bytes=789\n"
        long_log.write_bytes(b"x" * 4097 + b"\n" + final)
        inspected = run(dll_b, ["--inspect-legacy-log", "--source-log", str(long_log)])
        assert inspected.returncode == 0, inspected.stdout + inspected.stderr
        assert inspected.stdout.strip() == "rows=4/4 elapsed_s=12.3 db_bytes=456"
        invalid_log = home / "bounded-oversized-progress.log"
        invalid_log.write_bytes(final + b"rows=" + b"9" * 4097 + b"\n")
        refused_log = run(dll_b, ["--inspect-legacy-log", "--source-log", str(invalid_log)])
        assert refused_log.returncode == 2, refused_log.stdout + refused_log.stderr

        print(f"E1 bounded suite passed: one 3,000-host/15,000-sensor/3,000-map seed, "
              f"205 values; DB={seed_db_bytes} bytes; init+seed={seed_seconds:.2f}s; "
              f"normal adoption DB={before_bytes} bytes in {normal_seconds:.2f}s; "
              "three adoption crash retries, true tool-identity upgrade, prefix/map/foreign-owner refusals, "
              f"and 4 KiB bounded-log case passed. Fixture retained at {home}.")


def continue_bounded_e1(home, approved_source):
    """Continue the one retained seed after fixing a diagnosed recovery defect."""
    root = home / "single-owned-seed"
    compat_root = home / "approved-core-fixture"
    generator = compat_root / "scripts/prtg-workload/storage-generator"
    database = next(root.glob("n6-sqlite-*.db"))
    if not ((root / "n6-run-manifest.json").is_file() and database.is_file()):
        raise SystemExit("Continuation requires the retained initialized fixture; it never seeds a new root.")

    env = dict(os.environ)
    env.pop("LF_CAPACITY_SQLSERVER_CONNECTION", None)

    def run(executable, args):
        return subprocess.run(["dotnet", str(executable), *args], cwd=compat_root, env=env,
                              capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=180)

    # Compile current source against the approved frozen Core snapshot. Only the
    # temporary clone's exception line is expanded, with local paths redacted.
    source_program = Path(__file__).resolve().parent / "Program.cs"
    clone_program = generator / "Program.cs"
    source_text = source_program.read_text(encoding="utf-8")
    generic_failure = 'Console.Error.WriteLine($"FAILED: {ex.GetType().Name}. Check input, provider availability and dedicated output path; connection details are intentionally omitted.");'
    diagnostic_failure = r'var detail = System.Text.RegularExpressions.Regex.Replace(ex.Message, @"[A-Za-z]:\\.*", "[local path]"); Console.Error.WriteLine($"CONTRACT FAILED: {ex.GetType().Name}: {detail}");'
    assert generic_failure in source_text
    clone_program.write_text(source_text.replace(generic_failure, diagnostic_failure, 1), encoding="utf-8")
    project = generator / "PrtgStorageGenerator.csproj"
    project_text = project.read_text(encoding="utf-8")
    project_text = re.sub(r"<AssemblyVersion>.*?</AssemblyVersion>", "", project_text)
    project_text = project_text.replace("</PropertyGroup>",
                                        "<AssemblyVersion>1.0.0.2</AssemblyVersion></PropertyGroup>", 1)
    project.write_text(project_text, encoding="utf-8")
    build = subprocess.run(["dotnet", "build", str(project), "-v:q", "-p:LfBuildIdentityEnabled=false"],
                          cwd=compat_root, capture_output=True, text=True, encoding="utf-8",
                          errors="replace", timeout=240)
    assert build.returncode == 0, (build.stdout or "") + (build.stderr or "")
    version_c = compat_root / "tool-versions/tool-version-c"
    version_c.parent.mkdir(parents=True, exist_ok=True)
    shutil.copytree(generator / "bin/Debug/net8.0-windows", version_c, dirs_exist_ok=True)
    dll_c = version_c / "PrtgStorageGenerator.dll"
    project_text = project.read_text(encoding="utf-8")
    project_text = project_text.replace("<AssemblyVersion>1.0.0.2</AssemblyVersion>",
                                        "<AssemblyVersion>1.0.0.3</AssemblyVersion>", 1)
    project.write_text(project_text, encoding="utf-8")
    build = subprocess.run(["dotnet", "build", str(project), "-v:q", "-p:LfBuildIdentityEnabled=false"],
                          cwd=compat_root, capture_output=True, text=True, encoding="utf-8",
                          errors="replace", timeout=240)
    assert build.returncode == 0, (build.stdout or "") + (build.stderr or "")
    version_d = compat_root / "tool-versions/tool-version-d"
    shutil.copytree(generator / "bin/Debug/net8.0-windows", version_d, dirs_exist_ok=True)
    dll_d = version_d / "PrtgStorageGenerator.dll"

    # Adopt multiple independent copies of the one retained database; this
    # reuses structure/values and does not seed another root.
    progress = json.loads((root / "n6-progress.json").read_text(encoding="utf-8"))
    assert progress["committedRows"] == progress["requestedRows"] == 205
    old_log = home / "bounded-legacy.log"
    old_log.write_text(f"rows=205/205 elapsed_s={progress['elapsedSeconds']:.1f} "
                       f"db_bytes={progress['dbHighWaterBytes']} process_ws_peak_bytes=1\n", encoding="ascii")

    def make_legacy(name):
        legacy = home / name
        if legacy.exists():
            if (legacy / "n6-run-manifest.json").exists() or (legacy / "n6-checkpoint.json").exists():
                raise SystemExit(f"Refusing to recreate or replace an already adopted continuation fixture: {legacy}")
            matches = list(legacy.glob("n6-sqlite-*.db"))
            if len(matches) != 1 or not (legacy / "n6-progress.json").is_file():
                raise SystemExit(f"Existing continuation clone is not a reusable pre-adoption fixture: {legacy}")
            legacy_db = matches[0]
            with closing(sqlite3.connect(f"file:{legacy_db}?mode=ro", uri=True)) as connection:
                marker_count = connection.execute(
                    "select count(*) from sqlite_master where type='table' and name='lf_n6_generation_control'").fetchone()[0]
            if marker_count:
                raise SystemExit(f"Existing continuation clone has unexpected ownership metadata: {legacy}")
            return legacy, legacy_db
        legacy.mkdir()
        legacy_db = legacy / database.name
        shutil.copy2(database, legacy_db)
        shutil.copy2(root / "n6-progress.json", legacy / "n6-progress.json")
        with closing(sqlite3.connect(legacy_db)) as connection:
            connection.execute("DROP TABLE lf_n6_generation_control")
            connection.commit()
        return legacy, legacy_db

    def adoption_args(legacy):
        return ["--mode", "n6", "--root", str(legacy), "--rows", "205", "--provider", "sqlite",
                "--adopt-legacy", "--source-root", str(approved_source), "--source-log", str(old_log)]

    def adopt(name):
        legacy = home / name
        if legacy.exists() and (legacy / "n6-run-manifest.json").is_file() and (legacy / "n6-checkpoint.json").is_file():
            legacy_db = next(legacy.glob("n6-sqlite-*.db"))
            return legacy, legacy_db, 0.0
        legacy, legacy_db = make_legacy(name)
        started = time.perf_counter()
        result = run(dll_c, adoption_args(legacy))
        elapsed = time.perf_counter() - started
        assert result.returncode == 0, result.stdout + result.stderr
        checkpoint = json.loads((legacy / "n6-checkpoint.json").read_text(encoding="utf-8"))
        assert checkpoint["committedRows"] == 205
        return legacy, legacy_db, elapsed

    normal, normal_db, normal_seconds = adopt("legacy-normal")
    normal_manifest_bytes = (normal / "n6-run-manifest.json").read_bytes()
    normal_checkpoint_bytes = (normal / "n6-checkpoint.json").read_bytes()
    checkpoint = json.loads(normal_checkpoint_bytes)
    assert checkpoint["committedRows"] == 205
    with closing(sqlite3.connect(f"file:{normal_db}?mode=ro", uri=True)) as connection:
        assert connection.execute("select count(*) from lf_prtg_values").fetchone()[0] == 205
        assert connection.execute("select manifest_sha256 from lf_n6_generation_control").fetchone()[0] == checkpoint["manifestSha256"]

    # One isolated adoption copy per upgrade write boundary keeps its verified
    # checkpoint available while each crash/retry is exercised.
    evidence = home / "upgrade-result-evidence"
    evidence.mkdir(exist_ok=True)
    for phase in ("intent", "db-marker", "manifest", "checkpoint"):
        prior_upgrade = home / f"owned-upgrade-{phase}"
        if ((prior_upgrade / "n6-high-water-result.json").is_file() and
                not (prior_upgrade / "n6-checkpoint.json").exists() and
                not (prior_upgrade / "n6-upgrade-intent.json").exists()):
            continue
        upgrade_root, _, _ = adopt(f"owned-upgrade-{phase}")
        upgrade_db = next(upgrade_root.glob("n6-sqlite-*.db"))
        manifest_path = upgrade_root / "n6-run-manifest.json"
        checkpoint_path = upgrade_root / "n6-checkpoint.json"
        before_manifest = manifest_path.read_bytes()
        before_checkpoint = checkpoint_path.read_bytes()
        mismatch = run(dll_d, ["--mode", "n6", "--root", str(upgrade_root), "--rows", "205", "--resume"])
        assert mismatch.returncode == 2 and "fingerprint" in mismatch.stderr.lower(), mismatch.stdout + mismatch.stderr
        upgrade_args = ["--mode", "n6", "--root", str(upgrade_root), "--rows", "205", "--resume", "--upgrade-owned-run"]
        interrupted = run(dll_d, upgrade_args + ["--simulate-upgrade-crash-after", phase])
        assert interrupted.returncode == 76, f"upgrade {phase}: {interrupted.stdout}{interrupted.stderr}"
        intent = json.loads((upgrade_root / "n6-upgrade-intent.json").read_text(encoding="utf-8"))
        if phase in ("intent", "db-marker"):
            assert manifest_path.read_bytes() == before_manifest and checkpoint_path.read_bytes() == before_checkpoint
            if phase == "db-marker":
                with closing(sqlite3.connect(f"file:{upgrade_db}?mode=ro", uri=True)) as connection:
                    marker_hash = connection.execute("select manifest_sha256 from lf_n6_generation_control").fetchone()[0]
                assert marker_hash == intent["newManifestSha256"]
        elif phase == "manifest":
            assert json.loads(manifest_path.read_text(encoding="utf-8"))["manifestSha256"] == intent["newManifestSha256"]
            assert checkpoint_path.read_bytes() == before_checkpoint
        else:
            updated_checkpoint = json.loads(checkpoint_path.read_text(encoding="utf-8"))
            assert updated_checkpoint["manifestSha256"] == intent["newManifestSha256"]
        retried = run(dll_d, upgrade_args)
        assert retried.returncode == 0, f"upgrade retry {phase}: {retried.stdout}{retried.stderr}"
        shutil.copy2(upgrade_root / "n6-high-water-result.json", evidence / f"upgrade-{phase}.json")
        assert not (upgrade_root / "n6-upgrade-intent.json").exists()

    for corruption in ("prefix", "mapping", "timestamps"):
        corrupt_root, corrupt_db, _ = adopt(f"owned-corrupt-{corruption}")
        with closing(sqlite3.connect(corrupt_db)) as connection:
            if corruption == "prefix":
                connection.execute("UPDATE lf_prtg_values SET avg_value=avg_value+1 WHERE id=1")
            elif corruption == "mapping":
                connection.execute("UPDATE lf_prtg_devices SET name='foreign-map' WHERE objid=1")
            else:
                connection.execute("UPDATE lf_prtg_devices SET synced_at=datetime(created_at, '-1 second') WHERE objid=1")
            connection.commit()
        refusal = run(dll_c, ["--mode", "n6", "--root", str(corrupt_root), "--rows", "205", "--resume"])
        assert refusal.returncode == 2, f"{corruption}: {refusal.stdout}{refusal.stderr}"

    for phase in ("manifest", "db-marker", "checkpoint"):
        prior_adoption = home / f"legacy-crash-{phase}"
        if ((prior_adoption / "n6-run-manifest.json").is_file() and
                (prior_adoption / "n6-checkpoint.json").is_file()):
            continue
        legacy, legacy_db = make_legacy(f"legacy-crash-{phase}")
        interrupted = run(dll_c, adoption_args(legacy) + ["--simulate-adopt-crash-after", phase])
        assert interrupted.returncode == 76, f"adoption {phase}: {interrupted.stdout}{interrupted.stderr}"
        stable_manifest = (legacy / "n6-run-manifest.json").read_bytes()
        stable_checkpoint = ((legacy / "n6-checkpoint.json").read_bytes()
                            if (legacy / "n6-checkpoint.json").exists() else None)
        retried = run(dll_c, adoption_args(legacy))
        assert retried.returncode == 0, f"adoption retry {phase}: {retried.stdout}{retried.stderr}"
        assert (legacy / "n6-run-manifest.json").read_bytes() == stable_manifest
        if stable_checkpoint is not None:
            assert (legacy / "n6-checkpoint.json").read_bytes() == stable_checkpoint
        assert json.loads((legacy / "n6-checkpoint.json").read_text(encoding="utf-8"))["committedRows"] == 205
        assert legacy_db.stat().st_size >= database.stat().st_size

    foreign = home / "legacy-foreign-marker"
    if not foreign.exists():
        foreign, foreign_db = make_legacy("legacy-foreign-marker")
        interrupted = run(dll_c, adoption_args(foreign) + ["--simulate-adopt-crash-after", "manifest"])
        assert interrupted.returncode == 76, interrupted.stdout + interrupted.stderr
    else:
        foreign_db = next(foreign.glob("n6-sqlite-*.db"))
    with closing(sqlite3.connect(foreign_db)) as connection:
        connection.execute("CREATE TABLE IF NOT EXISTS lf_n6_generation_control (run_id TEXT NOT NULL PRIMARY KEY, manifest_sha256 TEXT NOT NULL, committed_rows INTEGER NOT NULL)")
        if connection.execute("SELECT COUNT(*) FROM lf_n6_generation_control").fetchone()[0] == 0:
            connection.execute("INSERT INTO lf_n6_generation_control VALUES ('foreign-run', 'foreign-hash', 205)")
        connection.commit()
    foreign_before = foreign_db.read_bytes()
    refused = run(dll_c, adoption_args(foreign))
    assert refused.returncode == 2, refused.stdout + refused.stderr
    assert foreign_db.read_bytes() == foreign_before

    long_log = home / "bounded-long-line.log"
    long_log.write_bytes(b"x" * 4097 + b"\nrows=4/4 elapsed_s=12.3 db_bytes=456 process_ws_peak_bytes=789\n")
    inspected = run(dll_c, ["--inspect-legacy-log", "--source-log", str(long_log)])
    assert inspected.returncode == 0 and inspected.stdout.strip() == "rows=4/4 elapsed_s=12.3 db_bytes=456"
    invalid_log = home / "bounded-oversized-progress.log"
    invalid_log.write_bytes(b"rows=4/4 elapsed_s=12.3 db_bytes=456 process_ws_peak_bytes=789\nrows=" + b"9" * 4097 + b"\n")
    refused_log = run(dll_c, ["--inspect-legacy-log", "--source-log", str(invalid_log)])
    assert refused_log.returncode == 2, refused_log.stdout + refused_log.stderr
    marker_fixture_bytes = exercise_marker_without_checkpoint(home, approved_source, dll_c)
    print(f"E1 continuation passed using retained 205-row seed; DB={database.stat().st_size} bytes; "
          f"normal adoption DB={normal_db.stat().st_size} bytes in {normal_seconds:.2f}s; "
          "four audited upgrade boundaries, three adoption boundaries, prefix/map/timestamp/foreign-owner refusals, "
          f"bounded-log and no-checkpoint marker recovery (DB={marker_fixture_bytes} bytes) passed. "
          f"Fixture retained at {home}.")


def main():
    if len(sys.argv) >= 2 and sys.argv[1] == "--bounded-e1":
        if len(sys.argv) != 4:
            raise SystemExit("Usage: python test_contracts.py --bounded-e1 <built DLL> <approved source root>")
        run_bounded_e1(Path(sys.argv[2]).resolve(strict=True), Path(sys.argv[3]).resolve(strict=True))
        return
    dll = Path(sys.argv[1]).resolve(strict=True)
    approved_source = Path(sys.argv[2]).resolve(strict=True) if len(sys.argv) > 2 else None
    with tempfile.TemporaryDirectory(prefix="lf-storage-contracts-") as folder:
        home = Path(folder)

        def run(extra, connection=None):
            env = dict(os.environ)
            env.pop("LF_CAPACITY_SQLSERVER_CONNECTION", None)
            if connection:
                env["LF_CAPACITY_SQLSERVER_CONNECTION"] = connection
            return subprocess.run(["dotnet", str(dll), *extra], env=env,
                                  capture_output=True, text=True, timeout=180)

        def run_tool(executable, extra):
            env = dict(os.environ)
            env.pop("LF_CAPACITY_SQLSERVER_CONNECTION", None)
            return subprocess.run(["dotnet", str(executable), *extra], env=env,
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

        # Each initialization artifact boundary must be restartable. These roots
        # are tiny, test-owned databases; the 41,140,900-row handoff copy is never
        # opened by this contract.
        for phase in ("manifest", "db-marker", "checkpoint"):
            root = home / f"init-{phase}"
            interrupted_init = run(["--mode", "n6", "--root", str(root), "--rows", "4",
                                    "--simulate-init-crash-after", phase])
            assert interrupted_init.returncode == 76, interrupted_init.stdout + interrupted_init.stderr
            resumed_init = run(["--mode", "n6", "--root", str(root), "--rows", "4", "--resume"])
            assert resumed_init.returncode == 0, resumed_init.stdout + resumed_init.stderr
            init_report = json.loads((root / "n6-high-water-result.json").read_text(encoding="utf-8"))
            assert init_report["persistedRows"] == 4

        # Resume audits the entire committed prefix and all deterministic source
        # mappings before it can write another row.
        for corruption in ("prefix", "mapping"):
            root = home / f"tampered-{corruption}"
            stopped = run(["--mode", "n6", "--root", str(root), "--rows", "205",
                           "--stop-after-batches", "1"])
            assert stopped.returncode == 75, stopped.stdout + stopped.stderr
            db = next(root.glob("n6-sqlite-*.db"))
            with closing(sqlite3.connect(db)) as connection:
                if corruption == "prefix":
                    connection.execute("UPDATE lf_prtg_values SET avg_value=avg_value+1 WHERE id=25")
                else:
                    connection.execute("UPDATE lf_prtg_devices SET name='foreign-map' WHERE objid=1")
                connection.commit()
            refused = run(["--mode", "n6", "--root", str(root), "--rows", "205", "--resume"])
            assert refused.returncode == 2, refused.stdout + refused.stderr

        upgrade = home / "audited-tool-upgrade"
        stopped = run(["--mode", "n6", "--root", str(upgrade), "--rows", "205", "--stop-after-batches", "1"])
        assert stopped.returncode == 75, stopped.stdout + stopped.stderr
        upgraded = run(["--mode", "n6", "--root", str(upgrade), "--rows", "205", "--resume", "--upgrade-owned-run"])
        assert upgraded.returncode == 0, upgraded.stdout + upgraded.stderr
        assert json.loads((upgrade / "n6-high-water-result.json").read_text(encoding="utf-8"))["persistedRows"] == 205

        # An over-limit line is ignored without allocating an entire input;
        # the final canonical progress sample remains readable.
        huge_log = home / "large-legacy.log"
        final_sample = b"rows=4/4 elapsed_s=12.3 db_bytes=456 process_ws_peak_bytes=789\n"
        huge_log.write_bytes(b"x" * 4097 + b"\n" + final_sample)
        inspected = run(["--inspect-legacy-log", "--source-log", str(huge_log)])
        assert inspected.returncode == 0, inspected.stdout + inspected.stderr
        assert inspected.stdout.strip() == "rows=4/4 elapsed_s=12.3 db_bytes=456"

        if approved_source is not None:
            # Compile the current generator against the explicitly approved old
            # storage sources. This keeps frozen-source adoption checks intact
            # while exercising adoption only on small, test-owned databases.
            compat_root = home / "approved-core-fixture"
            shutil.copytree(approved_source / "LogForesight.Core", compat_root / "LogForesight.Core",
                            ignore=shutil.ignore_patterns("bin", "obj"))
            for relative in ["Directory.Build.props", "Directory.Build.targets", "global.json", "NuGet.Config"]:
                shutil.copy2(approved_source / relative, compat_root / relative)
            build_stamp = compat_root / "scripts/Write-BuildIdentityStamp.ps1"
            build_stamp.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(approved_source / "scripts/Write-BuildIdentityStamp.ps1", build_stamp)
            compat_generator = compat_root / "scripts/prtg-workload/storage-generator"
            shutil.copytree(Path(__file__).resolve().parent, compat_generator,
                            ignore=shutil.ignore_patterns("bin", "obj"))
            compat_dll = compat_generator / "bin/Debug/net8.0-windows/PrtgStorageGenerator.dll"
            build = subprocess.run(["dotnet", "build", str(compat_generator / "PrtgStorageGenerator.csproj"), "-v:q"],
                                   cwd=compat_root, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=240)
            assert build.returncode == 0, (build.stdout or "") + (build.stderr or "")
            seed = home / "approved-core-seed"
            seeded = run_tool(compat_dll, ["--mode", "n6", "--root", str(seed), "--rows", "4"])
            assert seeded.returncode == 0, seeded.stdout + seeded.stderr
            seed_progress = json.loads((seed / "n6-progress.json").read_text(encoding="utf-8"))
            old_log = home / "approved-core-large.log"
            old_log.write_bytes(b"x" * 4097 +
                                f"\nrows=4/4 elapsed_s={seed_progress['elapsedSeconds']:.1f} "
                                f"db_bytes={seed_progress['dbHighWaterBytes']} process_ws_peak_bytes=1\n".encode("ascii"))
            for phase in ("manifest", "db-marker", "checkpoint"):
                legacy = home / f"approved-legacy-{phase}"
                legacy.mkdir()
                shutil.copy2(seed / "n6-progress.json", legacy / "n6-progress.json")
                source_db = next(seed.glob("n6-sqlite-*.db"))
                shutil.copy2(source_db, legacy / source_db.name)
                with closing(sqlite3.connect(legacy / source_db.name)) as connection:
                    connection.execute("DROP TABLE lf_n6_generation_control")
                    connection.commit()
                args = ["--mode", "n6", "--root", str(legacy), "--rows", "4", "--provider", "sqlite",
                        "--adopt-legacy", "--source-root", str(approved_source), "--source-log", str(old_log)]
                interrupted_adoption = run_tool(compat_dll, args + ["--simulate-adopt-crash-after", phase])
                assert interrupted_adoption.returncode == 76, interrupted_adoption.stdout + interrupted_adoption.stderr
                resumed_adoption = run_tool(compat_dll, args)
                assert resumed_adoption.returncode == 0, resumed_adoption.stdout + resumed_adoption.stderr
                assert json.loads((legacy / "n6-checkpoint.json").read_text(encoding="utf-8"))["committedRows"] == 4

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
                       "--rows", "205", "--resume", "--upgrade-owned-run"])
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
