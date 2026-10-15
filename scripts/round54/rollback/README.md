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

The preservation catalog uses schema `lf-round54-rollback-preservation-v1`, exact counts for `case`, `humanReply`, `notificationReceipt`, and `pendingTask`, plus each stable ID, `held-unreconciled` status, payload path, and payload hash. The tool copies every listed payload into `held-differences/<category>/<id>` and checks the exact bytes before launch. An empty category must have an explicit zero count. The catalog alone is never treated as proof of reconciliation.

For a reviewed post-backup delta, `capture-delta` reads the paired rollback DB and a separate checkpointed SQLite snapshot read-only. It emits a v2 JSON catalog with exact before/after rows, stable IDs, per-row hashes, table-schema hashes, source DB hashes, and a `clockAuthority` record containing a hashed host binding, Windows timezone ID, current local UTC offset, and an explicit same-host requirement. `reconcile` refuses the catalog if that host binding, timezone ID, or current offset differs; the comparison for local JSON timestamps and offset-free SQLite values uses the captured host's local-zone conversion and never rewrites either timestamp. It has an explicit table allowlist; it is not a general database migration tool. A table whose exact CREATE SQL, columns, primary key, indexes, or triggers differ is listed but held. The F2 `lf_prtg_scope_purge_jobs` table is always held for older releases. Physical deletions are refused. To keep Reply capture bounded on large databases, `handling_log` is read from its append-only sequence tail after the paired snapshot's last handling-log sequence; sequence regression or retention-boundary movement is refused. This supports native `EfRecordHandlingStore` appends, while out-of-band edits to pre-existing JSONL rows are outside this append-only contract.

`reconcile` consumes only a delta whose SHA-256 is supplied by the operator and whose base DB hash equals the exact DB already paired by `prepare`. It copies the full delta manifest into the owned root, seals an intent before writing, and performs an atomic SQLite transaction. A same-schema native Reply is eligible only as one complete graph: the exact parent work-order update, newly appended `replied` event, changed active cases, either a valid pending `CaseDayIntent` whose native row count exceeds the coordinator's production 5,000-row inline limit and has no day writes, or the exact issue-day rows and `handling_log` entries (up to 5,000), and the optional matching system `closed` event. The production coordinator limit remains 5,000; only the private contract-test seam lowers the threshold to 2 to exercise the pending-intent branch with a tiny native fixture. The graph verifier requires an unaliased host, resolves issue/day identity through `lf_top_issues`, validates the native `case_sync` history lines and row/log values, and accepts no unrelated delta item as part of the graph. Partial graphs, extra replies, collisions, unknown aliases, non-native order/case edits, purge jobs, and changed table schemas remain held. The graph is applied in one transaction with per-row compare-and-swap; a PK or natural-key conflict rolls the entire transaction back. A small real EF SQLite fixture exercises `WorkOrderCoordinator.Reply` and exports before/after snapshots at `work/round54/rollback-native-reply-fixture/fixture`; run it from the repository root with the public .NET test command below, then capture those snapshots with `capture-delta` to inspect the actual graph decision. The fixture uses a single synthetic host/case workflow and is not production or capacity evidence.

```powershell
dotnet test LogForesight.Tests/LogForesight.Tests.csproj --no-restore --filter "FullyQualifiedName~RollbackGraphFixture_ExportsBeforeAndAfterSnapshotsFromNativeSqliteReply"
```

To run the tiny pending-intent branch separately, use the same public project command with `--filter "FullyQualifiedName~RollbackGraphFixture_PendingReplyUsesNativeBatchConsumerAtPrivateTwoRowThreshold"`. That test-only threshold is 2; production remains at 5,000.

For `mail_notify_state`, the complete native blob row is compared and swapped only when the target still matches the exact preimage; previously accepted recipients and their timestamps cannot regress. Unknown or in-flight SMTP results remain a mail hold, including when that state already exists in the paired restore database and the delta contains no mail row. After the transaction, SQLite's backup API creates `reconcile-backup/logforesight.after-reconcile.db`; the receipt pins the delta, restore DB, after-backup, metadata, and each reconciled row. `verify` rechecks that receipt and that every reconciled protected row is present identically in both DBs. A sealed intent allows safe retry after a process interruption; a different delta cannot resume that root.

`release-check` reports separate data and mail gates. `release` seals the controlled data-recovery decision only if every original preservation item exactly matches a reconciled delta item and no non-mail held item remains. An SMTP hold may remain while the data restore is allowed. Even after dedup review, `launch` keeps mail, source writes, schedule, and dispatch disabled; a later operator must enable them through the normal settings path. No CLI command enables SMTP or synthesizes accepted receipts.

After the owned native fixture test succeeds, capture its two SQLite snapshots with a new output path:

```powershell
python scripts/round54/rollback/rollback_control.py capture-delta `
  --before-db 'work/round54/rollback-native-reply-fixture/fixture/before.db' `
  --current-db 'work/round54/rollback-native-reply-fixture/fixture/after.db' `
  --output 'work/round54/rollback-native-reply-fixture/fixture/native-reply-delta.json'
```

Inspect the catalog's `replayable` flags and `counts`; this contract fixture has one case and a small native day/log projection. It is a behavioral fixture only and must not be used as a capacity or production qualification result.

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
python scripts/round54/rollback/rollback_control.py capture-delta `
  --before-db 'D:\rollback-input\paired.db' `
  --current-db 'D:\snapshots\post-upgrade-checkpoint.db' `
  --output 'D:\rollback-input\round54-delta.json'
python scripts/round54/rollback/rollback_control.py reconcile `
  --restore-root 'D:\isolated\restore-2026-10-14' `
  --expected-target-commit $TargetCommit `
  --delta-manifest 'D:\rollback-input\round54-delta.json' `
  --expected-delta-sha256 '<reviewed delta SHA-256>'
python scripts/round54/rollback/rollback_control.py release-check `
  --restore-root 'D:\isolated\restore-2026-10-14' `
  --expected-target-commit $TargetCommit
python scripts/round54/rollback/rollback_control.py release `
  --restore-root 'D:\isolated\restore-2026-10-14' `
  --expected-target-commit $TargetCommit
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

`verify` reports `verified-closed`; after a delta is consumed, `launch` also requires a sealed `release` decision. Windows `CommandLineToArgvW` parses the actual process arguments for exact executable/DLL/contentRoot/URL tokens, rejecting substrings and additional switches; the actual loopback listener must belong exclusively to that Web PID. `stop` verifies PID, process start time, executable and full command line before stopping that process. A rejection exits 2 with `refused-closed`, before Web starts; a post-start identity rejection stops the exact spawned process and keeps the restore closed. This is a controlled path for an isolated copy; it does not prevent someone from manually launching an old binary outside the tool. Once Web is running, the gate covers startup settings and storage isolation; it is not an OS sandbox against later manually invoked application features. SQL Server recovery, business graph changes outside the exact native Reply shape, cross-schema migration, and externally provable SMTP exactly-once delivery remain unsupported and fail closed.

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
