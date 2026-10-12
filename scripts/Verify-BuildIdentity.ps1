param([Parameter(Mandatory = $true)][string]$Directory)

$ErrorActionPreference = 'Stop'
$directoryPath = [System.IO.Path]::GetFullPath($Directory)
$manifestPath = Join-Path $directoryPath 'build-identity.json'
function Get-FileSha256([string]$Path) {
    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($algorithm.ComputeHash([System.IO.File]::ReadAllBytes($Path))) -replace '-', '').ToLowerInvariant() }
    finally { $algorithm.Dispose() }
}
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    Write-Output 'FAIL (ManifestMissing)'
    exit 1
}
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.schema -ne 'logforesight-build-identity-v1' -or $manifest.assemblies.Count -ne 2) {
    Write-Output 'FAIL (ManifestShapeInvalid)'
    exit 1
}
# Compare metadata read from the actual DLLs, not merely caller-editable manifest claims.
$reader = Join-Path $PSScriptRoot 'build-identity/bin/Debug/net8.0/BuildIdentityManifest.dll'
if (-not (Test-Path -LiteralPath $reader -PathType Leaf)) {
    Write-Output 'FAIL (AssemblyMetadataReaderMissing)'
    exit 1
}
$actualManifestPath = Join-Path ([IO.Path]::GetTempPath()) ('lf-build-identity-' + [Guid]::NewGuid().ToString('N') + '.json')
try {
    & dotnet $reader $directoryPath $actualManifestPath | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Assembly metadata reader failed.' }
    $actualManifest = Get-Content -LiteralPath $actualManifestPath -Raw | ConvertFrom-Json
    foreach ($claim in $manifest.assemblies) {
        $actual = @($actualManifest.assemblies | Where-Object { $_.name -eq $claim.name })
        if ($actual.Count -ne 1) { throw 'Assembly metadata set mismatch.' }
        foreach ($field in @('informationalVersion','moduleVersionId','fileSha256','sourceStamp','sourceRevisionIdOverride')) {
            if ([string]$claim.$field -cne [string]$actual[0].$field) { throw 'Assembly metadata claim mismatch.' }
        }
    }
}
catch {
    Write-Output 'FAIL (ActualAssemblyMetadataMismatch)'
    exit 1
}
finally {
    if (Test-Path -LiteralPath $actualManifestPath -PathType Leaf) { Remove-Item -LiteralPath $actualManifestPath }
}
$byName = @{}
foreach ($assembly in $manifest.assemblies) {
    if ($assembly.name -notin @('LogForesight.Web', 'LogForesight.Core') -or $byName.ContainsKey($assembly.name)) {
        Write-Output 'FAIL (AssemblySetInvalid)'
        exit 1
    }
    $path = Join-Path $directoryPath ($assembly.name + '.dll')
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        Write-Output "FAIL (AssemblyMissing:$($assembly.name))"
        exit 1
    }
    $actualHash = Get-FileSha256 $path
    if ($actualHash -ne $assembly.fileSha256) {
        Write-Output "FAIL (AssemblyHashMismatch:$($assembly.name))"
        exit 1
    }
    $byName[$assembly.name] = $assembly
}
$web = $byName['LogForesight.Web']
$core = $byName['LogForesight.Core']
if (-not $web.sourceStamp -or $web.sourceStamp -ne $core.sourceStamp) {
    Write-Output 'FAIL (AssemblySourceStampMismatch)'
    exit 1
}
$pieces = $web.sourceStamp -split '\|'
if ($pieces.Count -ne 4 -or $pieces[0] -ne 'lfbuild-v1' -or $pieces[1] -eq 'unknown') {
    Write-Output 'FAIL (SourceUnknown)'
    exit 1
}
if ($pieces[1] -notin @('clean', 'modified') -or $pieces[2] -notmatch '^[0-9a-f]{40}$' -or $pieces[3] -notmatch '^[0-9a-f]{64}$') {
    Write-Output 'FAIL (SourceStampInvalid)'
    exit 1
}
foreach ($assembly in @($web, $core)) {
    $version = [string]$assembly.informationalVersion
    $suffix = if ($version.Contains('+')) { $version.Substring($version.LastIndexOf('+') + 1) } else { '' }
    if ($suffix -ne $pieces[2]) {
        Write-Output "FAIL (InformationalVersionSourceMismatch:$($assembly.name))"
        exit 1
    }
    if (-not [string]::IsNullOrWhiteSpace($assembly.sourceRevisionIdOverride) -and
        $assembly.sourceRevisionIdOverride -ne $pieces[2]) {
        Write-Output "FAIL (SourceRevisionIdOverrideMismatch:$($assembly.name))"
        exit 1
    }
}
if ($web.informationalVersion -ne $core.informationalVersion) {
    Write-Output 'FAIL (InformationalVersionMismatch)'
    exit 1
}
Write-Output 'PASS (BuildIdentityVerified)'
exit 0
