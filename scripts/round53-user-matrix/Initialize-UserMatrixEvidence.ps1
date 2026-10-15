[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$BuildDirectory,
    [Parameter(Mandatory = $true)][string]$FixtureDatabase,
    [Parameter(Mandatory = $true)][string]$FixtureKey,
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9a-f]{40}$')][string]$ExpectedSourceCommit,
    [string]$EvidencePath
)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$matrixPath = Join-Path $PSScriptRoot 'user-matrix.json'
$matrix = Get-Content -LiteralPath $matrixPath -Raw | ConvertFrom-Json
$buildRoot = [IO.Path]::GetFullPath($BuildDirectory)
$databasePath = [IO.Path]::GetFullPath($FixtureDatabase)
$keyPath = [IO.Path]::GetFullPath($FixtureKey)
$identityScript = Join-Path $repoRoot 'scripts\Verify-BuildIdentity.ps1'
$identityPath = Join-Path $buildRoot 'build-identity.json'
foreach ($path in @($identityScript, $identityPath, (Join-Path $buildRoot 'LogForesight.Web.dll'),
        (Join-Path $buildRoot 'LogForesight.Core.dll'), (Join-Path $buildRoot 'wwwroot'), $databasePath, $keyPath)) {
    if (!(Test-Path -LiteralPath $path)) { throw "Required source/build/fixture input missing: $path" }
}
$identity = Get-Content -LiteralPath $identityPath -Raw | ConvertFrom-Json
if ($identity.schema -cne 'logforesight-build-identity-v1' -or @($identity.assemblies).Count -ne 2) {
    throw 'Build identity manifest is invalid.'
}
$verification = & (Get-Command pwsh -CommandType Application -ErrorAction Stop).Source -NoProfile -File $identityScript -Directory $buildRoot
if ($LASTEXITCODE -ne 0) { throw "Build identity validation failed; refusing to prepare browser evidence. $($verification -join ' ')" }
if (!$EvidencePath) {
    $runId = [Guid]::NewGuid().ToString('N')
    $EvidencePath = Join-Path $PSScriptRoot "runs\$runId\evidence.json"
}
$evidenceFullPath = [IO.Path]::GetFullPath($EvidencePath)
$runsRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'runs'))
if (!$evidenceFullPath.StartsWith($runsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Evidence output must stay under this owned round53-user-matrix/runs folder.'
}
if (Test-Path -LiteralPath $evidenceFullPath) { throw 'Evidence output already exists; refusing overwrite.' }
function Get-Sha256([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
$assetRoot = Join-Path $buildRoot 'wwwroot'
$assetEntries = foreach ($asset in Get-ChildItem -LiteralPath $assetRoot -File -Recurse | Sort-Object FullName) {
    $relative = [IO.Path]::GetRelativePath($assetRoot, $asset.FullName).Replace('\', '/')
    "$relative`n$(Get-Sha256 $asset.FullName)`n"
}
$assetTreeBytes = [Text.Encoding]::UTF8.GetBytes(($assetEntries -join ''))
$assetTreeSha = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($assetTreeBytes)).ToLowerInvariant()
$webIdentity = @($identity.assemblies | Where-Object { $_.name -eq 'LogForesight.Web' })[0]
$coreIdentity = @($identity.assemblies | Where-Object { $_.name -eq 'LogForesight.Core' })[0]
foreach ($entry in @($webIdentity,$coreIdentity)) {
    if ([string]$entry.sourceStamp -cnotmatch '^lfbuild-v1\|(clean|modified)\|([0-9a-f]{40})\|([0-9a-f]{64})$' -or $Matches[2] -cne $ExpectedSourceCommit) {
        throw "Build source stamp must explicitly match expected commit $ExpectedSourceCommit."
    }
}
$record = [ordered]@{
    schema = 'logforesight-round53-user-matrix-evidence-v1'
    runId = [IO.Path]::GetFileName([IO.Path]::GetDirectoryName($evidenceFullPath))
    readiness = 'prepared-not-browser-accepted'
    browser = [ordered]@{ performed = $false; operator = $null; startedAt = $null; origin = $null }
    sourceBuild = [ordered]@{
        directory = $buildRoot
        expectedSourceCommit = $ExpectedSourceCommit
        sourceRevision = [string]$webIdentity.sourceRevisionIdOverride
        buildIdentitySha256 = Get-Sha256 $identityPath
        webDllSha256 = Get-Sha256 (Join-Path $buildRoot 'LogForesight.Web.dll')
        coreDllSha256 = Get-Sha256 (Join-Path $buildRoot 'LogForesight.Core.dll')
        webBuildStamp = [string]$webIdentity.sourceStamp
        coreBuildStamp = [string]$coreIdentity.sourceStamp
        staticAssetTreeSha256 = $assetTreeSha
        assemblies = @($identity.assemblies)
    }
    fixture = [ordered]@{
        databaseSha256 = Get-Sha256 $databasePath
        keySha256 = Get-Sha256 $keyPath
        syntheticOnly = $true
        sourceConnectionsEnabled = $false
    }
    roles = @($matrix.roles | ForEach-Object { [ordered]@{ id = $_.id; result = 'pending'; evidence = @() } })
    acceptanceCriteria = @($matrix.acceptanceCriteria | ForEach-Object { [ordered]@{ id = $_.id; journeys = @($_.journeys); result = 'pending'; evidence = @() } })
    userJourneys = @($matrix.userJourneys | ForEach-Object { [ordered]@{ id = $_.id; scenario = $_.scenario; result = 'pending'; evidence = @() } })
    qualityObjectives = @($matrix.qualityObjectives | ForEach-Object { [ordered]@{ id = $_.id; journeys = @($_.journeys); result = 'pending'; evidence = @() } })
    controlledSourceScenarios = @($matrix.controlledSourceScenarios | ForEach-Object { [ordered]@{ id = $_.id; source = $_.source; mode = $_.mode; result = 'pending'; evidence = @() } })
    round54Reviews = @((Get-Content -LiteralPath (Join-Path $repoRoot 'scripts\round53-user-matrix\coverage-inventory.json') -Raw | ConvertFrom-Json).round54ReviewAdditions | ForEach-Object { $journeyIds=@($_.journeys); [ordered]@{ id=$_.id; batch=$_.batch; originalTodoIds=@($_.originalTodoIds); acceptanceCriteria=@($_.acceptanceCriteria); journeys=$journeyIds; qualityObjectives=@($matrix.qualityObjectives | Where-Object { @($_.journeys | Where-Object { $journeyIds -contains $_ }).Count -gt 0 } | ForEach-Object id); requiredChecks=@($_.requiredChecks); result='pending'; evidence=@() } })
    round54Contracts = @((Get-Content -LiteralPath (Join-Path $repoRoot 'scripts\round53-user-matrix\coverage-inventory.json') -Raw | ConvertFrom-Json).round54ContractCorrections | ForEach-Object { [ordered]@{ id=$_.id; reviews=@($_.reviews); stage=$_.stage; refinementIds=@($_.refinementIds); requiredChecks=@($_.required); result='pending'; evidence=@() } })
    round54ExecutionRefinements = @((Get-Content -LiteralPath (Join-Path $repoRoot 'scripts\round53-user-matrix\coverage-inventory.json') -Raw | ConvertFrom-Json).round54ExecutionRefinements | ForEach-Object { [ordered]@{ id=$_.id; contracts=@($_.contracts); reviews=@($_.reviews); stages=@($_.stages); checks=@($_.checks); result='pending'; evidence=@() } })
    maintenanceRoles = @($matrix.maintenanceRoleMatrix.roles | ForEach-Object { [ordered]@{ id=$_.id; identity=$_.identity; fixtureAccountKey=$_.fixtureAccountKey; loginMode=$_.loginMode; maintain=$_.maintain; viewAll=$_.viewAll; result='pending'; evidence=@() } })
    syntheticDefenseCases = @($matrix.maintenanceRoleMatrix.syntheticDefenseCases | ForEach-Object { [ordered]@{ id=$_.id; evidenceType='synthetic-authorization-component'; browserAcceptance=$false; provisionAccount=$false; result='pending'; evidence=@() } })
    liveAuthorityAcceptance = [ordered]@{ refinementId=$matrix.maintenanceRoleMatrix.liveAuthorityAcceptance.refinementId; result='pending'; evidence=@() }
    limitations = @('No browser evidence is created by this initializer.', 'Synthetic stubs do not establish native source qualification or production outcomes.')
}
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($evidenceFullPath)) | Out-Null
[IO.File]::WriteAllText($evidenceFullPath, ($record | ConvertTo-Json -Depth 40), [Text.UTF8Encoding]::new($false))
Write-Output $evidenceFullPath
