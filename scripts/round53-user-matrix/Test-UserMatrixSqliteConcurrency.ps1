[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$WebOutputDirectory,
    [Parameter(Mandatory=$true)][string]$SourceFixtureBundlePath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion -lt [Version]'7.4') { throw 'Requires PowerShell 7.4+.' }
$stageRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$webOutput = [IO.Path]::GetFullPath($WebOutputDirectory)
$sourceBundle = [IO.Path]::GetFullPath($SourceFixtureBundlePath)
$sourceDb = Join-Path $sourceBundle 'data\Db\logforesight.db'
$sourceKey = Join-Path $sourceBundle 'data\keys\lf-crypto.key'
foreach ($path in @($sourceDb,$sourceKey,(Join-Path $webOutput 'LogForesight.Core.dll'))) {
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing required owned test input: $path" }
}
$sourceDbHash = (Get-FileHash -LiteralPath $sourceDb -Algorithm SHA256).Hash.ToLowerInvariant()
$sourceKeyHash = (Get-FileHash -LiteralPath $sourceKey -Algorithm SHA256).Hash.ToLowerInvariant()
$runsRoot = Join-Path $stageRoot 'runs'
[IO.Directory]::CreateDirectory($runsRoot) | Out-Null
$runId = '_sqlite-concurrency-' + [guid]::NewGuid().ToString('N')
$runRoot = Join-Path $runsRoot $runId
$cloneDb = Join-Path $runRoot 'db\logforesight.db'
$cloneKey = Join-Path $runRoot 'key\lf-crypto.key'
$helperProject = Join-Path $stageRoot 'sqlite-concurrency\SqliteConcurrencyHarness.csproj'
$helperOutput = Join-Path $runRoot 'helper'
$helperObj = Join-Path $runRoot 'obj'
try {
    $pythonCandidates = @(Get-Command python.exe -CommandType Application -All -ErrorAction Stop | ForEach-Object { $_.Source } | Select-Object -Unique)
    $python = @($pythonCandidates | Where-Object { $_ -notmatch '\\Microsoft\\WindowsApps\\' -and (Test-Path -LiteralPath $_ -PathType Leaf) }) | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace([string]$python)) { throw 'A real python.exe was not found; WindowsApps aliases are refused.' }
    $seedScript = Join-Path $stageRoot 'seed_fixture.py'
    $seedOutput = & $python $seedScript $sourceDb $sourceKey $cloneDb $cloneKey 'http://127.0.0.1:1' '0' $runId 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Owned clone seed failed: $($seedOutput -join [Environment]::NewLine)" }
    [IO.Directory]::CreateDirectory($helperOutput) | Out-Null
    $buildOutput = & dotnet build $helperProject --configuration Release --output $helperOutput "-p:WebOutputDirectory=$webOutput" "-p:BaseIntermediateOutputPath=$helperObj\" 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Private EF harness build failed: $($buildOutput -join [Environment]::NewLine)" }
    $harness = Join-Path $helperOutput 'SqliteConcurrencyHarness.dll'
    $harnessOutput = & dotnet $harness $cloneDb $sourceDb 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Owned-clone consecutive mutation failed: $($harnessOutput -join [Environment]::NewLine)" }
    $afterDbHash = (Get-FileHash -LiteralPath $sourceDb -Algorithm SHA256).Hash.ToLowerInvariant()
    $afterKeyHash = (Get-FileHash -LiteralPath $sourceKey -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($sourceDbHash -cne $afterDbHash -or $sourceKeyHash -cne $afterKeyHash) { throw 'Read-only source DB/key hashes changed during clone and mutation test.' }
    $resultJson = @($harnessOutput | Where-Object { $_ -match '^\{' } | Select-Object -Last 1)
    if ($resultJson.Count -ne 1) { throw "Private EF harness did not return a result JSON: $($harnessOutput -join [Environment]::NewLine)" }
    $result = $resultJson[0] | ConvertFrom-Json
    [pscustomobject]@{
        Result='PASS'
        Harness=$result
        SourceDatabaseSha256Before=$sourceDbHash
        SourceDatabaseSha256After=$afterDbHash
        SourceKeySha256Before=$sourceKeyHash
        SourceKeySha256After=$afterKeyHash
        RunRoot=$runRoot
        Scope='owned synthetic SQLite clone; no product source writes'
    }
}
catch {
    [pscustomobject]@{ Result='FAILED'; RunRoot=$runRoot; Error=$_.Exception.Message }
    throw
}
