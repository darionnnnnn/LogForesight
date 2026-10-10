#Requires -Version 7.0
<#
Canonical bounded adapter from exact all-sensor raw qualification to the existing
six-cell mixed-workload consumer. This file is fixture-only: all Web, saved PRTG,
and saved Sentinel endpoints must be explicit numeric loopback addresses. It never
creates bindings/proofs, calls PRTG/Sentinel itself, or promotes source/capacity/
retention/whole-round claims. Run Start/Resume/Check explicitly; invoke Matrix for
one fixed condition at a time after a sealed, exact coverage receipt exists. The
consumer is delivered as Invoke-QualificationMatrixConsumer.ps1; no private binary
or provider database runner is used.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Start','Resume','Check','Matrix')][string]$Action,
    [Parameter(Mandatory)][ValidateSet('fullstore-3000','controlled-loopback-3000')][string]$Profile,
    [Parameter(Mandatory)][uri]$BaseUri,
    [Parameter(Mandatory)][uri]$PrtgFixtureUri,
    [Parameter(Mandatory)][uri]$SentinelFixtureUri,
    [Parameter(Mandatory)][string]$ManifestPath,
    [Parameter(Mandatory)][Microsoft.PowerShell.Commands.WebRequestSession]$WebSession,
    [Parameter(Mandatory)][string]$CoverageAssemblyPath,
    [Parameter(Mandatory)][string]$MatrixRunDirectory,
    [Parameter(Mandatory)][string]$EvidenceRoot,
    [string]$JobId,
    [long]$ExpectedVersion=0,
    [int]$ExpectedWave=0,
    [ValidateRange(1,600)][int]$MaxSeconds=600,
    [ValidateRange(1,60)][int]$PollSeconds=60,
    [ValidateRange(0,6)][int]$ConditionIndex=0,
    [ValidateRange(60,3600)][int]$MatrixCommandMaxSeconds=3600,
    [string]$CookieName='lf_auth'
)
$ErrorActionPreference='Stop'
$candidateRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
function Assert-Loopback([uri]$Value,[string]$Label) {
    $ip=$null
    if($Value.Scheme -cnotin @('http','https') -or -not[Net.IPAddress]::TryParse($Value.DnsSafeHost.Trim('[',']'),[ref]$ip) -or
       -not[Net.IPAddress]::IsLoopback($ip) -or $Value.Port -lt 1 -or $Value.UserInfo -or $Value.Query -or $Value.Fragment) {
        throw "$Label must be an explicit numeric-loopback fixture URL without credentials, query, or fragment."
    }
}
function Read-JsonFile([string]$Path) { Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -Depth 32 }
function Write-JsonAtomic([string]$Path,[object]$Value) {
    $full=[IO.Path]::GetFullPath($Path);$null=New-Item -ItemType Directory -Path (Split-Path -Parent $full) -Force
    $tmp=$full+'.tmp';[IO.File]::WriteAllText($tmp,($Value|ConvertTo-Json -Depth 16),[Text.UTF8Encoding]::new($false));[IO.File]::Move($tmp,$full,$true)
}
function Get-Sha256([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
function Assert-NoReparseAncestor([string]$Path,[string]$Label) {
    $cursor=[IO.Path]::GetFullPath($Path)
    while(-not[string]::IsNullOrWhiteSpace($cursor)) {
        if(Test-Path -LiteralPath $cursor){$item=Get-Item -LiteralPath $cursor -Force;if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw "$Label or one of its existing ancestors is a reparse point; refusing lexical path escape."}}
        $parent=[IO.Path]::GetDirectoryName($cursor);if([string]::IsNullOrWhiteSpace($parent) -or $parent -ceq $cursor){break};$cursor=$parent
    }
}
function Assert-OwnedMatrixContext {
    Assert-NoReparseAncestor $MatrixRunDirectory 'Matrix run directory'
    $script:matrixRoot=[IO.Path]::GetFullPath($MatrixRunDirectory)
    $ownerPath=Join-Path $matrixRoot 'run-owner.json';$profilePath=Join-Path $matrixRoot 'round53-profile.json';$preflightPath=Join-Path $matrixRoot 'full-consumer-authority-preflight.json';$hostIdsPath=Join-Path $matrixRoot 'host-ids.txt'
    foreach($file in @($ownerPath,$profilePath,$preflightPath,$hostIdsPath)){if(-not(Test-Path -LiteralPath $file -PathType Leaf)){throw "Owned matrix prerequisite is missing: $file"}}
    $owner=Read-JsonFile $ownerPath;$profileDocument=Read-JsonFile $profilePath;$preflight=Read-JsonFile $preflightPath
    if($owner.schema -cne 'round53-mixed-owner-v1' -or $owner.profile -cne $Profile -or $profileDocument.schema -notin @('round53-full-consumer-profile-v1','round53-controlled-loopback-profile-v1') -or
       $preflight.schema -cne 'round53-full-consumer-authority-preflight-v1' -or $profileDocument.nativeSourceVerified -or $profileDocument.formalAcceptance -or $profileDocument.wholeRoundAccepted -or
       $preflight.nativeSourceVerified -or $preflight.formalAcceptance -or $preflight.wholeRoundAccepted){throw "Owned profile/provider evidence is missing, changed, or attempts a source/formal promotion (owner='$($owner.schema)', profile='$($profileDocument.schema)', preflight='$($preflight.schema)', flags=$($profileDocument.nativeSourceVerified)/$($profileDocument.formalAcceptance)/$($profileDocument.wholeRoundAccepted)/$($preflight.nativeSourceVerified)/$($preflight.formalAcceptance)/$($preflight.wholeRoundAccepted))."}
    if($profileDocument.netiqHosts -ne 3000 -or $profileDocument.prtgSensors -ne 15000 -or $profileDocument.retentionDays -ne 180 -or
       $preflight.hosts -ne 3000 -or $preflight.sensors -ne 15000 -or $preflight.settingsEnabled -ne $true){throw 'Matrix preflight requires the exact 3,000-host, 15,000-sensor, 180-day, enabled-current-source descriptor.'}
    $allowedStatus=if($Action -eq 'Matrix'){@('current-qualified-proofs')}else{@('owned-fixture-declared-awaiting-qualification','current-qualified-proofs')}
    if($preflight.status -notin $allowedStatus){throw 'Run descriptor status is not eligible for this explicit lifecycle action.'}
    $provider=([string]$profileDocument.provider).ToLowerInvariant();if($provider -notin @('sqlite','localdb')){throw 'Unsupported owned provider identity; refusing substitution.'}
    $dataRoot=[IO.Path]::GetFullPath([string]$profileDocument.dataRoot)
    if(-not[IO.Path]::IsPathFullyQualified([string]$profileDocument.dataRoot) -or [IO.Path]::GetFullPath([string]$owner.dataRoot) -cne $dataRoot){throw 'Owned provider data root differs from the profile/owner receipt.'}
    if($provider -eq 'sqlite') { if(-not[IO.Path]::IsPathFullyQualified([string]$profileDocument.databaseFile) -or -not(Test-Path -LiteralPath $profileDocument.databaseFile -PathType Leaf)){throw 'Owned SQLite provider file is missing; no provider substitution is allowed.'} }
    elseif([string]::IsNullOrWhiteSpace([string]$profileDocument.instanceName) -or [string]::IsNullOrWhiteSpace([string]$profileDocument.databaseName)){throw 'Owned LocalDB provider identity is incomplete.'}
    foreach($tuple in @(@($BaseUri,[string]$profileDocument.sourceEndpoints.web),@($PrtgFixtureUri,[string]$profileDocument.sourceEndpoints.prtg),@($SentinelFixtureUri,[string]$profileDocument.sourceEndpoints.sentinel))) {
        $expected=[uri]$tuple[0];$saved=[uri]$tuple[1];Assert-Loopback $expected 'Explicit fixture endpoint';Assert-Loopback $saved 'Saved source endpoint'
        if($saved.AbsoluteUri.TrimEnd('/') -cne $expected.AbsoluteUri.TrimEnd('/')){throw 'Saved Web/PRTG/Sentinel source endpoint does not match its explicit loopback fixture.'}
    }
    $hostIds=@(Get-Content -LiteralPath $hostIdsPath|ForEach-Object{[long]::Parse($_,[Globalization.CultureInfo]::InvariantCulture)})
    $hostHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($hostIds -join "`n"))))
    if($hostIds.Count -ne 3000 -or @($hostIds|Sort-Object -Unique).Count -ne 3000 -or $hostHash -cne $profileDocument.hostIdsSha256){throw 'Owned full-scope host ID list differs from its recorded provider/profile fence.'}
    return [pscustomobject]@{owner=$owner;profile=$profileDocument;preflight=$preflight;hostIdsPath=$hostIdsPath;profilePath=$profilePath;dataRoot=$dataRoot;provider=$provider}
}
Assert-Loopback $BaseUri 'Web fixture URL';Assert-Loopback $PrtgFixtureUri 'PRTG fixture URL';Assert-Loopback $SentinelFixtureUri 'Sentinel fixture URL'
$matrix=Assert-OwnedMatrixContext
$candidatePrefix=$candidateRoot.TrimEnd('\')+'\'
$matrixRootFull=[IO.Path]::GetFullPath($MatrixRunDirectory);$evidenceRootFull=[IO.Path]::GetFullPath($EvidenceRoot);$assemblyFull=[IO.Path]::GetFullPath($CoverageAssemblyPath)
foreach($path in @($matrixRootFull,$evidenceRootFull,$assemblyFull)){if(-not$path.StartsWith($candidatePrefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Matrix outputs and the coverage verifier must remain inside this isolated candidate.'}}
if(-not$evidenceRootFull.StartsWith($matrixRootFull.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'EvidenceRoot must be a child of MatrixRunDirectory so the sealed coverage receipt remains inside the owned run.'}
Assert-NoReparseAncestor $evidenceRootFull 'Evidence root';Assert-NoReparseAncestor $assemblyFull 'Coverage verifier assembly'
if(-not(Test-Path -LiteralPath $assemblyFull -PathType Leaf)){throw 'The compiled QualificationCoverage verifier assembly is missing.'}
$manifestFull=[IO.Path]::GetFullPath($ManifestPath);if(-not$manifestFull.StartsWith($candidateRoot.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Qualification manifest must be inside this isolated candidate.'};Assert-NoReparseAncestor $manifestFull 'Qualification manifest';Assert-NoReparseAncestor ([string]$matrix.profile.providerDescriptorPath) 'Provider descriptor'
$manifestBytes=[IO.File]::ReadAllBytes($manifestFull);if($manifestBytes.Length -gt 8MB){throw 'Qualification manifest exceeds the coordinator 8 MiB limit.'}
$manifest=Read-JsonFile $manifestFull;$sensorIds=@($manifest.sensorIds|ForEach-Object{[long]$_})
$sensorHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($sensorIds -join "`n"))))
if($manifest.schema -cne 'qualification-job-manifest-v1' -or $sensorIds.Count -ne 15000 -or @($sensorIds|Sort-Object -Unique).Count -ne 15000 -or
   $sensorHash -cne $matrix.profile.sensorIdsSha256 -or $manifest.policyRevision -cne $matrix.profile.policyRevision -or $manifest.sourceGeneration -cne $matrix.profile.sourceGeneration){throw 'Qualification manifest does not represent the exact saved 15,000-sensor provider profile.'}
$coordPath=Join-Path $PSScriptRoot 'Invoke-QualificationJob.ps1';$collectorPath=Join-Path $PSScriptRoot 'Read-QualificationCoverage.ps1';$consumerPath=Join-Path $PSScriptRoot 'Invoke-QualificationMatrixConsumer.ps1'
if(-not(Test-Path -LiteralPath $coordPath -PathType Leaf) -or -not(Test-Path -LiteralPath $collectorPath -PathType Leaf) -or -not(Test-Path -LiteralPath $consumerPath -PathType Leaf)){throw 'Canonical coordinator, exact coverage collector, or delivered public-Web consumer is missing.'}
$receiptDir=Join-Path $matrixRoot 'qualification-lifecycle';$jobReceipt=Join-Path $receiptDir 'qualification-job-receipt.json';$gateReceipt=Join-Path $receiptDir 'qualification-matrix-gate.json'
if($Action -in @('Start','Resume','Check')){$null=New-Item -ItemType Directory -Path $receiptDir -Force}
if($Action -in @('Start','Resume','Check')) {
    if($Action -eq 'Check' -and $ConditionIndex -ne 0){throw 'Check only verifies the durable job and coverage gate; use a separate Matrix action for each condition.'}
    if($Action -ne 'Check' -and $ConditionIndex -ne 0){throw 'Start and Resume cannot start a matrix condition; complete Check and coverage first.'}
    $arguments=@{Action=$Action;BaseUri=$BaseUri;PrtgFixtureUri=$PrtgFixtureUri;SentinelFixtureUri=$SentinelFixtureUri;ManifestPath=$manifestFull;ReceiptPath=$jobReceipt;WebSession=$WebSession;MaxSeconds=$MaxSeconds;PollSeconds=$PollSeconds}
    if($Action -in @('Resume','Check')){$arguments.JobId=$JobId;$arguments.ExpectedVersion=$ExpectedVersion;$arguments.ExpectedWave=$ExpectedWave}
    $job=& $coordPath @arguments
    if($job.status -cne 'completed'){[pscustomobject]@{status='qualification-pending-explicit-action-required';jobReceipt=$jobReceipt;job=$job;matrixStarted=$false;nativeSourceVerified=$false;capacityAccepted=$false;retentionAccepted=$false;wholeRoundAccepted=$false};return}
    $matrixCheck=@{Action='Check';BaseUri=$BaseUri;PrtgFixtureUri=$PrtgFixtureUri;SentinelFixtureUri=$SentinelFixtureUri;ManifestPath=$manifestFull;ReceiptPath=$jobReceipt;WebSession=$WebSession;JobId=$job.jobId;ExpectedVersion=$job.version;ExpectedWave=$job.wave;MaxSeconds=$MaxSeconds;PollSeconds=$PollSeconds;RequireMatrixReady=$true}
    $job=& $coordPath @matrixCheck
    if($job.status -cne 'completed'){throw 'Durable qualification job changed before the completed all-sensor matrix gate.'}
    $coverageManifest=Join-Path $receiptDir 'coverage-manifest.json';$coverageDirectory=Join-Path $EvidenceRoot ('coverage-'+[guid]::NewGuid().ToString('N'))
    $coverageShape=[ordered]@{jobId=$job.jobId;sensorIds=$sensorIds;scopeFingerprint=$manifest.scopeFingerprint;settingsRevision=$manifest.settingsRevision;policyRevision=$manifest.policyRevision;sourceGeneration=$manifest.sourceGeneration;authorityContextFingerprint=$manifest.authorityContextFingerprint}
    Write-JsonAtomic $coverageManifest $coverageShape
    $coverage=& $collectorPath -BaseUri $BaseUri -ManifestPath $coverageManifest -AssemblyPath $CoverageAssemblyPath -OutputDirectory $coverageDirectory -WebSession $WebSession -MaxSeconds $MaxSeconds
    if(-not$coverage.ready -or $coverage.expectedSensors -ne 15000 -or $coverage.qualifiedSensors -ne 15000 -or $coverage.readyProfiles -ne 15000){throw "Exact all-sensor raw/profile coverage was refused or incomplete ($($coverage.reason)); no matrix condition was started."}
    $preflightPath=Join-Path $matrixRoot 'full-consumer-authority-preflight.json';$qualifiedPreflight=Read-JsonFile $preflightPath
    if($qualifiedPreflight.status -notin @('owned-fixture-declared-awaiting-qualification','current-qualified-proofs') -or $qualifiedPreflight.manifestSha256 -cne (Get-Sha256 $manifestFull) -or
       $qualifiedPreflight.hostIdsSha256 -cne $matrix.profile.hostIdsSha256 -or $qualifiedPreflight.settingsRevision -cne $manifest.settingsRevision){throw 'Operator-declared preflight changed while qualification was being checked.'}
    $qualifiedPreflight.status='current-qualified-proofs';$qualifiedPreflight.qualificationJobId=$job.jobId;$qualifiedPreflight.qualificationJobVersion=$job.version;$qualifiedPreflight.qualificationJobWave=$job.wave
    $qualifiedPreflight.coverageReceiptPath=[IO.Path]::GetFullPath((Join-Path $coverageDirectory 'coverage-receipt.json'));$qualifiedPreflight.coverageReceiptSha256=Get-Sha256 $qualifiedPreflight.coverageReceiptPath
    $qualifiedPreflight.qualifiedSensors=15000;$qualifiedPreflight.readyProfiles=15000;$qualifiedPreflight.qualificationObservedUtc=[DateTimeOffset]::UtcNow.ToString('o')
    $qualifiedPreflight.nativeSourceVerified=$false;$qualifiedPreflight.formalAcceptance=$false;$qualifiedPreflight.wholeRoundAccepted=$false
    Write-JsonAtomic $preflightPath $qualifiedPreflight
    $gate=[ordered]@{schema='prtg-qualified-matrix-gate-v1';status='ready-for-fixed-matrix';profile=$Profile;hostCount=3000;selectedSensorCount=15000;retentionDays=180;provider=$matrix.provider;runId=$matrix.owner.runId;jobId=$job.jobId;jobVersion=$job.version;jobWave=$job.wave;manifestSha256=(Get-Sha256 $manifestFull);coverageManifestSha256=(Get-Sha256 $coverageManifest);coverageManifestPath=[IO.Path]::GetFullPath($coverageManifest);coverageEvidenceDirectory=[IO.Path]::GetFullPath($coverageDirectory);coverage=$coverage;conditionOrder=@('netiq-only','combined','combined','netiq-only','netiq-only','combined');nativeSourceVerified=$false;capacityAccepted=$false;retentionAccepted=$false;wholeRoundAccepted=$false}
    Write-JsonAtomic $gateReceipt $gate
    if($ConditionIndex -eq 0){[pscustomobject]$gate;return}
} else {
    if($ConditionIndex -lt 1 -or -not(Test-Path -LiteralPath $gateReceipt -PathType Leaf)){throw 'Matrix requires a sealed completed-job and exact coverage gate; invoke Check first.'}
    $gate=Read-JsonFile $gateReceipt
    if($matrix.preflight.status -cne 'current-qualified-proofs' -or $matrix.preflight.qualificationJobId -cne $gate.jobId -or
       $matrix.preflight.qualifiedSensors -ne 15000 -or $matrix.preflight.readyProfiles -ne 15000 -or
       -not(Test-Path -LiteralPath ([string]$matrix.preflight.coverageReceiptPath) -PathType Leaf) -or $matrix.preflight.coverageReceiptSha256 -cne (Get-Sha256 ([string]$matrix.preflight.coverageReceiptPath)) -or
       $matrix.preflight.coverageReceiptPath -cnotlike (([IO.Path]::GetFullPath($matrixRoot).TrimEnd('\')+'\*')) -or
       $gate.schema -cne 'prtg-qualified-matrix-gate-v1' -or $gate.status -cne 'ready-for-fixed-matrix' -or $gate.profile -cne $Profile -or $gate.runId -cne $matrix.owner.runId -or
       $gate.manifestSha256 -cne (Get-Sha256 $manifestFull) -or $gate.provider -cne $matrix.provider -or $gate.hostCount -ne 3000 -or $gate.selectedSensorCount -ne 15000 -or $gate.retentionDays -ne 180 -or
       $gate.nativeSourceVerified -or $gate.capacityAccepted -or $gate.retentionAccepted -or $gate.wholeRoundAccepted -or -not$gate.coverage.ready -or [IO.Path]::GetFullPath([string]$gate.coverageManifestPath) -cne [IO.Path]::GetFullPath((Join-Path $receiptDir 'coverage-manifest.json')) -or -not(Test-Path -LiteralPath $gate.coverageManifestPath -PathType Leaf) -or $gate.coverageManifestSha256 -cne (Get-Sha256 $gate.coverageManifestPath)){throw 'Sealed qualification gate is stale, incomplete, or contains an unauthorized acceptance promotion.'}
}
if($ConditionIndex -lt 1 -or $ConditionIndex -gt 6){throw 'A ready gate is required; ConditionIndex must select exactly one fixed matrix cell (1..6).'}
$checkpointPath=Join-Path $matrixRoot 'qualified-matrix-checkpoint.json';$conditions=@('netiq-only','combined','combined','netiq-only','netiq-only','combined')
$expectedSettingsRevision=[string]$matrix.preflight.settingsRevision
$completed=0;if(Test-Path -LiteralPath $checkpointPath){
    $checkpoint=Read-JsonFile $checkpointPath
    if($checkpoint.schema -cne 'prtg-qualified-matrix-checkpoint-v1' -or $checkpoint.runId -cne $matrix.owner.runId -or $checkpoint.profile -cne $Profile -or $checkpoint.manifestSha256 -cne (Get-Sha256 $manifestFull) -or $checkpoint.jobId -cne $gate.jobId -or ($checkpoint.conditionOrder -join ',') -cne ($conditions -join ',') -or [int]$checkpoint.completedConditions -lt 1 -or [int]$checkpoint.completedConditions -gt 6){throw 'Matrix checkpoint belongs to another run/profile/job or fixed condition order.'}
    if([string]::IsNullOrWhiteSpace([string]$checkpoint.settingsRevision)){throw 'Matrix checkpoint omitted the continuous Settings revision.'};$expectedSettingsRevision=[string]$checkpoint.settingsRevision
    $completed=[int]$checkpoint.completedConditions
    for($priorIndex=1;$priorIndex -le $completed;$priorIndex++){$priorCondition=$conditions[$priorIndex-1];$priorOwnerPath=Join-Path $matrixRoot ("condition-{0:D2}-{1}\condition-owner.json" -f $priorIndex,$priorCondition);if(-not(Test-Path -LiteralPath $priorOwnerPath -PathType Leaf)){throw "Completed checkpoint cell $priorIndex has no saved owner receipt."};$priorOwner=Read-JsonFile $priorOwnerPath;if($priorOwner.schema -cne 'round53-condition-attempt-v1' -or $priorOwner.runId -cne $matrix.owner.runId -or $priorOwner.profile -cne $Profile -or $priorOwner.conditionIndex -ne $priorIndex -or $priorOwner.condition -cne $priorCondition -or $priorOwner.state -cne 'complete'){throw "Completed checkpoint cell $priorIndex is missing a matching complete owner receipt for this run."}}
}
if($ConditionIndex -ne $completed+1){throw "Fixed matrix sequencing requires condition $($completed+1) next; no condition is skipped or replayed."}
$condition=$conditions[$ConditionIndex-1];$conditionDir=Join-Path $matrixRoot ("condition-{0:D2}-{1}" -f $ConditionIndex,$condition)
if(Test-Path -LiteralPath $conditionDir){throw 'Condition directory already exists; preserve it for review rather than overwriting an existing consumer attempt.'}
$null=New-Item -ItemType Directory -Path $conditionDir
Copy-Item -LiteralPath $matrix.profilePath -Destination (Join-Path $conditionDir 'round53-profile.json')
$conditionOwner=[ordered]@{schema='round53-condition-attempt-v1';runId=$matrix.owner.runId;profile=$Profile;conditionIndex=$ConditionIndex;condition=$condition;attemptId=[guid]::NewGuid().ToString('N');state='running';startedUtc=[DateTimeOffset]::UtcNow.ToString('o');nativeSourceVerified=$false;formalAcceptance=$false;wholeRoundAccepted=$false;n6Overall=$false}
Write-JsonAtomic (Join-Path $conditionDir 'condition-owner.json') $conditionOwner
$null=New-Item -ItemType Directory -Path $conditionDir -Force
try {
    $cellStage='set-condition'
    $conditionSettings=& $consumerPath -Action SetCondition -BaseUri $BaseUri -WebSession $WebSession -OutputDirectory $conditionDir -Condition $condition -ExpectedSettingsRevision $expectedSettingsRevision -MaxSeconds $MatrixCommandMaxSeconds
    if([string]::IsNullOrWhiteSpace([string]$conditionSettings.settingsRevision)){throw 'Settings condition step returned no live revision.'}
    $cellStage='pre-measurement-fence'
    $fencePath=Join-Path $PSScriptRoot 'Test-LiveQualificationFence.ps1';if(-not(Test-Path -LiteralPath $fencePath -PathType Leaf)){throw 'Per-cell live qualification fence script is missing.'}
    $fenceReceipt=& $fencePath -BaseUri $BaseUri -PrtgFixtureUri $PrtgFixtureUri -WebSession $WebSession -ManifestPath $manifestFull -ProofFencePath (Join-Path $matrixRoot 'matrix-proof-fence.json') -JobId $gate.jobId -JobVersion $gate.jobVersion -JobWave $gate.jobWave -ExpectedPrtgEnabled ($condition -ceq 'combined') -MaxSeconds $MatrixCommandMaxSeconds
    if($fenceReceipt.settingsRevision -cne $conditionSettings.settingsRevision){throw 'Pre-measurement fence revision differs from the CAS result.'}
    if($fenceReceipt.status -cne 'current' -or $fenceReceipt.jobId -cne $gate.jobId -or $fenceReceipt.jobVersion -ne $gate.jobVersion -or $fenceReceipt.jobWave -ne $gate.jobWave -or $fenceReceipt.rawRows -ne 15000 -or $fenceReceipt.profileRows -ne 15000 -or [string]::IsNullOrWhiteSpace([string]$fenceReceipt.proofFenceSha256)){throw 'Live current profile/job/source fence did not match the sealed 15,000-row gate.'}
    Write-JsonAtomic (Join-Path $conditionDir 'live-qualification-fence.json') $fenceReceipt
    $cellStage='schedule'
    & $consumerPath -Action Schedule -BaseUri $BaseUri -WebSession $WebSession -OutputDirectory $conditionDir -ProfilePath $matrix.profilePath -HostIdsPath $matrix.hostIdsPath -MaxSeconds $MatrixCommandMaxSeconds | Out-Null
    if(-not(Test-Path -LiteralPath (Join-Path $conditionDir 'schedule-complete.json') -PathType Leaf)){throw 'Existing schedule consumer returned without its completion receipt.'}
    $cellStage='route-measurement'
    $conditionHostIds=Join-Path $conditionDir 'host-ids.txt';Copy-Item -LiteralPath $matrix.hostIdsPath -Destination $conditionHostIds
    & $consumerPath -Action MeasureRoutes -BaseUri $BaseUri -WebSession $WebSession -OutputDirectory $conditionDir -HostIdsPath $conditionHostIds -SamplesPerRoute 100 -RequestDeadlineMs 2000 -MaxSeconds $MatrixCommandMaxSeconds | Out-Null
    if(-not(Test-Path -LiteralPath (Join-Path $conditionDir 'route-measurement.json') -PathType Leaf)){throw 'Existing route consumer returned without its bounded measurement receipt.'}
    $cellStage='post-measurement-fence'
    $finalFence=& $fencePath -BaseUri $BaseUri -PrtgFixtureUri $PrtgFixtureUri -WebSession $WebSession -ManifestPath $manifestFull -ProofFencePath (Join-Path $matrixRoot 'matrix-proof-fence.json') -JobId $gate.jobId -JobVersion $gate.jobVersion -JobWave $gate.jobWave -ExpectedPrtgEnabled ($condition -ceq 'combined') -MaxSeconds $MatrixCommandMaxSeconds
    if($finalFence.settingsRevision -cne $fenceReceipt.settingsRevision){throw 'Settings revision changed during this cell; the cell is not sealed.'}
    if($finalFence.status -cne 'current' -or $finalFence.jobId -cne $gate.jobId -or $finalFence.jobVersion -ne $gate.jobVersion -or $finalFence.jobWave -ne $gate.jobWave -or $finalFence.rawRows -ne 15000 -or $finalFence.profileRows -ne 15000 -or $finalFence.proofFenceSha256 -cne $fenceReceipt.proofFenceSha256){throw 'Final post-schedule/route live fence changed during this matrix cell; the cell is not sealed.'}
    Write-JsonAtomic (Join-Path $conditionDir 'live-qualification-fence-final.json') $finalFence
    $conditionOwner.state='complete';$conditionOwner.completedUtc=[DateTimeOffset]::UtcNow.ToString('o');Write-JsonAtomic (Join-Path $conditionDir 'condition-owner.json') $conditionOwner
} catch {
    $conditionOwner.state='failed';$conditionOwner.failureStage=$cellStage;$conditionOwner.failureReasonCode='bounded-cell-stage-failed';$conditionOwner.completedUtc=[DateTimeOffset]::UtcNow.ToString('o')
    Write-JsonAtomic (Join-Path $conditionDir 'condition-owner.json') $conditionOwner
    throw "Matrix cell $ConditionIndex failed during '$cellStage'; its attempt is preserved as failed and the sequence checkpoint was not advanced. Start a fresh initialized owned run after rechecking the retained qualification job."
}
$completed=$ConditionIndex
$checkpoint=[ordered]@{schema='prtg-qualified-matrix-checkpoint-v1';runId=$matrix.owner.runId;profile=$Profile;jobId=$gate.jobId;manifestSha256=(Get-Sha256 $manifestFull);settingsRevision=$finalFence.settingsRevision;completedConditions=$completed;updatedUtc=[DateTimeOffset]::UtcNow.ToString('o');conditionOrder=$conditions;nativeSourceVerified=$false;capacityAccepted=$false;retentionAccepted=$false;wholeRoundAccepted=$false}
Write-JsonAtomic $checkpointPath $checkpoint
if($completed -eq 6){& $consumerPath -Action Evaluate -BaseUri $BaseUri -WebSession $WebSession -OutputDirectory $matrixRoot -MatrixRunDirectory $matrixRoot -ProfileName $Profile -ExpectedRunId ([string]$matrix.owner.runId) | Out-Null;$evaluationPath=Join-Path $matrixRoot 'matrix-evaluation.json';$evaluation=Read-JsonFile $evaluationPath
    $result=[ordered]@{schema='prtg-qualified-fixed-matrix-result-v1';status=if($evaluation.metricChecksPass -and $evaluation.observableSubchecksPass){'metrics-pass-source-and-retention-unqualified'}else{'metrics-failed'};matrixEvaluation=$evaluationPath;qualificationGate=$gateReceipt;conditionOrder=$conditions;nativeSourceVerified=$false;capacityAccepted=$false;retentionAccepted=$false;wholeRoundAccepted=$false}
    Write-JsonAtomic (Join-Path $matrixRoot 'qualified-matrix-result.json') $result
    [pscustomobject]$result
} else {[pscustomobject]@{status='matrix-cell-complete-next-fixed-cell-required';completedConditions=$completed;nextConditionIndex=$completed+1;matrixStarted=$true;nativeSourceVerified=$false;capacityAccepted=$false;retentionAccepted=$false;wholeRoundAccepted=$false}}
