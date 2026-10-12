from __future__ import annotations
import datetime, hashlib, json, pathlib, shutil, sqlite3, sys

if len(sys.argv) != 8:
    raise SystemExit("usage: seed_fixture.py SOURCE_DB SOURCE_KEY DEST_DB DEST_KEY LOOPBACK_HTTP_URL SMTP_PORT RUN_ID")
src_db, src_key, dest_db, dest_key, http_url, smtp_port, run_id = map(str, sys.argv[1:])
source_db, source_key = pathlib.Path(src_db).resolve(), pathlib.Path(src_key).resolve()
target_db, target_key = pathlib.Path(dest_db).resolve(), pathlib.Path(dest_key).resolve()
if not source_db.is_file() or not source_key.is_file():
    raise SystemExit("synthetic source DB/key not found")
if target_db.exists() or target_key.exists():
    raise SystemExit("refusing to overwrite an existing destination DB/key")
if not http_url.startswith("http://127.0.0.1:"):
    raise SystemExit("HTTP source stub must use literal 127.0.0.1")

def digest(path: pathlib.Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            h.update(block)
    return h.hexdigest()

source_before = {"database": digest(source_db), "key": digest(source_key)}
target_db.parent.mkdir(parents=True, exist_ok=False)
target_key.parent.mkdir(parents=True, exist_ok=False)
with sqlite3.connect(f"file:{source_db.as_posix()}?mode=ro", uri=True) as source, sqlite3.connect(target_db) as target:
    source.backup(target, pages=256, sleep=0.01)
shutil.copyfile(source_key, target_key)

def get_blob(db: sqlite3.Connection, key: str, default: object) -> object:
    row = db.execute("SELECT content FROM lf_blobs WHERE blob_key=?", (key,)).fetchone()
    return default if row is None else json.loads(row[0])

def set_blob(db: sqlite3.Connection, key: str, value: object) -> None:
    body = json.dumps(value, ensure_ascii=False, separators=(",", ":"))
    current = db.execute("SELECT version FROM lf_blobs WHERE blob_key=?", (key,)).fetchone()
    # Match Microsoft.Data.Sqlite's DateTime text parameter format used for this
    # concurrency token: seven fractional digits, with trailing zeroes removed.
    # The original source DB remains untouched; only the owned clone receives this.
    now = (datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%d %H:%M:%S.%f") + "0").rstrip("0").rstrip(".")
    if current:
        db.execute("UPDATE lf_blobs SET content=?,updated_at=?,version=? WHERE blob_key=?", (body, now, int(current[0]) + 1, key))
    else:
        db.execute("INSERT INTO lf_blobs(blob_key,content,updated_at,version) VALUES(?,?,?,1)", (key, body, now))

with sqlite3.connect(target_db) as db:
    settings = get_blob(db, "system_settings", {})
    # Start with source access disabled. Root enables only the owned loopback endpoint through normal UI settings.
    settings["PrtgEnabled"] = False
    settings["PrtgUrl"] = ""
    settings["PrtgApiTokenEnc"] = ""
    settings["AiBaseUrl"] = ""
    settings["AiApiKeyEnc"] = ""
    settings["MailEnabled"] = False
    settings["SmtpServer"] = ""
    settings["SmtpPort"] = int(smtp_port)
    settings["SmtpUseTls"] = False
    settings["SmtpAccount"] = ""
    settings["SmtpPasswordEnc"] = ""
    settings["MailFrom"] = ""
    settings["MailRecipients"] = []
    settings["AutoDispatchEnabled"] = False
    set_blob(db, "system_settings", settings)

    schedules = get_blob(db, "schedule_options", {})
    schedules["Enabled"] = False
    # The cloned Web host must not read its own Windows Security channel during
    # a synthetic NetIQ-only run. This is the schedule page's "分析本機主機"
    # switch (ScheduleOptions.LocalAnalysisEnabled), independent of Enabled.
    schedules["LocalAnalysisEnabled"] = False
    if "AiEnabled" in schedules:
        schedules["AiEnabled"] = False
    set_blob(db, "schedule_options", schedules)

    sentinels = get_blob(db, "sentinels", [])
    if not isinstance(sentinels, list):
        raise SystemExit("sentinel fixture blob is not a list")
    for sentinel in sentinels:
        sentinel["Active"] = False
        sentinel["BaseUrl"] = ""
        sentinel["Username"] = ""
        sentinel["PasswordEnc"] = ""
    set_blob(db, "sentinels", sentinels)

    hosts = get_blob(db, "hosts", [])
    if not isinstance(hosts, list):
        raise SystemExit("host fixture blob is not a list")
    synthetic_hosts = sorted(
        (host for host in hosts if host.get("Active") is True and str(host.get("Source", "")).lower() == "netiq"),
        key=lambda host: int(host.get("HostId", 0)),
    )[:4]
    if len(synthetic_hosts) != 4:
        raise SystemExit(f"expected four active synthetic NetIQ hosts, found {len(synthetic_hosts)}")
    for index, host in enumerate(synthetic_hosts, 1):
        host["IpAddress"] = f"192.0.2.{index}"
        host["SentinelId"] = None
        host["NetiqServer"] = None
        host["Source"] = "netiq"
    set_blob(db, "hosts", hosts)

    netiq = get_blob(db, "netiq_options", {})
    netiq["RetryCount"] = 0
    netiq["TimeoutSeconds"] = 3
    netiq["QueryDelayMs"] = 0
    netiq["BackfillDays"] = 1
    set_blob(db, "netiq_options", netiq)

    db.commit()
    check_settings = get_blob(db, "system_settings", {})
    check_schedule = get_blob(db, "schedule_options", {})
    check_sentinels = get_blob(db, "sentinels", [])
    if check_settings.get("PrtgEnabled") is not False or check_settings.get("MailEnabled") is not False or check_settings.get("AutoDispatchEnabled") is not False:
        raise SystemExit("unsafe source settings remained enabled in clone")
    if (check_settings.get("AiBaseUrl") or check_settings.get("SmtpServer") or
            check_schedule.get("Enabled") is not False or
            check_schedule.get("LocalAnalysisEnabled") is not False):
        raise SystemExit("external source or schedule settings remained configured")
    if any(s.get("Active") or s.get("BaseUrl") or s.get("Username") or s.get("PasswordEnc") for s in check_sentinels):
        raise SystemExit("active or external NetIQ server remained configured")

source_after = {"database": digest(source_db), "key": digest(source_key)}
if source_before != source_after:
    raise SystemExit("source DB or key changed during clone")
print(json.dumps({
    "runId": run_id,
    "database": str(target_db),
    "key": str(target_key),
    "sourceDatabaseSha256": source_before["database"],
    "sourceKeySha256": source_before["key"],
    "sourceUnchanged": True,
    "localHostAnalysisEnabled": False,
    "disabledExternalSources": ["NetIQ", "PRTG", "AI", "SMTP", "schedules", "autodispatch", "local-host-analysis"],
    "scope": "synthetic fixture only"
}, ensure_ascii=False))
