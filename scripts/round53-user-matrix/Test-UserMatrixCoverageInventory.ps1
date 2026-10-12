[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$stageRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$repoRoot = [IO.Path]::GetFullPath((Join-Path $stageRoot '..\..'))
$matrix = Get-Content -LiteralPath (Join-Path $stageRoot 'user-matrix.json') -Raw | ConvertFrom-Json
$inventory = Get-Content -LiteralPath (Join-Path $stageRoot 'coverage-inventory.json') -Raw | ConvertFrom-Json
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
