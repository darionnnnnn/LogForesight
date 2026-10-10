#Requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$scriptPath=Join-Path $PSScriptRoot 'Invoke-QualificationJob.ps1'
$fixturePath=Join-Path $PSScriptRoot 'qualification_job_fixture.py'
$python=(Get-Command python -ErrorAction Stop).Source
$tempRoot=Join-Path ([IO.Path]::GetTempPath()) ('prtg-qualification-job-tests-'+[guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $tempRoot
$scope='a'*64;$authority='b'*64;$manifestPath=Join-Path $tempRoot 'manifest.json'
$fences=@(@{sensorObjid=1001;identityEpoch=1;channelGeneration='channel-1';bindingRevision=1;bindingFingerprint=('c'*64)},@{sensorObjid=1002;identityEpoch=1;channelGeneration='channel-2';bindingRevision=1;bindingFingerprint=('d'*64)})
@{schema='qualification-job-manifest-v1';sensorIds=@(1001,1002);sensorFences=$fences;settingsRevision='settings-r1';policyRevision='policy-r1';scopeFingerprint=$scope;sourceGeneration='source-r1';authorityContextFingerprint=$authority}|ConvertTo-Json -Depth 8|Set-Content -Encoding utf8 $manifestPath
function Get-FreePort { $l=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0);$l.Start();$p=([Net.IPEndPoint]$l.LocalEndpoint).Port;$l.Stop();$p }
function Invoke-Scenario([string]$scenario,[string]$action='Start',[long]$expectedVersion=0,[int]$expectedWave=0,[bool]$expectSuccess=$false,[bool]$expectMutation=$false,[int]$maxSeconds=5,[bool]$requireMatrix=$false,[string]$manifestOverride='') {
    $port=Get-FreePort;$requestLog=Join-Path $tempRoot "$scenario.requests.jsonl";$stdout=Join-Path $tempRoot "$scenario.out";$stderr=Join-Path $tempRoot "$scenario.err";$receipt=Join-Path $tempRoot "$scenario.receipt.json"
    $psi=[Diagnostics.ProcessStartInfo]::new();$psi.FileName=$python;$psi.UseShellExecute=$false;$psi.CreateNoWindow=$true;$psi.RedirectStandardOutput=$true;$psi.RedirectStandardError=$true
    foreach($arg in @($fixturePath,'--port',[string]$port,'--scenario',$scenario,'--request-log',$requestLog)){$psi.ArgumentList.Add($arg)}
    $server=[Diagnostics.Process]::Start($psi);$serverOut=$server.StandardOutput.ReadToEndAsync();$serverErr=$server.StandardError.ReadToEndAsync()
    try {
        $ready=$false;for($i=0;$i -lt 100;$i++){try{$tcp=[Net.Sockets.TcpClient]::new();$tcp.Connect('127.0.0.1',$port);$tcp.Close();$ready=$true;break}catch{Start-Sleep -Milliseconds 50}}
        if(-not $ready){throw "Python fixture failed to bind for ${scenario}: $($serverErr.Result)"}
        $session=New-Object Microsoft.PowerShell.Commands.WebRequestSession;$failed=$false;$manifestUse=if($manifestOverride){$manifestOverride}else{$manifestPath};$scriptArgs=@{Action=$action;BaseUri="http://127.0.0.1:$port/app/";PrtgFixtureUri="http://127.0.0.1:$port/prtg";SentinelFixtureUri="http://127.0.0.1:$port/sentinel";ManifestPath=$manifestUse;ReceiptPath=$receipt;WebSession=$session;MaxSeconds=$maxSeconds;PollSeconds=1}
        if($action -in @('Resume','Check')){$scriptArgs.JobId='0123456789abcdef0123456789abcdef';$scriptArgs.ExpectedVersion=$expectedVersion;$scriptArgs.ExpectedWave=$expectedWave}
        if($requireMatrix){$scriptArgs.RequireMatrixReady=$true}
        if($requireMatrix){$scriptArgs.RequireMatrixReady=$true}
        $failure='';$clock=[Diagnostics.Stopwatch]::StartNew();try { & $scriptPath @scriptArgs | Out-Null } catch {$failed=$true;$failure=$_.Exception.Message};$clock.Stop()
        if($expectSuccess -eq $failed){if(Test-Path $requestLog){Write-Host (Get-Content $requestLog -Raw)};throw "Unexpected coordinator result for fixture scenario '$scenario': $failure"}
        if($expectSuccess){if(-not(Test-Path $receipt)){throw "Successful scenario '$scenario' did not persist a durable receipt."};$receiptValue=Get-Content $receipt -Raw|ConvertFrom-Json;if($receiptValue.schema -cne 'prtg-qualification-job-receipt-v1' -or $receiptValue.nativeSourceVerified -or $receiptValue.capacityAccepted -or $receiptValue.retentionAccepted -or $receiptValue.wholeRoundAccepted){throw "Receipt schema or conservative flags are invalid for '$scenario'."};if($scenario -eq 'active-start' -and ($receiptValue.pollDeadlineReached -ne $true -or $receiptValue.status -notin @('initializing','running','waiting-capacity'))){throw 'Active accepted progress must persist pollDeadlineReached=true while the durable job remains active.'};if($scenario -in @('ready-start','unqualified','version-progress','resume') -and $receiptValue.pollDeadlineReached -ne $false){throw "Non-deadline progress was incorrectly marked pollDeadlineReached for '$scenario'."};$expectedCount=if($scenario -eq 'large-15000'){15000}else{2};if($receiptValue.selectedSensors -ne $expectedCount){throw "Receipt selected-sensor count is invalid for '$scenario'."};$manifestHash=(Get-FileHash $manifestUse -Algorithm SHA256).Hash;if($receiptValue.manifestSha256 -cne $manifestHash){throw "Receipt manifest hash is invalid for '$scenario'."}}
        $requests=@();if(Test-Path $requestLog){$requests=@(Get-Content $requestLog|ForEach-Object{ConvertFrom-Json $_})}
        $mutated=@($requests|Where-Object{$_.path.EndsWith('/start') -or $_.path.EndsWith('/resume')}).Count -gt 0
        if($mutated -ne $expectMutation){throw "Unexpected mutation-call count for fixture scenario '$scenario'."}
        if($expectMutation -and (Test-Path $receipt)){$receiptValue=Get-Content $receipt -Raw|ConvertFrom-Json;if($receiptValue.schema -cne 'prtg-qualification-job-receipt-v1' -or $receiptValue.nativeSourceVerified -or $receiptValue.capacityAccepted -or $receiptValue.retentionAccepted -or $receiptValue.wholeRoundAccepted){throw "Durable accepted-job receipt was lost or promoted for '$scenario'."};if($scenario -eq 'wave-drift' -and $receiptValue.wave -ne 3){throw 'Wave drift overwrote the accepted-wave receipt.'};if($scenario -in @('version-progress','resume') -and $receiptValue.version -lt 5){throw "Accepted stable-wave job version progress was not persisted for '$scenario'."}}
        if($scenario -eq 'active-start' -and $clock.Elapsed.TotalSeconds -gt 8){throw 'Active Start exceeded its whole-invocation deadline instead of returning accepted progress.'}
        if($action -eq 'Resume' -and $expectMutation){$post=$requests|Where-Object{$_.path.EndsWith('/resume')}|Select-Object -First 1;$body=$post.body|ConvertFrom-Json;if($body.expectedVersion -ne $expectedVersion){throw 'Resume version CAS payload mismatch.'}}
    } finally { if(-not $server.HasExited){$server.Kill($true);$server.WaitForExit(3000)|Out-Null};$server.Dispose() }
}
try {
    $session=New-Object Microsoft.PowerShell.Commands.WebRequestSession;$remoteRejected=$false
    try { & $scriptPath -Action Check -BaseUri 'https://example.com/' -PrtgFixtureUri 'http://127.0.0.1:8001/prtg' -SentinelFixtureUri 'http://127.0.0.1:8002/sentinel' -ManifestPath $manifestPath -ReceiptPath (Join-Path $tempRoot 'remote.json') -WebSession $session } catch {$remoteRejected=$_.Exception.Message -match 'numeric-loopback'}
    if(-not $remoteRejected){throw 'Remote endpoint was not rejected before connecting.'}
    Invoke-Scenario 'ready-start' 'Start' 0 0 $true $true
    Invoke-Scenario 'unqualified' 'Start' 0 0 $true $true
    Invoke-Scenario 'active-start' 'Start' 0 0 $true $true 2
    Invoke-Scenario 'wave-drift' 'Start' 0 0 $false $true
    Invoke-Scenario 'version-progress' 'Start' 0 0 $true $true
    Invoke-Scenario 'resume' 'Resume' 3 2 $true $true
    Invoke-Scenario 'stale-version' 'Resume' 9 2 $false $false
    Invoke-Scenario 'stale-wave' 'Resume' 3 9 $false $false
    Invoke-Scenario 'job-context-drift' 'Resume' 3 2 $false $false
    Invoke-Scenario 'context-drift' 'Start' 0 0 $false $false
    Invoke-Scenario 'missing-binding' 'Start' 0 0 $false $false
    Invoke-Scenario 'duplicate-id' 'Start' 0 0 $false $false
    Invoke-Scenario 'partial-id' 'Start' 0 0 $false $false
    Invoke-Scenario 'incomplete' 'Start' 0 0 $false $false
    Invoke-Scenario 'no-pilot' 'Start' 0 0 $false $false
    Invoke-Scenario 'redirect' 'Start' 0 0 $false $false
    Invoke-Scenario 'unauthorized' 'Start' 0 0 $false $false
    Invoke-Scenario 'mime' 'Start' 0 0 $false $false
    Invoke-Scenario 'oversize' 'Start' 0 0 $false $false
    Invoke-Scenario 'slow' 'Start' 0 0 $false $false 1
    Invoke-Scenario 'matrix-ready' 'Check' 4 3 $true $false 5 $true
    Invoke-Scenario 'matrix-incomplete' 'Check' 4 3 $false $false 5 $true
    $largeManifest=Join-Path $tempRoot 'manifest-15000.json';$largeFences=[Collections.Generic.List[object]]::new();for($id=1;$id -le 15000;$id++){$largeFences.Add(@{sensorObjid=$id;identityEpoch=1;channelGeneration="channel-$id";bindingRevision=1;bindingFingerprint=('c'*64)})};@{schema='qualification-job-manifest-v1';sensorIds=@(1..15000);sensorFences=$largeFences.ToArray();settingsRevision='settings-r1';policyRevision='policy-r1';scopeFingerprint=$scope;sourceGeneration='source-r1';authorityContextFingerprint=$authority}|ConvertTo-Json -Depth 8 -Compress|Set-Content -Encoding utf8 $largeManifest;$largeBytes=(Get-Item $largeManifest).Length;if($largeBytes -le 512KB -or $largeBytes -gt 8MB){throw "15,000-sensor manifest counterexample size outside expected range: $largeBytes bytes."};Invoke-Scenario 'large-15000' 'Start' 0 0 $true $true 120 $false $largeManifest
    'PASS: active Start persists on whole-deadline; pinned wave rejects concurrent resume; stable-wave version progress and explicit Resume; Start-before-raw-proof; stale CAS/context; identity drift; duplicate/partial IDs; missing bindings/profiles; capacity pilot; completed/matrix proof gate; 15,000-sensor manifest; loopback endpoint fence; remote URL; redirects; 401; MIME; byte cap; invocation cancellation.'
} finally {
    $expectedTempParent=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar,[IO.Path]::AltDirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar
    $resolvedTempRoot=[IO.Path]::GetFullPath($tempRoot)
    if($resolvedTempRoot.StartsWith($expectedTempParent,[StringComparison]::OrdinalIgnoreCase) -and
       (Split-Path -Leaf $resolvedTempRoot) -match '^prtg-qualification-job-tests-[0-9a-f]{32}$' -and
       (Get-Item -LiteralPath $resolvedTempRoot -ErrorAction SilentlyContinue).Attributes -notmatch 'ReparsePoint') {
        $reparse=Get-ChildItem -LiteralPath $resolvedTempRoot -Force -Recurse -ErrorAction Stop|Where-Object{$_.Attributes -band [IO.FileAttributes]::ReparsePoint}|Select-Object -First 1
        if(-not $reparse){Remove-Item -LiteralPath $resolvedTempRoot -Recurse -Force}
    } else { throw 'Refusing to clean a test directory outside the verified task-specific temp subtree.' }
}
