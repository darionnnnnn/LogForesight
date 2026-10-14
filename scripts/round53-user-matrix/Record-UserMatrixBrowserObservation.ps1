[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OwnerFile,
    [Parameter(Mandatory = $true)][string]$RequirementId,
    [Parameter(Mandatory = $true)][ValidateSet('passed','failed','inconclusive')][string]$ObservedResult,
    [string]$CheckId,
    [Parameter(Mandatory = $true)][string]$Observation,
    [Parameter(Mandatory = $true)][string]$EvidenceFile
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion -lt [Version]'7.4') { throw 'Requires PowerShell 7.4+.' }
$ownerPath = [IO.Path]::GetFullPath($OwnerFile)
$owner = Get-Content -LiteralPath $ownerPath -Raw | ConvertFrom-Json
if ($owner.Schema -cne 'lf-round53-user-matrix-process-owner-v1' -or $owner.Status -cne 'running') { throw 'Fixture process owner identity/state invalid.' }
$runRoot = [IO.Path]::GetFullPath([string]$owner.BundlePath)
$runsRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'runs'))
if ([IO.Path]::GetDirectoryName($runRoot).TrimEnd('\') -ine $runsRoot.TrimEnd('\') -or [IO.Path]::GetFileName($runRoot) -cnotmatch '^[0-9a-f]{32}$' -or
    [IO.Path]::GetFullPath([IO.Path]::GetDirectoryName($ownerPath)) -ine $runRoot) { throw 'Owner bundle/file is outside the canonical owned runs root.' }
$markerPath = Join-Path $runRoot '.lf-round53-user-matrix-owner.json'
$marker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json
if ($marker.Schema -cne 'lf-round53-user-matrix-owner-v1' -or $marker.RunId -cne $owner.RunId -or [IO.Path]::GetFullPath($marker.BundlePath) -ine $runRoot) { throw 'Fixture owner marker mismatch.' }
$manifestPath = Join-Path $runRoot 'fixture-manifest.json'
$fixture = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($fixture.Schema -cne 'lf-round53-user-matrix-fixture-v1' -or $fixture.RunId -cne $owner.RunId -or $fixture.Status -cne 'running' -or $fixture.SyntheticOnly -ne $true) { throw 'Fixture manifest identity or synthetic boundary mismatch.' }

$matrix = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'user-matrix.json') -Raw | ConvertFrom-Json
$inventory = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'coverage-inventory.json') -Raw | ConvertFrom-Json
$matchList = @()
foreach ($group in @(
    @{ Kind='acceptanceCriteria'; Items=$matrix.acceptanceCriteria },
    @{ Kind='userJourneys'; Items=$matrix.userJourneys },
    @{ Kind='qualityObjectives'; Items=$matrix.qualityObjectives },
    @{ Kind='roles'; Items=$matrix.roles },
    @{ Kind='controlledSourceScenarios'; Items=$matrix.controlledSourceScenarios }
)) { foreach ($item in $group.Items) { if ($item.id -ceq $RequirementId) { $matchList += [pscustomobject]@{ Kind=$group.Kind; Item=$item } } } }
$contractGroups = @(
    @{ Kind='round54Reviews'; Items=@($inventory.round54ReviewAdditions) },
    @{ Kind='round54Contracts'; Items=@($inventory.round54ContractCorrections) },
    @{ Kind='round54ExecutionRefinements'; Items=@($inventory.round54ExecutionRefinements) },
    @{ Kind='maintenanceRoles'; Items=@($matrix.maintenanceRoleMatrix.roles) },
    @{ Kind='syntheticDefenseCases'; Items=@($matrix.maintenanceRoleMatrix.syntheticDefenseCases) }
)
foreach ($group in $contractGroups) { foreach ($item in $group.Items) { if ($item.id -ceq $RequirementId) { $matchList += [pscustomobject]@{ Kind=$group.Kind; Item=$item } } } }
$matches = @($matchList)
if ($matches.Count -ne 1) { throw "RequirementId '$RequirementId' must identify exactly one matrix or Round54 contract ID." }
$requirement = $matches[0]
$contractKind = $requirement.Kind -in @('round54Reviews','round54Contracts','round54ExecutionRefinements','maintenanceRoles','syntheticDefenseCases')
if ($contractKind) {
    if ([string]::IsNullOrWhiteSpace($CheckId)) { throw 'Round54 observations require a CheckId so each mandatory check is recorded independently.' }
    $declaredChecks = switch ($requirement.Kind) {
        'round54Reviews' { @($requirement.Item.requiredChecks) }
        'round54Contracts' { @($requirement.Item.required) }
        'round54ExecutionRefinements' { @($requirement.Item.checks) }
        'maintenanceRoles' { @('identity-and-capability','visible-details-and-direct-api','effective-scope-and-side-effects') }
        'syntheticDefenseCases' { @('synthetic-component-authorization-filter') }
    }
    $declaredChecks = @($declaredChecks)
    $declaredCheckCount = $declaredChecks.Count
    if ($CheckId -cnotmatch '^check-\d{2}$' -or [int]$CheckId.Substring(6,2) -lt 1 -or [int]$CheckId.Substring(6,2) -gt $declaredCheckCount) { throw "CheckId '$CheckId' is not declared for $RequirementId." }
    $declaredCheck = [string]$declaredChecks[[int]$CheckId.Substring(6,2)-1]
} else { $declaredCheck = $null }
if ($requirement.Kind -eq 'syntheticDefenseCases' -and $ObservedResult -eq 'passed' -and
    ($requirement.Item.browserAcceptance -ne $false -or $requirement.Item.provisionAccount -ne $false)) { throw 'Synthetic defense evidence cannot be represented as browser acceptance or a provisioned account.' }

# Evidence must be an operator-captured, bounded artifact saved in this run. The recorder only
# appends an observation; it does not infer a pass from the artifact or change matrix readiness.
$evidenceFullPath = [IO.Path]::GetFullPath($EvidenceFile)
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $runRoot 'evidence\browser-artifacts'))
if (!$evidenceFullPath.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'EvidenceFile must be inside current run evidence\browser-artifacts folder.' }
$allowedExtensions = @('.png','.jpg','.jpeg','.webp','.txt')
if ([IO.Path]::GetExtension($evidenceFullPath).ToLowerInvariant() -notin $allowedExtensions) { throw 'Only screenshot or sanitized text artifact files are accepted.' }
if (!(Test-Path -LiteralPath $evidenceFullPath -PathType Leaf)) { throw 'EvidenceFile must already exist; this helper does not create browser evidence.' }
$artifactInfo = Get-Item -LiteralPath $evidenceFullPath
if ($artifactInfo.Length -le 0 -or $artifactInfo.Length -gt 15MB) { throw 'EvidenceFile must be nonempty and no larger than 15 MiB.' }
if ([string]::IsNullOrWhiteSpace($Observation) -or $Observation.Length -gt 2000) { throw 'Observation must be 1–2000 characters.' }

$recordName = if ($requirement.Kind -in @('round54Reviews','round54Contracts','round54ExecutionRefinements','maintenanceRoles','syntheticDefenseCases')) { 'round54-contract-observations.jsonl' } else { 'browser-observations.jsonl' }
$recordPath = Join-Path $runRoot (Join-Path 'evidence' $recordName)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($recordPath)) | Out-Null
[IO.Directory]::CreateDirectory($artifactRoot) | Out-Null
$relativeArtifact = [IO.Path]::GetRelativePath($runRoot, $evidenceFullPath).Replace('\','/')
$record = [ordered]@{
    schema=if ($recordName -eq 'browser-observations.jsonl') { 'lf-round53-user-matrix-browser-observation-v1' } else { 'lf-round54-contract-observation-v1' }
    recordedAtUtc=[DateTimeOffset]::UtcNow.ToString('O')
    runId=$owner.RunId
    requirementKind=$requirement.Kind
    requirementId=$RequirementId
    checkId=$CheckId
    declaredCheck=$declaredCheck
    evidenceType=if ($requirement.Kind -eq 'syntheticDefenseCases') { 'synthetic-authorization-component' } elseif ($requirement.Kind -eq 'maintenanceRoles') { 'real-login-maintenance-scenario' } else { 'operator-recorded-contract-observation' }
    observedResult=$ObservedResult
    observation=$Observation
    evidence=[ordered]@{ path=$relativeArtifact; sha256=(Get-FileHash -LiteralPath $evidenceFullPath -Algorithm SHA256).Hash.ToLowerInvariant(); bytes=$artifactInfo.Length }
      sourceBuild=[ordered]@{ expectedSourceCommit=$fixture.SourceBuild.ExpectedSourceCommit; sourceStamp=$fixture.SourceBuild.SourceStamp; sourceRevision=$fixture.SourceBuild.SourceRevision; webDllSha256=$fixture.SourceBuild.WebDllSha256; coreDllSha256=$fixture.SourceBuild.CoreDllSha256; staticAssetsSourceSha256=$fixture.SourceBuild.StaticAssetsSourceSha256; staticAssetsRuntimeSha256=$fixture.SourceBuild.StaticAssetsRuntimeSha256 }
    limitations=@('Manually recorded observation; this helper does not validate the screenshot or browser interaction.', 'One observation does not mark an AC, journey, quality objective, or full matrix accepted.', 'Synthetic fixture evidence is not production or native-source evidence.')
}
Add-Content -LiteralPath $recordPath -Value ($record | ConvertTo-Json -Depth 12 -Compress) -Encoding utf8
[pscustomobject]@{ RunId=$owner.RunId; RequirementKind=$requirement.Kind; RequirementId=$RequirementId; Result=$ObservedResult; EvidencePath=$relativeArtifact; RecordPath=$recordPath; AggregateAcceptanceChanged=$false; BrowserAcceptanceChanged=$false }
