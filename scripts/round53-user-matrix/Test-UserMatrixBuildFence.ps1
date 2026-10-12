[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$WebOutputDirectory,
    [Parameter(Mandatory = $true)][string]$WebAssetsDirectory,
    [Parameter(Mandatory = $true)][string]$SourceFixtureBundlePath,
    [Parameter(Mandatory = $true)][string]$AlternativeCoreAssembly
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion -lt [Version]'7.4') { throw 'Requires PowerShell 7.4+.' }
$stageRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$runsRoot = [IO.Path]::GetFullPath((Join-Path $stageRoot 'runs'))
$webSource = [IO.Path]::GetFullPath($WebOutputDirectory)
$assetSource = [IO.Path]::GetFullPath($WebAssetsDirectory)
$alternativeCore = [IO.Path]::GetFullPath($AlternativeCoreAssembly)
foreach ($path in @((Join-Path $webSource 'LogForesight.Web.dll'),(Join-Path $webSource 'LogForesight.Core.dll'),(Join-Path $webSource 'build-identity.json'),$assetSource,$alternativeCore)) {
    if (!(Test-Path -LiteralPath $path)) { throw "Build-fence test input missing: $path" }
}
$dotnet = (Get-Command dotnet.exe -CommandType Application -ErrorAction Stop).Source
$startScript = Join-Path $stageRoot 'Start-UserMatrixFixture.ps1'
$fenceRoot = Join-Path $runsRoot ('_identity-fence-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($fenceRoot) | Out-Null
function Copy-TestTree([string]$Source,[string]$Destination) {
    [IO.Directory]::CreateDirectory($Destination) | Out-Null
    foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($Source)) {
        $attributes = [IO.File]::GetAttributes($entry)
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse point refused in test build: $entry" }
        $target = Join-Path $Destination ([IO.Path]::GetFileName($entry))
        if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) { Copy-TestTree $entry $target }
        else { [IO.File]::Copy($entry,$target,$false) }
    }
}
function Get-RunMarkers() {
    foreach ($directory in Get-ChildItem -LiteralPath $runsRoot -Directory) {
        $markerPath = Join-Path $directory.FullName '.lf-round53-user-matrix-owner.json'
        if (Test-Path -LiteralPath $markerPath -PathType Leaf) {
            try { [pscustomobject]@{ Path=$directory.FullName; Marker=(Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json) } } catch { }
        }
    }
}
function Assert-NoOwnedProcess([string]$Path) {
    $match = @(Get-CimInstance Win32_Process | Where-Object { [string]$_.CommandLine -like "*$Path*" })
    if ($match.Count -gt 0) { throw "A process still refers to failed test run: $($match.ProcessId -join ',')." }
}

try {
    $sourceIdentity = Get-Content -LiteralPath (Join-Path $webSource 'build-identity.json') -Raw | ConvertFrom-Json
    if ($sourceIdentity.Schema -cne 'logforesight-build-identity-v1') { throw 'Selected source identity is not valid for the negative launch tests.' }
    $selectedWebIdentity = @($sourceIdentity.Assemblies | Where-Object { $_.Name -ceq 'LogForesight.Web' })
    if ($selectedWebIdentity.Count -ne 1 -or [string]$selectedWebIdentity[0].SourceStamp -cnotmatch '^lfbuild-v1\|(clean|modified)\|([0-9a-f]{40})\|[0-9a-f]{64}$') { throw 'Selected source stamp does not provide an expected commit for the negative launch tests.' }
    $expectedCommit = $Matches[2]

    # A valid, internally consistent old build still must not launch when it fails the
    # operator's explicit commit fence. This guards against silently treating a stale
    # snapshot as "latest" merely because its own files and stamp agree.
    $wrongExpectedCommit = if ($expectedCommit -ceq ('f' * 40)) { '0' * 40 } else { 'f' * 40 }
    $beforeMarkers = @(Get-RunMarkers | ForEach-Object { $_.Marker.RunId })
    $wrongCommitRejected = $false
    try { & $startScript -WebOutputDirectory $webSource -WebAssetsDirectory $assetSource -SourceFixtureBundlePath $SourceFixtureBundlePath -ExpectedSourceCommit $wrongExpectedCommit | Out-Null }
    catch { $wrongCommitRejected = $_.Exception.Message -match 'does not match explicitly expected commit' }
    if (!$wrongCommitRejected) { throw 'Consistent build with a nonmatching explicit expected commit was accepted.' }
    $newMarkers = @(Get-RunMarkers | Where-Object { $_.Marker.RunId -notin $beforeMarkers })
    if ($newMarkers.Count -gt 0) { throw 'Wrong-expected-commit rejection allocated a fixture run.' }

    # Case 1: preserve valid source stamps and DLL hashes, but forge the Web MVID claim. The
    # launcher must independently read the assemblies it copied to the runtime app folder.
    $forgedDirectory = Join-Path $fenceRoot 'forged-manifest'
    Copy-TestTree $webSource $forgedDirectory
    $forgedPath = Join-Path $forgedDirectory 'build-identity.json'
    $forged = Get-Content -LiteralPath $forgedPath -Raw | ConvertFrom-Json
    $webClaim = @($forged.Assemblies | Where-Object { $_.Name -ceq 'LogForesight.Web' })[0]
    $webClaim.ModuleVersionId = [guid]::NewGuid().ToString()
    [IO.File]::WriteAllText($forgedPath,($forged | ConvertTo-Json -Depth 12),[Text.UTF8Encoding]::new($false))
    $beforeMarkers = @(Get-RunMarkers | ForEach-Object { $_.Marker.RunId })
    $forgedRejected = $false
    try { & $startScript -WebOutputDirectory $forgedDirectory -WebAssetsDirectory $assetSource -SourceFixtureBundlePath $SourceFixtureBundlePath -ExpectedSourceCommit $expectedCommit | Out-Null }
    catch { $forgedRejected = $_.Exception.Message -match 'ModuleVersionId' }
    if (!$forgedRejected) { throw 'Forged ModuleVersionId manifest was accepted.' }
    $forgedRun = @(Get-RunMarkers | Where-Object { $_.Marker.RunId -notin $beforeMarkers -and $_.Marker.Status -eq 'failed' }) | Select-Object -Last 1
    if (!$forgedRun -or (Test-Path -LiteralPath (Join-Path $forgedRun.Path 'process-owner.json'))) { throw 'Forged manifest did not fail before process ownership was recorded.' }
    Assert-NoOwnedProcess $forgedRun.Path

    # Case 2: replace only Core.dll with an assembly from a different build, then create a
    # self-consistent hash manifest from those actual DLLs. The source-stamp fence must reject
    # the mixed pair before allocating a run or starting either listener.
    $mixedDirectory = Join-Path $fenceRoot 'mixed-source'
    Copy-TestTree $webSource $mixedDirectory
    [IO.File]::Copy($alternativeCore,(Join-Path $mixedDirectory 'LogForesight.Core.dll'),$true)
    $reader = Join-Path (Split-Path $stageRoot -Parent) 'build-identity\bin\Debug\net8.0\BuildIdentityManifest.dll'
    if (!(Test-Path -LiteralPath $reader -PathType Leaf)) { throw 'Existing metadata reader is required to describe the mixed assembly pair.' }
    $mixedIdentityPath = Join-Path $mixedDirectory 'build-identity.json'
    $readerRun = & $dotnet $reader $mixedDirectory $mixedIdentityPath 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Could not describe the actual mixed assembly pair: $($readerRun -join ' ')" }
    $mixedIdentity = Get-Content -LiteralPath $mixedIdentityPath -Raw | ConvertFrom-Json
    $mixedStamps = @($mixedIdentity.Assemblies | ForEach-Object { [string]$_.SourceStamp } | Select-Object -Unique)
    if ($mixedStamps.Count -eq 1 -and ![string]::IsNullOrWhiteSpace($mixedStamps[0])) { throw 'Alternative Core assembly did not provide a distinct source stamp; choose an assembly from another build.' }
    $beforeMarkers = @(Get-RunMarkers | ForEach-Object { $_.Marker.RunId })
    $mixedRejected = $false
    try { & $startScript -WebOutputDirectory $mixedDirectory -WebAssetsDirectory $assetSource -SourceFixtureBundlePath $SourceFixtureBundlePath -ExpectedSourceCommit $expectedCommit | Out-Null }
    catch { $mixedRejected = $_.Exception.Message -match 'source stamps are unknown, mixed or malformed' }
    if (!$mixedRejected) { throw 'Actual mixed Web/Core assembly pair was not rejected at the source-stamp fence.' }
    $unexpectedRun = @(Get-RunMarkers | Where-Object { $_.Marker.RunId -notin $beforeMarkers -and $_.Marker.BundlePath -notlike "$fenceRoot*" })
    if ($unexpectedRun.Count -gt 0) { throw 'Mixed-source rejection unexpectedly allocated a product fixture run.' }

    [pscustomobject]@{ Result='PASS'; WrongExpectedCommit='rejected-before-run-allocation'; ForgedManifest='rejected-before-process-start'; MixedSource='rejected-before-run-allocation'; RunMarkersChecked=@((Get-RunMarkers).Count); SyntheticFixtureBundle=$SourceFixtureBundlePath; ProductProcessesStarted=$false }
}
finally {
    $canonical = [IO.Path]::GetFullPath($fenceRoot)
    if ([IO.Path]::GetDirectoryName($canonical).TrimEnd('\') -ine $runsRoot.TrimEnd('\') -or [IO.Path]::GetFileName($canonical) -notlike '_identity-fence-*') { throw 'Refusing to remove test workspace outside its owned runs folder.' }
    if (Test-Path -LiteralPath $canonical) { Remove-Item -LiteralPath $canonical -Recurse -Force }
}
