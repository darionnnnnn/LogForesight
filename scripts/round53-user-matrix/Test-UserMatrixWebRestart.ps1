[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$WebOutputDirectory,
    [Parameter(Mandatory=$true)][string]$WebAssetsDirectory,
    [Parameter(Mandatory=$true)][string]$SourceFixtureBundlePath,
    [Parameter(Mandatory=$true)][ValidatePattern('^[0-9a-f]{40}$')][string]$ExpectedSourceCommit
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion -lt [Version]'7.4') { throw 'Requires PowerShell 7.4+.' }
$stageRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$startScript = Join-Path $stageRoot 'Start-UserMatrixFixture.ps1'
$restartScript = Join-Path $stageRoot 'Restart-UserMatrixWeb.ps1'
$stopScript = Join-Path $stageRoot 'Stop-UserMatrixFixture.ps1'
$started = $null
$ownerPath = $null
$originalOwnerText = $null
$manifestPath = $null
$originalManifestText = $null
$ownerWasTemporarilyChanged = $false
$manifestWasTemporarilyChanged = $false

function Assert-RefusedWithoutRestart([string]$Label, [string]$Path) {
    $refused = $false
    try { & $restartScript -OwnerFile $Path | Out-Null } catch { $refused = $true }
    if (!$refused) { throw "Restart helper accepted invalid $Label." }
    $currentOwner = $originalOwnerText | ConvertFrom-Json
    $web = @($currentOwner.Processes | Where-Object Kind -CEQ 'web')
    $source = @($currentOwner.Processes | Where-Object Kind -CEQ 'sources')
    if ($web.Count -ne 1 -or $source.Count -ne 1) { throw "Invalid $Label changed the fixture process list." }
    foreach ($record in @($web[0],$source[0])) {
        $process = Get-Process -Id ([int]$record.ProcessId) -ErrorAction Stop
        $process.Refresh()
        if ($process.HasExited -or $process.StartTime.ToUniversalTime().Ticks -ne [long]$record.StartTimeUtcTicks) { throw "Invalid $Label stopped an owned process." }
    }
}

try {
    $started = & $startScript -WebOutputDirectory $WebOutputDirectory -WebAssetsDirectory $WebAssetsDirectory -SourceFixtureBundlePath $SourceFixtureBundlePath -ExpectedSourceCommit $ExpectedSourceCommit
    $ownerPath = [IO.Path]::GetFullPath($started.OwnerFile)
    $originalOwnerText = Get-Content -LiteralPath $ownerPath -Raw
    $owner = $originalOwnerText | ConvertFrom-Json
    $manifestPath = [IO.Path]::GetFullPath($started.ManifestPath)
    $originalManifestText = Get-Content -LiteralPath $manifestPath -Raw
    $manifest = $originalManifestText | ConvertFrom-Json
    $webBefore = @($owner.Processes | Where-Object Kind -CEQ 'web')[0]
    $sourceBefore = @($owner.Processes | Where-Object Kind -CEQ 'sources')[0]
    $dataRoot = [IO.Path]::GetFullPath([string]$manifest.Runtime.DataRoot)
    $dbPath = Join-Path $dataRoot 'Db\logforesight.db'
    $keyPath = Join-Path $dataRoot 'keys\lf-crypto.key'
    $pythonStateScript = Join-Path $started.BundlePath 'capture-restart-durable-state.py'
    $pythonState = @'
import base64, hashlib, json, sqlite3, sys
db = sys.argv[1]
conn = sqlite3.connect("file:" + db.replace("\\", "/") + "?mode=ro", uri=True, timeout=3)
conn.execute("PRAGMA query_only=ON")
tables = ["lf_log_lines", "lf_daily_records", "lf_top_issues", "lf_issue_cases", "lf_work_orders", "lf_work_order_events", "lf_prtg_sampled_batches"]
def val(v):
    if isinstance(v, bytes): return {"base64": base64.b64encode(v).decode("ascii")}
    return v
out = {"databasePath": db, "applicationId": conn.execute("PRAGMA application_id").fetchone()[0], "userVersion": conn.execute("PRAGMA user_version").fetchone()[0], "tables": {}}
existing = {row[0] for row in conn.execute("SELECT name FROM sqlite_master WHERE type='table'")}
for table in tables:
    if table not in existing: raise RuntimeError("expected production table missing: " + table)
    info = list(conn.execute("PRAGMA table_info('" + table + "')"))
    columns = [r[1] for r in info]
    pk = [r[1] for r in sorted((r for r in info if r[5]), key=lambda r: r[5])]
    rows = [[val(v) for v in row] for row in conn.execute('SELECT * FROM "' + table + '"')]
    encoded = sorted(json.dumps(row, sort_keys=True, separators=(",", ":"), ensure_ascii=False) for row in rows)
    digest = hashlib.sha256("\n".join(encoded).encode("utf-8")).hexdigest()
    ids = []
    if pk:
        idx = [columns.index(name) for name in pk]
        ids = sorted([row[i] for i in idx] for row in rows)
    out["tables"][table] = {"count": len(rows), "rowsSha256": digest, "primaryKeyColumns": pk, "primaryKeyValues": ids}
print(json.dumps(out, sort_keys=True, separators=(",", ":")))
'@
    [IO.File]::WriteAllText($pythonStateScript,$pythonState,[Text.UTF8Encoding]::new($false))
    $python = @(Get-Command python.exe -CommandType Application -All -ErrorAction Stop | ForEach-Object Source | Select-Object -Unique | Where-Object { $_ -notmatch '\\Microsoft\\WindowsApps\\' -and (Test-Path -LiteralPath $_ -PathType Leaf) } | Select-Object -First 1)
    if ($python.Count -ne 1) { throw 'A real python.exe is required for read-only SQLite durable-state verification.' }
    function Capture-DurableState([string]$Label) {
        $result = & $python[0] $pythonStateScript $dbPath 2>&1
        if ($LASTEXITCODE -ne 0) { throw "Could not capture $Label production database state: $($result -join ' ')" }
        $parsed = ($result | Select-Object -Last 1) | ConvertFrom-Json
        if ([IO.Path]::GetFullPath([string]$parsed.databasePath) -ine [IO.Path]::GetFullPath($dbPath)) { throw "Read-only $Label database identity path mismatch." }
        $snapshotPath = Join-Path $started.BundlePath ("durable-state-$Label.json")
        [IO.File]::WriteAllText($snapshotPath,($parsed | ConvertTo-Json -Depth 16),[Text.UTF8Encoding]::new($false))
        return $parsed
    }
    $probeRoot = Join-Path $started.BundlePath 'restart-store-probe'
    $probeSource = Join-Path $probeRoot 'Program.cs'
    $probeProject = Join-Path $probeRoot 'RestartStoreProbe.csproj'
    $probeOutput = Join-Path $probeRoot 'out'
    [IO.Directory]::CreateDirectory($probeRoot) | Out-Null
    $source = @'
using System.Text.Json;
using LogForesight.Core.Configuration;
using LogForesight.Core.Persistence;

if (args.Length != 4) throw new ArgumentException("Expected write/read, data root, key and nonce.");
var mode = args[0]; var dataRoot = Path.GetFullPath(args[1]); var key = args[2]; var nonce = args[3];
var backend = new StorageBackend(new StorageSettings { Type = "Sqlite", DataRoot = dataRoot }, dataRoot);
var store = backend.Blob(key);
if (mode == "write")
{
    var payload = JsonSerializer.Serialize(new { schema = "lf-u53-web-restart-sentinel-v1", runId = Path.GetFileName(Path.GetDirectoryName(dataRoot)), nonce });
    var saved = store.Mutate<string>(current =>
    {
        if (current is not null) throw new InvalidOperationException("Restart sentinel key already exists.");
        return (payload, payload);
    });
    Console.WriteLine(saved);
}
else if (mode == "read")
{
    var payload = store.Read() ?? throw new InvalidOperationException("Restart sentinel disappeared.");
    using var parsed = JsonDocument.Parse(payload);
    var root = parsed.RootElement;
    if (root.GetProperty("schema").GetString() != "lf-u53-web-restart-sentinel-v1" ||
        root.GetProperty("runId").GetString() != Path.GetFileName(Path.GetDirectoryName(dataRoot)) ||
        root.GetProperty("nonce").GetString() != nonce) throw new InvalidDataException("Restart sentinel identity/content changed.");
    Console.WriteLine(payload);
}
else throw new ArgumentException("Mode must be write or read.");
'@
    [IO.File]::WriteAllText($probeSource, $source, [Text.UTF8Encoding]::new($false))
    $references = foreach ($dll in Get-ChildItem -LiteralPath (Join-Path $started.BundlePath 'app') -Filter '*.dll' -File) {
        $relative = [IO.Path]::GetRelativePath($probeRoot,$dll.FullName).Replace('\','/')
        "<Reference Include=`"$($dll.BaseName)`"><HintPath>$relative</HintPath></Reference>"
    }
    $project = @"
<Project Sdk=`"Microsoft.NET.Sdk`">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0-windows</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
  <ItemGroup><FrameworkReference Include=`"Microsoft.AspNetCore.App`" />$($references -join "`n    ")</ItemGroup>
</Project>
"@
    [IO.File]::WriteAllText($probeProject,$project,[Text.UTF8Encoding]::new($false))
    $dotnet = [string]$owner.DotnetPath
    $buildLog = & $dotnet build $probeProject -c Release --nologo -o $probeOutput 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Owned production-store restart probe failed to build: $($buildLog -join ' ')" }
    $probeDll = Join-Path $probeOutput 'RestartStoreProbe.dll'
    $nativePath = Join-Path $started.BundlePath 'app\runtimes\win-x64\native'
    $savedPath = $env:PATH
    try {
        $env:PATH = $nativePath + ';' + $env:PATH
        $nonce = [guid]::NewGuid().ToString('N')
        $sentinelKey = 'round53_web_restart_' + $started.RunId
        $writeOutput = & $dotnet $probeDll 'write' $dataRoot $sentinelKey $nonce 2>&1
        if ($LASTEXITCODE -ne 0) { throw "Production StorageBackend sentinel write failed: $($writeOutput -join ' ')" }
        $written = ($writeOutput | Select-Object -Last 1 | ConvertFrom-Json)
        if ($written.schema -cne 'lf-u53-web-restart-sentinel-v1' -or $written.runId -cne $started.RunId -or $written.nonce -cne $nonce) { throw 'Production store did not return the exact owned restart sentinel.' }
    }
    finally { $env:PATH = $savedPath }

    # Invalid-owner/data/build fences are exercised against this isolated run before the valid restart.
    $foreignOwner = Join-Path $started.BundlePath 'foreign-owner.json'
    [IO.File]::WriteAllText($foreignOwner,$originalOwnerText,[Text.UTF8Encoding]::new($false))
    Assert-RefusedWithoutRestart 'owner path outside canonical fixture files' $foreignOwner
    Remove-Item -LiteralPath $foreignOwner -Force
    foreach ($mutation in @(
        @{ Label='PID mismatch'; Change={ param($candidate) $candidate.Processes | Where-Object Kind -CEQ 'web' | ForEach-Object { $_.ProcessId = [int]$_.ProcessId + 100000 } } },
        @{ Label='mixed assembly hash'; Change={ param($candidate) $candidate.Processes | Where-Object Kind -CEQ 'web' | ForEach-Object { $_.AssemblySha256 = ('0' * 64) } } }
    )) {
        $candidate = $originalOwnerText | ConvertFrom-Json
        & $mutation.Change $candidate
        [IO.File]::WriteAllText($ownerPath,($candidate | ConvertTo-Json -Depth 24),[Text.UTF8Encoding]::new($false))
        $ownerWasTemporarilyChanged = $true
        Assert-RefusedWithoutRestart $mutation.Label $ownerPath
        [IO.File]::WriteAllText($ownerPath,$originalOwnerText,[Text.UTF8Encoding]::new($false))
        $ownerWasTemporarilyChanged = $false
    }
    $wrongRootManifest = $originalManifestText | ConvertFrom-Json
    $wrongRootManifest.Runtime.DataRoot = [IO.Path]::GetFullPath((Join-Path $dataRoot 'other-data-root'))
    [IO.File]::WriteAllText($manifestPath,($wrongRootManifest | ConvertTo-Json -Depth 60),[Text.UTF8Encoding]::new($false))
    $manifestWasTemporarilyChanged = $true
    Assert-RefusedWithoutRestart 'other manifest data root' $ownerPath
    [IO.File]::WriteAllText($manifestPath,$originalManifestText,[Text.UTF8Encoding]::new($false))
    $manifestWasTemporarilyChanged = $false

    $beforeLogin = Invoke-WebRequest -Uri $started.LoginUrl -UseBasicParsing -TimeoutSec 10
    if ($beforeLogin.StatusCode -ge 500) { throw 'Owned Web login failed before restart.' }
    $dbPathBefore = [IO.Path]::GetFullPath($dbPath)
    $keyHashBefore = (Get-FileHash -LiteralPath $keyPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $durableStateBefore = Capture-DurableState 'before'
    $restart = & $restartScript -OwnerFile $ownerPath
    if ($restart.RunId -cne $started.RunId -or [IO.Path]::GetFullPath($restart.DataRoot) -cne $dataRoot -or
        [IO.Path]::GetFullPath($restart.DatabasePath) -cne $dbPathBefore -or $restart.SourceProcessId -ne $sourceBefore.ProcessId -or
        $restart.WebProcessId -eq $webBefore.ProcessId -or $restart.OwnerStatus -cne 'running' -or $restart.ManifestStatus -cne 'running') {
        throw 'Restart receipt did not prove same run/data root, retained source helper, and updated Web process identity.'
    }
    $afterOwner = Get-Content -LiteralPath $ownerPath -Raw | ConvertFrom-Json
    $afterManifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($afterOwner.Status -cne 'running' -or $afterManifest.Status -cne 'running' -or
        [IO.Path]::GetFullPath([string]$afterManifest.Runtime.DataRoot) -cne $dataRoot -or
        [IO.Path]::GetFullPath([string]$afterOwner.BundlePath) -cne [IO.Path]::GetFullPath($started.BundlePath)) { throw 'Restart changed fixture owner/manifest run or data identity.' }
    $newWeb = @($afterOwner.Processes | Where-Object Kind -CEQ 'web')
    $newSource = @($afterOwner.Processes | Where-Object Kind -CEQ 'sources')
    $manifestNewWeb = @($afterManifest.Processes | Where-Object Kind -CEQ 'web')
    $manifestNewSource = @($afterManifest.Processes | Where-Object Kind -CEQ 'sources')
    if ($newWeb.Count -ne 1 -or $newSource.Count -ne 1 -or $manifestNewWeb.Count -ne 1 -or $manifestNewSource.Count -ne 1 -or
        [int]$newWeb[0].ProcessId -ne [int]$manifestNewWeb[0].ProcessId -or
        [long]$newWeb[0].StartTimeUtcTicks -ne [long]$manifestNewWeb[0].StartTimeUtcTicks -or
        [int]$newSource[0].ProcessId -ne [int]$sourceBefore.ProcessId -or [long]$newSource[0].StartTimeUtcTicks -ne [long]$sourceBefore.StartTimeUtcTicks) {
        throw 'Owner and manifest do not carry the same new Web identity and original live source identity.'
    }
    $receipt = @($afterOwner.RestartReceipts | Where-Object RestartId -CEQ $restart.RestartId)
    if ($receipt.Count -ne 1 -or $receipt[0].Status -cne 'completed' -or
        $receipt[0].KeySha256Before -cne $keyHashBefore -or $receipt[0].KeySha256After -cne $keyHashBefore -or
        $receipt[0].DatabasePath -cne $dbPathBefore -or $receipt[0].DataRoot -cne $dataRoot) { throw 'Restart receipt did not preserve key/path identity.' }
    $loginUrl = ([string]$afterOwner.WebBaseUrl).TrimEnd('/') + '/browser-fixture/login'
    $afterLogin = Invoke-WebRequest -Uri $loginUrl -UseBasicParsing -TimeoutSec 10
    if ($afterLogin.StatusCode -ge 500) { throw 'Same-run Web login failed after restart.' }
    $health = Invoke-RestMethod -Uri ($afterOwner.SourceHttpBaseUrl.TrimEnd('/') + '/__fixture/health') -TimeoutSec 3
    if ($health.runId -cne $started.RunId -or $health.loopbackOnly -ne $true) { throw 'Original controlled source helper is not alive after Web restart.' }
    $durableStateAfter = Capture-DurableState 'after'
    if ($durableStateBefore.applicationId -ne $durableStateAfter.applicationId -or
        $durableStateBefore.userVersion -ne $durableStateAfter.userVersion -or
        (ConvertTo-Json -InputObject $durableStateBefore.tables -Depth 16 -Compress) -cne (ConvertTo-Json -InputObject $durableStateAfter.tables -Depth 16 -Compress)) {
        throw 'Selected persisted run/record/case/work-order/batch rows or database identity changed across the Web restart.'
    }
    $savedPath = $env:PATH
    try {
        $env:PATH = $nativePath + ';' + $env:PATH
        $readOutput = & $dotnet $probeDll 'read' $dataRoot $sentinelKey $nonce 2>&1
        if ($LASTEXITCODE -ne 0) { throw "Production StorageBackend could not read the same-run sentinel after restart: $($readOutput -join ' ')" }
        $read = ($readOutput | Select-Object -Last 1 | ConvertFrom-Json)
        if ($read.runId -cne $started.RunId -or $read.nonce -cne $nonce -or $read.schema -cne 'lf-u53-web-restart-sentinel-v1') { throw 'Persisted production-store sentinel changed across same-DB Web restart.' }
    }
    finally { $env:PATH = $savedPath }
    $keyHashAfter = (Get-FileHash -LiteralPath $keyPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($keyHashAfter -cne $keyHashBefore) { throw 'Run-owned key changed across Web restart.' }
    $null = & $stopScript -OwnerFile $ownerPath
    $stopped = Get-Content -LiteralPath $ownerPath -Raw | ConvertFrom-Json
    $stoppedManifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $stoppedMarker = Get-Content -LiteralPath (Join-Path $started.BundlePath '.lf-round53-user-matrix-owner.json') -Raw | ConvertFrom-Json
    if ($stopped.Status -cne 'stopped' -or $stoppedManifest.Status -cne 'stopped' -or $stoppedMarker.Status -cne 'stopped') { throw 'Owner, manifest and marker did not all persist stopped status after restart.' }
    [pscustomobject]@{
        Result='PASS'; RunId=$started.RunId; RestartId=$restart.RestartId; BundlePath=$started.BundlePath
        SameDataRoot=($dataRoot -ceq [IO.Path]::GetFullPath($restart.DataRoot)); DatabasePath=$dbPath
        DatabaseSha256Before=$receipt[0].DatabaseSha256Before; DatabaseSha256BeforeClosed=$receipt[0].DatabaseSha256BeforeClosed
        DatabaseSha256After=$receipt[0].DatabaseSha256After; DatabaseHashEqual=([string]$receipt[0].DatabaseSha256BeforeClosed -ceq [string]$receipt[0].DatabaseSha256After)
        KeySha256Before=$keyHashBefore; KeySha256After=$keyHashAfter; DurableSentinelKey=$sentinelKey; DurableSentinelVerifiedAfterRestart=$true
        SelectedPersistedRowsBefore=$durableStateBefore.tables; SelectedPersistedRowsAfter=$durableStateAfter.tables
        SameRunId=$true; SameBuildSourceStamp=($receipt[0].BuildSourceStamp -ceq $manifest.SourceBuild.SourceStamp)
        SourceProcessRetained=($newSource[0].ProcessId -eq $sourceBefore.ProcessId); WebPidChanged=($newWeb[0].ProcessId -ne $webBefore.ProcessId)
        OwnerManifestSynchronized=$true; RefusalChecks=@('foreign owner path','PID mismatch','mixed assembly hash','other manifest data root')
        BrowserAcceptance='not-performed'; SyntheticOnly=$true; N6OutcomeClaimed=$false
    }
}
finally {
    if ($ownerWasTemporarilyChanged -and $null -ne $ownerPath -and $null -ne $originalOwnerText) {
        try { [IO.File]::WriteAllText($ownerPath,$originalOwnerText,[Text.UTF8Encoding]::new($false)) } catch { }
    }
    if ($manifestWasTemporarilyChanged -and $null -ne $manifestPath -and $null -ne $originalManifestText) {
        try { [IO.File]::WriteAllText($manifestPath,$originalManifestText,[Text.UTF8Encoding]::new($false)) } catch { }
    }
    if ($null -ne $started -and (Test-Path -LiteralPath $started.OwnerFile)) {
        try {
            $currentOwner = Get-Content -LiteralPath $started.OwnerFile -Raw | ConvertFrom-Json
            if ($currentOwner.Status -ceq 'running') { & $stopScript -OwnerFile $started.OwnerFile | Out-Null }
        } catch { }
    }
}
