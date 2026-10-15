"""Read-only E2 source receipt and SQLite catalog preflight.

The command line always requires the production fixed shape. The optional
expected_counts argument exists only for the tiny in-process contract tests.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import sqlite3
import tempfile
import unicodedata
from contextlib import closing
from pathlib import Path
from collections import OrderedDict
from urllib.parse import urlparse
import ipaddress

FIXED = {"devices": 3_000, "sensors": 15_000, "hostMaps": 3_000, "values": 64_800_000}


def is_safe_time_basis_reference(value: object) -> bool:
    """Mirror PrtgMonitoringController.SafeTimeBasisReference exactly."""
    if not isinstance(value, str) or not value.strip():
        return False
    # .NET string.Length counts UTF-16 code units; Python len() counts Unicode
    # code points, so use the API's unit for references containing astral text.
    if len(value.encode("utf-16-le", errors="surrogatepass")) // 2 > 128:
        return False
    if any(unicodedata.category(character) == "Cc" for character in value):
        return False
    return not any(separator in value for separator in ("://", "/", "\\", "?", "#", "@"))


def validate_monitoring_plan_reference(path: str | Path) -> None:
    plan_path = _resolve_no_reparse(path)
    plan = _json(plan_path, 8 * 1024 * 1024)
    if plan.get("schema") != "round54-e2-monitoring-plan-v1":
        raise ValueError("unsupported operator-reviewed monitoring plan")
    if not is_safe_time_basis_reference(plan.get("timeBasisEvidenceReference")):
        raise ValueError("monitoring plan timeBasisEvidenceReference violates the public API safe-reference contract")


def _is_reparse(path: Path) -> bool:
    if path.is_symlink():
        return True
    if os.name == "nt":
        import ctypes
        attrs = ctypes.windll.kernel32.GetFileAttributesW(str(path))
        return attrs != 0xFFFFFFFF and bool(attrs & 0x400)  # FILE_ATTRIBUTE_REPARSE_POINT
    return False


def _json(path: Path, limit: int) -> dict:
    if _is_reparse(path) or path.stat().st_size > limit:
        raise ValueError("receipt is reparse-linked or exceeds its byte limit")
    value = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(value, dict):
        raise ValueError("receipt root must be an object")
    return value


def _resolve_no_reparse(path: str | Path) -> Path:
    lexical = Path(os.path.abspath(path))
    for ancestor in (lexical, *lexical.parents):
        if _is_reparse(ancestor):
            raise ValueError("path or an ancestor is a reparse point")
    return lexical.resolve(strict=True)


def _manifest_hash(manifest: dict) -> str:
    # Property order and camel-case names match Program.cs HashManifest's
    # anonymous projection. All numeric integral doubles are written as JSON
    # integers, as System.Text.Json writes 0.0/1.0 in its shortest form.
    names = ("schema", "schemaVersion", "runId", "rootPath", "databasePath", "provider",
             "hosts", "sensors", "days", "requestedRows", "rowOrder", "typedSlotsPerRow",
             "sourceFingerprint", "originalProvenance", "createdUtc", "runDate",
             "originalSourceFingerprint", "originalProgressSha256", "originalLogSha256",
             "originalProgressRows", "originalAuditedRows", "originalUnloggedTailRows",
             "originalProgressElapsedSeconds", "originalProgressSampledDbBytes",
             "databaseBytesAtAdoption", "walBytesAtAdoption", "shmBytesAtAdoption")
    projected = OrderedDict((name, manifest.get(name, _default_manifest_value(name))) for name in names)
    # System.Text.Json's WriteIndented uses CRLF on Windows. The manifest
    # checksum covers the serialized bytes, so normalize the Python encoder's
    # LF output to the producer's platform newline before hashing.
    encoded = json.dumps(projected, ensure_ascii=True, indent=2, separators=(",", ": ")).replace("\n", "\r\n")
    return hashlib.sha256(encoded.encode("utf-8")).hexdigest().upper()


def _default_manifest_value(name: str):
    if name in {"schemaVersion", "hosts", "sensors", "days", "requestedRows", "typedSlotsPerRow",
                "originalProgressRows", "originalAuditedRows", "originalUnloggedTailRows",
                "originalProgressSampledDbBytes", "databaseBytesAtAdoption", "walBytesAtAdoption", "shmBytesAtAdoption"}:
        return 0
    if name == "originalProgressElapsedSeconds":
        return 0
    if name in {"createdUtc"}:
        return "0001-01-01T00:00:00+00:00"
    return ""


def validate_source(root: str | Path, descriptor_path: str | Path,
                    expected_counts: dict[str, int] = FIXED,
                    monitoring_plan_path: str | Path | None = None) -> dict:
    root = _resolve_no_reparse(root)
    if not root.is_dir():
        raise ValueError("owned source root must be a regular directory")

    if monitoring_plan_path is not None:
        validate_monitoring_plan_reference(monitoring_plan_path)

    descriptor_path = _resolve_no_reparse(descriptor_path)
    desc = _json(descriptor_path, 64 * 1024)
    if desc.get("schema") != "e2-owned-provider-v1" or desc.get("provider") != "sqlite":
        raise ValueError("provider descriptor must declare the owned SQLite runtime")
    endpoint = urlparse(str(desc.get("prtgUrl", "")))
    try:
        endpoint_ip = ipaddress.ip_address(endpoint.hostname or "")
    except ValueError as exc:
        raise ValueError("provider descriptor must pin a numeric-loopback PRTG URL") from exc
    if endpoint.scheme not in {"http", "https"} or not endpoint_ip.is_loopback or endpoint.port is None or endpoint.username or endpoint.password or endpoint.query or endpoint.fragment:
        raise ValueError("provider descriptor PRTG URL is not an explicit loopback endpoint")
    data_root = _resolve_no_reparse(desc.get("dataRoot", ""))
    if data_root != root or desc.get("retentionDays") != 180:
        raise ValueError("descriptor dataRoot/provider retention declaration differs from source root")

    manifest_path = root / "n6-run-manifest.json"
    result_path = root / "n6-high-water-result.json"
    checkpoint_path = root / "n6-checkpoint.json"
    if checkpoint_path.exists():
        raise ValueError("source still has a checkpoint; full completed receipt is required")
    manifest = _json(manifest_path, 256 * 1024)
    result = _json(result_path, 1 * 1024 * 1024)
    if manifest.get("schema") != "LogForesight.N6StorageRunManifest" or manifest.get("schemaVersion") != 1:
        raise ValueError("unsupported E1 manifest")
    if result.get("schema") != "LogForesight.N6GeneratedHighWaterResult" or result.get("schemaVersion") != 2:
        raise ValueError("unsupported E1 completion receipt")
    if manifest.get("rootPath") != str(root) or manifest.get("provider") != "sqlite":
        raise ValueError("manifest root or provider does not identify this owned root")
    if result.get("runId") != manifest.get("runId") or result.get("manifestSha256") != manifest.get("manifestSha256"):
        raise ValueError("completion receipt is not bound to the exact manifest identity")
    if manifest.get("manifestSha256", "") == "" or len(manifest["manifestSha256"]) != 64:
        raise ValueError("manifest identity hash is malformed")
    if manifest["manifestSha256"].upper() != _manifest_hash(manifest):
        raise ValueError("manifest logical checksum is invalid")
    if manifest.get("requestedRows") != expected_counts["values"] or result.get("persistedRows") != expected_counts["values"] or result.get("rowsCommitted") != expected_counts["values"]:
        raise ValueError("E1 receipt does not declare the complete fixed value denominator")
    if result.get("fullFixedShape") is not True or result.get("fullWorkloadAccepted") is not False or result.get("capacityAccepted") is not False or result.get("retentionAccepted") is not False or result.get("recoveryAccepted") is not False or result.get("wholeRoundAccepted") is not False:
        raise ValueError("receipt shape or non-promotion flags are invalid")
    if manifest.get("hosts") != expected_counts["devices"] or manifest.get("sensors") != expected_counts["sensors"] or manifest.get("days") != 180 or manifest.get("typedSlotsPerRow") != 4:
        raise ValueError("manifest does not declare the exact 3,000/15,000/180-day/four-slot catalog shape")

    database = _resolve_no_reparse(manifest.get("databasePath", ""))
    if root not in database.parents:
        raise ValueError("manifest database escapes the owned source root or is a reparse point")
    if desc.get("databaseFile") != str(database):
        raise ValueError("provider descriptor database path differs from manifest")
    uri = f"file:{database.as_posix()}?mode=ro"
    with closing(sqlite3.connect(uri, uri=True, timeout=2.0)) as db:
        db.execute("PRAGMA query_only=ON")
        tables = {row[0] for row in db.execute("SELECT name FROM sqlite_master WHERE type='table'")}
        required = {"lf_prtg_devices", "lf_prtg_sensors", "lf_prtg_host_map", "lf_prtg_values", "lf_n6_generation_control"}
        if not required.issubset(tables):
            raise ValueError("SQLite catalog is missing required E1 ownership/data tables")
        counts = {
            "devices": db.execute("SELECT COUNT(*) FROM lf_prtg_devices").fetchone()[0],
            "sensors": db.execute("SELECT COUNT(*) FROM lf_prtg_sensors").fetchone()[0],
            "hostMaps": db.execute("SELECT COUNT(*) FROM lf_prtg_host_map").fetchone()[0],
            "values": db.execute("SELECT COUNT(*) FROM lf_prtg_values").fetchone()[0],
        }
        marker = db.execute("SELECT run_id,manifest_sha256,committed_rows FROM lf_n6_generation_control").fetchall()
        if marker != [(manifest["runId"], manifest["manifestSha256"], expected_counts["values"])]:
            raise ValueError("SQLite ownership marker differs from the complete manifest")
    if counts != expected_counts:
        raise ValueError("SQLite catalog counts do not match the fixed complete shape")
    stable_identity = {
        "schema": "e2-source-preflight-identity-v1", "runId": manifest["runId"],
        "manifestSha256": manifest["manifestSha256"].upper(),
        "manifestFileSha256": hashlib.sha256(manifest_path.read_bytes()).hexdigest().upper(),
        "completionReceiptFileSha256": hashlib.sha256(result_path.read_bytes()).hexdigest().upper(),
        "descriptorSha256": hashlib.sha256(descriptor_path.read_bytes()).hexdigest().upper(),
        "dataRoot": str(root), "databasePath": str(database), "catalog": counts,
        "retentionDaysDeclared": 180,
    }
    identity_sha = hashlib.sha256(json.dumps(stable_identity, sort_keys=True, separators=(",", ":")).encode("utf-8")).hexdigest().upper()
    return {
        "schema": "e2-source-preflight-v1", "status": "complete-owned-source",
        "runId": manifest["runId"], "manifestSha256": manifest["manifestSha256"],
        "manifestFileSha256": stable_identity["manifestFileSha256"],
        "completionReceiptFileSha256": stable_identity["completionReceiptFileSha256"],
        "descriptorSha256": stable_identity["descriptorSha256"], "sourceIdentitySha256": identity_sha,
        "dataRoot": str(root), "databasePath": str(database), "databaseBytes": database.stat().st_size,
        "catalog": counts, "retentionDaysDeclared": 180,
        "capacityAccepted": False, "retentionAccepted": False,
        "formalAcceptance": False, "wholeRoundAccepted": False,
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source-root", required=True)
    parser.add_argument("--provider-descriptor", required=True)
    parser.add_argument("--monitoring-plan", required=True)
    parser.add_argument("--output")
    args = parser.parse_args()
    report = validate_source(args.source_root, args.provider_descriptor,
                             monitoring_plan_path=args.monitoring_plan)
    payload = (json.dumps(report, indent=2) + "\n").encode("utf-8")
    if args.output:
        output = Path(os.path.abspath(args.output))
        for ancestor in (output, *output.parents):
            if _is_reparse(ancestor):
                raise ValueError("preflight output path or an ancestor is a reparse point")
        output.parent.mkdir(parents=True, exist_ok=True)
        if output.exists():
            if output.read_bytes() != payload:
                raise ValueError("existing preflight output is not byte-identical; preserving it")
        else:
            temp_name = None
            try:
                with tempfile.NamedTemporaryFile(mode="wb", dir=output.parent, prefix=output.name + ".tmp-", delete=False) as temp:
                    temp_name = temp.name
                    temp.write(payload)
                    temp.flush()
                    os.fsync(temp.fileno())
                os.rename(temp_name, output)
            finally:
                if temp_name and os.path.exists(temp_name):
                    os.unlink(temp_name)
    print(json.dumps(report, separators=(",", ":")))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
