[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$OwnerFile)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ownerPath = [IO.Path]::GetFullPath($OwnerFile)
$owner = Get-Content -LiteralPath $ownerPath -Raw | ConvertFrom-Json
if ($owner.Schema -cne 'lf-round53-user-matrix-process-owner-v1' -or $owner.Status -cne 'running') { throw 'Fixture process owner identity/state invalid.' }
$runRoot = [IO.Path]::GetFullPath([string]$owner.BundlePath)
$runsRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'runs'))
if ([IO.Path]::GetDirectoryName($runRoot).TrimEnd('\') -ine $runsRoot.TrimEnd('\') -or [IO.Path]::GetFileName($runRoot) -cnotmatch '^[0-9a-f]{32}$') { throw 'Owner bundle is outside the canonical owned runs root.' }
$lifecycleLock = $null
try { $lifecycleLock = [IO.File]::Open((Join-Path $runRoot '.lifecycle.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None) }
catch { throw 'Another fixture lifecycle operation is active; refusing to stop processes concurrently.' }
try {
$owner = Get-Content -LiteralPath $ownerPath -Raw | ConvertFrom-Json
if ($owner.Schema -cne 'lf-round53-user-matrix-process-owner-v1' -or $owner.Status -cne 'running' -or [IO.Path]::GetFullPath([string]$owner.BundlePath) -ine $runRoot) { throw 'Fixture process owner changed before the lifecycle lock was acquired.' }
$markerPath = Join-Path $runRoot '.lf-round53-user-matrix-owner.json'
$marker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json
if ($marker.Schema -cne 'lf-round53-user-matrix-owner-v1' -or $marker.RunId -cne $owner.RunId -or [IO.Path]::GetFullPath($marker.BundlePath) -ine $runRoot) { throw 'Fixture owner marker mismatch.' }
if ($ownerPath -ine [IO.Path]::GetFullPath((Join-Path $runRoot 'process-owner.json'))) { throw 'Owner file path differs from the canonical bundle.' }
$manifestPath = Join-Path $runRoot 'fixture-manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.Schema -cne 'lf-round53-user-matrix-fixture-v1' -or $manifest.RunId -cne $owner.RunId -or
    [IO.Path]::GetFullPath([string]$manifest.Runtime.DataRoot) -ine [IO.Path]::GetFullPath((Join-Path $runRoot 'data'))) { throw 'Fixture manifest identity differs from this owned run.' }

$processes = @()
foreach ($processOwner in $owner.Processes) {
    $process = Get-Process -Id ([int]$processOwner.ProcessId) -ErrorAction SilentlyContinue
    if ($null -eq $process) { continue }
    $process.Refresh()
    $cim = Get-CimInstance Win32_Process -Filter "ProcessId = $($processOwner.ProcessId)" -ErrorAction Stop
    if ([IO.Path]::GetFullPath([string]$cim.ExecutablePath) -ine [IO.Path]::GetFullPath([string]$owner.DotnetPath) -or
        [string]$cim.CommandLine -cne [string]$processOwner.CommandLine -or
        $process.StartTime.ToUniversalTime().Ticks -ne [long]$processOwner.StartTimeUtcTicks -or
        [IO.Path]::GetFullPath([string]$processOwner.AssemblyPath) -notlike "$runRoot*") {
        throw "Refusing to stop $($processOwner.Kind) PID $($processOwner.ProcessId): process identity no longer matches owner record."
    }
    if ((Get-FileHash -LiteralPath $processOwner.AssemblyPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne [string]$processOwner.AssemblySha256) { throw "Owned $($processOwner.Kind) executable file changed." }
    foreach ($port in $processOwner.Ports) {
        $listeners = @(Get-NetTCPConnection -State Listen -LocalPort ([int]$port) -ErrorAction SilentlyContinue)
        if ($listeners.Count -ne 1) { throw "Port $port no longer has exactly one owned listener." }
        if ([int]$listeners[0].OwningProcess -ne [int]$processOwner.ProcessId) {
            $httpPort = if ($processOwner.Kind -eq 'sources') { [int]([uri]$owner.SourceHttpBaseUrl).Port } else { -1 }
            if ([int]$port -ne $httpPort -or [int]$listeners[0].OwningProcess -ne 4) { throw "Port $port belongs to a foreign process." }
            try { $health = Invoke-RestMethod -Uri ($owner.SourceHttpBaseUrl.TrimEnd('/') + '/__fixture/health') -TimeoutSec 3 }
            catch { throw 'HTTP.sys listener did not answer the run-owned controlled-source health probe.' }
            if ($health.runId -cne $owner.RunId -or $health.loopbackOnly -ne $true) { throw 'HTTP.sys listener health identity is not this owned source helper.' }
        }
    }
    $processes += $process
}
foreach ($process in $processes) { $process.Kill(); $process.WaitForExit(10000) }
foreach ($processOwner in $owner.Processes) {
    foreach ($port in $processOwner.Ports) {
        $remaining = @(Get-NetTCPConnection -State Listen -LocalPort ([int]$port) -ErrorAction SilentlyContinue)
        if ($remaining.Count -gt 0) { throw "Port $port remains bound after stopping the owned process." }
    }
}
$owner.Status = 'stopped'; $owner | Add-Member -NotePropertyName StoppedAtUtc -NotePropertyValue ([DateTimeOffset]::UtcNow.ToString('O')) -Force
$marker.Status = 'stopped'; $marker | Add-Member -NotePropertyName StoppedAtUtc -NotePropertyValue ([DateTimeOffset]::UtcNow.ToString('O')) -Force
$manifest.Status = 'stopped'; $manifest | Add-Member -NotePropertyName StoppedAtUtc -NotePropertyValue $owner.StoppedAtUtc -Force
[IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 60), [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText($ownerPath, ($owner | ConvertTo-Json -Depth 24), [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText($markerPath, ($marker | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
[pscustomobject]@{ RunId=$owner.RunId; Status='stopped'; BundlePath=$runRoot; Retained=$true }
}
finally { if ($null -ne $lifecycleLock) { $lifecycleLock.Dispose() } }
