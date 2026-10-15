"""Exact-schema delta capture and isolated SQLite reconciliation for C54-04.

This is deliberately not a schema migration framework. It compares only the
case, human reply, open work-order, and durable mail-state records needed by the
Round-54 rollback contract. Inputs are immutable snapshots; writes target only a
new rollback root created by rollback_control.prepare.
"""
from __future__ import annotations

import hashlib
import json
import os
import re
import shutil
import socket
import sqlite3
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from rollback_errors import Refusal


DELTA_SCHEMA = "lf-round54-rollback-delta-v2"
BACKUP_SCHEMA = "lf-round54-rollback-reconcile-backup-v1"
TABLES = {
    "case": ("lf_issue_cases", "case_id"),
    "humanReply": ("lf_work_order_events", "event_id"),
    "pendingTask": ("lf_work_orders", "work_order_id"),
    "issueHandling": ("lf_issue_handling", "id"),
    "handlingLog": ("lf_log_lines", "seq"),
}
EXTRA_HELD_TABLES = {"lf_prtg_scope_purge_jobs": "job_id"}
CAPTURE_ORDER = ("pendingTask", "case", "issueHandling", "handlingLog", "humanReply", "notificationReceipt")
UNKNOWN_MAIL_STATES = {"sending-result-unknown", "failed-or-unknown"}
TERMINAL_INTENT_STATES = {"smtp-accepted", "cancelled-formal-revoked", "no-qualified-recipient"}
TERMINAL_RECIPIENT_STATES = {"smtp-accepted", "cancelled-formal-revoked"}
_ISO_DATETIME_100NS = re.compile(
    r"^(?P<base>\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2})"
    r"(?:\.(?P<fraction>\d{1,7}))?(?P<zone>Z|[+-]\d{2}:\d{2})?$"
)
_TICKS_PER_SECOND = 10_000_000
_TICKS_PER_DAY = 86400 * _TICKS_PER_SECOND


def _sha(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            h.update(block)
    return h.hexdigest()


def _canonical(value: Any) -> bytes:
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")


def _local_clock_authority() -> dict[str, Any]:
    now = datetime.now().astimezone()
    timezone_id = None
    if os.name == "nt":
        try:
            import winreg
            with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, r"SYSTEM\CurrentControlSet\Control\TimeZoneInformation") as key:
                timezone_id = winreg.QueryValueEx(key, "TimeZoneKeyName")[0]
        except (ImportError, OSError):
            pass
    if not isinstance(timezone_id, str) or not timezone_id.strip():
        timezone_id = str(now.tzinfo) if now.tzinfo is not None else "unknown"
    offset = now.utcoffset()
    if offset is None: _refuse("local timezone offset is unavailable")
    return {
        "hostBindingSha256": hashlib.sha256(socket.gethostname().strip().casefold().encode("utf-8")).hexdigest(),
        "timezoneId": timezone_id,
        "utcOffsetSecondsNow": int(offset.total_seconds()),
        "sameHostRequired": True,
        "sqliteNaiveTimeBasis": "local-wall-clock",
    }


def _refuse(message: str) -> None:
    raise Refusal(message)


def _readonly_db(path: Path) -> sqlite3.Connection:
    from rollback_control import reject_reparse
    path = path.absolute()
    reject_reparse(path)
    path = path.resolve(strict=True)
    for suffix in ("-wal", "-shm", "-journal"):
        if Path(str(path) + suffix).exists():
            _refuse(f"SQLite snapshot is not checkpointed; sidecar present: {suffix}")
    connection = sqlite3.connect(path.as_uri() + "?mode=ro&immutable=1", uri=True)
    connection.row_factory = sqlite3.Row
    if connection.execute("PRAGMA quick_check").fetchone()[0] != "ok":
        connection.close()
        _refuse("SQLite quick_check failed for delta snapshot")
    return connection


def _metadata(connection: sqlite3.Connection) -> dict[str, Any]:
    app_id = int(connection.execute("PRAGMA application_id").fetchone()[0])
    user_version = int(connection.execute("PRAGMA user_version").fetchone()[0])
    rows = connection.execute(
        "SELECT type,name,tbl_name,COALESCE(sql,'') AS sql FROM sqlite_master "
        "WHERE name NOT LIKE 'sqlite_%' ORDER BY type,name,tbl_name"
    ).fetchall()
    schema_text = "\n".join("\x1f".join(str(row[k]) for k in ("type", "name", "tbl_name", "sql")) for row in rows)
    return {"applicationId": app_id, "userVersion": user_version,
            "schemaSha256": hashlib.sha256(schema_text.encode("utf-8")).hexdigest()}


def _row_value(value: Any) -> Any:
    if isinstance(value, bytes):
        return {"$bytesHex": value.hex()}
    if value is None or type(value) in (str, int, float):
        return value
    _refuse("delta row contains an unsupported SQLite value type")


def _encode_row(row: sqlite3.Row | None) -> dict[str, Any] | None:
    return None if row is None else {str(key): _row_value(row[key]) for key in row.keys()}


def _decode_value(value: Any) -> Any:
    if isinstance(value, dict) and set(value) == {"$bytesHex"}:
        raw = value["$bytesHex"]
        if not isinstance(raw, str) or len(raw) % 2 or any(c not in "0123456789abcdef" for c in raw):
            _refuse("delta contains a malformed byte value")
        return bytes.fromhex(raw)
    if isinstance(value, (dict, list)):
        _refuse("delta row contains an unsupported encoded SQLite value")
    if value is not None and type(value) not in (str, int, float):
        _refuse("delta row contains an unsupported encoded SQLite value")
    return value


def _table_columns(connection: sqlite3.Connection, table: str) -> list[str]:
    names = [str(row[1]) for row in connection.execute(f'PRAGMA table_info("{table}")')]
    if not names:
        _refuse(f"required delta table is absent: {table}")
    return names


def _table_schema_sha(connection: sqlite3.Connection, table: str) -> str | None:
    row = connection.execute("SELECT sql FROM sqlite_master WHERE type='table' AND name=?", (table,)).fetchone()
    if row is None:
        return None
    columns = [tuple(value) for value in connection.execute(f'PRAGMA table_info("{table}")')]
    indexes = [tuple(value) for value in connection.execute(
        "SELECT type,name,COALESCE(sql,'') FROM sqlite_master WHERE tbl_name=? AND type IN ('index','trigger') ORDER BY type,name", (table,)
    )]
    serialized = json.dumps({"tableSql": str(row[0] or ""), "columns": columns, "indexesAndTriggers": indexes},
                            ensure_ascii=False, sort_keys=True, separators=(",", ":"))
    return hashlib.sha256(serialized.encode("utf-8")).hexdigest()


def _iter_table(connection: sqlite3.Connection, category: str):
    table, key_column = TABLES[category]
    columns = _table_columns(connection, table)
    if key_column not in columns:
        _refuse(f"required identity column is absent: {table}.{key_column}")
    where = ""
    if category == "handlingLog":
        if "log_key" not in columns: _refuse(f"required log partition is absent: {table}.log_key")
        where = " WHERE log_key='handling_log'"
    query = f'SELECT * FROM "{table}"{where} ORDER BY "{key_column}"'
    for row in connection.execute(query):
        encoded = _encode_row(row)
        key = encoded[key_column]
        yield key, encoded


def _iter_raw_table(connection: sqlite3.Connection, table: str, key_column: str):
    if _table_schema_sha(connection, table) is None:
        return
    if key_column not in _table_columns(connection, table):
        _refuse(f"held job table lacks its stable identity: {table}.{key_column}")
    for row in connection.execute(f'SELECT * FROM "{table}" ORDER BY "{key_column}"'):
        encoded = _encode_row(row)
        yield encoded[key_column], encoded


def _log_tail_bounds(connection: sqlite3.Connection) -> tuple[int | None, int | None]:
    if _table_schema_sha(connection, "lf_log_lines") is None: return None, None
    columns = set(_table_columns(connection, "lf_log_lines"))
    if not {"seq", "log_key"}.issubset(columns): _refuse("handling log schema is unknown")
    row = connection.execute("SELECT min(seq),max(seq) FROM lf_log_lines WHERE log_key='handling_log'").fetchone()
    return (None if row[0] is None else int(row[0]), None if row[1] is None else int(row[1]))


def _iter_appended_handling_logs(connection: sqlite3.Connection, after_seq: int):
    for row in connection.execute(
        "SELECT * FROM lf_log_lines WHERE log_key='handling_log' AND seq>? ORDER BY seq", (after_seq,)
    ):
        encoded = _encode_row(row)
        yield encoded["seq"], encoded


def _next_or_none(iterator):
    try: return next(iterator)
    except StopIteration: return None


def _changed_rows(old_iter, new_iter):
    old = _next_or_none(iter(old_iter)); new = _next_or_none(iter(new_iter))
    while old is not None or new is not None:
        if new is None or (old is not None and old[0] < new[0]):
            yield old[0], old[1], None
            old = _next_or_none(old_iter)
        elif old is None or new[0] < old[0]:
            yield new[0], None, new[1]
            new = _next_or_none(new_iter)
        else:
            if old[1] != new[1]: yield old[0], old[1], new[1]
            old = _next_or_none(old_iter); new = _next_or_none(new_iter)


def _split_issue_key(raw: Any) -> tuple[str, str, int, int, str] | None:
    if not isinstance(raw, str): return None
    parts = raw.split("|")
    if len(parts) not in (4, 5): return None
    try: event_id, entry_type = int(parts[2]), int(parts[3])
    except ValueError: return None
    return parts[0], parts[1].upper(), event_id, entry_type, parts[4] if len(parts) == 5 else ""


def _signature_from_top_issue(row: sqlite3.Row) -> tuple[str, str, int, int, str]:
    return str(row["log_name"]), str(row["source_name"]).upper(), int(row["event_id"]), int(row["entry_type"]), str(row["event_key"])


def _json_property(value: dict[str, Any], name: str) -> Any:
    for key, item in value.items():
        if str(key).casefold() == name.casefold(): return item
    return None


def _parse_datetime_100ns(value: Any) -> tuple[datetime, int] | None:
    """Parse the .NET ISO timestamp precision exactly, rejecting finer precision."""
    if not isinstance(value, str): return None
    match = _ISO_DATETIME_100NS.fullmatch(value)
    if match is None: return None
    try:
        base = datetime.fromisoformat(match.group("base") + (match.group("zone") or ""))
    except ValueError:
        return None
    fraction = match.group("fraction") or ""
    return base, int(fraction.ljust(7, "0") or "0")


def _datetime_100ns_ticks(base: datetime, fraction_ticks: int) -> int:
    """Return a wall-clock tick count for a datetime with its fraction kept separately."""
    seconds = (base.toordinal() - 1) * 86400 + base.hour * 3600 + base.minute * 60 + base.second
    return seconds * _TICKS_PER_SECOND + fraction_ticks


def _same_datetime(left: Any, right: Any) -> bool:
    """Compare native SQLite/JSON datetime encodings without losing .NET's 100ns tick."""
    a = _parse_datetime_100ns(left)
    b = _parse_datetime_100ns(right)
    if a is None or b is None: return False
    a_base, a_fraction = a
    b_base, b_fraction = b
    if (a_base.tzinfo is None) != (b_base.tzinfo is None): return False
    if a_base.tzinfo is not None:
        a_base = a_base.astimezone(timezone.utc).replace(tzinfo=None)
        b_base = b_base.astimezone(timezone.utc).replace(tzinfo=None)
    return _datetime_100ns_ticks(a_base, a_fraction) == _datetime_100ns_ticks(b_base, b_fraction)


def _same_optional_datetime(left: Any, right: Any) -> bool:
    return left is None and right is None or _same_datetime(left, right)


def _same_native_actor(left_id: Any, left_account: Any, right_id: Any, right_account: Any) -> bool:
    return (type(left_id) is int and type(right_id) is int and left_id == right_id
            and isinstance(left_account, str) and bool(left_account)
            and isinstance(right_account, str) and left_account == right_account)


def _same_optional_sqlite_local_datetime(left: Any, right: Any) -> bool:
    return left is None and right is None or _same_sqlite_local_datetime(left, right)


def _same_sqlite_local_datetime(left: Any, right: Any) -> bool:
    """Compare a serialized local DateTime with EF SQLite's offset-free local value.

    WorkOrderActor.OccurredAt is DateTime.Now in the normal UI path. EF stores that
    value without an offset, while the JSON handling log retains the local offset.
    Convert only the aware JSON value through this machine's local zone; if the
    source was captured under another zone, the comparison fails closed.
    """
    if _same_datetime(left, right): return True
    a = _parse_datetime_100ns(left)
    b = _parse_datetime_100ns(right)
    if a is None or b is None: return False
    a_base, a_fraction = a
    b_base, b_fraction = b
    if a_base.tzinfo is None and b_base.tzinfo is None or a_base.tzinfo is not None and b_base.tzinfo is not None:
        return False
    aware_base, aware_fraction, naive_base, naive_fraction = (
        (a_base, a_fraction, b_base, b_fraction) if a_base.tzinfo is not None
        else (b_base, b_fraction, a_base, a_fraction)
    )
    local_base = aware_base.astimezone().replace(tzinfo=None)
    return _datetime_100ns_ticks(local_base, aware_fraction) == _datetime_100ns_ticks(naive_base, naive_fraction)


def _reply_event_note(status: str, note: Any, case_count: int) -> str:
    prefix, suffix = f"{status}：", f"（{case_count} 台）"
    if not isinstance(note, str) or not note.strip(): return f"{status}{suffix}"
    if len(prefix) + len(note) + len(suffix) <= 1000: return prefix + note + suffix
    if len(prefix) + len(note) <= 1000: return prefix + note
    return note if len(note) <= 1000 else note[:1000]


def _native_log_note(status: str, note: Any, due_date: Any) -> Any:
    if status != "observing" or due_date is None: return note
    try: day = datetime.fromisoformat(str(due_date).replace("Z", "+00:00")).strftime("%Y-%m-%d")
    except ValueError: return None
    suffix = f"（觀察至 {day}）"
    return suffix if note is None or note == "" else f"{note}{suffix}"


def _native_candidate_days(connection: sqlite3.Connection, issue_case: dict[str, Any]) -> set[str]:
    """Resolve the narrow no-alias host path used by IssueCaseCoordinator.FindCandidateDays."""
    tables = {str(r[0]) for r in connection.execute("SELECT name FROM sqlite_master WHERE type='table'")}
    if not {"lf_blobs", "lf_top_issues"}.issubset(tables): _refuse("reply graph lacks the native host/history tables")
    blob = connection.execute("SELECT content FROM lf_blobs WHERE blob_key='hosts'").fetchone()
    if blob is None: _refuse("reply graph lacks the native host identity catalog")
    try: hosts = json.loads(str(blob[0]))
    except json.JSONDecodeError: _refuse("reply graph host identity catalog is malformed")
    if not isinstance(hosts, list): _refuse("reply graph host identity catalog is not an array")
    matches = [h for h in hosts if isinstance(h, dict) and str(_json_property(h, "HostName") or "").casefold() == str(issue_case.get("host_name", "")).casefold()]
    if len(matches) != 1: _refuse("reply graph host identity is absent or ambiguous")
    host_id = _json_property(matches[0], "HostId")
    if type(host_id) is not int or host_id <= 0: _refuse("reply graph host identity has no positive native ID")
    if _json_property(matches[0], "MergedInto") is not None or any(
        isinstance(h, dict) and _json_property(h, "MergedInto") == host_id for h in hosts
    ):
        _refuse("reply graph host has aliases; this narrow replay path requires unmerged identity")
    signature = _split_issue_key(issue_case.get("issue_key"))
    if signature is None: _refuse("reply graph issue signature is not a recognized full key")
    columns = {str(r[1]) for r in connection.execute("PRAGMA table_info('lf_top_issues')")}
    required = {"host_id", "record_date", "log_name", "source_name", "event_id", "entry_type", "event_key"}
    if not required.issubset(columns): _refuse("reply graph native history schema is unknown")
    rows = connection.execute(
        "SELECT host_id,record_date,log_name,source_name,event_id,entry_type,event_key FROM lf_top_issues WHERE host_id=? ORDER BY record_date",
        (host_id,),
    )
    return {str(row["record_date"])[:10] for row in rows if _signature_from_top_issue(row) == signature}


def _is_closed_issue_status(status: Any) -> bool:
    return status in {"resolved", "wont_fix", "false_positive", "known_noise"}


def _native_pending_sync_rows(connection: sqlite3.Connection, case: dict[str, Any]) -> int:
    """Count rows PlanCaseDays would touch for a reply without loading payload/log data."""
    days = sorted(_native_candidate_days(connection, case))
    if not days: return 0
    if len(days) > 10000: _refuse("reply graph case history exceeds the bounded pending-intent verifier")
    existing: dict[str, str] = {}
    for offset in range(0, len(days), 400):
        chunk = days[offset:offset + 400]
        marks = ",".join("?" for _ in chunk)
        rows = connection.execute(
            f"SELECT record_date,status FROM lf_issue_handling WHERE lower(host_name)=lower(?) AND issue_key=? AND substr(record_date,1,10) IN ({marks})",
            (case["host_name"], case["issue_key"], *chunk),
        ).fetchall()
        for row in rows:
            day = str(row["record_date"])[:10]
            if day in existing: _refuse("reply graph source has duplicate issue/day rows")
            existing[day] = str(row["status"])
    return sum(1 for day in days if not _is_closed_issue_status(existing.get(day)))


def _native_reply_graph_items(items: list[dict[str, Any]], base_database: Path, *, inline_row_limit: int = 5000) -> set[tuple[str, str, str]]:
    """Return only identities covered by one complete native Reply write graph.

    The accepted shape is intentionally narrow: one new `replied` event, exact
    parent/case/day/log rows, and an optional matching system-closure event.
    Unknown aliases, partial day projections, concurrent events, and unrelated
    table changes remain held. `inline_row_limit` is private test injection only;
    production capture and validation always use the default native limit.
    """
    if type(inline_row_limit) is not int or not 0 <= inline_row_limit <= 5000:
        return set()
    def matches(category: str, predicate) -> list[dict[str, Any]]:
        return [item for item in items if item.get("category") == category and predicate(item)]
    replies = matches("humanReply", lambda item: item.get("before") is None and isinstance(item.get("after"), dict)
                      and str(item["after"].get("action", "")).casefold() in {"reply", "replied"})
    if len(replies) != 1: return set()
    reply = replies[0]; event = reply["after"]
    if str(event.get("action", "")).casefold() != "replied" or event.get("member_delta") != 0:
        return set()
    try: order_id = int(event["work_order_id"])
    except (KeyError, TypeError, ValueError): return set()
    occurred = str(event.get("created_at", ""))
    if not occurred: return set()
    parents = matches("pendingTask", lambda item: item.get("table") == "lf_work_orders" and str(item.get("id")) == str(order_id))
    if len(parents) != 1 or not isinstance(parents[0].get("before"), dict) or not isinstance(parents[0].get("after"), dict): return set()
    parent = parents[0]; old_order, new_order = parent["before"], parent["after"]
    if old_order.get("closed_at") is not None or not _same_datetime(new_order.get("last_reply_at"), occurred): return set()
    allowed_order_fields = {"last_reply_at", "updated_at", "closed_at", "closed_reason"}
    if {key for key in old_order if old_order[key] != new_order.get(key)} - allowed_order_fields: return set()
    case_items = matches("case", lambda item: isinstance(item.get("after"), dict)
                         and str(item["after"].get("work_order_id")) == str(order_id))
    if not case_items: return set()
    cases: dict[str, dict[str, Any]] = {}
    allowed_case_fields = {"status", "note", "due_date", "closed_at", "updated_at", "day_sync_pending", "day_sync_intent"}
    for item in case_items:
        old, new = item.get("before"), item.get("after")
        if not isinstance(old, dict) or not isinstance(new, dict) or old.get("closed_at") is not None: return set()
        if str(old.get("work_order_id")) != str(order_id) or not _same_datetime(new.get("updated_at"), occurred): return set()
        if {key for key in old if old[key] != new.get(key)} - allowed_case_fields: return set()
        if new.get("status") not in {"open", "in_progress", "observing", "resolved", "wont_fix", "false_positive", "known_noise", "escalated"}: return set()
        if new.get("status") == "open" and (new.get("note") is not None or new.get("due_date") is not None): return set()
        if new.get("status") not in {"in_progress", "observing"} and new.get("due_date") is not None: return set()
        expected_closed_at = occurred if _is_closed_issue_status(new.get("status")) else None
        if not _same_optional_datetime(new.get("closed_at"), expected_closed_at): return set()
        cases[str(new["case_id"])] = new
    if len(cases) != len(case_items): return set()
    case_ids = set(cases)
    statuses = {case.get("status") for case in cases.values()}
    notes = {case.get("note") for case in cases.values()}
    dues = {case.get("due_date") for case in cases.values()}
    if len(statuses) != 1 or len(notes) != 1 or len(dues) != 1: return set()
    status, note, due = next(iter(statuses)), next(iter(notes)), next(iter(dues))
    reply_actions = [event]
    closure_events = matches("humanReply", lambda item: item.get("table") == "lf_work_order_events" and item.get("before") is None
                             and isinstance(item.get("after"), dict) and str(item["after"].get("work_order_id")) == str(order_id)
                             and str(item["after"].get("action", "")).casefold() == "closed")
    if len(closure_events) > 1: return set()
    if closure_events: reply_actions.append(closure_events[0]["after"])
    relevant_issue = matches("issueHandling", lambda item: isinstance(item.get("after"), dict)
                             and str(item["after"].get("case_id")) in case_ids)
    relevant_logs = matches("handlingLog", lambda item: isinstance(item.get("after"), dict)
                            and str(item["after"].get("log_key")) == "handling_log")
    pending_flags = {bool(case.get("day_sync_pending")) for case in cases.values()}
    if len(pending_flags) != 1: return set()
    is_pending = next(iter(pending_flags))
    if is_pending:
        intents = [case.get("day_sync_intent") for case in cases.values()]
        if any(not isinstance(intent, str) for intent in intents) or len(set(intents)) != 1: return set()
        try: intent = json.loads(intents[0])
        except json.JSONDecodeError: return set()
        if not isinstance(intent, dict): return set()
        props = {str(k).casefold(): v for k, v in intent.items()}
        intent_due = props.get("dueDate", props.get("duedate"))
        if props.get("mode") != "sync" or props.get("status") != status or props.get("note") != note or not _same_optional_sqlite_local_datetime(intent_due, due):
            return set()
        if props.get("clearing") is not (status == "open") or props.get("triggerDate", props.get("triggerdate")) is not None:
            return set()
        if not _same_sqlite_local_datetime(props.get("occurredAt", props.get("occurredat")), occurred): return set()
        intent_actor_id = props.get("actorId", props.get("actorid"))
        event_actor_id = event.get("actor_id")
        intent_actor_account = props.get("actorAccount", props.get("actoraccount"))
        if not _same_native_actor(intent_actor_id, intent_actor_account, event_actor_id, event.get("actor_account")):
            return set()
        if relevant_issue or relevant_logs: return set()
        try:
            conn = _readonly_db(base_database)
            try:
                pending_rows = sum(_native_pending_sync_rows(conn, case) for case in cases.values())
            finally: conn.close()
        except Exception:
            return set()
        if pending_rows <= inline_row_limit: return set()
    else:
        if any(case.get("day_sync_intent") is not None for case in cases.values()): return set()
        if not relevant_issue or not relevant_logs: return set()
        try:
            conn = _readonly_db(base_database)
            try:
                expected_rows: set[tuple[str, str]] = set()
                expected_logs: set[tuple[str, str]] = set()
                for case_id, case in cases.items():
                    candidate_days = _native_candidate_days(conn, case)
                    log_rows = []
                    for log_item in relevant_logs:
                        try: log = json.loads(str(log_item["after"]["line"]))
                        except (json.JSONDecodeError, KeyError, TypeError): return set()
                        if str(_json_property(log, "HostName") or "").casefold() == str(case.get("host_name", "")).casefold() and _json_property(log, "IssueKey") == case.get("issue_key") and _same_datetime(_json_property(log, "CreatedAt"), occurred):
                            log_rows.append((log_item, log))
                    for day in candidate_days:
                        existing = conn.execute("SELECT * FROM lf_issue_handling WHERE lower(host_name)=lower(?) AND record_date LIKE ? AND issue_key=? ORDER BY id",
                                                (case["host_name"], day + "%", case["issue_key"])).fetchall()
                        if len(existing) > 1: return set()
                        if existing and _is_closed_issue_status(existing[0]["status"]): continue
                        target_matches = existing and existing[0]["status"] == status and existing[0]["note"] == note and _same_optional_datetime(existing[0]["due_date"], due) and existing[0]["case_id"] == case_id and _same_datetime(existing[0]["updated_at"], occurred)
                        if target_matches: continue
                        expected_rows.add((case_id, day))
                actual_rows = set()
                for item in relevant_issue:
                    row = item["after"]
                    case_id = str(row.get("case_id"))
                    case = cases.get(case_id)
                    if case is None or str(row.get("host_name", "")).casefold() != str(case.get("host_name", "")).casefold() or row.get("issue_key") != case.get("issue_key"):
                        return set()
                    actual_rows.add((case_id, str(row.get("record_date", ""))[:10]))
                    if row.get("status") != status or row.get("note") != note or not _same_optional_datetime(row.get("due_date"), due) or not _same_datetime(row.get("updated_at"), occurred):
                        return set()
                    row_actor_id = row.get("actor_id")
                    event_actor_id = event.get("actor_id")
                    if not _same_native_actor(row_actor_id, row.get("actor_account"),
                                              event_actor_id, event.get("actor_account")):
                        return set()
                if actual_rows != expected_rows: return set()
                if len(actual_rows) > 5000: return set()
                # Each changed day projection must have exactly one matching native history line.
                for log_item, log in ((item, json.loads(str(item["after"]["line"]))) for item in relevant_logs):
                    if _json_property(log, "Status") != status or _json_property(log, "ActorId") != event.get("actor_id") or _json_property(log, "ActorAccount") != event.get("actor_account") or not _same_sqlite_local_datetime(_json_property(log, "CreatedAt"), occurred):
                        return set()
                    row_host = str(_json_property(log, "HostName") or "").casefold()
                    row_day = str(_json_property(log, "Date") or "")[:10]
                    row_issue_key = _json_property(log, "IssueKey")
                    expected_case = next((value for value in cases.values() if str(value.get("host_name", "")).casefold() == row_host and value.get("issue_key") == row_issue_key), None)
                    if expected_case is None: return set()
                    action = _json_property(log, "Action")
                    if action != "case_sync": return set()
                    if _json_property(log, "Note") != _native_log_note(status, note, due): return set()
                    if _json_property(log, "IssueLabel") != expected_case.get("issue_label"): return set()
                    if _json_property(log, "HandlerId") is not None or _json_property(log, "CaseId") is not None: return set()
                    raw_log_id = _json_property(log, "LogId")
                    if type(raw_log_id) is not int or raw_log_id <= 0: return set()
                    key = (row_host, row_day, row_issue_key)
                    expected_logs.add((key[0], key[1]))
                expected_log_keys = {(str(row["after"].get("host_name", "")).casefold(), str(row["after"].get("record_date", ""))[:10]) for row in relevant_issue}
                if expected_logs != expected_log_keys or len(relevant_logs) != len(expected_log_keys): return set()
            finally:
                conn.close()
        except Exception:
            return set()
    # Validate reply description and parent closure invariants against the exact post-state.
    if event.get("actor_id") is None or not isinstance(event.get("actor_account"), str) or not event.get("actor_account"):
        return set()
    if event.get("note") != _reply_event_note(str(status), note, len(cases)): return set()
    if closure_events:
        close_event = closure_events[0]["after"]
        if close_event.get("actor_id") is not None or close_event.get("actor_account") != "(system)" or close_event.get("member_delta") != 0 or not _same_datetime(close_event.get("created_at"), occurred):
            return set()
        try: target_members = _post_reply_members(base_database, case_items, order_id)
        except Exception: return set()
        total, active = target_members
        reason = "moved" if total == 0 else "all_closed" if active == 0 else None
        if reason is None or new_order.get("closed_reason") != reason or not _same_datetime(new_order.get("closed_at"), occurred) or close_event.get("note") != reason:
            return set()
    elif new_order.get("closed_at") is not None or new_order.get("closed_reason") is not None:
        return set()
    elif _post_reply_members(base_database, case_items, order_id)[1] == 0:
        return set()
    covered = [reply, parents[0], *case_items, *relevant_issue, *relevant_logs, *closure_events]
    return {(str(item["category"]), str(item["table"]), str(item["id"])) for item in covered}


def _post_reply_members(base_database: Path, case_items: list[dict[str, Any]], work_order_id: int) -> tuple[int, int]:
    connection = _readonly_db(base_database)
    try:
        rows = {str(row["case_id"]): dict(row) for row in connection.execute("SELECT * FROM lf_issue_cases WHERE work_order_id=?", (work_order_id,))}
    finally: connection.close()
    for item in case_items: rows[str(item["id"])] = item["after"]
    return len(rows), sum(row.get("closed_at") is None for row in rows.values())


def _mail_row(connection: sqlite3.Connection) -> dict[str, Any] | None:
    tables = {str(r[0]) for r in connection.execute("SELECT name FROM sqlite_master WHERE type='table'")}
    if "lf_blobs" not in tables:
        _refuse("mail receipt reconciliation requires lf_blobs")
    columns = {str(row[1]) for row in connection.execute("PRAGMA table_info(lf_blobs)")}
    if not {"blob_key", "content"}.issubset(columns):
        _refuse("mail receipt blob schema is unknown")
    row = connection.execute("SELECT * FROM lf_blobs WHERE blob_key='mail_notify_state'").fetchone()
    return _encode_row(row)


def _mail_content(row: dict[str, Any] | None) -> str | None:
    return None if row is None else str(row.get("content", ""))


def capture_delta(before_db: Path, current_db: Path, output: Path) -> dict[str, Any]:
    """Capture a pinned exact-schema difference without modifying either DB."""
    before_db, current_db, output = Path(before_db), Path(current_db), Path(output)
    from rollback_control import reject_reparse
    output = output.absolute()
    reject_reparse(output)
    before_resolved = before_db.resolve(strict=True)
    current_resolved = current_db.resolve(strict=True)
    if output.resolve(strict=False) in {before_resolved, current_resolved}:
        _refuse("delta output must be separate from both immutable database snapshots")
    before = _readonly_db(before_db)
    current = _readonly_db(current_db)
    try:
        before_meta, current_meta = _metadata(before), _metadata(current)
        if before_meta["applicationId"] != current_meta["applicationId"]:
            _refuse("delta capture refused: SQLite application identity differs")
        entries: list[dict[str, Any]] = []
        table_names_before = {str(r[0]) for r in before.execute("SELECT name FROM sqlite_master WHERE type='table'")}
        table_names_current = {str(r[0]) for r in current.execute("SELECT name FROM sqlite_master WHERE type='table'")}
        for category in CAPTURE_ORDER:
            if category == "notificationReceipt":
                left_row, right_row = _mail_row(before), _mail_row(current)
                if left_row == right_row:
                    continue
                for raw in (_mail_content(left_row), _mail_content(right_row)):
                    if raw is not None:
                        try:
                            value = json.loads(raw)
                        except json.JSONDecodeError:
                            _refuse("mail notification state is malformed JSON")
                        if not isinstance(value, dict):
                            _refuse("mail notification state root is not an object")
                entries.append({"category": category, "id": "mail_notify_state", "table": "lf_blobs",
                                "keyColumn": "blob_key", "before": left_row, "after": right_row,
                                "beforeSha256": None if left_row is None else hashlib.sha256(_canonical(left_row)).hexdigest(),
                                "afterSha256": None if right_row is None else hashlib.sha256(_canonical(right_row)).hexdigest(),
                                "replayable": _table_schema_sha(before, "lf_blobs") == _table_schema_sha(current, "lf_blobs"),
                                "beforeTableSchemaSha256": _table_schema_sha(before, "lf_blobs"),
                                "currentTableSchemaSha256": _table_schema_sha(current, "lf_blobs")})
                continue
            table, key_column = TABLES[category]
            if category == "handlingLog":
                before_min, before_max = _log_tail_bounds(before)
                current_min, current_max = _log_tail_bounds(current)
                if before_min is not None and (current_min is None or current_min > before_min):
                    _refuse("handling log retention/deletion changed the original prefix; full history replay is unsupported")
                if before_max is not None and current_max is not None and current_max < before_max:
                    _refuse("handling log sequence regressed; full history replay is unsupported")
                old_rows = iter(())
                new_rows = _iter_appended_handling_logs(current, before_max or 0) if table in table_names_current else iter(())
            else:
                old_rows = _iter_table(before, category) if table in table_names_before else iter(())
                new_rows = _iter_table(current, category) if table in table_names_current else iter(())
            table_compatible = (_table_schema_sha(before, table) is not None and
                                _table_schema_sha(before, table) == _table_schema_sha(current, table))
            for key, old, new in _changed_rows(old_rows, new_rows):
                if new is None:
                    _refuse(f"physical deletion is unsupported for {category}/{key}")
                entries.append({"category": category, "id": str(key), "table": table,
                                "keyColumn": key_column, "before": old, "after": new,
                                "beforeSha256": None if old is None else hashlib.sha256(_canonical(old)).hexdigest(),
                                "afterSha256": hashlib.sha256(_canonical(new)).hexdigest(),
                                "replayable": False,
                                "beforeTableSchemaSha256": _table_schema_sha(before, table),
                                "currentTableSchemaSha256": _table_schema_sha(current, table)})
        # The F2 purge-job table is intentionally listed as held when the exact
        # target release does not have the same table definition. Older releases
        # must never be asked to interpret a newer durable-job row.
        for table, key_column in EXTRA_HELD_TABLES.items():
            if table not in table_names_current:
                continue
            current_rows = _iter_raw_table(current, table, key_column)
            before_rows = _iter_raw_table(before, table, key_column) if table in table_names_before else iter(())
            for key, old, new in _changed_rows(before_rows, current_rows):
                if new is None: _refuse(f"physical deletion is unsupported for pending task {key}")
                entries.append({"category": "pendingTask", "id": str(key), "table": table, "keyColumn": key_column,
                                "before": old, "after": new,
                                "beforeSha256": None if old is None else hashlib.sha256(_canonical(old)).hexdigest(),
                                "afterSha256": hashlib.sha256(_canonical(new)).hexdigest(),
                                "replayable": False, "beforeTableSchemaSha256": _table_schema_sha(before, table),
                                "currentTableSchemaSha256": _table_schema_sha(current, table)})
        graph_keys = _native_reply_graph_items(entries, before_db.resolve(strict=True))
        for item in entries:
            identity = (str(item["category"]), str(item["table"]), str(item["id"]))
            if identity in graph_keys and item.get("beforeTableSchemaSha256") == item.get("currentTableSchemaSha256"):
                item["replayable"] = True
        before.close(); current.close()
        counts = {category: sum(row["category"] == category for row in entries) for category in TABLES}
        counts["notificationReceipt"] = sum(row["category"] == "notificationReceipt" for row in entries)
        bundle = {
            "schema": DELTA_SCHEMA,
            "clockAuthority": _local_clock_authority(),
            "before": {"sha256": _sha(before_db.resolve(strict=True)), **before_meta},
            "current": {"sha256": _sha(current_db.resolve(strict=True)), **current_meta},
            "counts": counts,
            "items": entries,
        }
        if output.exists():
            _refuse("delta output must be a new path; existing bundles are immutable")
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_bytes(_canonical(bundle) + b"\n")
        return {"result": "delta-captured", "manifest": str(output.resolve()), "counts": counts,
                "beforeDatabaseSha256": bundle["before"]["sha256"], "currentDatabaseSha256": bundle["current"]["sha256"],
                "clockAuthority": bundle["clockAuthority"]}
    finally:
        try: before.close()
        except Exception: pass
        try: current.close()
        except Exception: pass


def _validate_delta(path: Path, base_database: Path, *, allow_committed_reconcile: bool = False) -> tuple[dict[str, Any], dict[str, Any]]:
    from rollback_control import read_json, db_metadata
    delta = read_json(path)
    if not isinstance(delta, dict) or delta.get("schema") != DELTA_SCHEMA or set(delta) != {"schema", "clockAuthority", "before", "current", "counts", "items"}:
        _refuse("rollback delta schema is unknown")
    if delta.get("clockAuthority") != _local_clock_authority():
        _refuse("delta local timestamp authority differs; capture and reconciliation require the same host and timezone offset")
    meta = db_metadata(base_database)
    base = delta.get("before")
    if not isinstance(base, dict) or (base.get("sha256") != _sha(base_database) and not allow_committed_reconcile) or any(
        base.get(k) != meta[k] for k in ("applicationId", "userVersion", "schemaSha256")
    ):
        _refuse("delta base database is stale or its schema authority differs")
    current = delta.get("current")
    if not isinstance(current, dict) or current.get("applicationId") != meta["applicationId"]:
        _refuse("delta source SQLite application identity is incompatible")
    items = delta.get("items")
    if not isinstance(items, list):
        _refuse("rollback delta items must be a list")
    counts = delta.get("counts")
    if not isinstance(counts, dict) or set(counts) != set((*TABLES.keys(), "notificationReceipt")) or any(
        type(value) is not int or value < 0 for value in counts.values()
    ):
        _refuse("rollback delta category counts are malformed")
    actual_counts = {category: 0 for category in (*TABLES.keys(), "notificationReceipt")}
    for row in items:
        if not isinstance(row, dict) or row.get("category") not in actual_counts:
            _refuse("rollback delta category is unknown")
        actual_counts[row["category"]] += 1
    if actual_counts != counts:
        _refuse("rollback delta category counts do not match its items")
    graph_keys = _native_reply_graph_items(items, Path(base_database).resolve(strict=True))
    seen: set[tuple[str, str, str]] = set()
    target_schema_connection = _readonly_db(base_database)
    try:
        target_tables = {str(r[0]) for r in target_schema_connection.execute("SELECT name FROM sqlite_master WHERE type='table'")}
        for item in items:
            if not isinstance(item, dict) or item.get("replayable") is not True:
                continue
            table = str(item.get("table"))
            actual_schema = _table_schema_sha(target_schema_connection, table) if table in target_tables else None
            if actual_schema is None or actual_schema != item.get("beforeTableSchemaSha256") or actual_schema != item.get("currentTableSchemaSha256"):
                _refuse(f"delta marks a row replayable without exact target table authority: {table}")
    finally:
        target_schema_connection.close()
    for item in items:
        required = {"category", "id", "table", "keyColumn", "before", "after", "beforeSha256", "afterSha256", "replayable", "beforeTableSchemaSha256", "currentTableSchemaSha256"}
        if not isinstance(item, dict) or set(item) != required or type(item["replayable"]) is not bool:
            _refuse("rollback delta item has unknown or missing fields")
        category, item_id = item["category"], item["id"]
        if category not in TABLES and category != "notificationReceipt":
            _refuse("rollback delta category is unknown")
        identity = (category, str(item.get("table")), str(item_id))
        if identity in seen: _refuse("rollback delta has a duplicate identity")
        seen.add(identity)
        if category == "notificationReceipt":
            if item_id != "mail_notify_state" or item["table"] != "lf_blobs" or item["keyColumn"] != "blob_key":
                _refuse("mail notification receipt identity is malformed")
            for field in ("before", "after"):
                if item[field] is not None and not isinstance(item[field], dict): _refuse("mail state row payload must be an object")
            for row in (item["before"], item["after"]):
                if row is not None and ("blob_key" not in row or "content" not in row or row.get("blob_key") != "mail_notify_state"):
                    _refuse("mail state row identity or required columns are malformed")
            calculated_before = None if item["before"] is None else hashlib.sha256(_canonical(item["before"])).hexdigest()
            calculated_after = None if item["after"] is None else hashlib.sha256(_canonical(item["after"])).hexdigest()
        else:
            table, key_column = TABLES[category]
            if category == "pendingTask" and item["table"] in EXTRA_HELD_TABLES:
                table, key_column = item["table"], EXTRA_HELD_TABLES[item["table"]]
            if item["table"] != table or item["keyColumn"] != key_column or not isinstance(item_id, str):
                _refuse("rollback delta row identity does not match its category")
            before, after = item["before"], item["after"]
            if before is not None and not isinstance(before, dict) or not isinstance(after, dict):
                _refuse("rollback delta row payload is malformed")
            calculated_before = None if before is None else hashlib.sha256(_canonical(before)).hexdigest()
            calculated_after = hashlib.sha256(_canonical(after)).hexdigest()
            if str(after.get(key_column)) != item_id or (before is not None and str(before.get(key_column)) != item_id):
                _refuse("rollback delta row key differs from its payload")
        if item["beforeSha256"] != calculated_before or item["afterSha256"] != calculated_after:
            _refuse("rollback delta payload hash mismatch")
        if item["replayable"] is True:
            identity = (str(item["category"]), str(item["table"]), str(item["id"]))
            if item["category"] != "notificationReceipt" and identity not in graph_keys:
                _refuse("delta marks a business row replayable outside a complete native Reply graph")
            if item["category"] == "notificationReceipt" and item["beforeTableSchemaSha256"] != item["currentTableSchemaSha256"]:
                _refuse("delta marks a mail row replayable across different table schemas")
    return delta, meta


def _mail_unknown_states(raw: str | None) -> list[str]:
    if raw is None:
        return []
    try: state = json.loads(raw)
    except json.JSONDecodeError: _refuse("mail notification state is malformed JSON")
    if not isinstance(state, dict): _refuse("mail notification state root is not an object")
    outbox = state.get("UrgentOutbox", {})
    if not isinstance(outbox, dict): _refuse("mail UrgentOutbox is not an object")
    unknown: list[str] = []
    # These ledgers were added after the rollback target was released. The
    # older application cannot reason about their deduplication semantics.
    # Preserve the complete JSON blob, but keep the mail lane held whenever
    # either opaque ledger is present with a non-empty or unexpected value.
    for ledger_name in ("AcceptedMailContributionReceipts", "HeldLegacyMailContributionReceipts"):
        if ledger_name in state and state[ledger_name] != {}:
            unknown.append(f"{ledger_name}:unsupported-legacy-ledger")
    for key, intent in outbox.items():
        if not isinstance(intent, dict) or intent.get("Key") != key:
            _refuse("mail outbox key does not match its durable intent")
        recipients = intent.get("Recipients", {})
        accepted_at = intent.get("SmtpAcceptedAtUtc", {})
        if not isinstance(recipients, dict) or not isinstance(accepted_at, dict):
            _refuse("mail recipient receipt maps are malformed")
        status = str(intent.get("Status", ""))
        if status in UNKNOWN_MAIL_STATES or any(str(value) in UNKNOWN_MAIL_STATES for value in recipients.values()):
            unknown.append(f"{key}:unknown-recipient-outcome")
        if status not in TERMINAL_INTENT_STATES and status not in {"pending", "waiting-for-data", "failed-or-unknown", "sending-result-unknown"}:
            _refuse("mail outbox contains an unknown delivery status")
        for recipient, recipient_status in recipients.items():
            if recipient_status == "smtp-accepted":
                stamp = accepted_at.get(recipient)
                if not isinstance(stamp, str) or not stamp.strip():
                    _refuse("accepted SMTP recipient has no durable acceptance timestamp")
                try:
                    parsed = datetime.fromisoformat(stamp.replace("Z", "+00:00"))
                except ValueError:
                    _refuse("accepted SMTP recipient timestamp is malformed")
                if parsed.tzinfo is None or parsed.utcoffset() != timezone.utc.utcoffset(parsed):
                    _refuse("accepted SMTP recipient timestamp is not explicit UTC")
            elif recipient_status not in TERMINAL_RECIPIENT_STATES | {"pending", "failed-or-unknown", "sending-result-unknown"}:
                _refuse("mail outbox contains an unknown recipient status")
        for recipient in set(accepted_at) - {name for name, status in recipients.items() if status == "smtp-accepted"}:
            unknown.append(f"{key}:{recipient}:timestamp-without-accepted-status")
        formal_states = intent.get("FormalIssueStates", {})
        if not isinstance(formal_states, dict): _refuse("mail formal-issue state map is malformed")
        for issue_key, issue_status in formal_states.items():
            if issue_status in UNKNOWN_MAIL_STATES:
                unknown.append(f"{key}:{issue_key}:unknown-formal-outcome")
            elif issue_status not in {"smtp-accepted", "revoked", "pending", "cancelled-formal-revoked", "failed-or-unknown"}:
                _refuse("mail outbox contains an unknown formal issue status")
    claims = state.get("FormalMailStartClaims", {})
    if not isinstance(claims, dict): _refuse("mail formal-start claim map is malformed")
    for key, claim in claims.items():
        if not isinstance(claim, dict): _refuse("mail formal-start claim is malformed")
        status = claim.get("Status")
        if status in UNKNOWN_MAIL_STATES:
            unknown.append(f"formal-claim:{key}:unknown-outcome")
        elif status not in {"pending", "smtp-accepted", "failed-or-unknown", "sending-result-unknown", "revoked"}:
            _refuse("mail formal-start claim contains an unknown status")
        if status == "smtp-accepted":
            stamp = claim.get("SmtpAcceptedAtUtc")
            if not isinstance(stamp, str) or not stamp.strip(): _refuse("accepted formal mail claim has no durable timestamp")
    return unknown


def _mail_acceptance_did_not_regress(before_raw: str | None, after_raw: str | None) -> bool:
    if before_raw is None:
        return True
    try:
        before = json.loads(before_raw)
        after = json.loads(after_raw) if after_raw is not None else {}
    except json.JSONDecodeError:
        _refuse("mail notification state is malformed JSON")
    if not isinstance(before, dict) or not isinstance(after, dict):
        _refuse("mail notification state root is not an object")
    old_outbox, new_outbox = before.get("UrgentOutbox", {}), after.get("UrgentOutbox", {})
    if not isinstance(old_outbox, dict) or not isinstance(new_outbox, dict):
        _refuse("mail outbox is malformed")
    for intent_key, old in old_outbox.items():
        if not isinstance(old, dict): _refuse("mail outbox intent is malformed")
        new = new_outbox.get(intent_key)
        old_recipients = old.get("Recipients", {})
        if not isinstance(old_recipients, dict): _refuse("mail recipient receipt map is malformed")
        accepted = {recipient for recipient, status in old_recipients.items() if status == "smtp-accepted"}
        if not accepted:
            continue
        if not isinstance(new, dict) or new.get("Key") != intent_key:
            return False
        new_recipients = new.get("Recipients", {})
        old_stamps = old.get("SmtpAcceptedAtUtc", {})
        new_stamps = new.get("SmtpAcceptedAtUtc", {})
        if not isinstance(new_recipients, dict) or not isinstance(old_stamps, dict) or not isinstance(new_stamps, dict):
            _refuse("mail accepted-recipient receipt maps are malformed")
        for recipient in accepted:
            if new_recipients.get(recipient) != "smtp-accepted" or new_stamps.get(recipient) != old_stamps.get(recipient):
                return False
        old_formal = old.get("FormalIssueStates", {})
        new_formal = new.get("FormalIssueStates", {})
        if not isinstance(old_formal, dict) or not isinstance(new_formal, dict):
            _refuse("mail formal issue state map is malformed")
        for issue_key, old_status in old_formal.items():
            if old_status in {"smtp-accepted", "revoked"} and new_formal.get(issue_key) != old_status:
                return False
    old_claims, new_claims = before.get("FormalMailStartClaims", {}), after.get("FormalMailStartClaims", {})
    if not isinstance(old_claims, dict) or not isinstance(new_claims, dict):
        _refuse("mail formal-start claim map is malformed")
    for key, claim in old_claims.items():
        if not isinstance(claim, dict): _refuse("mail formal-start claim is malformed")
        if claim.get("Status") in {"smtp-accepted", "revoked"}:
            newer = new_claims.get(key)
            if not isinstance(newer, dict) or newer.get("Status") != claim.get("Status") or newer.get("SmtpAcceptedAtUtc") != claim.get("SmtpAcceptedAtUtc"):
                return False
    old_facts, new_facts = before.get("PrtgUrgentAcceptedFacts", {}), after.get("PrtgUrgentAcceptedFacts", {})
    if not isinstance(old_facts, dict) or not isinstance(new_facts, dict): _refuse("mail accepted-fact map is malformed")
    for key, value in old_facts.items():
        if type(value) is not int or type(new_facts.get(key)) is not int or new_facts[key] < value:
            return False
    for field in ("UrgentSentKeys", "SummarySentKeys"):
        old_keys, new_keys = before.get(field, []), after.get(field, [])
        if not isinstance(old_keys, list) or not isinstance(new_keys, list): _refuse(f"mail {field} is malformed")
        if not set(old_keys).issubset(set(new_keys)): return False
    for field in ("LastDailySentDate", "LastWeeklySentDate"):
        old_date, new_date = before.get(field), after.get(field)
        if old_date is not None and (not isinstance(new_date, str) or new_date < old_date): return False
    return True


def _apply_row(connection: sqlite3.Connection, category: str, item: dict[str, Any]) -> str:
    table, key_column = TABLES[category]
    if category == "pendingTask" and item["table"] in EXTRA_HELD_TABLES:
        table, key_column = item["table"], EXTRA_HELD_TABLES[item["table"]]
    columns = _table_columns(connection, table)
    before, after = item["before"], item["after"]
    if set(after) != set(columns) or (before is not None and set(before) != set(columns)):
        _refuse(f"delta row columns do not exactly match target schema: {table}")
    key_value = _decode_value(after[key_column])
    existing = connection.execute(f'SELECT * FROM "{table}" WHERE "{key_column}"=?', (key_value,)).fetchone()
    actual = _encode_row(existing)
    if actual == after:
        return "already-applied"
    if actual != before:
        _refuse(f"delta compare-and-swap preimage mismatch: {category}/{item['id']}")
    if category == "humanReply":
        order_id = _decode_value(after.get("work_order_id"))
        parent = connection.execute("SELECT 1 FROM lf_work_orders WHERE work_order_id=?", (order_id,)).fetchone()
        if parent is None:
            _refuse(f"human reply parent work order is absent: {item['id']}")
    new_values = {column: _decode_value(after[column]) for column in columns}
    if before is None:
        names = ",".join('"' + column.replace('"', '""') + '"' for column in columns)
        markers = ",".join("?" for _ in columns)
        connection.execute(f'INSERT INTO "{table}" ({names}) VALUES ({markers})', [new_values[c] for c in columns])
    else:
        mutable = [column for column in columns if column != key_column]
        assignments = ",".join('"' + column.replace('"', '""') + '"=?' for column in mutable)
        where = ' AND '.join('"' + column.replace('"', '""') + '" IS ?' for column in columns)
        old_values = {column: _decode_value(before[column]) for column in columns}
        cur = connection.execute(f'UPDATE "{table}" SET {assignments} WHERE {where}',
                                 [new_values[c] for c in mutable] + [old_values[c] for c in columns])
        if cur.rowcount != 1: _refuse(f"delta compare-and-swap affected unexpected rows: {category}/{item['id']}")
    return "applied"


def reconcile(root: Path, expected_commit: str, delta_path: Path, expected_delta_sha256: str) -> dict[str, Any]:
    """CAS-reconcile an exact-schema delta in a prepared restore root and back it up."""
    from rollback_control import verify_restore_root, write_json_atomic, canonical_json
    root = Path(root).resolve(strict=True)
    state, meta = verify_restore_root(root, expected_commit)
    if state.get("phase") != "prepared-isolated":
        _refuse("reconciliation is permitted only before any Web process starts")
    database = root / "data" / "Db" / "logforesight.db"
    manifest_hash = _sha(Path(delta_path))
    if manifest_hash != expected_delta_sha256.lower():
        _refuse("delta manifest SHA-256 does not match the operator-pinned value")
    intent_path = root / "delta-reconcile-intent.json"
    delta, _ = _validate_delta(Path(delta_path), database, allow_committed_reconcile=intent_path.exists())
    if delta["before"].get("sha256") != state.get("preparedDatabaseSha256", state["files"]["databaseSha256"]):
        _refuse("delta base database differs from the exact paired rollback snapshot")
    delta_copy = root / "held-differences" / "delta-catalog.json"
    if delta_copy.exists():
        if _sha(delta_copy) != manifest_hash: _refuse("owned delta catalog differs from the operator-pinned manifest")
    else:
        shutil.copy2(Path(delta_path).resolve(strict=True), delta_copy)
    if intent_path.exists():
        intent = json.loads(intent_path.read_text(encoding="utf-8"))
        if intent.get("deltaManifestSha256") != manifest_hash:
            _refuse("a different delta is already in the owned reconciliation intent")
        if state.get("reconciliationIntentSha256") != _sha(intent_path):
            _refuse("owned reconciliation intent does not match sealed operation state")
    else:
        intent = {
            "schema": "lf-round54-rollback-reconcile-intent-v1",
            "root": str(root), "targetCommit": state["targetCommit"],
            "baseDatabaseSha256": state["files"]["databaseSha256"],
            "deltaManifestSha256": manifest_hash,
            "itemIdentities": [{"category": item["category"], "table": item["table"], "id": str(item["id"]),
                                "afterSha256": item["afterSha256"]} for item in json.loads(delta_copy.read_text(encoding="utf-8"))["items"]],
        }
        intent["sealSha256"] = hashlib.sha256(_canonical(intent)).hexdigest()
        intent_path.write_bytes(_canonical(intent) + b"\n")
        new_state = dict(state); new_state.pop("sealSha256", None)
        new_state["preparedDatabaseSha256"] = state["files"]["databaseSha256"]
        new_state["reconciliationIntentSha256"] = _sha(intent_path)
        new_state["deltaCatalogSha256"] = _sha(delta_copy)
        new_state["sealSha256"] = hashlib.sha256(_canonical(new_state)).hexdigest()
        from rollback_control import write_json_atomic
        write_json_atomic(root / "rollback-state.json", new_state)
        state = new_state

    receipt_path = root / "delta-reconciliation.json"
    if receipt_path.exists():
        existing = json.loads(receipt_path.read_text(encoding="utf-8"))
        if existing.get("deltaManifestSha256") != manifest_hash:
            _refuse("a different delta is already sealed in this restore root")
        receipt_seal = existing.get("sealSha256")
        unsigned = dict(existing); unsigned.pop("sealSha256", None)
        backup_path = root / "reconcile-backup" / "logforesight.after-reconcile.db"
        if not isinstance(receipt_seal, str) or hashlib.sha256(_canonical(unsigned)).hexdigest() != receipt_seal or not backup_path.is_file() or existing.get("postReconcileDatabaseSha256") != _sha(backup_path) or existing.get("restoreDatabaseSha256") != _sha(database):
            _refuse("existing reconciliation receipt or after-backup is incomplete; refusing automatic repair")
        if existing.get("restoreRoot") != str(root) or existing.get("targetCommit") != state["targetCommit"] or existing.get("authoritySha256") != state["authoritySha256"]:
            _refuse("existing reconciliation receipt belongs to another restore authority")
        repaired = dict(state); repaired.pop("sealSha256", None)
        repaired["preparedDatabaseSha256"] = existing["baseDatabaseSha256"]
        repaired["files"] = dict(state["files"]); repaired["files"]["databaseSha256"] = _sha(database)
        repaired["reconciliationIntentSha256"] = state.get("reconciliationIntentSha256", _sha(intent_path))
        repaired["deltaCatalogSha256"] = _sha(delta_copy)
        repaired["deltaReconciliationSha256"] = _sha(receipt_path)
        repaired["postReconcileBackupSha256"] = _sha(backup_path)
        repaired["sealSha256"] = hashlib.sha256(_canonical(repaired)).hexdigest()
        from rollback_control import write_json_atomic
        write_json_atomic(root / "rollback-state.json", repaired)
        return {"result": "reconciled-isolated", "restoreRoot": str(root), "items": existing.get("items", []),
                "postReconcileBackup": str(backup_path), "postReconcileBackupSha256": _sha(backup_path),
                "mailReleaseEligible": existing.get("mailReleaseEligible", False),
                "dataRecoveryEligible": existing.get("dataRecoveryEligible", False), "mailDispatch": "disabled"}

    applied: list[dict[str, str]] = []
    unknown_mail: list[str] = []
    connection = sqlite3.connect(database)
    connection.row_factory = sqlite3.Row
    try:
        connection.execute("PRAGMA foreign_keys=ON")
        connection.execute("BEGIN IMMEDIATE")
        if _metadata(connection) != {k: meta[k] for k in ("applicationId", "userVersion", "schemaSha256")}:
            _refuse("restore database schema authority changed before reconciliation")
        for category in CAPTURE_ORDER:
            for item in delta["items"]:
                if item["category"] != category: continue
                if not item["replayable"]:
                    applied.append({"category": category, "table": item["table"], "id": str(item["id"]), "afterSha256": item["afterSha256"], "outcome": "held-unreconciled"})
                    continue
                if category == "notificationReceipt":
                    before, after = item["before"], item["after"]
                    before_raw, after_raw = _mail_content(before), _mail_content(after)
                    if not _mail_acceptance_did_not_regress(before_raw, after_raw):
                        _refuse("mail delta would discard a previously accepted recipient receipt")
                    # A newer non-empty contribution ledger is opaque to this
                    # release. Remember it from both sides of the CAS so a
                    # delta cannot make the old target appear mail-safe by
                    # dropping the field.
                    unknown_mail.extend(_mail_unknown_states(before_raw))
                    actual_sql_row = connection.execute("SELECT * FROM lf_blobs WHERE blob_key='mail_notify_state'").fetchone()
                    actual = _encode_row(actual_sql_row)
                    if actual == after:
                        outcome = "already-applied"
                    elif actual != before:
                        _refuse("mail notification receipt compare-and-swap preimage mismatch")
                    else:
                        _mail_unknown_states(after_raw)
                        if after is None:
                            connection.execute("DELETE FROM lf_blobs WHERE blob_key='mail_notify_state'")
                        else:
                            columns = _table_columns(connection, "lf_blobs")
                            if set(after) != set(columns) or (before is not None and set(before) != set(columns)):
                                _refuse("mail notification row columns do not exactly match target schema")
                            after_values = {column: _decode_value(after[column]) for column in columns}
                            if before is None:
                                names = ",".join('"' + column.replace('"', '""') + '"' for column in columns)
                                markers = ",".join("?" for _ in columns)
                                connection.execute(f'INSERT INTO "lf_blobs" ({names}) VALUES ({markers})', [after_values[c] for c in columns])
                            else:
                                before_values = {column: _decode_value(before[column]) for column in columns}
                                mutable = [column for column in columns if column != "blob_key"]
                                assignments = ",".join('"' + column.replace('"', '""') + '"=?' for column in mutable)
                                where = ' AND '.join('"' + column.replace('"', '""') + '" IS ?' for column in columns)
                                cur = connection.execute(f'UPDATE "lf_blobs" SET {assignments} WHERE {where}',
                                                          [after_values[c] for c in mutable] + [before_values[c] for c in columns])
                                if cur.rowcount != 1: _refuse("mail notification receipt compare-and-swap affected unexpected rows")
                        outcome = "applied"
                    unknown_mail.extend(_mail_unknown_states(after_raw))
                else:
                    outcome = _apply_row(connection, category, item)
                applied.append({"category": category, "table": item["table"], "id": str(item["id"]), "afterSha256": item["afterSha256"], "outcome": outcome})
        # A pre-existing unknown/in-flight result is still an SMTP hold even
        # when this delta did not change the native mail-state blob.
        mail_row = connection.execute("SELECT content FROM lf_blobs WHERE blob_key='mail_notify_state'").fetchone()
        unknown_mail.extend(_mail_unknown_states(None if mail_row is None else str(mail_row[0])))
        connection.commit()
    except sqlite3.IntegrityError as exc:
        connection.rollback()
        _refuse("reply graph insert conflicts with a target identity or natural key; reconciliation was rolled back")
    except Exception:
        connection.rollback()
        raise
    finally:
        connection.close()

    after_db = root / "reconcile-backup" / "logforesight.after-reconcile.db"
    after_db.parent.mkdir(exist_ok=True)
    if after_db.exists(): after_db.unlink()
    src = sqlite3.connect(database)
    dest = sqlite3.connect(after_db)
    try:
        src.backup(dest)
        dest.execute("PRAGMA quick_check")
    finally:
        dest.close(); src.close()
    backup_connection = _readonly_db(after_db)
    try: backup_meta = _metadata(backup_connection)
    finally: backup_connection.close()
    if backup_meta != {k: meta[k] for k in ("applicationId", "userVersion", "schemaSha256")}:
        _refuse("post-reconcile backup schema authority changed")
    restore_db_hash = _sha(database)
    receipt = {
        "schema": BACKUP_SCHEMA,
        "restoreRoot": str(root),
        "targetCommit": state["targetCommit"],
        "authoritySha256": state["authoritySha256"],
        "deltaManifestSha256": manifest_hash,
        "clockAuthority": delta["clockAuthority"],
        "baseDatabaseSha256": delta["before"]["sha256"],
        "postReconcileDatabaseSha256": _sha(after_db),
        "restoreDatabaseSha256": restore_db_hash,
        "postReconcileMetadata": backup_meta,
        "items": applied,
        "mailUnknownOutcomes": sorted(set(unknown_mail)),
        "mailReleaseEligible": not unknown_mail and not any(x["category"] == "notificationReceipt" and x["outcome"] == "held-unreconciled" for x in applied),
        "dataRecoveryEligible": not any(x["category"] != "notificationReceipt" and x["outcome"] == "held-unreconciled" for x in applied),
        "externalMail": "held" if unknown_mail or any(x["category"] == "notificationReceipt" and x["outcome"] == "held-unreconciled" for x in applied) else "dedup-reviewed; application dispatch remains disabled",
    }
    receipt["sealSha256"] = hashlib.sha256(_canonical(receipt)).hexdigest()
    receipt_path.write_bytes(_canonical(receipt) + b"\n")
    new_state = dict(state)
    new_state["files"] = dict(state["files"])
    new_state["files"]["databaseSha256"] = restore_db_hash
    new_state["deltaCatalogSha256"] = _sha(delta_copy)
    new_state["deltaReconciliationSha256"] = _sha(receipt_path)
    new_state["postReconcileBackupSha256"] = _sha(after_db)
    new_state.pop("sealSha256", None)
    new_state["sealSha256"] = hashlib.sha256(canonical_json(new_state)).hexdigest()
    write_json_atomic(root / "rollback-state.json", new_state)
    return {"result": "reconciled-isolated", "restoreRoot": str(root), "items": applied,
            "postReconcileBackup": str(after_db), "postReconcileBackupSha256": _sha(after_db),
            "mailReleaseEligible": receipt["mailReleaseEligible"], "dataRecoveryEligible": receipt["dataRecoveryEligible"], "mailDispatch": "disabled"}


def verify_backup_projection(database: Path, backup: Path, receipt: dict[str, Any]) -> None:
    """Verify every reconciled whitelist row is identical in the live copy and after-backup."""
    source = _readonly_db(Path(database)); saved = _readonly_db(Path(backup))
    try:
        for item in receipt.get("items", []):
            if not isinstance(item, dict) or item.get("outcome") == "held-unreconciled": continue
            category, table, key_column = item.get("category"), item.get("table"), None
            if category == "case":
                expected_table, key_column = TABLES["case"]
            elif category == "humanReply":
                expected_table, key_column = TABLES["humanReply"]
            elif category == "issueHandling":
                expected_table, key_column = TABLES["issueHandling"]
            elif category == "handlingLog":
                expected_table, key_column = TABLES["handlingLog"]
            elif category == "pendingTask" and table in EXTRA_HELD_TABLES:
                expected_table, key_column = table, EXTRA_HELD_TABLES[table]
            elif category == "pendingTask":
                expected_table, key_column = TABLES["pendingTask"]
            elif category == "notificationReceipt":
                expected_table, key_column = "lf_blobs", "blob_key"
            else:
                raise Refusal("backup receipt contains an unknown protected category")
            if table != expected_table:
                raise Refusal("backup receipt table is outside the protected whitelist")
            key_value: Any = item["id"]
            if category == "notificationReceipt": key_value = "mail_notify_state"
            row_source = source.execute(f'SELECT * FROM "{table}" WHERE "{key_column}"=?', (key_value,)).fetchone()
            row_backup = saved.execute(f'SELECT * FROM "{table}" WHERE "{key_column}"=?', (key_value,)).fetchone()
            encoded_source, encoded_backup = _encode_row(row_source), _encode_row(row_backup)
            expected_hash = item.get("afterSha256")
            if encoded_source is None or encoded_backup is None or encoded_source != encoded_backup or hashlib.sha256(_canonical(encoded_source)).hexdigest() != expected_hash:
                raise Refusal(f"post-reconcile backup row relation failed: {category}/{item.get('id')}")
    finally:
        source.close(); saved.close()
