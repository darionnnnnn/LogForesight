# SQL Server M3 compatibility check

This private helper exercises the current production SQL Server provider on the existing installed SQL Server LocalDB Express instance. It creates a new, uniquely named LocalDB instance and one uniquely named database at a time, verifies provider edition first, and drops each database and instance in `finally`. It does not connect to or mutate a shared SQL database. No installer, download, private connection string, or reduced target list is used.

The datasets are synthetic provider/storage fixtures, not native PRTG evidence. Each database contains exactly one typed sampled value per sensor at the fixed targets 50, 500, 2,001, and 15,000 sensors (paired with 10, 100, 401, and 3,000 synthetic device rows). For each target the helper exercises the production `StorageBackend`, `EfPrtgStore` writes and period query, SQL-backed snapshot database identity plus durable filesystem checkpoint reload through a newly constructed journal instance, SQL recent-state lease exclusion and acknowledgement, production `BatchRunStore` RunId/log read and monotonic continuation after store re-open, a transaction rollback caused by a named CHECK constraint (`CK_e07_abort_sample_insert`, expected SQL error 547, matched against the provider's `SqlException.Errors`), and the production `Prune(180)` boundary using separate 181-day and 179-day sentinels. The rollback assertion verifies both attempted rows are absent after the failure; any other SQL error number or failure source fails the probe. These target databases contain one row per sensor, not a retention-period-density corpus; they do **not** satisfy M3's additional retention-period high-water read-path gate. The slice also does not capture SQL Server execution-plan output, so it cannot claim M3's index/query-plan check.

The report sets `sqlM3CompatibilityChecksPassed` only when all four target datasets and checks complete. It keeps `n6FullCapacityAccepted` and `n6RetentionAccepted` false. The 64,800,000-row SQL Express shape is denied before any workload database is created because the owned provider reports `EngineEdition=4`. This helper does not claim SQL full-capacity, full-retention, N6 mixed workload, backup/restore, upgrade/rollback, pause/recovery, or SQL Server full N6 acceptance. SQLite full-workload evidence remains separate and cannot substitute for SQL provider work.

## Build and run

Run from the repository root in this private worktree. `BaseIntermediateOutputPath` and `BaseOutputPath` isolate all referenced project outputs from the shared checkout.

```powershell
dotnet build scripts/prtg-workload/sqlserver-compat/sqlserver-compat.csproj -c Release -p:BaseIntermediateOutputPath=obj-e07/ -p:BaseOutputPath=bin-e07/
dotnet scripts/prtg-workload/sqlserver-compat/bin-e07/Release/net8.0-windows/LogForesight.Tests.dll
```

The JSON result is written under the helper's `evidence/sql-compat-<run-id>/` directory. It includes the exact source commit and SHA-256 hashes for the production SQL stores, journal, context, and helper. The owned SQL database and LocalDB instance are cleaned up after success or failure; the JSON evidence and isolated build outputs are retained for inspection.

## Existing evidence and code identity

Earlier actual LocalDB evidence exists, but it does not prove the current source unchanged:

- `../../../../prtg-round53-workload-harness/runs/a324d285395b4c8990bf3fa83bbfeebf/result.json` records 100,000 typed synthetic rows over 15,000 sensors. Its `EfPrtgStore.cs` hash was `45385ABF774C32A6801ACF9693647C4BFE97AD6453164336E1505042B62CD165`; current source at this helper's base commit hashes to `CE1732F9B4BCE518AD5CD5BCE260377CA30B4BF80F758A8AB8F7999BA778D4C3`.
- `../../../../logforesight-r01-scope/.gemini-tasks/sqlserver-provider-probe/evaluation-fixed-result.json` records prior live SQL provider checks for 15,000 IDs, candidate queries, conflict exclusion, replacement/prune rollback, and temporary ID-scope cleanup. It used `EfPrtgStore.cs` hash `8E32915EFE55B1049D1084505985B21FB20D7063AA629DE67C749ACC42D7121C` and `PrtgSqlServerIdScope.cs` hash `BE2B018AB353F2EFF11B30FB68970278A419F4420558F3C35957F1AF809E00B8`; the current SQL store and scope hashes differ.
- Current repository test `PrtgSqlServerIdScopeSourceTests.LargeIdScopeSubqueriesComposeIntoCandidateQueryWithoutExpandedIdParameters` checks generated SQL only. It is not live SQL Server evidence.

The current report records the source worktree `HEAD` at execution time and the live SHA-256 hashes for the referenced files. These compatibility checks are a bounded M3 segment; smaller datasets do not establish the fixed 64.8M-row N6, retention-period high-water read paths, recovery, or mixed-capacity gates.
