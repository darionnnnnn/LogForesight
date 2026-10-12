# SQLite owned-copy high-water checks

This helper measures SQLite backup/restore, the production 180-day prune boundary, and three additional 15,000-row typed-storage rounds on a separate owned copy of a completed generator database. It never writes to the source database. It requires the exact fixed input shape: 64,800,000 rows, 3,000 devices, 15,000 sensors, 180 days, 4,320 hourly rows per sensor, and four typed-proof slots per row. The generator's synthetic/no-native-authority label is preserved in the output.

The result describes these storage checks only. `measuredChecksPassed` covers the observed backup/restore, strict retention sentinels, source fence, and three storage rounds; `fullCapacityAccepted`, `retentionAccepted`, `recoveryAccepted`, `wholeRoundAccepted`, and SQL Server compatibility remain false. This is not a mixed workload or full-capacity verdict, and it does not test production pause/recovery or journal-fault behavior.

Run the commands from the repository root in PowerShell. Every output directory must be new, dedicated, on a local fixed volume, and outside a reparse-point path. The helper refuses to reuse an existing output directory.

Build the helper:

```powershell
dotnet build scripts/prtg-workload/sqlite-highwater/PrtgSqliteHighwater.csproj -c Release
```

Run the bounded safety self-test first:

```powershell
$selfTest = Join-Path $env:TEMP ('lf-sqlite-highwater-selftest-' + [guid]::NewGuid().ToString('N'))
dotnet run --project scripts/prtg-workload/sqlite-highwater/PrtgSqliteHighwater.csproj -c Release -- --self-test true --output $selfTest
```

Generate the exact SQLite source and its report with the isolated workload generator. A full run requires substantial free disk space; the generator checks its reserve before writing.

```powershell
$sourceRun = Join-Path $env:TEMP ('lf-n6-full-sqlite-' + [guid]::NewGuid().ToString('N'))
dotnet run --project scripts/prtg-workload/storage-generator/PrtgStorageGenerator.csproj -c Release -- --mode n6 --provider sqlite --root $sourceRun --rows 64800000
```

Pass the generated database and report to the helper, using a second new directory for the owned backup and restored working copy:

```powershell
$ownedRun = Join-Path $env:TEMP ('lf-sqlite-owned-highwater-' + [guid]::NewGuid().ToString('N'))
$sourceDb = Get-ChildItem -LiteralPath $sourceRun -Filter 'n6-sqlite-*.db' | Select-Object -First 1
$sourceReport = Join-Path $sourceRun 'n6-high-water-result.json'
dotnet run --project scripts/prtg-workload/sqlite-highwater/PrtgSqliteHighwater.csproj -c Release -- --source-db $sourceDb.FullName --source-report $sourceReport --output $ownedRun
```

The helper backs up the source to `source-backup.sqlite`, restores that copy to `restored-working.sqlite`, checks integrity and exact row/proof/index shape, and runs `PrtgStore.Prune(180)` against one sentinel just before and one just after the cutoff. It then appends three separate 15,000-row batches through production `MergeSampledValues`, measuring retained proof bytes, index bytes, database bytes, WAL/SHM bytes, and combined storage after each round. These rounds measure storage growth; they do not replay ingestion, analysis, API, or live-source traffic.

Independently verify a completed helper report. This opens the source and restored database read-only and rechecks counts, proof shape, report hash, and restored integrity:

```powershell
$result = Join-Path $ownedRun 'sqlite-owned-copy-highwater-result.json'
dotnet run --project scripts/prtg-workload/sqlite-highwater/PrtgSqliteHighwater.csproj -c Release -- --verify-result true --result $result
```
