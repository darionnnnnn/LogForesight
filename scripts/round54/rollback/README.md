# Round 54 controlled SQLite rollback

`rollback_control.py` is the C54-04-B startup gate. It accepts one exact, operator-pinned Web/Core release and its paired SQLite database, key, configuration, schema/authority manifest, and preservation catalog. It creates a new restore root and never edits an input or an existing restore root. SQL Server is explicitly unsupported by this first delivery.

The input manifest is JSON with this shape:

```json
{
  "schema": "lf-round54-rollback-input-v1",
  "versions": {
    "authority": "authority-54.1",
    "schema": "lf-schema-53",
    "source": "source-authority-54.1"
  },
  "files": {
    "webRoot": { "path": "release", "sha256": "<tree SHA-256>" },
    "database": { "path": "paired.db", "sha256": "<SHA-256>" },
    "key": { "path": "lf-crypto.key", "sha256": "<SHA-256>" },
    "config": { "path": "appsettings.json", "sha256": "<SHA-256>" },
    "authority": { "path": "authority-manifest.json", "sha256": "<SHA-256>" },
    "preservation": { "path": "preservation.json", "sha256": "<SHA-256>" }
  }
}
```

The authority manifest uses schema `lf-round54-rollback-authority-v1` and pins the target commit, authority/schema/source-authority versions, actual Web/Core DLL and assets hashes, the complete Web tree hash, SQLite `application_id`, `user_version`, schema hash, DB/key/config hashes, and key filename. Pass the exact full target commit and authority manifest SHA-256 from the reviewed release record. The tool reads `build-identity.json` and the database itself; it does not accept a JSON `verified`, `compatible`, or other Boolean claim as evidence. These hashes and the operation state's `sealSha256` detect byte changes relative to operator-supplied pins and the prepared state; they are not a digital signature or proof of who authored the manifest. The manifest SHA and target commit must come from an independently reviewed release record.

The preservation catalog uses schema `lf-round54-rollback-preservation-v1`, exact counts for `case`, `humanReply`, `notificationReceipt`, and `pendingTask`, plus each stable ID, `held-unreconciled` status, payload path, and payload hash. The tool copies every listed payload into `held-differences/<category>/<id>` and checks the exact bytes before launch. An empty category must have an explicit zero count. Differences stay held; no replay is attempted. Mail remains disabled while those receipts and pending items await separate deduplication.

Before preparing a root, the tool checks the exact source stamp/commit, Web/Core identity, all input hashes, the paired config, and read-only SQLite metadata. Before launch it rechecks the sealed operation state, copied Web/Core/assets/config/DB/key and preservation payloads, and database schema metadata. The application data root is copied using the product's actual layout: `data/Db/logforesight.db` and `data/keys/lf-crypto.key`. It requires scheduling and local analysis disabled, all mail channels and auto-dispatch disabled, PRTG disabled, `AiBaseUrl` empty, AD authentication disabled with no AD servers, and every persisted Sentinel explicitly inactive. It also requires the release config's `Auth.Provider` to be local `Stub`; startup runs in Development so the old release permits this isolated local auth mode. Missing or unknown values fail closed. Launch pins storage to the copied SQLite root, strips inherited app integration/storage/auth overrides while retaining Windows OS identity and .NET runtime variables needed by the old host, binds only to loopback, and refuses an occupied port before starting Web. Same-database downgrade is not provided.

Example operator sequence in PowerShell:

```powershell
$TargetCommit = '<full reviewed target commit>'
$AuthoritySha256 = '<reviewed authority-manifest SHA-256>'
python scripts/round54/rollback/rollback_control.py prepare `
  --manifest 'D:\rollback-input\rollback-input.json' `
  --expected-target-commit $TargetCommit `
  --expected-authority-sha256 $AuthoritySha256 `
  --restore-root 'D:\isolated\restore-2026-10-14'
python scripts/round54/rollback/rollback_control.py verify `
  --restore-root 'D:\isolated\restore-2026-10-14' `
  --expected-target-commit $TargetCommit
python scripts/round54/rollback/rollback_control.py launch `
  --restore-root 'D:\isolated\restore-2026-10-14' `
  --expected-target-commit $TargetCommit `
  --port 5097
python scripts/round54/rollback/rollback_control.py stop `
  --restore-root 'D:\isolated\restore-2026-10-14' `
  --expected-target-commit $TargetCommit
```

`verify` reports `verified-closed`; `launch` is the only supported Web startup path in this tool and reports `web-started-isolated`. Windows `CommandLineToArgvW` parses the actual process arguments for exact executable/DLL/contentRoot/URL tokens, rejecting substrings and additional switches; the actual loopback listener must belong exclusively to that Web PID. `stop` verifies PID, process start time, executable and full command line before stopping that process. A rejection exits 2 with `refused-closed`, before Web starts; a post-start identity rejection stops the exact spawned process and keeps the restore closed. This is a controlled path for an isolated copy; it does not prevent someone from manually launching an old binary outside the tool. Once Web is running, the gate covers startup settings and storage isolation; it is not an OS sandbox against later manually invoked application features. SQL Server recovery and external SMTP reconciliation remain outside this SQLite delivery.

Run the self-owned fixture checks with:

```powershell
python -m unittest discover -s scripts/round54/rollback/tests -v
```


To exercise the exact clean Round-53 Web release from a tiny, temporary database created by that old Web build (not the production DB/key), run:

```powershell
$Release = 'C:\Users\eldar\Documents\Codex\2026-10-01\c-users-eldar-claude-project-logforesight\work\root-round53-publish-638c5843'
python scripts/round54/rollback/tests/smoke_real_target.py --release-root $Release --report scripts/round54/rollback/tests/real-target-smoke-report.json
```

This runtime check first calls the repository's `Verify-BuildIdentity.ps1`, uses the actual Round-53 app to create the production SQLite schema and key paths in a temporary directory, then runs `prepare` → `verify` → `launch` → `stop`. It checks a small exact case/reply/task/notification-state fixture, durable sentinel/settings rows, a local external-connection trap, input DB/key hashes, and the target release tree hash. Its report is evidence for this target build and fixture only; it is not a SQL Server or production-reconciliation test.
