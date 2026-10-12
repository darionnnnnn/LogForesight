[CmdletBinding()]
param(
  [string]$ManifestPath = (Join-Path $PSScriptRoot 'user-matrix.json'),
  [string]$ReadmePath = (Join-Path $PSScriptRoot 'README.md')
)
$ErrorActionPreference = 'Stop'
$matrix = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
if ($matrix.schema -cne 'logforesight-round53-user-matrix-v1' -or $matrix.readiness -cne 'prepared-not-browser-accepted') { throw 'Matrix schema/readiness mismatch.' }
$expected = @{
  acceptanceCriteria = 1..22 | ForEach-Object { 'AC{0:D2}' -f $_ }
  userJourneys = 1..10 | ForEach-Object { 'U53-{0:D2}' -f $_ }
  qualityObjectives = 1..6 | ForEach-Object { 'Q{0}' -f $_ }
}
foreach ($group in $expected.Keys) {
  $actual = @($matrix.$group | ForEach-Object { $_.id })
  if (($actual -join ',') -cne ($expected[$group] -join ',')) { throw "$group must enumerate the full fixed set in canonical order." }
}
if ($matrix.roles.Count -ne 6 -or $matrix.controlledSourceScenarios.Count -ne 7) { throw 'Expected six roles and seven distinct controlled-source scenarios.' }
if ($matrix.acceptanceBoundary.markupOrApiTestsAreBrowserAcceptance -ne $false -or
    $matrix.acceptanceBoundary.stubSuccessIsNativeSourceQualification -ne $false -or
    $matrix.acceptanceBoundary.syntheticSourceEvidenceIsProductionEvidence -ne $false) { throw 'Acceptance boundary was weakened.' }
$requiredSources = @('NetIQ','PRTG','AI','SMTP')
if (@($matrix.controlledSourceScenarios.source | Select-Object -Unique).Count -lt $requiredSources.Count -or
    @($requiredSources | Where-Object { $_ -notin $matrix.controlledSourceScenarios.source }).Count -gt 0) { throw 'Controlled source matrix is incomplete.' }
$readme = Get-Content -LiteralPath $ReadmePath -Raw
foreach ($journey in $matrix.userJourneys) {
  $id = [string]$journey.id
  $rows = [regex]::Matches($readme, '(?m)^\|\s*' + [regex]::Escape($id) + '\s*\|([^|]*)\|([^|]*)\|\s*$')
  if ($rows.Count -ne 1) { throw "README must contain one fixed journey mapping row for $id." }
  $expectedAc = @($matrix.acceptanceCriteria | Where-Object { $id -in $_.journeys } | ForEach-Object { $_.id })
  $expectedQ = @($matrix.qualityObjectives | Where-Object { $id -in $_.journeys } | ForEach-Object { $_.id })
  $actualAc = @($rows[0].Groups[1].Value.Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_ })
  $actualQ = @($rows[0].Groups[2].Value.Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_ })
  if (($actualAc -join ',') -cne ($expectedAc -join ',') -or ($actualQ -join ',') -cne ($expectedQ -join ',')) {
    throw "README and machine-readable AC/Q mapping differ for $id."
  }
}
'PASS: complete 22 AC / 10 U53 / 6 Q inventory, six roles, seven controlled-source scenarios across four sources; browser acceptance remains pending.'
