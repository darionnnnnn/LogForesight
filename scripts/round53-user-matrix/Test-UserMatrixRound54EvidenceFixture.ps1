[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion -lt [Version]'7.4') { throw 'Requires PowerShell 7.4+.' }
$stageRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('r54-evidence-fixture-' + [Guid]::NewGuid().ToString('N'))
$repoRoot = Join-Path $tempRoot 'repo'
$stage = Join-Path $repoRoot 'scripts\round53-user-matrix'
$fakeVerifier = Join-Path $repoRoot 'scripts\Verify-BuildIdentity.ps1'
$build = Join-Path $tempRoot 'build'
$fixture = Join-Path $tempRoot 'fixture'
[IO.Directory]::CreateDirectory($stage) | Out-Null
[IO.Directory]::CreateDirectory((Split-Path -Parent $fakeVerifier)) | Out-Null
[IO.Directory]::CreateDirectory((Join-Path $build 'wwwroot')) | Out-Null
[IO.Directory]::CreateDirectory($fixture) | Out-Null
try {
  foreach ($file in @('Initialize-UserMatrixEvidence.ps1','Record-UserMatrixBrowserObservation.ps1','user-matrix.json','coverage-inventory.json')) {
    Copy-Item -LiteralPath (Join-Path $stageRoot $file) -Destination (Join-Path $stage $file)
  }
  Set-Content -LiteralPath $fakeVerifier -Value 'param([string]$Directory)' -Encoding utf8
  $commit = '0123456789abcdef0123456789abcdef01234567'
  $stamp = "lfbuild-v1|modified|$commit|$('a' * 64)"
  foreach ($dll in @('LogForesight.Web.dll','LogForesight.Core.dll')) { [IO.File]::WriteAllText((Join-Path $build $dll),"test-owned $dll") }
  [IO.File]::WriteAllText((Join-Path $build 'wwwroot\app.js'),'fixture asset')
  $identity = [ordered]@{ schema='logforesight-build-identity-v1'; assemblies=@(
    [ordered]@{ name='LogForesight.Web'; sourceStamp=$stamp; sourceRevisionIdOverride=$commit },
    [ordered]@{ name='LogForesight.Core'; sourceStamp=$stamp; sourceRevisionIdOverride=$commit }
  ) }
  $identity | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $build 'build-identity.json') -Encoding utf8
  $db = Join-Path $fixture 'matrix.db'; $key = Join-Path $fixture 'matrix.key'
  [IO.File]::WriteAllText($db,'owned synthetic database'); [IO.File]::WriteAllText($key,'owned synthetic key')
  $initializer = Join-Path $stage 'Initialize-UserMatrixEvidence.ps1'
  $pwsh = (Get-Command pwsh -CommandType Application -ErrorAction Stop).Source
  $initOutput = & $pwsh -NoProfile -File $initializer -BuildDirectory $build -FixtureDatabase $db -FixtureKey $key -ExpectedSourceCommit $commit
  if ($LASTEXITCODE -ne 0) { throw "Owned fixture initialization failed: $($initOutput -join ' ')" }
  $evidencePath = [IO.Path]::GetFullPath([string]($initOutput | Select-Object -Last 1))
  $runRoot = Split-Path -Parent $evidencePath
  $evidence = Get-Content -LiteralPath $evidencePath -Raw | ConvertFrom-Json
  if (@($evidence.round54Reviews).Count -ne 8 -or @($evidence.round54Contracts).Count -ne 5 -or @($evidence.round54ExecutionRefinements).Count -ne 4 -or
      @($evidence.maintenanceRoles).Count -ne 4 -or @($evidence.syntheticDefenseCases).Count -ne 1 -or
      @($evidence.round54Reviews | Where-Object result -ne 'pending').Count -ne 0 -or
      @($evidence.syntheticDefenseCases | Where-Object { $_.browserAcceptance -ne $false -or $_.provisionAccount -ne $false }).Count -ne 0) { throw 'Initializer did not create the complete pending Round54 evidence shape.' }
  "PASS init fixture: $evidencePath"

  $runsRoot = Join-Path $stage 'runs'
  $artifactRoot = Join-Path $runRoot 'evidence\browser-artifacts'
  [IO.Directory]::CreateDirectory($artifactRoot) | Out-Null
  $runId = [IO.Path]::GetFileName($runRoot)
  $sourceBuild = [ordered]@{ ExpectedSourceCommit=$commit; SourceStamp=$stamp; SourceRevision=$commit; WebDllSha256=('a'*64); CoreDllSha256=('b'*64); StaticAssetsSourceSha256=('c'*64); StaticAssetsRuntimeSha256=('d'*64) }
  $ownerPath = Join-Path $runRoot 'process-owner.json'
  $owner = [ordered]@{ Schema='lf-round53-user-matrix-process-owner-v1'; RunId=$runId; BundlePath=$runRoot; Status='running' }
  $marker = [ordered]@{ Schema='lf-round53-user-matrix-owner-v1'; RunId=$runId; BundlePath=$runRoot; Status='running' }
  $manifest = [ordered]@{ Schema='lf-round53-user-matrix-fixture-v1'; RunId=$runId; Status='running'; SyntheticOnly=$true; SourceBuild=$sourceBuild }
  [IO.File]::WriteAllText($ownerPath,($owner | ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
  [IO.File]::WriteAllText((Join-Path $runRoot '.lf-round53-user-matrix-owner.json'),($marker | ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
  [IO.File]::WriteAllText((Join-Path $runRoot 'fixture-manifest.json'),($manifest | ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
  $artifact = Join-Path $artifactRoot 'contract-note.txt'
  [IO.File]::WriteAllText($artifact,'Test-owned contract fixture only; no browser used.')
  $recorder = Join-Path $stage 'Record-UserMatrixBrowserObservation.ps1'
  function Record([string]$id,[string]$result='inconclusive',[string]$check='check-01') {
    & $recorder -OwnerFile $ownerPath -RequirementId $id -ObservedResult $result -CheckId $check -Observation 'Owned fixture recorder contract check; no browser result.' -EvidenceFile $artifact
  }
  $r54 = Record 'R54-07' passed
  $c54 = Record 'C54-01' passed
  $refinement = Record 'C54-05-A' passed
  $realRole = Record 'M54-global' passed
  $syntheticRole = Record 'M54-scoped' passed
  $legacy = & $recorder -OwnerFile $ownerPath -RequirementId 'U53-05' -ObservedResult inconclusive -Observation 'Legacy contract path remains available; no browser result.' -EvidenceFile $artifact
  if ($r54.RequirementKind -ne 'round54Reviews' -or $c54.RequirementKind -ne 'round54Contracts' -or $refinement.RequirementKind -ne 'round54ExecutionRefinements' -or
      $realRole.RequirementKind -ne 'maintenanceRoles' -or $syntheticRole.RequirementKind -ne 'syntheticDefenseCases' -or $legacy.RequirementKind -ne 'userJourneys') { throw 'Recorder did not resolve one or more real consumers.' }
  $contractLog = Join-Path $runRoot 'evidence\round54-contract-observations.jsonl'
  $contractRecords = @(Get-Content -LiteralPath $contractLog | ForEach-Object { ConvertFrom-Json $_ })
  $cRecord = @($contractRecords | Where-Object requirementId -CEQ 'C54-01')[0]
  if ($cRecord.declaredCheck -cne $evidence.round54Contracts[0].requiredChecks -or $syntheticRole.BrowserAcceptanceChanged -ne $false -or
      @($contractRecords | Where-Object { $_.requirementId -eq 'M54-scoped' -and $_.evidenceType -ne 'synthetic-authorization-component' }).Count -ne 0) { throw 'Contract record did not bind exact C54 check or maintain synthetic classification.' }
  "PASS recorder fixture: R54/C54/refinement/real+synthetic roles and legacy U53 route recorded by ID/check"

  $before = (Get-Item -LiteralPath $contractLog).Length
  $missingCheckRejected = $false
  try { & $recorder -OwnerFile $ownerPath -RequirementId 'C54-01' -ObservedResult passed -Observation 'missing check' -EvidenceFile $artifact | Out-Null } catch { $missingCheckRejected = $true }
  $wrongCheckRejected = $false
  try { Record 'C54-01' passed 'check-99' | Out-Null } catch { $wrongCheckRejected = $true }
  $unknownIdRejected = $false
  try { Record 'C54-99' passed | Out-Null } catch { $unknownIdRejected = $true }
  if (!$missingCheckRejected -or !$wrongCheckRejected -or !$unknownIdRejected -or (Get-Item -LiteralPath $contractLog).Length -ne $before) { throw 'Recorder accepted invalid check/ID or mutated ledger on rejection.' }
  $badCommitRejected = $false
  try {
    & $pwsh -NoProfile -File $initializer -BuildDirectory $build -FixtureDatabase $db -FixtureKey $key -ExpectedSourceCommit ('f'*40) -EvidencePath (Join-Path $runsRoot (([guid]::NewGuid().ToString('N')) + '\evidence.json')) | Out-Null
    $badCommitRejected = $LASTEXITCODE -ne 0
  } catch { $badCommitRejected = $true }
  if (!$badCommitRejected) { throw 'Initializer accepted an identity/source commit mismatch.' }
  'PASS negative fixture: missing/wrong CheckId, unknown ID, and initializer source-commit mismatch rejected'
} finally {
  $canonical = [IO.Path]::GetFullPath($tempRoot)
  if ([IO.Path]::GetDirectoryName($canonical) -ine [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')) { throw 'Refusing cleanup outside the owned temporary root.' }
  if (Test-Path -LiteralPath $canonical) { Remove-Item -LiteralPath $canonical -Recurse -Force }
}

