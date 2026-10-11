# Isolated qualification workload matrix

These scripts run the saved qualification-job workflow and six fixed workload cells through the authenticated public Web API. They require PowerShell 7 and numeric-loopback URLs for Web, PRTG, and Sentinel. They do not create source bindings or proofs, and they keep native-source, formal-acceptance, retention-acceptance, and whole-round flags false.

## Prepare the exact sensor manifest

Use the authenticated public API at the same Web fixture URL that will be passed as `BaseUri`:

1. Read `GET api/admin/settings` and save its `revision` and configured `prtgUrl`.
2. Read `GET api/prtg/monitoring/trusted-sampling/qualification-jobs/contract`. The saved scope must be exactly 15,000 selected sensors and must have a current capacity pilot before `Start`.
3. Read every page of `GET api/prtg/monitoring/trusted-sampling/profiles?offset={0,100,...}&limit=100`. Preserve the ordered `sensorObjid` list and, for each row, the exact `currentIdentityEpoch`, `currentChannelGeneration`, `bindingRevision`, and `bindingFingerprint` values.
4. Re-read settings and contract after paging. Discard the snapshot if the settings revision, policy revision, scope fingerprint, source generation, authority context, enabled state, or selected sensor count changed.

Save a UTF-8 JSON manifest no larger than 8 MiB with this shape (the arrays contain the actual 15,000 values read above):

```json
{
  "schema": "qualification-job-manifest-v1",
  "sensorIds": [101, 102],
  "sensorFences": [
    {
      "sensorObjid": 101,
      "identityEpoch": 4,
      "channelGeneration": "...",
      "bindingRevision": 8,
      "bindingFingerprint": "..."
    }
  ],
  "settingsRevision": "...",
  "policyRevision": "...",
  "scopeFingerprint": "...",
  "sourceGeneration": "...",
  "authorityContextFingerprint": "..."
}
```

The abbreviated example above is only a schema illustration; the runnable manifest must contain all 15,000 ordered IDs and exactly one matching fence per ID. The lifecycle scripts enforce that contract and re-read the live profile pages before accepting qualification.

## Declare the owned provider and initialize a run

Create the provider descriptor yourself. For SQLite, the database file must already exist, be a valid SQLite database, and live under an existing owned `dataRoot` inside the repository. The script checks the file header; it does not create or populate the database. For LocalDB, supply the actual instance and database names. In either case, `retentionDays: 180` is an operator declaration, not retention evidence.

SQLite descriptor:

```json
{
  "schema": "round53-owned-run-descriptor-v1",
  "provider": "sqlite",
  "dataRoot": "C:\\work\\logforesight\\qualification-data",
  "databaseFile": "C:\\work\\logforesight\\qualification-data\\owned.db",
  "retentionDays": 180
}
```

LocalDB descriptor:

```json
{
  "schema": "round53-owned-run-descriptor-v1",
  "provider": "localdb",
  "dataRoot": "C:\\work\\logforesight\\qualification-data",
  "instanceName": "(localdb)\\LogForesightQualification",
  "databaseName": "LogForesightQualification",
  "retentionDays": 180
}
```

Build the public coverage verifier from its source in this directory:

```powershell
$ToolRoot = 'C:\path\to\repository\scripts\prtg-workload'
Set-Location $ToolRoot
dotnet build .\PrtgWorkloadGate.csproj -c Release
$CoverageAssembly = Join-Path $ToolRoot 'bin\Release\net8.0-windows\PrtgWorkloadGate.dll'
```

Keep one authenticated `WebRequestSession` for all commands. `$BaseUri`, `$PrtgFixtureUri`, and `$SentinelFixtureUri` must be explicit numeric-loopback URLs; the saved `prtgUrl` must match the supplied PRTG URL. Run the examples from this script directory, choose an empty run directory, and keep evidence under the repository:

```powershell
$Common = @{
  Profile = 'fullstore-3000'
  BaseUri = [uri]'http://127.0.0.1:5100/'
  PrtgFixtureUri = [uri]'http://127.0.0.1:5101/'
  SentinelFixtureUri = [uri]'http://127.0.0.1:5102/'
  ManifestPath = (Join-Path $ToolRoot 'owned\sensor-manifest.json')
  WebSession = $WebSession
  CoverageAssemblyPath = $CoverageAssembly
  MatrixRunDirectory = (Join-Path $ToolRoot 'owned\run-001')
  EvidenceRoot = (Join-Path $ToolRoot 'owned\run-001\evidence')
}
$Init = @{
  Profile = $Common.Profile; BaseUri = $Common.BaseUri; PrtgFixtureUri = $Common.PrtgFixtureUri
  SentinelFixtureUri = $Common.SentinelFixtureUri; ManifestPath = $Common.ManifestPath
  ProviderDescriptorPath = (Join-Path $ToolRoot 'owned\provider.json')
  MatrixRunDirectory = $Common.MatrixRunDirectory; WebSession = $Common.WebSession
}
& .\Initialize-QualificationMatrixRun.ps1 @Init
```

The initializer writes `run-owner.json`, `round53-profile.json`, `full-consumer-authority-preflight.json`, and a hash-fenced `host-ids.txt` from the live `GET api/admin/hosts/all` DTO. Its status is `owned-fixture-declared-awaiting-qualification`; it never claims current proof, physical source, or successful retention.

## Run qualification and the six cells

Start the durable job explicitly. If it remains active at the bounded polling deadline, resume only with the exact returned job ID, version, and wave. Once it completes, run `Check`; a ready result must cover all 15,000 raw rows and all 15,000 current profile rows. Only then does the preflight advance to `current-qualified-proofs` and bind the exact job and coverage receipt hashes.

```powershell
$Start = & .\Invoke-QualificationMatrix.ps1 @Common -Action Start
# If pending, read qualification-lifecycle\qualification-job-receipt.json and pass its exact jobId/version/wave:
$Resume = & .\Invoke-QualificationMatrix.ps1 @Common -Action Resume -JobId $JobId -ExpectedVersion $Version -ExpectedWave $Wave
$Check = & .\Invoke-QualificationMatrix.ps1 @Common -Action Check -JobId $JobId -ExpectedVersion $Version -ExpectedWave $Wave
```

Run each matrix cell in order, as a separate explicit action. The sequence is fixed: `netiq-only`, `combined`, `combined`, `netiq-only`, `netiq-only`, `combined`.

```powershell
1..6 | ForEach-Object {
  & .\Invoke-QualificationMatrix.ps1 @Common -Action Matrix -ConditionIndex $_
}
```

Each cell verifies the live source/policy/identity/channel/binding/proof/job fence before scheduling and again after schedule plus route measurement. It requires the schedule timing RunId to match the actual last scheduler BatchRunId, previews exactly 3,000 hosts before POST, and measures both public host-list and host-detail routes with 100 successful samples each. The fixed gates are p95 NetIQ delta at most 10%, each route's p95 delta at most 10% for all three fixed baseline/combined pairs, route p95 at most 2 seconds, and at most 75% of the configured window. A zero route baseline cannot prove a relative delta and fails closed. A metric pass is limited to this isolated fixture; it does not establish physical-source, 180-day retention, or formal acceptance.

`Test-InitializeQualificationMatrixRun.ps1`, `Test-InvokeQualificationJob.ps1`, `Test-QualificationCoverage-Collector.ps1`, `Test-LiveQualificationFence.ContractTests.ps1`, `Test-InvokeQualificationMatrixConsumer.ps1`, and `Test-InvokeQualificationMatrix.WrapperIntegration.ps1` exercise the public contracts and fail-closed cases. The wrapper fixture runs Check and all six cells against public Web DTO shapes, then verifies both a persisted failed-cell/no-checkpoint case and strict trigger-time rejection under deliberate negative clock skew. Its success timestamps are offset by two seconds only inside the controlled fixture to model API-host/client clock skew; production correlation remains strict and requires a fresh timing RunId matching the actual scheduler BatchRunId and trigger boundary. These fixtures do not establish physical-source capacity or retention evidence.

The Settings revision is carried continuously from the qualified preflight through each cell. A cell may advance it only through its own compare-and-swap condition change; the pre-measurement fence must match that returned revision and the post-measurement fence must match the pre-measurement revision. This rejects an intervening setting update even if a later update restores the visible value.

If a cell fails, keep its failed owner receipt and do not resume by overwriting that attempt. Create a new owned run and use a current manifest/contract for its qualification. Recheck the retained completed job only when its original settings revision and source context still match; otherwise start a fresh qualification job in the current context, reusing existing qualified binding evidence only where the service supports that operation. Never rewrite an old job or receipt to make its revision appear current.

Route success requires the real ApiResponse success flag and object DTO, an exact non-truncated 3,000-item host-list identity set, and the requested host-detail ID. HTTP 200 error/empty envelopes count as failures. Route bodies remain bounded to 2 MiB with the original two-second request deadline.

Evaluation requires the complete raw samples for both routes in every cell, the requested denominator (100–10,000 per route), and exactly twice that many requests. It independently recomputes nearest-rank p95, maximum, successes and failures; absent, truncated, nonnumeric, foreign-route or contradictory measurements fail closed. The wrapper's positive functional fixture deliberately performs slower real server work for baseline routes than combined routes, so its measured timing vectors are separated without changing the consumer clock or acceptance thresholds. This controlled work is not a provider-capacity measurement.
