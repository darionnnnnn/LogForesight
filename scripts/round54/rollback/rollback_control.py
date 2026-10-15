#!/usr/bin/env python3
"""Controlled, isolated SQLite rollback entry point for Round 54 C54-04-B.

The tool never edits an input or an existing restore directory. It copies a fully
identified release and paired state into a newly-created restore root, and only
starts Web after rechecking the sealed copy and its actual SQLite metadata/settings.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import socket
import sqlite3
import subprocess
import sys
import time
from pathlib import Path
from typing import Any

from rollback_errors import Refusal


SCHEMA = "lf-round54-rollback-input-v1"
STATE_SCHEMA = "lf-round54-rollback-state-v1"
AUTHORITY_SCHEMA = "lf-round54-rollback-authority-v1"
SUPPORTED_VERSIONS = {"authority": {"authority-54.1"}, "schema": {"lf-schema-53", "lf-schema-54"}, "source": {"source-authority-54.1"}}
REQUIRED_FILES = ("webRoot", "database", "key", "config", "authority", "preservation")
PROTECTED_CATEGORIES = {"case", "humanReply", "notificationReceipt", "pendingTask"}
MAIL_FLAGS = ("MailEnabled", "MailOnRunCompleted", "MailDailyEnabled", "MailWeeklyEnabled", "MailUrgentEnabled", "MailNotifyWorkOrders")


def fail(message: str) -> None:
    raise Refusal(message)


def canonical_json(value: Any) -> bytes:
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")


def sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def reject_reparse(path: Path) -> None:
    current = path
    while True:
        if current.is_symlink():
            fail(f"reparse/symlink input refused: {current}")
        if current.parent == current:
            return
        current = current.parent


def read_json(path: Path) -> Any:
    reject_reparse(path)
    try:
        return json.loads(path.read_text(encoding="utf-8-sig"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        fail(f"invalid JSON input {path}: {exc}")


def read_json_with_comments(path: Path) -> Any:
    """Parse the .NET appsettings JSONC syntax (comments and trailing commas)."""
    raw = path.read_text(encoding="utf-8-sig")
    out: list[str] = []
    i, in_string, escaped = 0, False, False
    while i < len(raw):
        ch = raw[i]
        if in_string:
            out.append(ch)
            if escaped:
                escaped = False
            elif ch == "\\":
                escaped = True
            elif ch == '"':
                in_string = False
            i += 1
            continue
        if ch == '"':
            in_string = True; out.append(ch); i += 1; continue
        if ch == "/" and i + 1 < len(raw) and raw[i + 1] == "/":
            i += 2
            while i < len(raw) and raw[i] not in "\r\n": i += 1
            continue
        if ch == "/" and i + 1 < len(raw) and raw[i + 1] == "*":
            i += 2
            end = raw.find("*/", i)
            if end < 0: fail(f"unterminated JSON comment in {path}")
            i = end + 2
            continue
        if ch == ",":
            look = i + 1
            while look < len(raw) and raw[look].isspace(): look += 1
            if look < len(raw) and raw[look] in "]}":
                i += 1
                continue
        out.append(ch); i += 1
    try:
        return json.loads("".join(out))
    except json.JSONDecodeError as exc:
        fail(f"invalid .NET appsettings JSON: {exc}")


def file_entry(bundle: Path, files: dict[str, Any], name: str) -> Path:
    row = files.get(name)
    if not isinstance(row, dict) or set(row) != {"path", "sha256"}:
        fail(f"file entry {name!r} must contain only path and sha256")
    rel = Path(row["path"])
    if rel.is_absolute() or ".." in rel.parts or not rel.parts:
        fail(f"unsafe relative path for {name}: {rel}")
    candidate = bundle / rel
    reject_reparse(candidate)
    path = candidate.resolve(strict=True)
    try:
        path.relative_to(bundle.resolve(strict=True))
    except ValueError:
        fail(f"input escaped bundle: {name}")
    reject_reparse(path)
    expected = str(row["sha256"]).lower()
    if len(expected) != 64 or any(ch not in "0123456789abcdef" for ch in expected):
        fail(f"malformed SHA-256 for {name}")
    if name == "webRoot":
        if not path.is_dir():
            fail("webRoot must be a directory")
        actual = tree_sha256(path)
    else:
        if not path.is_file():
            fail(f"required input file missing: {name}")
        actual = sha256_file(path)
    if actual != expected:
        fail(f"input hash mismatch: {name}")
    return path


def tree_sha256(root: Path) -> str:
    reject_reparse(root)
    rows: list[str] = []
    for path in sorted(root.rglob("*"), key=lambda p: p.relative_to(root).as_posix().casefold()):
        reject_reparse(path)
        if path.is_file():
            rows.append(f"{path.relative_to(root).as_posix()}\n{sha256_file(path)}\n")
        elif not path.is_dir():
            fail(f"unsupported release entry: {path}")
    return hashlib.sha256("".join(rows).encode("utf-8")).hexdigest()


def db_metadata(path: Path) -> dict[str, Any]:
    """Read SQLite metadata without creating journals or modifying the database."""
    for suffix in ("-wal", "-shm", "-journal"):
        sidecar = Path(str(path) + suffix)
        if sidecar.exists():
            fail(f"SQLite paired backup is incomplete or not checkpointed; sidecar present: {sidecar.name}")
    uri = path.resolve().as_uri() + "?mode=ro&immutable=1"
    try:
        connection = sqlite3.connect(uri, uri=True)
        connection.row_factory = sqlite3.Row
        app_id = connection.execute("PRAGMA application_id").fetchone()[0]
        user_version = connection.execute("PRAGMA user_version").fetchone()[0]
        integrity = connection.execute("PRAGMA quick_check").fetchone()[0]
        if integrity != "ok":
            fail(f"SQLite quick_check failed: {integrity}")
        schema_rows = connection.execute(
            "SELECT type, name, tbl_name, COALESCE(sql, '') AS sql FROM sqlite_master "
            "WHERE name NOT LIKE 'sqlite_%' ORDER BY type, name, tbl_name"
        ).fetchall()
        schema_text = "\n".join("\x1f".join(str(row[k]) for k in ("type", "name", "tbl_name", "sql")) for row in schema_rows)
        tables = {str(row["name"]) for row in schema_rows if row["type"] == "table"}
        blobs: dict[str, Any] = {}
        if "lf_blobs" in tables:
            columns = {str(row[1]) for row in connection.execute("PRAGMA table_info(lf_blobs)")}
            if not {"blob_key", "content"}.issubset(columns):
                fail("SQLite lf_blobs schema is unknown")
        for key in ("schedule_options", "system_settings", "sentinels"):
                row = connection.execute("SELECT content FROM lf_blobs WHERE blob_key=?", (key,)).fetchone()
                if row is None:
                    fail(f"SQLite startup-safety setting is absent: {key}")
                try:
                    blobs[key] = json.loads(row[0])
                except (TypeError, json.JSONDecodeError):
                    fail(f"SQLite startup-safety setting is malformed: {key}")
        connection.close()
        return {
            "applicationId": int(app_id),
            "userVersion": int(user_version),
            "schemaSha256": hashlib.sha256(schema_text.encode("utf-8")).hexdigest(),
            "tables": sorted(tables),
            "startupSettings": blobs,
        }
    except sqlite3.Error as exc:
        fail(f"SQLite read-only metadata inspection failed: {exc}")


def require_isolated_settings(meta: dict[str, Any]) -> None:
    settings = meta["startupSettings"]
    schedule = settings["schedule_options"]
    system = settings["system_settings"]
    if not isinstance(schedule, dict) or schedule.get("Enabled") is not False:
        fail("rollback refused: schedule_options.Enabled must be present and false")
    if schedule.get("LocalAnalysisEnabled") is not False:
        fail("rollback refused: schedule_options.LocalAnalysisEnabled must be present and false")
    if not isinstance(system, dict):
        fail("rollback refused: system_settings is not an object")
    for flag in MAIL_FLAGS:
        if system.get(flag) is not False:
            fail(f"rollback refused: system_settings.{flag} must be present and false")
    if system.get("PrtgEnabled") is not False:
        fail("rollback refused: system_settings.PrtgEnabled must be present and false")
    if system.get("AutoDispatchEnabled") is not False:
        fail("rollback refused: system_settings.AutoDispatchEnabled must be present and false")
    if system.get("AiBaseUrl") != "":
        fail("rollback refused: system_settings.AiBaseUrl must be present and empty")
    if system.get("AdAuthEnabled") is not False:
        fail("rollback refused: system_settings.AdAuthEnabled must be present and false")
    if system.get("AdServers") != []:
        fail("rollback refused: system_settings.AdServers must be present and empty")
    sentinels = meta["startupSettings"]["sentinels"]
    if not isinstance(sentinels, list):
        fail("rollback refused: sentinels blob must be a list")
    for sentinel in sentinels:
        if not isinstance(sentinel, dict) or sentinel.get("Active") is not False:
            fail("rollback refused: every persisted Sentinel must be explicitly inactive")


def validate_preservation(path: Path) -> list[dict[str, Any]]:
    value = read_json(path)
    if not isinstance(value, dict) or value.get("schema") != "lf-round54-rollback-preservation-v1":
        fail("preservation catalog schema is missing or unknown")
    items = value.get("items")
    if not isinstance(items, list):
        fail("preservation catalog items must be an array")
    seen: set[tuple[str, str]] = set()
    categories: set[str] = set()
    normalized: list[dict[str, Any]] = []
    for item in items:
        if not isinstance(item, dict) or set(item) != {"category", "id", "status", "payloadPath", "payloadSha256"}:
            fail("preservation item must contain category, id, status, payloadPath, payloadSha256")
        category, item_id = item["category"], item["id"]
        if category not in PROTECTED_CATEGORIES or not isinstance(item_id, str) or not re.fullmatch(r"[A-Za-z0-9._-]{1,160}", item_id) or item_id in {".", ".."}:
            fail("unknown preservation category or empty item identity")
        if item["status"] != "held-unreconciled":
            fail(f"preservation item {category}/{item_id} must remain held-unreconciled")
        identity = (category.casefold(), item_id.casefold())
        if identity in seen:
            fail(f"duplicate preservation identity: {category}/{item_id}")
        seen.add(identity)
        categories.add(category)
        payload_rel = Path(item["payloadPath"])
        if payload_rel.is_absolute() or ".." in payload_rel.parts or not payload_rel.parts:
            fail(f"unsafe preservation payload path: {item_id}")
        payload_candidate = path.parent / payload_rel
        reject_reparse(payload_candidate)
        payload = payload_candidate.resolve(strict=True)
        try:
            payload.relative_to(path.parent.resolve(strict=True))
        except ValueError:
            fail(f"preservation payload escaped bundle: {item_id}")
        reject_reparse(payload)
        if not payload.is_file() or sha256_file(payload) != str(item["payloadSha256"]).lower():
            fail(f"preservation payload missing or hash mismatch: {category}/{item_id}")
        normalized.append({"category": category, "id": item_id, "status": item["status"], "payloadPath": payload_rel.as_posix(), "payloadSha256": item["payloadSha256"].lower()})
    # A category may be empty only when the catalog explicitly records zero rows for it.
    counts = value.get("counts")
    if not isinstance(counts, dict) or set(counts) != PROTECTED_CATEGORIES:
        fail("preservation catalog must give exact counts for all four protected categories")
    for category in PROTECTED_CATEGORIES:
        actual = sum(item["category"] == category for item in normalized)
        if type(counts[category]) is not int or counts[category] != actual:
            fail(f"preservation count mismatch for {category}")
    return normalized


def validate_bundle(manifest_path: Path, expected_commit: str, expected_authority_sha256: str) -> tuple[dict[str, Any], dict[str, Path], dict[str, Any]]:
    manifest_path = manifest_path.absolute()
    reject_reparse(manifest_path)
    manifest_path = manifest_path.resolve(strict=True)
    manifest = read_json(manifest_path)
    if not isinstance(manifest, dict) or manifest.get("schema") != SCHEMA:
        fail("rollback input manifest schema is unknown")
    bundle = manifest_path.parent.resolve(strict=True)
    files = manifest.get("files")
    if not isinstance(files, dict) or set(files) != set(REQUIRED_FILES):
        fail("input manifest must enumerate exactly the required paired files")
    paths = {name: file_entry(bundle, files, name) for name in REQUIRED_FILES}
    expected_commit = expected_commit.lower()
    if len(expected_commit) != 40 or any(ch not in "0123456789abcdef" for ch in expected_commit):
        fail("expected target commit must be a full 40-character lowercase commit id")
    authority_hash = sha256_file(paths["authority"])
    if authority_hash != expected_authority_sha256.lower():
        fail("authority manifest hash does not match the operator-pinned hash")

    authority = read_json(paths["authority"])
    if not isinstance(authority, dict) or authority.get("schema") != AUTHORITY_SCHEMA:
        fail("authority manifest schema is unknown")
    expected_versions = manifest.get("versions")
    if not isinstance(expected_versions, dict) or set(expected_versions) != {"authority", "schema", "source"}:
        fail("input manifest must pin authority, schema, and source-authority versions")
    for version_kind, allowed in SUPPORTED_VERSIONS.items():
        if expected_versions.get(version_kind) not in allowed:
            fail(f"unknown {version_kind} version; this rollback tool does not support it")
    for manifest_key, authority_key in (("authority", "authorityVersion"), ("schema", "schemaVersion"), ("source", "sourceAuthorityVersion")):
        if not isinstance(expected_versions[manifest_key], str) or authority.get(authority_key) != expected_versions[manifest_key]:
            fail(f"unknown or mismatched {authority_key}")
    if authority.get("targetCommit") != expected_commit:
        fail("authority target commit does not match the explicit target commit")

    web_root = paths["webRoot"]
    if not web_root.is_dir():
        fail("webRoot must be a directory")
    for leaf in ("LogForesight.Web.dll", "LogForesight.Core.dll", "build-identity.json", "appsettings.json"):
        if not (web_root / leaf).is_file():
            fail(f"paired Web release missing {leaf}")
    web_sha, core_sha = sha256_file(web_root / "LogForesight.Web.dll"), sha256_file(web_root / "LogForesight.Core.dll")
    asset_root = web_root / "wwwroot"
    assets_sha = tree_sha256(asset_root)
    web_tree_sha = tree_sha256(web_root)
    if not isinstance(authority.get("artifacts"), dict) or authority["artifacts"] != {
        "webSha256": web_sha, "coreSha256": core_sha, "assetsTreeSha256": assets_sha, "webRootTreeSha256": web_tree_sha,
    }:
        fail("Web/Core/assets do not match the authority manifest")
    identity = read_json(web_root / "build-identity.json")
    if not isinstance(identity, dict) or identity.get("Schema") != "logforesight-build-identity-v1":
        fail("build identity is missing or unknown")
    entries = identity.get("Assemblies")
    web_id = [x for x in entries or [] if isinstance(x, dict) and x.get("Name") == "LogForesight.Web"]
    core_id = [x for x in entries or [] if isinstance(x, dict) and x.get("Name") == "LogForesight.Core"]
    if len(web_id) != 1 or len(core_id) != 1:
        fail("build identity must contain exactly one Web and one Core assembly")
    stamp = web_id[0].get("SourceStamp", "")
    parts = stamp.split("|") if isinstance(stamp, str) else []
    if len(parts) != 4 or parts[0] != "lfbuild-v1" or parts[1] not in {"clean", "modified"} or parts[2] != expected_commit or len(parts[3]) != 64:
        fail("Web source stamp is unknown or does not match the exact target commit")
    if core_id[0].get("SourceStamp") != stamp or web_id[0].get("FileSha256", "").lower() != web_sha or core_id[0].get("FileSha256", "").lower() != core_sha:
        fail("Web/Core build identity is mixed or does not match the actual files")

    config = read_json_with_comments(web_root / "appsettings.json")
    storage = config.get("Storage") if isinstance(config, dict) else None
    if not isinstance(storage, dict) or storage.get("Type") != "Sqlite" or storage.get("ConnectionString", "") != "":
        fail("release configuration is not an explicit default SQLite configuration")
    auth = config.get("Auth") if isinstance(config, dict) else None
    if not isinstance(auth, dict) or auth.get("Provider") != "Stub":
        fail("isolated startup requires the release's local Stub auth provider; AD auth is refused")
    for special in ("appsettings.Production.json", "appsettings.Development.json"):
        if (web_root / special).exists():
            fail(f"environment-specific config is unknown and cannot be launched: {special}")

    db_meta = db_metadata(paths["database"])
    require_isolated_settings(db_meta)
    expected_db = authority.get("database")
    if not isinstance(expected_db, dict) or expected_db != {k: db_meta[k] for k in ("applicationId", "userVersion", "schemaSha256")}:
        fail("SQLite application id, user_version, or schema hash does not match the paired authority")
    if sha256_file(paths["config"]) != sha256_file(web_root / "appsettings.json"):
        fail("separately paired config input does not match the actual Web release config")
    if authority.get("pairedHashes") != {
        "databaseSha256": sha256_file(paths["database"]),
        "keySha256": sha256_file(paths["key"]),
        "configSha256": sha256_file(web_root / "appsettings.json"),
    }:
        fail("database/key/config are not the exact authority-paired inputs")
    if authority.get("keyFileName") != paths["key"].name:
        fail("authority key identity does not match the supplied key")
    preservation = validate_preservation(paths["preservation"])
    return manifest, paths, {"authority": authority, "database": db_meta, "preservation": preservation, "webSha256": web_sha, "coreSha256": core_sha, "assetsTreeSha256": assets_sha}


def safe_restore_root(root: Path, source_paths: dict[str, Path]) -> Path:
    absolute = root.absolute()
    reject_reparse(absolute)
    if absolute.exists():
        fail("restore root must not exist; only a new owned isolated restore root is allowed")
    parent = absolute.parent.resolve(strict=True)
    if not parent.is_dir():
        fail("restore root parent must already exist")
    reject_reparse(parent)
    canonical = parent / absolute.name
    for source in source_paths.values():
        try:
            canonical.relative_to(source.resolve(strict=True))
            fail("restore root cannot be inside an input file or directory")
        except ValueError:
            pass
        try:
            source.resolve(strict=True).relative_to(canonical)
            fail("restore root cannot contain an input file or directory")
        except ValueError:
            pass
    return canonical


def write_json_atomic(path: Path, value: Any) -> None:
    temp = path.with_name(path.name + ".tmp")
    temp.write_bytes(canonical_json(value) + b"\n")
    os.replace(temp, path)


def prepare(args: argparse.Namespace) -> dict[str, Any]:
    manifest, sources, validated = validate_bundle(Path(args.manifest), args.expected_target_commit, args.expected_authority_sha256)
    root = safe_restore_root(Path(args.restore_root), sources)
    created_root = False
    try:
        root.mkdir()
        created_root = True
        app = root / "app"
        data = root / "data"
        held = root / "held-differences"
        app.mkdir(); data.mkdir(); held.mkdir()
        (data / "Db").mkdir()
        (data / "keys").mkdir()
        shutil.copytree(sources["webRoot"], app, dirs_exist_ok=True)
        shutil.copy2(sources["database"], data / "Db" / "logforesight.db")
        shutil.copy2(sources["key"], data / "keys" / "lf-crypto.key")
        shutil.copy2(sources["config"], app / "appsettings.json")
        preservation = read_json(sources["preservation"])
        for item in validated["preservation"]:
            src = (sources["preservation"].parent / item["payloadPath"]).resolve(strict=True)
            target = held / item["category"] / item["id"]
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(src, target)
        copied = {
            "webRootTreeSha256": tree_sha256(app),
            "webSha256": sha256_file(app / "LogForesight.Web.dll"),
            "coreSha256": sha256_file(app / "LogForesight.Core.dll"),
            "identitySha256": sha256_file(app / "build-identity.json"),
            "assetsTreeSha256": tree_sha256(app / "wwwroot"),
            "databaseSha256": sha256_file(data / "Db" / "logforesight.db"),
            "keySha256": sha256_file(data / "keys" / "lf-crypto.key"),
            "configSha256": sha256_file(app / "appsettings.json"),
            "preservationSha256": sha256_file(sources["preservation"]),
        }
        state = {
            "schema": STATE_SCHEMA,
            "phase": "prepared-isolated",
            "targetCommit": args.expected_target_commit.lower(),
            "authoritySha256": sha256_file(sources["authority"]),
            "authorityVersion": validated["authority"]["authorityVersion"],
            "schemaVersion": validated["authority"]["schemaVersion"],
            "sourceAuthorityVersion": validated["authority"]["sourceAuthorityVersion"],
            "root": str(root.resolve()),
            "files": copied,
            "sqlite": {k: validated["database"][k] for k in ("applicationId", "userVersion", "schemaSha256")},
            "preservationCounts": preservation["counts"],
            "preservationItems": validated["preservation"],
            "externalMail": "held; dispatch disabled until separately deduplicated",
        }
        state["sealSha256"] = hashlib.sha256(canonical_json(state)).hexdigest()
        write_json_atomic(root / "rollback-state.json", state)
        return {"result": "prepared-isolated", "restoreRoot": str(root), "targetCommit": state["targetCommit"], "database": state["sqlite"], "preservationCounts": state["preservationCounts"], "mailDispatch": "disabled"}
    except Exception:
        # The root was absent and created by this invocation; clean only this exact directory.
        if created_root and root.exists():
            shutil.rmtree(root)
        raise


def verify_restore_root(root: Path, expected_commit: str) -> tuple[dict[str, Any], dict[str, Any]]:
    reject_reparse(root.absolute())
    root = root.resolve(strict=True)
    reject_reparse(root)
    state_path = root / "rollback-state.json"
    state = read_json(state_path)
    if not isinstance(state, dict) or state.get("schema") != STATE_SCHEMA or state.get("phase") != "prepared-isolated":
        fail("restore operation state is not prepared and isolated")
    seal = state.get("sealSha256")
    unsigned = dict(state); unsigned.pop("sealSha256", None)
    if not isinstance(seal, str) or hashlib.sha256(canonical_json(unsigned)).hexdigest() != seal:
        fail("sealed operation state was modified")
    if Path(state.get("root", "")).resolve() != root or state.get("targetCommit") != expected_commit.lower():
        fail("restore root or target commit does not match the sealed operation")
    app, data = root / "app", root / "data"
    actuals = {
        "webRootTreeSha256": tree_sha256(app),
        "webSha256": sha256_file(app / "LogForesight.Web.dll"),
        "coreSha256": sha256_file(app / "LogForesight.Core.dll"),
        "identitySha256": sha256_file(app / "build-identity.json"),
        "assetsTreeSha256": tree_sha256(app / "wwwroot"),
        "databaseSha256": sha256_file(data / "Db" / "logforesight.db"),
        "keySha256": sha256_file(data / "keys" / "lf-crypto.key"),
        "configSha256": sha256_file(app / "appsettings.json"),
    }
    intent_valid = False
    if "reconciliationIntentSha256" in state:
        intent_path = root / "delta-reconcile-intent.json"
        if not intent_path.is_file() or sha256_file(intent_path) != state["reconciliationIntentSha256"]:
            fail("sealed delta reconciliation intent is missing or changed")
        intent = read_json(intent_path)
        prepared_database_hash = state.get("preparedDatabaseSha256", state["files"].get("databaseSha256"))
        if not isinstance(intent, dict) or intent.get("schema") != "lf-round54-rollback-reconcile-intent-v1" or intent.get("root") != str(root) or intent.get("targetCommit") != state["targetCommit"] or intent.get("baseDatabaseSha256") != prepared_database_hash:
            fail("delta reconciliation intent does not match the owned restore root")
        intent_seal = intent.get("sealSha256")
        intent_unsigned = dict(intent); intent_unsigned.pop("sealSha256", None)
        if not isinstance(intent_seal, str) or hashlib.sha256(canonical_json(intent_unsigned)).hexdigest() != intent_seal:
            fail("delta reconciliation intent seal is invalid")
        delta_copy = root / "held-differences" / "delta-catalog.json"
        if not delta_copy.is_file() or sha256_file(delta_copy) != state.get("deltaCatalogSha256") or sha256_file(delta_copy) != intent.get("deltaManifestSha256"):
            fail("owned delta catalog is missing or differs from the sealed reconciliation intent")
        intent_valid = True
    for key, value in actuals.items():
        if value != state["files"].get(key) and not (key == "databaseSha256" and intent_valid):
            fail(f"restore input changed after preparation: {key}")
    meta = db_metadata(data / "Db" / "logforesight.db")
    require_isolated_settings(meta)
    for key in ("applicationId", "userVersion", "schemaSha256"):
        if meta[key] != state["sqlite"].get(key):
            fail(f"SQLite metadata changed after preparation: {key}")
    # The restored copy must remain SQLite-only and must not carry any SQL Server connection string.
    config = read_json_with_comments(app / "appsettings.json")
    storage = config.get("Storage") if isinstance(config, dict) else None
    if not isinstance(storage, dict) or storage.get("Type") != "Sqlite" or storage.get("ConnectionString", "") != "":
        fail("restored config no longer describes isolated SQLite storage")
    for item in state.get("preservationItems", []):
        held = root / "held-differences" / item["category"] / item["id"]
        if not held.is_file() or sha256_file(held) != item["payloadSha256"]:
            fail(f"held recovery difference missing or changed: {item['category']}/{item['id']}")
    if "deltaReconciliationSha256" in state:
        receipt_path = root / "delta-reconciliation.json"
        backup_path = root / "reconcile-backup" / "logforesight.after-reconcile.db"
        if not receipt_path.is_file() or sha256_file(receipt_path) != state["deltaReconciliationSha256"]:
            fail("sealed delta reconciliation receipt is missing or changed")
        if not backup_path.is_file() or sha256_file(backup_path) != state.get("postReconcileBackupSha256"):
            fail("sealed post-reconcile SQLite backup is missing or changed")
        receipt = read_json(receipt_path)
        if not isinstance(receipt, dict) or receipt.get("schema") != "lf-round54-rollback-reconcile-backup-v1":
            fail("post-reconcile backup receipt schema is unknown")
        receipt_seal = receipt.get("sealSha256")
        unsigned_receipt = dict(receipt); unsigned_receipt.pop("sealSha256", None)
        if not isinstance(receipt_seal, str) or hashlib.sha256(canonical_json(unsigned_receipt)).hexdigest() != receipt_seal:
            fail("post-reconcile backup receipt seal is invalid")
        if receipt.get("postReconcileDatabaseSha256") != sha256_file(backup_path) or receipt.get("targetCommit") != state["targetCommit"]:
            fail("post-reconcile backup no longer matches its sealed authority")
        if receipt.get("restoreDatabaseSha256") != sha256_file(data / "Db" / "logforesight.db"):
            fail("reconciled restore database differs from its sealed after-backup relation")
        if receipt.get("deltaManifestSha256") != intent.get("deltaManifestSha256"):
            fail("reconciliation receipt differs from its sealed intent")
        backup_meta = db_metadata(backup_path)
        for key in ("applicationId", "userVersion", "schemaSha256"):
            if backup_meta[key] != meta[key] or receipt.get("postReconcileMetadata", {}).get(key) != meta[key]:
                fail(f"post-reconcile backup schema authority changed: {key}")
        from delta_reconcile import verify_backup_projection
        verify_backup_projection(data / "Db" / "logforesight.db", backup_path, receipt)
    return state, meta


def port_is_free(port: int) -> bool:
    probe = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    try:
        if hasattr(socket, "SO_EXCLUSIVEADDRUSE"):
            probe.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
        probe.bind(("127.0.0.1", port))
        probe.listen(1)
        return True
    except OSError:
        return False
    finally:
        probe.close()


def launch(args: argparse.Namespace) -> dict[str, Any]:
    root = Path(args.restore_root).resolve(strict=True)
    state, meta = verify_restore_root(root, args.expected_target_commit)
    if not port_is_free(args.port):
        fail(f"loopback port {args.port} is already owned or unavailable; Web was not started")
    dotnet = shutil.which("dotnet")
    if not dotnet:
        fail("dotnet executable was not found; Web was not started")
    app = root / "app"
    dll = app / "LogForesight.Web.dll"
    if not dll.is_file() or sha256_file(dll) != state["files"]["webSha256"]:
        fail("verified Web assembly is missing or changed; Web was not started")
    # Keep the OS identity/runtime variables required by Windows group resolution while
    # stripping inherited application configuration that could redirect DB or integrations.
    # Explicit OS/runtime allowlist avoids inherited proxy variables and unknown app knobs.
    allowed_os = {"PATH", "SYSTEMROOT", "WINDIR", "COMSPEC", "PATHEXT", "TEMP", "TMP",
                  "USERNAME", "USERDOMAIN", "COMPUTERNAME", "DOTNET_ROOT", "DOTNET_ROOT_X64"}
    env = {key: value for key, value in os.environ.items() if key.upper() in allowed_os}
    env.update({"DOTNET_NOLOGO": "1", "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
                "ASPNETCORE_ENVIRONMENT": "Development", "DOTNET_ENVIRONMENT": "Development", "Auth__Provider": "Stub",
                "Storage__Type": "Sqlite", "Storage__DataRoot": str(root / "data"), "Storage__ConnectionString": ""})
    url = f"http://127.0.0.1:{args.port}"
    if not port_is_free(args.port):
        fail(f"loopback port {args.port} became occupied during prelaunch checks; Web was not started")
    with (root / "web.stdout.log").open("ab") as stdout_log, (root / "web.stderr.log").open("ab") as stderr_log:
        process = subprocess.Popen([dotnet, str(dll), "--contentRoot", str(app), "--urls", url], cwd=app, env=env,
                                   stdin=subprocess.DEVNULL, stdout=stdout_log, stderr=stderr_log, close_fds=True)
    ready = False
    # Old supported builds can cold-start EF schema validation slowly on a tiny restore root.
    # This remains a bounded readiness wait; timeout always kills this exact child and stays closed.
    deadline = time.monotonic() + 60
    while time.monotonic() < deadline:
        if process.poll() is not None:
            fail(f"verified Web process exited during startup with code {process.returncode}; restore remains isolated")
        probe = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        probe.settimeout(0.2)
        try:
            ready = probe.connect_ex(("127.0.0.1", args.port)) == 0
        finally:
            probe.close()
        if ready:
            break
        time.sleep(0.1)
    if not ready:
        process.terminate()
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            process.kill(); process.wait(timeout=5)
        fail("verified Web did not bind its isolated loopback endpoint; restore remains closed")
    try:
        owner = read_running_process_identity(process.pid, dotnet, dll, app, url)
        new_state = dict(state); new_state.pop("sealSha256", None)
        new_state["phase"] = "web-started-isolated"
        new_state["webProcess"] = owner
        new_state["sealSha256"] = hashlib.sha256(canonical_json(new_state)).hexdigest()
        write_json_atomic(root / "rollback-state.json", new_state)
    except Exception:
        if process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                process.kill(); process.wait(timeout=5)
        raise
    return {"result": "web-started-isolated", "restoreRoot": str(root), "targetCommit": state["targetCommit"], "pid": process.pid,
            "url": url, "schedule": "disabled", "sources": "disabled at startup", "mail": "disabled; notification differences held", "sqlite": {k: meta[k] for k in ("applicationId", "userVersion", "schemaSha256")}}


def windows_command_tokens(command: str) -> list[str]:
    if os.name != "nt" or not command.strip():
        fail("Windows command-line parsing is unavailable or the command line is empty")
    import ctypes
    from ctypes import wintypes
    argc = ctypes.c_int()
    shell = ctypes.WinDLL("shell32", use_last_error=True)
    parse = shell.CommandLineToArgvW
    parse.argtypes = [wintypes.LPCWSTR, ctypes.POINTER(ctypes.c_int)]
    parse.restype = ctypes.POINTER(wintypes.LPWSTR)
    argv = parse(command, ctypes.byref(argc))
    if not argv:
        fail("Windows command-line parsing failed")
    free = ctypes.WinDLL("kernel32", use_last_error=True).LocalFree
    free.argtypes = [wintypes.HLOCAL]
    free.restype = wintypes.HLOCAL
    try:
        return [argv[index] for index in range(argc.value)]
    finally:
        free(ctypes.cast(argv, wintypes.HLOCAL))


def require_launch_tokens(tokens: list[str], executable: str, dll: Path, app: Path, url: str) -> None:
    expected = [executable, str(dll.resolve()), "--contentRoot", str(app.resolve()), "--urls", url]
    if len(tokens) != len(expected):
        fail("started Web arguments differ from the exact isolated launcher")
    for index, (actual, wanted) in enumerate(zip(tokens, expected)):
        if index in (0, 1, 3):
            matches = Path(actual).resolve() == Path(wanted).resolve()
        else:
            matches = actual == wanted
        if not matches:
            fail("started Web arguments differ from the exact isolated launcher")


def read_running_process_identity(pid: int, expected_executable: str, dll: Path, app: Path, url: str) -> dict[str, Any]:
    pwsh = shutil.which("pwsh") or shutil.which("powershell")
    if not pwsh:
        fail("PowerShell is required to verify the actual Windows process identity")
    script = "$ErrorActionPreference='Stop'; $proc=Get-Process -Id %d; $proc.Refresh(); $cim=Get-CimInstance Win32_Process -Filter 'ProcessId = %d'; " % (pid, pid)
    port = int(url.rsplit(":", 1)[1])
    script += "$listeners=@(Get-NetTCPConnection -State Listen -LocalPort %d -ErrorAction Stop | Select-Object LocalAddress,OwningProcess); " % port
    script += "[pscustomobject]@{ProcessId=$proc.Id;StartTimeUtcTicks=$proc.StartTime.ToUniversalTime().Ticks;ExecutablePath=$cim.ExecutablePath;CommandLine=$cim.CommandLine;Listeners=$listeners}|ConvertTo-Json -Compress -Depth 4"
    result = subprocess.run([pwsh, "-NoProfile", "-NonInteractive", "-Command", script], capture_output=True, text=True, timeout=15)
    if result.returncode != 0:
        fail("could not verify launched Web process identity")
    try:
        identity = json.loads(result.stdout.strip())
    except json.JSONDecodeError:
        fail("PowerShell returned malformed Web process identity")
    expected_exe = str(Path(expected_executable).resolve())
    command = str(identity.get("CommandLine", ""))
    if int(identity.get("ProcessId", -1)) != pid or Path(str(identity.get("ExecutablePath", ""))).resolve() != Path(expected_exe):
        fail("started Web executable or PID differs from the verified launcher")
    require_launch_tokens(windows_command_tokens(command), expected_exe, dll, app, url)
    listeners = identity.get("Listeners")
    if not isinstance(listeners, list) or not listeners or any(
        listener.get("LocalAddress") != "127.0.0.1" or int(listener.get("OwningProcess", -1)) != pid
        for listener in listeners
    ):
        fail("loopback listener is not owned exclusively by the verified Web process")
    return {"processId": pid, "startTimeUtcTicks": int(identity["StartTimeUtcTicks"]), "executablePath": expected_exe, "commandLine": command}


def stop(args: argparse.Namespace) -> dict[str, Any]:
    root = Path(args.restore_root).absolute()
    reject_reparse(root)
    root = root.resolve(strict=True)
    state = read_json(root / "rollback-state.json")
    if not isinstance(state, dict) or state.get("schema") != STATE_SCHEMA or state.get("phase") != "web-started-isolated":
        fail("restore root has no recorded isolated Web process to stop")
    seal = state.get("sealSha256")
    unsigned = dict(state); unsigned.pop("sealSha256", None)
    if not isinstance(seal, str) or hashlib.sha256(canonical_json(unsigned)).hexdigest() != seal:
        fail("sealed operation state was modified; refusing to stop any process")
    if Path(state.get("root", "")).resolve() != root or state.get("targetCommit") != args.expected_target_commit.lower():
        fail("restore root or target commit differs from the recorded Web process")
    owner = state.get("webProcess")
    if not isinstance(owner, dict) or set(owner) != {"processId", "startTimeUtcTicks", "executablePath", "commandLine"}:
        fail("isolated Web process identity record is malformed")
    stop_windows_process(owner, root)
    new_state = dict(state); new_state.pop("sealSha256", None)
    new_state["phase"] = "stopped-isolated"
    new_state["stoppedAtUtc"] = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
    new_state["sealSha256"] = hashlib.sha256(canonical_json(new_state)).hexdigest()
    write_json_atomic(root / "rollback-state.json", new_state)
    return {"result": "stopped-isolated", "restoreRoot": str(root), "pid": owner["processId"], "targetCommit": state["targetCommit"]}


def stop_windows_process(owner: dict[str, Any], root: Path) -> None:
    pwsh = shutil.which("pwsh") or shutil.which("powershell")
    if not pwsh:
        fail("PowerShell is required to stop a process by verified Windows identity")
    pid = int(owner["processId"])
    ticks = int(owner["startTimeUtcTicks"])
    expected_exe = str(owner["executablePath"])
    expected_cmd = str(owner["commandLine"])
    b64 = lambda value: __import__("base64").b64encode(str(value).encode("utf-8")).decode("ascii")
    root_b64, exe_b64, cmd_b64 = b64(root), b64(expected_exe), b64(expected_cmd)
    script = "$ErrorActionPreference='Stop'; $id=%d; $ticks=[long]%d; " % (pid, ticks)
    script += "$root=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('%s')); " % root_b64
    script += "$expectedExe=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('%s')); " % exe_b64
    script += "$expectedCmd=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('%s')); " % cmd_b64
    script += "$p=Get-Process -Id $id -ErrorAction Stop; $p.Refresh(); $c=Get-CimInstance Win32_Process -Filter ('ProcessId = '+$id) -ErrorAction Stop; "
    script += "if($p.StartTime.ToUniversalTime().Ticks -ne $ticks -or [IO.Path]::GetFullPath($c.ExecutablePath) -ine $expectedExe -or $c.CommandLine -cne $expectedCmd -or $c.CommandLine -notlike ('*'+$root+'*')){throw 'Process identity mismatch'}; "
    script += "$p.Kill(); if(-not $p.WaitForExit(15000)){throw 'Owned process did not exit'}; Write-Output 'stopped-exact-owned-process'"
    result = subprocess.run([pwsh, "-NoProfile", "-NonInteractive", "-Command", script], capture_output=True, text=True, timeout=25)
    if result.returncode != 0 or "stopped-exact-owned-process" not in result.stdout:
        fail("exact Web process identity did not verify; no unrelated PID was stopped")


def release_gate_status(root: Path, state: dict[str, Any], receipt: dict[str, Any]) -> dict[str, Any]:
    """Require every initially held payload to match a reconciled native delta item."""
    delta_items = {(item.get("category"), str(item.get("id"))): item for item in receipt.get("items", []) if isinstance(item, dict)}
    unresolved: list[dict[str, str]] = []
    mail_hold = bool(receipt.get("mailUnknownOutcomes"))
    for held_item in state.get("preservationItems", []):
        category, item_id = held_item.get("category"), str(held_item.get("id"))
        entry = delta_items.get((category, item_id))
        resolved = bool(entry and entry.get("outcome") in {"applied", "already-applied"})
        payload = root / "held-differences" / category / item_id
        if resolved:
            try:
                payload_value = json.loads(payload.read_text(encoding="utf-8"))
                delta_copy = read_json(root / "held-differences" / "delta-catalog.json")
                source_entry = next(item for item in delta_copy["items"] if item.get("category") == category and str(item.get("id")) == item_id)
                expected_value = source_entry.get("after")
                if category == "notificationReceipt" and isinstance(expected_value, dict):
                    expected_value = json.loads(str(expected_value.get("content", "")))
                resolved = payload_value == expected_value
            except (OSError, StopIteration, TypeError, ValueError, json.JSONDecodeError):
                resolved = False
        if not resolved:
            unresolved.append({"category": str(category), "id": item_id})
            if category == "notificationReceipt": mail_hold = True
    held_delta = [item for item in receipt.get("items", []) if item.get("outcome") == "held-unreconciled"]
    unresolved_data = [item for item in held_delta if item.get("category") != "notificationReceipt"]
    mail_hold = mail_hold or bool(receipt.get("mailUnknownOutcomes")) or any(item.get("category") == "notificationReceipt" for item in held_delta)
    return {"dataRecoveryEligible": not unresolved_data and not any(item["category"] != "notificationReceipt" for item in unresolved),
            "mailDispatchEligible": bool(receipt.get("mailReleaseEligible")) and not mail_hold,
            "unresolvedDataItems": unresolved_data + [item for item in unresolved if item["category"] != "notificationReceipt"],
            "unresolvedPreservationItems": unresolved,
            "mailHold": mail_hold}


def parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(description=__doc__)
    sub = p.add_subparsers(dest="command", required=True)
    prep = sub.add_parser("prepare", help="validate exact paired rollback inputs and copy them into a new isolated root")
    prep.add_argument("--manifest", required=True)
    prep.add_argument("--expected-target-commit", required=True)
    prep.add_argument("--expected-authority-sha256", required=True)
    prep.add_argument("--restore-root", required=True)
    check = sub.add_parser("verify", help="recheck the sealed isolated restore root before any Web launch")
    check.add_argument("--restore-root", required=True)
    check.add_argument("--expected-target-commit", required=True)
    start = sub.add_parser("launch", help="start only the verified Web copy on loopback with isolated SQLite and disabled integrations")
    start.add_argument("--restore-root", required=True)
    start.add_argument("--expected-target-commit", required=True)
    start.add_argument("--port", type=int, default=5097)
    halt = sub.add_parser("stop", help="stop only the exact Web process recorded by this isolated restore root")
    halt.add_argument("--restore-root", required=True)
    halt.add_argument("--expected-target-commit", required=True)
    capture = sub.add_parser("capture-delta", help="capture exact rollback-owned business-row and mail-state differences from immutable SQLite snapshots")
    capture.add_argument("--before-db", required=True)
    capture.add_argument("--current-db", required=True)
    capture.add_argument("--output", required=True)
    reconcile_cmd = sub.add_parser("reconcile", help="CAS-reconcile a reviewed exact-schema delta in a new restore root and create a sealed after-backup")
    reconcile_cmd.add_argument("--restore-root", required=True)
    reconcile_cmd.add_argument("--expected-target-commit", required=True)
    reconcile_cmd.add_argument("--delta-manifest", required=True)
    reconcile_cmd.add_argument("--expected-delta-sha256", required=True)
    release = sub.add_parser("release-check", help="report data recovery and SMTP dedup gates from the sealed reconciliation receipt")
    release.add_argument("--restore-root", required=True)
    release.add_argument("--expected-target-commit", required=True)
    authorize = sub.add_parser("release", help="seal a controlled restore decision after data and SMTP gates; integrations remain disabled at startup")
    authorize.add_argument("--restore-root", required=True)
    authorize.add_argument("--expected-target-commit", required=True)
    return p


def main(argv: list[str] | None = None) -> int:
    args = parser().parse_args(argv)
    try:
        if args.command == "capture-delta":
            from delta_reconcile import capture_delta
            result = capture_delta(Path(args.before_db), Path(args.current_db), Path(args.output))
        elif args.command == "reconcile":
            from delta_reconcile import reconcile
            if len(args.expected_delta_sha256) != 64 or any(c not in "0123456789abcdef" for c in args.expected_delta_sha256.lower()):
                fail("expected delta SHA-256 must be 64 hexadecimal characters")
            result = reconcile(Path(args.restore_root), args.expected_target_commit, Path(args.delta_manifest), args.expected_delta_sha256.lower())
        elif args.command == "release-check":
            state, _ = verify_restore_root(Path(args.restore_root), args.expected_target_commit)
            receipt_path = Path(args.restore_root) / "delta-reconciliation.json"
            if not receipt_path.is_file(): fail("no sealed delta reconciliation receipt exists")
            receipt = read_json(receipt_path)
            gates = release_gate_status(Path(args.restore_root), state, receipt)
            result = {"result": "release-gates-reviewed", "restoreRoot": state["root"], "targetCommit": state["targetCommit"],
                      "dataRecoveryEligible": gates["dataRecoveryEligible"], "mailDispatchEligible": gates["mailDispatchEligible"],
                      "unresolvedDataItems": gates["unresolvedDataItems"], "unresolvedPreservationItems": gates["unresolvedPreservationItems"],
                      "mailDispatch": "held" if gates["mailHold"] else "dedup-reviewed; still disabled until an operator enables through the normal settings path"}
        elif args.command == "release":
            root = Path(args.restore_root).resolve(strict=True)
            state, _ = verify_restore_root(root, args.expected_target_commit)
            receipt_path = root / "delta-reconciliation.json"
            if not receipt_path.is_file(): fail("no sealed delta reconciliation receipt exists")
            receipt = read_json(receipt_path)
            gates = release_gate_status(root, state, receipt)
            if not gates["dataRecoveryEligible"]: fail("data recovery delta remains held; no controlled restore decision was recorded")
            if state.get("releaseDecision") is not None: fail("a controlled restore decision is already sealed")
            mail_hold = not gates["mailDispatchEligible"]
            new_state = dict(state); new_state.pop("sealSha256", None)
            new_state["releaseDecision"] = {"deltaReconciliationSha256": state["deltaReconciliationSha256"],
                                             "dataRecovery": "released", "mailDispatch": "held" if mail_hold else "dedup-reviewed-but-disabled",
                                             "unresolvedPreservationItems": gates["unresolvedPreservationItems"],
                                             "targetCommit": state["targetCommit"]}
            new_state["sealSha256"] = hashlib.sha256(canonical_json(new_state)).hexdigest()
            write_json_atomic(root / "rollback-state.json", new_state)
            result = {"result": "controlled-restore-released", "restoreRoot": state["root"], "targetCommit": state["targetCommit"],
                      "dataRecovery": "released", "mailDispatch": "held" if mail_hold else "dedup-reviewed-but-disabled"}
        elif args.command == "prepare":
            result = prepare(args)
        elif args.command == "verify":
            state, meta = verify_restore_root(Path(args.restore_root), args.expected_target_commit)
            result = {"result": "verified-closed", "restoreRoot": state["root"], "targetCommit": state["targetCommit"],
                      "preservationCounts": state["preservationCounts"], "mailDispatch": "disabled", "sqlite": {k: meta[k] for k in ("applicationId", "userVersion", "schemaSha256")}}
        elif args.command == "launch":
            if not 1024 <= args.port <= 65535:
                fail("port must be 1024..65535")
            root = Path(args.restore_root).resolve(strict=True)
            state, _ = verify_restore_root(root, args.expected_target_commit)
            if "reconciliationIntentSha256" in state and ("deltaReconciliationSha256" not in state or not isinstance(state.get("releaseDecision"), dict)):
                fail("delta reconciliation has not passed the controlled restore release gate; Web was not started")
            result = launch(args)
        else:
            result = stop(args)
        print(json.dumps(result, ensure_ascii=False, sort_keys=True))
        return 0
    except (Refusal, OSError, ValueError, KeyError, TypeError) as exc:
        print(json.dumps({"result": "refused-closed", "reason": str(exc)}, ensure_ascii=False), file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
