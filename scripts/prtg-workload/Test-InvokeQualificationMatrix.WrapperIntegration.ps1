#Requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$fixture=Join-Path $PSScriptRoot 'matrix_wrapper_fixture.py';$matrix=Join-Path $PSScriptRoot 'Invoke-QualificationMatrix.ps1';$initializer=Join-Path $PSScriptRoot 'Initialize-QualificationMatrixRun.ps1'
$candidateRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'));$taskRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot ('wrapper-integration-'+[guid]::NewGuid().ToString('N'))))
$expectedParent=[IO.Path]::GetFullPath($PSScriptRoot);$portFile=Join-Path ([IO.Path]::GetTempPath()) ('lf-matrix-wrapper-port-'+[guid]::NewGuid().ToString('N')+'.txt')
$fixtureLogRoot=[IO.Path]::GetFullPath((Join-Path (Join-Path $PSScriptRoot '..\..') '.gemini-tasks\primary-results\matrix-wrapper-fixtures'));$null=New-Item -ItemType Directory -Path $fixtureLogRoot -Force
$logId=[guid]::NewGuid().ToString('N');$stderr=Join-Path $fixtureLogRoot "wrapper-fixture-$logId.stderr.log";$stdout=Join-Path $fixtureLogRoot "wrapper-fixture-$logId.stdout.log";$process=$null
function Assert([bool]$condition,[string]$message){if(-not$condition){throw $message};$script:passed++}
$passed=0
try{
    $null=New-Item -ItemType Directory -Path $taskRoot
    $argumentLine='"{0}" --port 0 --port-file "{1}"' -f $fixture,$portFile
    $process=Start-Process -FilePath 'python' -ArgumentList $argumentLine -WindowStyle Hidden -PassThru -RedirectStandardError $stderr -RedirectStandardOutput $stdout
    $until=[DateTime]::UtcNow.AddSeconds(10);while(-not(Test-Path -LiteralPath $portFile) -and [DateTime]::UtcNow -lt $until){if($process.HasExited){throw "Public matrix fixture exited: $(Get-Content -LiteralPath $stderr -Raw)"};Start-Sleep -Milliseconds 20}
    if(-not(Test-Path -LiteralPath $portFile)){throw 'Public matrix fixture failed to publish its bound loopback port.'}
    $port=[int](Get-Content -LiteralPath $portFile -Raw);$base=[uri]"http://127.0.0.1:$port/";$prtg=[uri]"http://127.0.0.1:$port/prtg";$sentinel=[uri]'http://127.0.0.1:65530/sentinel'
    $ready=$false;$until=[DateTime]::UtcNow.AddSeconds(10);while([DateTime]::UtcNow -lt $until){try{$null=Invoke-WebRequest -Uri ([uri]::new($base,'health')) -TimeoutSec 1 -UseBasicParsing;$ready=$true;break}catch{};Start-Sleep -Milliseconds 50};if(-not$ready){throw 'Public matrix fixture did not answer its health probe.'}
    $session=[Microsoft.PowerShell.Commands.WebRequestSession]::new();$session.Cookies.Add($base,[Net.Cookie]::new('lf_auth','fixture'))
    $ids=1..15000;$fences=@(foreach($id in $ids){@{sensorObjid=$id;identityEpoch=1;channelGeneration="channel-$id";bindingRevision=1;bindingFingerprint=('b'*64)}})
    $manifestPath=Join-Path $taskRoot 'sensor-manifest.json';$manifest=@{schema='qualification-job-manifest-v1';sensorIds=$ids;sensorFences=$fences;settingsRevision='1';policyRevision='policy-wrapper-1';scopeFingerprint='scope-wrapper-1';sourceGeneration='source-wrapper-1';authorityContextFingerprint='authority-wrapper-1'}
    [IO.File]::WriteAllText($manifestPath,($manifest|ConvertTo-Json -Depth 8 -Compress),[Text.UTF8Encoding]::new($false))
    $dataRoot=Join-Path $taskRoot 'owned-data';$null=New-Item -ItemType Directory -Path $dataRoot;$db=Join-Path $dataRoot 'matrix.db';[IO.File]::WriteAllBytes($db,[Text.Encoding]::ASCII.GetBytes("SQLite format 3`0"+'0123456789'))
    $providerPath=Join-Path $taskRoot 'provider.json';[IO.File]::WriteAllText($providerPath,(@{schema='round53-owned-run-descriptor-v1';provider='sqlite';dataRoot=$dataRoot;databaseFile=$db;retentionDays=180}|ConvertTo-Json -Compress),[Text.UTF8Encoding]::new($false))
    $runRoot=Join-Path $taskRoot 'run';$evidenceRoot=Join-Path $runRoot 'evidence';$assembly=Join-Path $PSScriptRoot 'bin\Release\net8.0-windows\PrtgWorkloadGate.dll'
    if(-not(Test-Path -LiteralPath $assembly -PathType Leaf)){throw 'Build the public PrtgWorkloadGate project in Release before running this wrapper integration test.'}
    $init=& $initializer -Profile controlled-loopback-3000 -BaseUri $base -PrtgFixtureUri $prtg -SentinelFixtureUri $sentinel -ManifestPath $manifestPath -ProviderDescriptorPath $providerPath -MatrixRunDirectory $runRoot -WebSession $session -MaxSeconds 30
    Assert ($init.hostCount -eq 3000 -and $init.sensorCount -eq 15000 -and $init.status -ceq 'owned-fixture-declared-awaiting-qualification') 'Initializer did not derive the public 3,000-host and 15,000-sensor scope.'
    $common=@{Profile='controlled-loopback-3000';BaseUri=$base;PrtgFixtureUri=$prtg;SentinelFixtureUri=$sentinel;ManifestPath=$manifestPath;WebSession=$session;CoverageAssemblyPath=$assembly;MatrixRunDirectory=$runRoot;EvidenceRoot=$evidenceRoot;MaxSeconds=180;PollSeconds=1;MatrixCommandMaxSeconds=180}
    $badEvidenceCommon=$common.Clone();$badEvidenceCommon.EvidenceRoot=Join-Path $taskRoot 'outside-run-evidence';$badEvidenceRejected=$false
    try{$null=& $matrix @badEvidenceCommon -Action Check -JobId ('a'*32) -ExpectedVersion 1 -ExpectedWave 1}catch{$badEvidenceRejected=$_.Exception.Message -like '*EvidenceRoot must be a child of MatrixRunDirectory*'}
    Assert ($badEvidenceRejected -and -not(Test-Path -LiteralPath (Join-Path $runRoot 'qualification-lifecycle'))) 'Wrapper accepted an evidence root outside the owned run or wrote partial lifecycle artifacts.'
    $check=& $matrix @common -Action Check -JobId ('a'*32) -ExpectedVersion 1 -ExpectedWave 1
    $preflight=Get-Content -LiteralPath (Join-Path $runRoot 'full-consumer-authority-preflight.json') -Raw|ConvertFrom-Json
    Assert ($check.status -ceq 'ready-for-fixed-matrix' -and $check.selectedSensorCount -eq 15000 -and $preflight.status -ceq 'current-qualified-proofs') 'Public Check did not advance only after its exact 15,000-row verifier gate.'
    Assert ($preflight.qualificationJobId -ceq ('a'*32) -and (Test-Path -LiteralPath $preflight.coverageReceiptPath) -and -not$preflight.nativeSourceVerified -and -not$preflight.wholeRoundAccepted) 'Check receipt is not bound to its actual job/coverage receipt or promoted an unrelated claim.'
    $expected=@('netiq-only','combined','combined','netiq-only','netiq-only','combined')
    for($i=1;$i -le 6;$i++){$cell=& $matrix @common -Action Matrix -ConditionIndex $i;if($i -lt 6){Assert ($cell.status -ceq 'matrix-cell-complete-next-fixed-cell-required' -and $cell.completedConditions -eq $i) "Matrix wrapper did not seal fixed cell $i."}else{Assert ($cell.status -ceq 'metrics-pass-source-and-retention-unqualified') 'Six-cell wrapper did not retain its explicitly unqualified metric result.'}}
    $savedOrder=@(Get-ChildItem -LiteralPath $runRoot -Directory -Filter 'condition-*'|Sort-Object Name|ForEach-Object{(Get-Content -LiteralPath (Join-Path $_.FullName 'condition-owner.json') -Raw|ConvertFrom-Json).condition})
    $evaluation=Get-Content -LiteralPath (Join-Path $runRoot 'matrix-evaluation.json') -Raw|ConvertFrom-Json -Depth 16
    Assert (($savedOrder -join ',') -ceq ($expected -join ',') -and $evaluation.metricChecksPass -and $evaluation.observableSubchecksPass) 'Wrapper did not preserve the exact six-cell sequence or metric subchecks.'
    Assert ($evaluation.routePairs.Count -eq 6 -and @($evaluation.routePairs|Where-Object { -not$_.pass -or $_.relativeP95Increase -gt 0.10 }).Count -eq 0 -and @($evaluation.routeSamples|Where-Object { -not$_.rawSamplesValid -or $_.rawSampleCount -ne 100 }).Count -eq 0) 'Wrapper did not evaluate all six route pairs using the complete independently recalculated measurements.'
    Assert (-not$evaluation.nativeSourceVerified -and -not$evaluation.formalAcceptance -and -not$evaluation.wholeRoundAccepted -and -not$evaluation.sourceQualification.qualified) 'Fixture wrapper result promoted physical-source or formal evidence.'

    # The success fixture uses API-host timestamps 2 seconds ahead to model
    # bounded clock skew without weakening the consumer's strict trigger check.
    # A deliberate negative skew must still reject timing that predates trigger.
    $null=Invoke-WebRequest -Uri ([uri]::new($base,'__fixture/set-clock-offset?seconds=-1')) -UseBasicParsing -TimeoutSec 3
    $clockSkewDir=Join-Path $taskRoot 'clock-skew-negative';$skewFailure=$false;$skewMessage=''
    try{$null=& (Join-Path $PSScriptRoot 'Invoke-QualificationMatrixConsumer.ps1') -Action Schedule -BaseUri $base -WebSession $session -OutputDirectory $clockSkewDir -ProfilePath (Join-Path $runRoot 'round53-profile.json') -HostIdsPath (Join-Path $runRoot 'host-ids.txt') -MaxSeconds 30}catch{$skewFailure=$true;$skewMessage=$_.Exception.Message}
    Assert ($skewFailure -and $skewMessage -like '*do not correlate to this explicit trigger boundary*' -and (Test-Path -LiteralPath (Join-Path $clockSkewDir 'schedule-start.json')) -and -not(Test-Path -LiteralPath (Join-Path $clockSkewDir 'schedule-complete.json'))) 'Strict trigger boundary did not reject a deliberately stale/skewed API timestamp.'
    $null=Invoke-WebRequest -Uri ([uri]::new($base,'__fixture/set-clock-offset?seconds=2')) -UseBasicParsing -TimeoutSec 3

    # Copying a preflight and advancing its revision is synthetic fixture setup only; production retries require a new initialized run.
    $failedRoot=Join-Path $taskRoot 'failure-probe-run';$failedEvidence=Join-Path $failedRoot 'evidence';$failedLifecycle=Join-Path $failedRoot 'qualification-lifecycle'
    $null=New-Item -ItemType Directory -Path $failedEvidence,$failedLifecycle -Force
    foreach($name in @('run-owner.json','round53-profile.json','full-consumer-authority-preflight.json','host-ids.txt')){Copy-Item -LiteralPath (Join-Path $runRoot $name) -Destination (Join-Path $failedRoot $name)}
    Copy-Item -LiteralPath (Join-Path $runRoot 'qualification-lifecycle\qualification-job-receipt.json') -Destination $failedLifecycle
    Copy-Item -LiteralPath (Join-Path $runRoot 'qualification-lifecycle\coverage-manifest.json') -Destination $failedLifecycle
    $sourcePreflight=Get-Content -LiteralPath (Join-Path $failedRoot 'full-consumer-authority-preflight.json') -Raw|ConvertFrom-Json
    $failedCoverage=Join-Path $failedEvidence 'coverage';$null=New-Item -ItemType Directory -Path $failedCoverage
    Copy-Item -LiteralPath ([string]$sourcePreflight.coverageReceiptPath) -Destination $failedCoverage
    $sourcePreflight.coverageReceiptPath=Join-Path $failedCoverage 'coverage-receipt.json'
    $currentSettings=Invoke-RestMethod -Uri ([uri]::new($base,'api/admin/settings')) -WebSession $session -TimeoutSec 5
    $sourcePreflight.settingsRevision=[string]$currentSettings.data.revision
    $failedCommon=$common.Clone();$failedCommon.MatrixRunDirectory=$failedRoot;$failedCommon.EvidenceRoot=$failedEvidence
    $preflightPath=Join-Path $failedRoot 'full-consumer-authority-preflight.json';[IO.File]::WriteAllText($preflightPath,($sourcePreflight|ConvertTo-Json -Depth 16),[Text.UTF8Encoding]::new($false))
    $gatePath=Join-Path $runRoot 'qualification-lifecycle\qualification-matrix-gate.json';$failedGate=Get-Content -LiteralPath $gatePath -Raw|ConvertFrom-Json -Depth 16
    $failedGate.coverageManifestPath=Join-Path $failedLifecycle 'coverage-manifest.json';$failedGate.coverageEvidenceDirectory=$failedCoverage
    [IO.File]::WriteAllText((Join-Path $failedLifecycle 'qualification-matrix-gate.json'),($failedGate|ConvertTo-Json -Depth 16),[Text.UTF8Encoding]::new($false))
    $foreignCheckpointPath=Join-Path $failedRoot 'qualified-matrix-checkpoint.json';$fixedConditions=@('netiq-only','combined','combined','netiq-only','netiq-only','combined');$foreignCheckpoint=@{schema='prtg-qualified-matrix-checkpoint-v1';runId='foreign-run';profile='controlled-loopback-3000';jobId=('a'*32);manifestSha256=(Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToUpperInvariant();settingsRevision=[string]$currentSettings.data.revision;completedConditions=1;conditionOrder=$fixedConditions};[IO.File]::WriteAllText($foreignCheckpointPath,($foreignCheckpoint|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false));$foreignCheckpointRejected=$false;$foreignCheckpointMessage="";try{$null=& $matrix @failedCommon -Action Matrix -ConditionIndex 2}catch{$foreignCheckpointRejected=$true;$foreignCheckpointMessage=$_.Exception.Message};Assert ($foreignCheckpointRejected -and $foreignCheckpointMessage -like "*checkpoint belongs to another run/profile/job*" -and -not(Test-Path -LiteralPath (Join-Path $failedRoot 'condition-02-combined'))) 'A checkpoint copied from another run was accepted or created a new cell.';Remove-Item -LiteralPath $foreignCheckpointPath -Force
    $null=Invoke-WebRequest -Uri ([uri]::new($base,'__fixture/drift-revision-on-next-route')) -UseBasicParsing -TimeoutSec 3
    $failedCommon=$common.Clone();$failedCommon.MatrixRunDirectory=$failedRoot;$failedCommon.EvidenceRoot=$failedEvidence
    $expectedFailure=$false;try{$null=& $matrix @failedCommon -Action Matrix -ConditionIndex 1}catch{$expectedFailure=$true}
    $failedAttempt=Get-Content -LiteralPath (Join-Path $failedRoot 'condition-01-netiq-only\condition-owner.json') -Raw|ConvertFrom-Json
    Assert ($expectedFailure -and $failedAttempt.state -ceq 'failed' -and $failedAttempt.failureStage -ceq 'post-measurement-fence' -and $failedAttempt.failureReasonCode -ceq 'bounded-cell-stage-failed') 'Mid-cell settings flip/restore did not persist a post-fence failure owner.'
    Assert (-not(Test-Path -LiteralPath (Join-Path $failedRoot 'qualified-matrix-checkpoint.json'))) 'Failed condition incorrectly advanced the fixed matrix checkpoint.'
    "PASS: $passed actual wrapper integration assertions; Check -> exact coverage gate -> all six live-fenced public-API fixture cells plus persisted failure/no-checkpoint case; all formal/source flags false."
}finally{
    if($process){try{if(-not$process.HasExited){Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue;Wait-Process -Id $process.Id -Timeout 5 -ErrorAction SilentlyContinue}}catch{}}
    if((Test-Path -LiteralPath $portFile) -and (Split-Path -Leaf $portFile) -match '^lf-matrix-wrapper-port-[0-9a-f]{32}\.txt$'){Remove-Item -LiteralPath $portFile -Force -ErrorAction SilentlyContinue}
    if(Test-Path -LiteralPath $taskRoot){$resolved=(Resolve-Path -LiteralPath $taskRoot).Path;$parent=(Split-Path -Parent $resolved);$item=Get-Item -LiteralPath $resolved -Force;if($parent -cne $expectedParent -or (Split-Path -Leaf $resolved) -notmatch '^wrapper-integration-[0-9a-f]{32}$' -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Refusing cleanup: wrapper test root failed exact parent/name/reparse checks.'};Remove-Item -LiteralPath $resolved -Recurse -Force}
}
