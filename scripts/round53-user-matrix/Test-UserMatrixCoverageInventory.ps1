[CmdletBinding()]
param(
    [string]$UserMatrixPath = (Join-Path $PSScriptRoot 'user-matrix.json'),
    [string]$CoverageInventoryPath = (Join-Path $PSScriptRoot 'coverage-inventory.json'),
    [string]$RepositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$stageRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$repoRoot = [IO.Path]::GetFullPath($RepositoryRoot)
$matrix = Get-Content -LiteralPath $UserMatrixPath -Raw | ConvertFrom-Json
$inventory = Get-Content -LiteralPath $CoverageInventoryPath -Raw | ConvertFrom-Json
$checks = @(
    @{ Label='AC'; Expected=@($matrix.acceptanceCriteria.id); Actual=@($inventory.acceptanceCriteria.id) },
    @{ Label='U'; Expected=@($matrix.userJourneys.id); Actual=@($inventory.userJourneys.id) },
    @{ Label='Q'; Expected=@($matrix.qualityObjectives.id); Actual=@($inventory.qualityObjectives.id) },
    @{ Label='SRC'; Expected=@($matrix.controlledSourceScenarios.id); Actual=@($inventory.controlledSourceScenarios.id) }
)
foreach ($check in $checks) {
    if (@($check.Actual | Select-Object -Unique).Count -ne $check.Actual.Count -or
        (@($check.Expected | Sort-Object) -join "`n") -cne (@($check.Actual | Sort-Object) -join "`n")) {
        throw "$($check.Label) inventory must include every matrix ID exactly once."
    }
}
foreach ($journey in $matrix.userJourneys) {
    $journeyId = [string]$journey.id
    $expectedQ = @($matrix.qualityObjectives | Where-Object { $journeyId -in $_.journeys } | ForEach-Object id | Sort-Object)
    $actualQ = @($inventory.qualityObjectives | Where-Object { $journeyId -in $_.journeys } | ForEach-Object id | Sort-Object)
    if (($expectedQ -join ',') -cne ($actualQ -join ',')) { throw "Q journey association differs from the fixed matrix for $journeyId." }
}
foreach ($evidence in @($inventory.fixedRuleUniverse.sourceEvidence) + @($inventory.acceptanceCriteria.sourceEvidence | ForEach-Object { $_ }) + @($inventory.userJourneys.sourceEvidence | ForEach-Object { $_ })) {
    foreach ($reference in @($evidence)) {
        if ([string]::IsNullOrWhiteSpace([string]$reference)) { continue }
        $parts = ([string]$reference -split '::', 2)
        $path = Join-Path $repoRoot $parts[0]
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Evidence source path is missing: $($parts[0])" }
        if ($parts.Count -eq 2) {
            $sourceText = Get-Content -LiteralPath $path -Raw
            if ($sourceText -notmatch [regex]::Escape($parts[1])) { throw "Evidence source locator is missing from $($parts[0]): $($parts[1])" }
        }
    }
}
$allowedStatuses = @('existing-test-source','partial-existing-test','not-covered','browser-not-run')
foreach ($criterion in $inventory.acceptanceCriteria) {
    if ($criterion.status -notin $allowedStatuses -or @($criterion.subcontracts).Count -eq 0 -or @($criterion.uncovered).Count -eq 0) {
        throw "$($criterion.id) must state source coverage and explicit remaining gaps."
    }
}
foreach ($journey in $inventory.userJourneys) {
    if ($journey.status -notin $allowedStatuses -or @($journey.subcontracts).Count -eq 0 -or @($journey.uncovered).Count -eq 0) {
        throw "$($journey.id) must state source coverage and explicit remaining gaps."
    }
}
# Round 54 source implementation and acceptance are separate facts. A wired
# candidate is not accepted until its linked checks have independently passed.
$allowedImplementationStatuses = @('not-implemented','partial-candidate','implemented-awaiting-acceptance')
function Assert-ImplementationMetadata($item, [string]$kind) {
    if ($item.status -cne 'not-accepted' -or $item.acceptanceStatus -cne 'not-accepted') {
        throw "$($item.id) $kind acceptance must remain not-accepted until all required checks pass."
    }
    if ($item.implementationStatus -notin $allowedImplementationStatuses) {
        throw "$($item.id) $kind has an unknown implementationStatus."
    }
    $references = @($item.sourceReferences)
    $remaining = @($item.remainingImplementation)
    if ($null -ne $item.PSObject.Properties['requiredChecks']) { $acceptanceSource = @($item.requiredChecks) }
    elseif ($null -ne $item.PSObject.Properties['required']) { $acceptanceSource = @([string]$item.required) }
    else { $acceptanceSource = @($item.checks) }
    $remainingAcceptance = @($item.remainingAcceptance)
    if ($remainingAcceptance.Count -ne $acceptanceSource.Count) {
        throw "$($item.id) must preserve every original acceptance check in remainingAcceptance."
    }
    for ($index = 0; $index -lt $acceptanceSource.Count; $index++) {
        if ([string]$remainingAcceptance[$index] -cne [string]$acceptanceSource[$index]) {
            throw "$($item.id) remainingAcceptance must exactly preserve the original acceptance check at index $index."
        }
    }
    switch ([string]$item.implementationStatus) {
        'not-implemented' {
            if ($references.Count -ne 0 -or $remaining.Count -eq 0) { throw "$($item.id) not-implemented status requires no source references and explicit remaining implementation." }
        }
        'partial-candidate' {
            if ($references.Count -eq 0 -or $remaining.Count -eq 0) { throw "$($item.id) partial-candidate status requires source references and explicit remaining implementation." }
        }
        'implemented-awaiting-acceptance' {
            if ($references.Count -eq 0 -or $remaining.Count -ne 0) { throw "$($item.id) implemented-awaiting-acceptance requires source references and no known implementation gaps." }
        }
    }
    foreach ($reference in $references) {
        $parts = ([string]$reference -split '::', 2)
        if ($parts.Count -ne 2 -or [string]::IsNullOrWhiteSpace($parts[1])) { throw "$($item.id) $kind source reference must include a file and exact source locator." }
        $path = Join-Path $repoRoot $parts[0]
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "$($item.id) $kind source reference path is missing: $($parts[0])" }
        $sourceText = Get-Content -LiteralPath $path -Raw
        if ($sourceText -notmatch [regex]::Escape($parts[1])) { throw "$($item.id) $kind source locator is missing from $($parts[0]): $($parts[1])" }
    }
}
# Every original denominator and review ID is fixed; source evidence only
# annotates implementation progress and never changes acceptance state.
if ($inventory.coverageCounting.originalTodoCount -ne 27 -or
    $inventory.coverageCounting.inheritedExplicitGapCount -ne 53 -or
    $inventory.coverageCounting.additionalReviewItemCount -ne 8 -or
    $inventory.coverageCounting.countsAreCeilings -ne $false) {
    throw 'Round54 must preserve the original TODO/gap denominators and eight review additions.'
}
$reviewIds = @('R54-01','R54-02','R54-03','R54-04','R54-05','R54-06','R54-07','R54-08')
$reviews = @($inventory.round54ReviewAdditions)
if (@($reviews.id).Count -ne 8 -or @($reviews.id | Select-Object -Unique).Count -ne 8 -or
    (@($reviews.id | Sort-Object) -join ',') -cne (@($reviewIds | Sort-Object) -join ',')) { throw 'Round54 review additions must contain every R54 ID exactly once.' }
$contracts = @($inventory.round54ContractCorrections)
$contractIds = @('C54-01','C54-02','C54-03','C54-04','C54-05')
if (@($contracts.id).Count -ne 5 -or @($contracts.id | Select-Object -Unique).Count -ne 5 -or
    (@($contracts.id | Sort-Object) -join ',') -cne (@($contractIds | Sort-Object) -join ',')) { throw 'Round54 contracts must contain every C54 ID exactly once.' }
$refinements = @($inventory.round54ExecutionRefinements)
$refinementIds = @('C54-03-O','C54-05-A','C54-03-T','C54-04-B')
if (@($refinements.id).Count -ne 4 -or @($refinements.id | Select-Object -Unique).Count -ne 4 -or
    (@($refinements.id | Sort-Object) -join ',') -cne (@($refinementIds | Sort-Object) -join ',')) { throw 'Round54 execution refinements must contain all four IDs exactly once.' }
foreach ($item in $reviews) {
    Assert-ImplementationMetadata $item 'review'
    if (@($item.requiredChecks).Count -eq 0 -or @($item.acceptanceCriteria).Count -eq 0 -or @($item.journeys).Count -eq 0) { throw "$($item.id) must retain checks, AC and journey links." }
    foreach ($journeyId in $item.journeys) {
        $journey = @($matrix.userJourneys | Where-Object id -CEQ $journeyId)
        if ($journey.Count -ne 1) { throw "$($item.id) references unknown or duplicate journey $journeyId." }
        foreach ($q in @($matrix.qualityObjectives | Where-Object { $journeyId -in $_.journeys })) { if ($q.id -notin @($inventory.qualityObjectives.id)) { throw "$($item.id) journey $journeyId has no Q inventory link." } }
    }
    foreach ($ac in $item.acceptanceCriteria) { if ($ac -notin @($matrix.acceptanceCriteria.id)) { throw "$($item.id) references unknown AC $ac." } }
    $expectedQForReview = @($matrix.qualityObjectives | Where-Object { @($_.journeys | Where-Object { $item.journeys -contains $_ }).Count -gt 0 } | ForEach-Object id | Sort-Object)
    $actualQForReview = @($item.journeys | ForEach-Object { $journeyId = $_; @($matrix.qualityObjectives | Where-Object { $journeyId -in $_.journeys } | ForEach-Object id) } | Select-Object -Unique | Sort-Object)
    if (($expectedQForReview -join ',') -cne ($actualQForReview -join ',')) { throw "$($item.id) Q journey links do not resolve to the fixed matrix." }
}
foreach ($contract in $contracts) {
    Assert-ImplementationMetadata $contract 'contract'
    if (@($contract.reviews).Count -eq 0) { throw "$($contract.id) must link to R54." }
    foreach ($id in $contract.reviews) { if ($id -notin $reviewIds) { throw "$($contract.id) references unknown review $id." } }
    foreach ($id in $contract.refinementIds) { if ($id -notin $refinementIds) { throw "$($contract.id) references unknown refinement $id." } }
}
$expectedContractReviews = @{
    'C54-01'='R54-07'; 'C54-02'='R54-06'; 'C54-03'='R54-01'; 'C54-04'='R54-05'; 'C54-05'='R54-01'
}
$expectedContractRefinements = @{
    'C54-01'=@(); 'C54-02'=@(); 'C54-03'=@('C54-03-O','C54-03-T'); 'C54-04'=@('C54-04-B'); 'C54-05'=@('C54-05-A','C54-03-T')
}
foreach ($contract in $contracts) {
    if ((@($contract.reviews | Sort-Object) -join ',') -cne $expectedContractReviews[$contract.id]) { throw "$($contract.id) is cross-linked to the wrong R54 review." }
    if ((@($contract.refinementIds | Sort-Object) -join ',') -cne (@($expectedContractRefinements[$contract.id] | Sort-Object) -join ',')) { throw "$($contract.id) has an incomplete or incorrect refinement dependency list." }
}
$expectedRefinementLinks = @{
    'C54-03-O'='C54-03'; 'C54-05-A'='C54-05'; 'C54-03-T'='C54-03,C54-05'; 'C54-04-B'='C54-04'
}
foreach ($refinement in $refinements) {
    Assert-ImplementationMetadata $refinement 'refinement'
    if (@($refinement.checks).Count -eq 0) { throw "$($refinement.id) must retain required checks." }
    foreach ($id in $refinement.contracts) { if ($id -notin $contractIds) { throw "$($refinement.id) references unknown contract $id." } }
    foreach ($id in $refinement.reviews) { if ($id -notin $reviewIds) { throw "$($refinement.id) references unknown review $id." } }
    if ((@($refinement.contracts | Sort-Object) -join ',') -cne $expectedRefinementLinks[$refinement.id]) { throw "$($refinement.id) is cross-linked to the wrong C54 contract." }
}
if ($inventory.browserAccepted -ne $false -or $inventory.nativeSourceQualified -ne $false -or $inventory.inventoryOnly -ne $true) {
    throw 'Coverage inventory must remain explicitly non-acceptance and non-native-qualification evidence.'
}
$dateAudit = @($inventory.auditFindings | Where-Object { $_.id -ceq 'RUNS-DATE-SEMANTICS-001' })
if ($dateAudit.Count -ne 1 -or $dateAudit[0].status -cnotmatch 'resolved-by-visible-date-caption-and-hash-navigation' -or
    $dateAudit[0].interpretation -notmatch 'not evidence of a missing NetIQ record') {
    throw 'The execution-date vs source-data-date audit finding must stay explicit and must not be misreported as a missing record.'
}
$manifestPath = Join-Path $repoRoot 'LogForesight.Tests/Fixtures/prtg-round53-formal-cases.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$realMaintenance = @($matrix.maintenanceRoleMatrix.roles)
$syntheticMaintenance = @($matrix.maintenanceRoleMatrix.syntheticDefenseCases)
if ($realMaintenance.Count -ne 4 -or @($realMaintenance.id | Select-Object -Unique).Count -ne 4 -or
    (@($realMaintenance.id | Sort-Object) -join ',') -cne ((@('M54-rescue','M54-global','M54-readall','M54-nomaintain') | Sort-Object) -join ',')) { throw 'Maintenance matrix must have exactly four unique real-login scenarios.' }
$expectedCapabilities = @{
    'M54-rescue' = @{ Maintain=$true; ViewAll=$false; Identity='actual serverAdmin authentication path; not an ordinary user renamed serverAdmin' }
    'M54-global' = @{ Maintain=$true; ViewAll=$true; Identity='ordinary Maintain and ViewAll user' }
    'M54-readall' = @{ Maintain=$false; ViewAll=$true; Identity='ordinary ViewAll user without Maintain' }
    'M54-nomaintain' = @{ Maintain=$false; ViewAll=$false; Identity='ordinary scoped user without Maintain' }
}
foreach ($role in $realMaintenance) {
    $expectedRole = $expectedCapabilities[[string]$role.id]
    if ($null -eq $expectedRole -or $role.maintain -ne $expectedRole.Maintain -or $role.viewAll -ne $expectedRole.ViewAll -or $role.identity -cne $expectedRole.Identity) { throw "$($role.id) identity/capability classification is invalid." }
}
if ($syntheticMaintenance.Count -ne 1 -or $syntheticMaintenance[0].id -cne 'M54-scoped' -or $syntheticMaintenance[0].browserAcceptance -ne $false -or $syntheticMaintenance[0].provisionAccount -ne $false) { throw 'M54-scoped must remain a non-provisioned synthetic component defense.' }
if ($syntheticMaintenance[0].maintain -ne $true -or $syntheticMaintenance[0].viewAll -ne $false) { throw 'M54-scoped synthetic defense must exercise restricted Maintain without ViewAll.' }
if ($matrix.maintenanceRoleMatrix.authorizationModelExpansion -ne $false -or $matrix.maintenanceRoleMatrix.baselineRolesUnchanged -ne $true -or $matrix.roles.Count -ne 6) { throw 'Baseline roles and authorization model must remain unchanged.' }
if ($matrix.sourcePolicy.required -notmatch 'Round 54 frozen clean build' -or $matrix.sourcePolicy.syntheticOnly -ne $true -or $matrix.sourcePolicy.externalConnections -ne $false) { throw 'Round54 frozen-build and synthetic source policy is required.' }
if ((@($matrix.round54ReviewIds | Sort-Object) -join ',') -cne (@($reviews.id | Sort-Object) -join ',')) { throw 'Matrix and coverage inventory R54 review IDs differ.' }
$caseIds = @($manifest.families | ForEach-Object { $_.positiveCases; $_.negativeAndBoundaryCases } | ForEach-Object { $_.caseId })
if ($caseIds.Count -ne 222 -or @($caseIds | Select-Object -Unique).Count -ne 222 -or @($manifest.families).Count -ne 8) {
    throw 'Fixed rule source manifest is no longer exactly 222 unique cases across eight families.'
}
[pscustomobject]@{
    Result='PASS-INVENTORY-CONSISTENCY-ONLY'
    AcceptanceClaimed=$false
    BrowserAccepted=$false
    NativeSourceQualified=$false
    AcceptanceCriteria=$inventory.acceptanceCriteria.Count
    UserJourneys=$inventory.userJourneys.Count
    QualityObjectives=$inventory.qualityObjectives.Count
    ControlledSourceScenarios=$inventory.controlledSourceScenarios.Count
    FixedRuleCases=$caseIds.Count
    RemainingExplicitGaps=@($inventory.acceptanceCriteria.uncovered + $inventory.userJourneys.uncovered + $inventory.qualityObjectives.uncovered).Count
    InventoryPath=(Join-Path $stageRoot 'coverage-inventory.json')
}
