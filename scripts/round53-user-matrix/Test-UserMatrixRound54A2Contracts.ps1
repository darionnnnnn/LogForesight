[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$matrixPath = Join-Path $PSScriptRoot 'user-matrix.json'
$inventoryPath = Join-Path $PSScriptRoot 'coverage-inventory.json'
$validator = Join-Path $PSScriptRoot 'Test-UserMatrixCoverageInventory.ps1'
$temp = Join-Path ([IO.Path]::GetTempPath()) ('r54-a2-contracts-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temp) | Out-Null
try {
  $baselineMatrix = Get-Content -LiteralPath $matrixPath -Raw | ConvertFrom-Json
  $baselineInventory = Get-Content -LiteralPath $inventoryPath -Raw | ConvertFrom-Json
  function Invoke-Case([string]$name, [scriptblock]$mutateMatrix, [scriptblock]$mutateInventory) {
    $m = $baselineMatrix | ConvertTo-Json -Depth 60 | ConvertFrom-Json
    $i = $baselineInventory | ConvertTo-Json -Depth 60 | ConvertFrom-Json
    if ($mutateMatrix) { & $mutateMatrix $m }
    if ($mutateInventory) { & $mutateInventory $i }
    $mp = Join-Path $temp "$name-matrix.json"; $ip = Join-Path $temp "$name-inventory.json"
    $m | ConvertTo-Json -Depth 60 | Set-Content -LiteralPath $mp -Encoding utf8
    $i | ConvertTo-Json -Depth 60 | Set-Content -LiteralPath $ip -Encoding utf8
    $failed = $false
    try { & $validator -UserMatrixPath $mp -CoverageInventoryPath $ip -RepositoryRoot $root | Out-Null } catch { $failed = $true }
    if (!$failed) { throw "Negative contract test '$name' was not rejected." }
    "PASS negative: $name"
  }
  & $validator -UserMatrixPath $matrixPath -CoverageInventoryPath $inventoryPath -RepositoryRoot $root | Out-Null
  'PASS baseline: A2 Round54 contract matrix accepted as pending'
  Invoke-Case 'missing-review-id' $null { param($i) $i.round54ReviewAdditions = @($i.round54ReviewAdditions | Where-Object id -ne 'R54-07') }
  Invoke-Case 'wrong-review-dependency' $null { param($i) $i.round54ExecutionRefinements[0].contracts = @('C54-99') }
  Invoke-Case 'wrong-valid-review-crosslink' $null { param($i) $i.round54ContractCorrections[0].reviews = @('R54-06') }
  Invoke-Case 'wrong-role-capability' { param($m) $m.maintenanceRoleMatrix.roles[2].maintain = $true } $null
  Invoke-Case 'synthetic-as-browser' { param($m) $m.maintenanceRoleMatrix.syntheticDefenseCases[0].browserAcceptance = $true } $null
  Invoke-Case 'wrong-q-journey-link' { param($m) $m.qualityObjectives[0].journeys = @('U53-02') } $null
  Invoke-Case 'pending-misreported' $null { param($i) $i.round54ReviewAdditions[0].status = 'passed' }
  Invoke-Case 'accepted-without-evidence' $null { param($i) $i.round54ReviewAdditions[0].acceptanceStatus = 'accepted'; $i.round54ReviewAdditions[0].status = 'accepted'; $i.round54ReviewAdditions[0].sourceReferences = @() }
  Invoke-Case 'implemented-without-source' $null { param($i) $i.round54ReviewAdditions[0].implementationStatus = 'implemented-awaiting-acceptance'; $i.round54ReviewAdditions[0].sourceReferences = @() }
  Invoke-Case 'acceptance-description-omitted' $null { param($i) $i.round54ReviewAdditions[4].remainingAcceptance = @() }
  Invoke-Case 'known-implementation-gap-marked-complete' $null { param($i) $i.round54ReviewAdditions[4].implementationStatus = 'implemented-awaiting-acceptance' }
  Invoke-Case 'unresolvable-source-locator' $null { param($i) $i.round54ReviewAdditions[0].sourceReferences = @('LogForesight.Core/Service/PrtgDailyPipeline.cs::missing-locator') }
} finally { Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue }
