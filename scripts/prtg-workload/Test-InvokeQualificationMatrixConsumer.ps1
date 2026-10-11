#Requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$fixture=Join-Path $PSScriptRoot 'matrix_consumer_fixture.py'
$consumer=Join-Path $PSScriptRoot 'Invoke-QualificationMatrixConsumer.ps1'
$taskRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot ('matrix-consumer-contract-'+[guid]::NewGuid().ToString('N'))))
$expectedParent=[IO.Path]::GetFullPath($PSScriptRoot)
$processes=[Collections.Generic.List[Diagnostics.Process]]::new()
$portFiles=[Collections.Generic.List[string]]::new()
$fixtureLogRoot=[IO.Path]::GetFullPath((Join-Path (Join-Path $PSScriptRoot '..\..') '.gemini-tasks\primary-results\matrix-consumer-fixtures'))
$null=New-Item -ItemType Directory -Path $fixtureLogRoot -Force
$fixtureRunId=[guid]::NewGuid().ToString('N')
$passed=0
function Assert([bool]$condition,[string]$message){if(-not$condition){throw $message};$script:passed++}
function New-RouteReceipt {
    @{schema='round53-route-measurement-v1';requestedPerRoute=100;requests=200;status='measured';routes=@(foreach($name in @('host-list','host-detail')){
        @{route=$name;denominator=100;successes=100;failures=0;p95Ms=100;maxMs=100;deadlineMs=2000;samples=@(1..100|ForEach-Object{@{route=$name;statusCode=200;elapsedMs=100}})}
    })}
}
function Start-Fixture([string]$Mode='ok'){
    $portFile=Join-Path ([IO.Path]::GetTempPath()) ('lf-matrix-fixture-port-'+[guid]::NewGuid().ToString('N')+'.txt')
    $script:portFiles.Add($portFile)
    $errorFile=Join-Path $fixtureLogRoot ("consumer-fixture-$fixtureRunId-$Mode.stderr.log")
    $outputFile=Join-Path $fixtureLogRoot ("consumer-fixture-$fixtureRunId-$Mode.stdout.log")
    $argumentLine='"{0}" --port 0 --port-file "{1}" --mode "{2}"' -f $fixture,$portFile,$Mode
    $proc=Start-Process -FilePath 'python' -ArgumentList $argumentLine -WindowStyle Hidden -PassThru -RedirectStandardError $errorFile -RedirectStandardOutput $outputFile
    $script:processes.Add($proc)
    $portUntil=[DateTime]::UtcNow.AddSeconds(10)
    while(-not(Test-Path -LiteralPath $portFile) -and [DateTime]::UtcNow -lt $portUntil){if($proc.HasExited){$errorText=if(Test-Path -LiteralPath $errorFile){Get-Content -LiteralPath $errorFile -Raw}else{''};throw "Owned matrix fixture exited during startup with code $($proc.ExitCode): $errorText"};Start-Sleep -Milliseconds 20}
    if(-not(Test-Path -LiteralPath $portFile)){throw 'Owned matrix fixture did not publish its bound port within 10 seconds.'}
    $port=[int](Get-Content -LiteralPath $portFile -Raw)
    $uri=[uri]"http://127.0.0.1:$port/"
    $ready=$false;$lastReadyError='';$until=[DateTime]::UtcNow.AddSeconds(30)
    while([DateTime]::UtcNow -lt $until){try{$response=Invoke-WebRequest -Uri ([uri]::new($uri,'health')) -TimeoutSec 1 -UseBasicParsing;$ready=$response.StatusCode -eq 200;if($ready){break}}catch{$lastReadyError=$_.Exception.Message};Start-Sleep -Milliseconds 50}
    if(-not$ready){throw "Owned matrix fixture did not become ready within the 30-second startup bound: $lastReadyError"}
    return $uri
}
function New-Session([uri]$Uri){$session=[Microsoft.PowerShell.Commands.WebRequestSession]::new();$session.Cookies.Add($Uri,[Net.Cookie]::new('lf_auth','fixture'));return $session}
try{
    $null=New-Item -ItemType Directory -Path $taskRoot
    $idsPath=Join-Path $taskRoot 'host-ids.txt';[IO.File]::WriteAllLines($idsPath,[string[]](1..3000|ForEach-Object{[string]$_}))
    $profilePath=Join-Path $taskRoot 'profile.json';[IO.File]::WriteAllText($profilePath,'{"schema":"fixture-profile","nativeSourceVerified":false}',[Text.UTF8Encoding]::new($false))
    $uri=Start-Fixture;$session=New-Session $uri;$out=Join-Path $taskRoot 'schedule'
    $condition=& $consumer -Action SetCondition -BaseUri $uri -WebSession $session -OutputDirectory $out -Condition 'combined' -ExpectedSettingsRevision '1'
    Assert ($condition.prtgEnabled -and -not$condition.nativeSourceVerified -and -not$condition.capacityAccepted) 'Condition-setting response or false evidence flags were wrong.'
    $revisionDriftRejected=$false;try{$null=& $consumer -Action SetCondition -BaseUri $uri -WebSession $session -OutputDirectory $out -Condition 'netiq-only' -ExpectedSettingsRevision 'stale-revision'}catch{$revisionDriftRejected=$true};$currentSettings=Invoke-RestMethod -Uri ([uri]::new($uri,'api/admin/settings')) -WebSession $session -TimeoutSec 5;Assert ($revisionDriftRejected -and $currentSettings.data.revision -ceq '1' -and $currentSettings.data.prtgEnabled) 'SetCondition changed settings after its expected revision had drifted.'
    $schedule=& $consumer -Action Schedule -BaseUri $uri -WebSession $session -OutputDirectory $out -ProfilePath $profilePath -HostIdsPath $idsPath -MaxSeconds 60
    Assert ($schedule.hostCount -eq 3000 -and $schedule.segment -eq '10.33.0.0/20') 'Actual host DTO list did not resolve to the exact fixture segment.'
    Assert ($schedule.netiqHostDayTiming.status -eq 'complete' -and $schedule.netiqHostDayTiming.expectedHostDays -eq 15000 -and $schedule.netiqHostDayTiming.p95Milliseconds -eq 117.5) 'Schedule receipt omitted or distorted the actual aggregate timing DTO.'
    $routes=& $consumer -Action MeasureRoutes -BaseUri $uri -WebSession $session -OutputDirectory $out -HostIdsPath $idsPath -SamplesPerRoute 100 -RequestDeadlineMs 2000 -MaxSeconds 30
    Assert ($routes.requests -eq 200 -and $routes.routes.Count -eq 2 -and $routes.routes[0].denominator -eq 100) 'Route measurements did not keep the complete fixed sample denominator.'
    $routeFailUri=Start-Fixture 'route-fail';$routeFail=& $consumer -Action MeasureRoutes -BaseUri $routeFailUri -WebSession (New-Session $routeFailUri) -OutputDirectory (Join-Path $taskRoot 'route-fail') -HostIdsPath $idsPath -SamplesPerRoute 100 -RequestDeadlineMs 2000 -MaxSeconds 30
    Assert ($routeFail.routes.Count -eq 2 -and @($routeFail.routes|Where-Object{$_.successes -ne 0 -or $_.failures -ne 100}).Count -eq 0) 'Quick HTTP failures were not preserved in both complete route denominators.'
    foreach($badRouteMode in @('route-domain-fail','route-empty')){
        $badRouteUri=Start-Fixture $badRouteMode;$badRoute=& $consumer -Action MeasureRoutes -BaseUri $badRouteUri -WebSession (New-Session $badRouteUri) -OutputDirectory (Join-Path $taskRoot $badRouteMode) -HostIdsPath $idsPath -SamplesPerRoute 100 -RequestDeadlineMs 2000 -MaxSeconds 30
        Assert (@($badRoute.routes|Where-Object{$_.successes -ne 0 -or $_.failures -ne 100}).Count -eq 0) "HTTP 200 with $badRouteMode was counted as a successful route measurement."
    }
    $badUri=Start-Fixture 'wrong-preview-count';$badOut=Join-Path $taskRoot 'bad-preview';$bad=$false
    try{$null=& $consumer -Action Schedule -BaseUri $badUri -WebSession (New-Session $badUri) -OutputDirectory $badOut -ProfilePath $profilePath -HostIdsPath $idsPath -MaxSeconds 5}catch{$bad=$true}
    Assert ($bad -and -not(Test-Path (Join-Path $badOut 'schedule-start.json'))) 'Wrong preview denominator did not fail before schedule submission.'
    $staleUri=Start-Fixture 'stale-run-timing';$staleOut=Join-Path $taskRoot 'stale-run';$stale=$false
    try{$null=& $consumer -Action Schedule -BaseUri $staleUri -WebSession (New-Session $staleUri) -OutputDirectory $staleOut -ProfilePath $profilePath -HostIdsPath $idsPath -MaxSeconds 5}catch{$stale=$true}
    Assert ($stale -and -not(Test-Path (Join-Path $staleOut 'schedule-complete.json'))) 'A previous completed timing snapshot was accepted for a newly triggered run.'
    foreach($mode in @('redirect','unauthorized','mime','oversize')){$badUri=Start-Fixture $mode;$badOut=Join-Path $taskRoot ('bad-'+$mode);$bad=$false;try{$null=& $consumer -Action SetCondition -BaseUri $badUri -WebSession (New-Session $badUri) -OutputDirectory $badOut -Condition 'netiq-only' -ExpectedSettingsRevision '1' -MaxSeconds 5}catch{$bad=$true};Assert $bad "Consumer did not refuse $mode response."}
    $matrixRoot=Join-Path $taskRoot 'evaluation';$conditions=@('netiq-only','combined','combined','netiq-only','netiq-only','combined');$evaluationRunId='evaluation-run-53'
    for($i=0;$i -lt 6;$i++){$dir=Join-Path $matrixRoot ("condition-{0:D2}-{1}" -f ($i+1),$conditions[$i]);$null=New-Item -ItemType Directory -Path $dir -Force;$p95=if($conditions[$i] -eq 'combined'){105.0}else{100.0};$receipt=@{schema='round53-schedule-run-v1';elapsedMs=10000;configuredWindowMinutes=720;status='observed-idle';hostCount=3000;netiqHostDayTiming=@{status='complete';expectedHostDays=15000;observedHostDays=15000;committedHostDays=15000;durationSampleCount=15000;sourceFailedHostDays=0;unknownHostDays=0;duplicateHostDays=0;overflow=$false;p95Milliseconds=$p95};state=@{lastRunSuccess=$true}};[IO.File]::WriteAllText((Join-Path $dir 'schedule-complete.json'),($receipt|ConvertTo-Json -Depth 10),[Text.UTF8Encoding]::new($false));$routeReceipt=New-RouteReceipt;[IO.File]::WriteAllText((Join-Path $dir 'route-measurement.json'),($routeReceipt|ConvertTo-Json -Depth 10),[Text.UTF8Encoding]::new($false));$fence=@{status='current';rawRows=15000;profileRows=15000;proofFenceSha256=('A'*64);nativeSourceVerified=$false;formalAcceptance=$false;wholeRoundAccepted=$false};[IO.File]::WriteAllText((Join-Path $dir 'live-qualification-fence-final.json'),($fence|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false));$owner=@{schema='round53-condition-attempt-v1';runId=$evaluationRunId;profile='controlled-loopback-3000';conditionIndex=$i+1;condition=$conditions[$i];state='complete'};[IO.File]::WriteAllText((Join-Path $dir 'condition-owner.json'),($owner|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))}
    $evaluation=& $consumer -Action Evaluate -BaseUri $uri -WebSession $session -OutputDirectory $matrixRoot -MatrixRunDirectory $matrixRoot -ProfileName 'controlled-loopback-3000' -ExpectedRunId $evaluationRunId
    Assert ($evaluation.metricChecksPass -and $evaluation.observableSubchecksPass -and $evaluation.pairs.Count -eq 3 -and $evaluation.pairs[0].relativeP95Increase -eq 0.05) 'Fixed six-cell evaluation did not calculate exact known p95 vectors.'
    function Set-RouteP95([int]$cell,[string]$route,[double]$value){
        $path=Join-Path $matrixRoot ("condition-{0:D2}-{1}/route-measurement.json" -f $cell,$conditions[$cell-1])
        $receipt=Get-Content -LiteralPath $path -Raw|ConvertFrom-Json -Depth 10
        ($receipt.routes|Where-Object route -ceq $route).p95Ms=$value
        ($receipt.routes|Where-Object route -ceq $route).maxMs=$value
        foreach($sample in ($receipt.routes|Where-Object route -ceq $route).samples){$sample.elapsedMs=$value}
        [IO.File]::WriteAllText($path,($receipt|ConvertTo-Json -Depth 10),[Text.UTF8Encoding]::new($false))
    }
    foreach($pair in @(@(1,2),@(4,3),@(5,6))){foreach($route in @('host-list','host-detail')){
        Set-RouteP95 $pair[1] $route 111
        $overLimit=& $consumer -Action Evaluate -BaseUri $uri -WebSession $session -OutputDirectory $matrixRoot -MatrixRunDirectory $matrixRoot -ProfileName 'controlled-loopback-3000' -ExpectedRunId $evaluationRunId
        Assert (-not$overLimit.metricChecksPass -and -not$overLimit.observableSubchecksPass -and $overLimit.routePairs.Count -eq 6 -and @($overLimit.routePairs|Where-Object { $_.combinedIndex -eq $pair[1] -and $_.route -ceq $route -and -not$_.pass -and $_.relativeP95Increase -eq 0.11 }).Count -eq 1) "Route $route pair $($pair[0])/$($pair[1]) accepted an 11% increase below the 2-second absolute bound."
        Set-RouteP95 $pair[1] $route 100
    }}
    foreach($cell in @(2,3,6)){foreach($route in @('host-list','host-detail')){Set-RouteP95 $cell $route 110}}
    $atLimit=& $consumer -Action Evaluate -BaseUri $uri -WebSession $session -OutputDirectory $matrixRoot -MatrixRunDirectory $matrixRoot -ProfileName 'controlled-loopback-3000' -ExpectedRunId $evaluationRunId
    Assert ($atLimit.metricChecksPass -and $atLimit.observableSubchecksPass -and $atLimit.routePairs.Count -eq 6 -and @($atLimit.routePairs|Where-Object { -not$_.pass -or $_.relativeP95Increase -ne 0.1 }).Count -eq 0) 'Exact 10% route increases did not pass every fixed baseline/combined pair.'
    Set-RouteP95 1 'host-list' 0
    $zeroBaseline=& $consumer -Action Evaluate -BaseUri $uri -WebSession $session -OutputDirectory $matrixRoot -MatrixRunDirectory $matrixRoot -ProfileName 'controlled-loopback-3000' -ExpectedRunId $evaluationRunId
    Assert (-not$zeroBaseline.metricChecksPass -and -not$zeroBaseline.observableSubchecksPass -and $null -eq $zeroBaseline.routePairs[0].relativeP95Increase -and $zeroBaseline.routePairs[0].reason -ceq 'route-baseline-unusable') 'A zero route baseline fabricated a finite passing relative increase.'
    foreach($cell in 1..6){foreach($route in @('host-list','host-detail')){Set-RouteP95 $cell $route 100}}
    $foreignOwnerPath=Join-Path $matrixRoot 'condition-01-netiq-only' 'condition-owner.json';$foreignOwner=Get-Content -LiteralPath $foreignOwnerPath -Raw|ConvertFrom-Json;$foreignOwner.runId='foreign-run';[IO.File]::WriteAllText($foreignOwnerPath,($foreignOwner|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false));$foreignOwnerRejected=$false;$foreignOwnerMessage="";try{$null=& $consumer -Action Evaluate -BaseUri $uri -WebSession $session -OutputDirectory $matrixRoot -MatrixRunDirectory $matrixRoot -ProfileName 'controlled-loopback-3000' -ExpectedRunId $evaluationRunId}catch{$foreignOwnerRejected=$true;$foreignOwnerMessage=$_.Exception.Message};Assert ($foreignOwnerRejected -and $foreignOwnerMessage -like "*owner receipt does not match this exact run/profile/condition*") 'Evaluation accepted a condition owner receipt from a different run.';$foreignOwner.runId=$evaluationRunId;[IO.File]::WriteAllText($foreignOwnerPath,($foreignOwner|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
    $routeReceiptPath=Join-Path $matrixRoot 'condition-01-netiq-only' 'route-measurement.json';$routeReceipt=Get-Content -LiteralPath $routeReceiptPath -Raw|ConvertFrom-Json -Depth 8;$routeReceipt.routes=@($routeReceipt.routes|Where-Object{$_.route -eq 'host-list'});[IO.File]::WriteAllText($routeReceiptPath,($routeReceipt|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false));$missingRoute=& $consumer -Action Evaluate -BaseUri $uri -WebSession $session -OutputDirectory $matrixRoot -MatrixRunDirectory $matrixRoot -ProfileName 'controlled-loopback-3000' -ExpectedRunId $evaluationRunId;Assert (-not$missingRoute.metricChecksPass -and -not$missingRoute.observableSubchecksPass) 'A missing API route unexpectedly passed the route gate.'
    $routeReceipt.routes=@();[IO.File]::WriteAllText($routeReceiptPath,($routeReceipt|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false));$emptyRoutes=& $consumer -Action Evaluate -BaseUri $uri -WebSession $session -OutputDirectory $matrixRoot -MatrixRunDirectory $matrixRoot -ProfileName 'controlled-loopback-3000' -ExpectedRunId $evaluationRunId;Assert (-not$emptyRoutes.metricChecksPass -and -not$emptyRoutes.observableSubchecksPass -and $emptyRoutes.routeSamples.Count -eq 12 -and $emptyRoutes.routeSetChecks[0].pass -eq $false) 'An empty route array passed vacuously or omitted explicit per-cell route verdicts.'
    $routeReceipt.routes=@(@{route='host-list';denominator=100;successes=100;failures=0;p95Ms=$null},@{route='host-detail';denominator=100;successes=100;failures=0;p95Ms=100});[IO.File]::WriteAllText($routeReceiptPath,($routeReceipt|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false));$nullP95=& $consumer -Action Evaluate -BaseUri $uri -WebSession $session -OutputDirectory $matrixRoot -MatrixRunDirectory $matrixRoot -ProfileName 'controlled-loopback-3000' -ExpectedRunId $evaluationRunId;Assert (-not$nullP95.metricChecksPass -and -not$nullP95.observableSubchecksPass -and $null -eq $nullP95.routeSamples[0].p95Ms) 'A null/missing route p95 was coerced to zero and passed.'
    $routeReceipt.routes=@(@{route='host-list';denominator=100;successes=0;failures=100;p95Ms=1},@{route='host-detail';denominator=100;successes=0;failures=100;p95Ms=1});[IO.File]::WriteAllText($routeReceiptPath,($routeReceipt|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false));$allFail=& $consumer -Action Evaluate -BaseUri $uri -WebSession $session -OutputDirectory $matrixRoot -MatrixRunDirectory $matrixRoot -ProfileName 'controlled-loopback-3000' -ExpectedRunId $evaluationRunId;Assert (-not$allFail.metricChecksPass -and -not$allFail.observableSubchecksPass) 'All fast failed HTTP samples unexpectedly passed the route gate.'
    $routeReceipt.routes[0].denominator=100;$routeReceipt.routes[0].successes=80;$routeReceipt.routes[0].failures=15;$routeReceipt.routes[0].p95Ms=100;$routeReceipt.routes[1].successes=100;$routeReceipt.routes[1].failures=0;[IO.File]::WriteAllText($routeReceiptPath,($routeReceipt|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false));$contradictory=& $consumer -Action Evaluate -BaseUri $uri -WebSession $session -OutputDirectory $matrixRoot -MatrixRunDirectory $matrixRoot -ProfileName 'controlled-loopback-3000' -ExpectedRunId $evaluationRunId;Assert (-not$contradictory.metricChecksPass) 'Contradictory success/failure denominator unexpectedly passed the route gate.'
    $routeReceipt=New-RouteReceipt;[IO.File]::WriteAllText($routeReceiptPath,($routeReceipt|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
    foreach($mutation in @('missing-samples','short-samples','forged-p95','forged-max','failed-sample','foreign-route','negative-time','non-numeric-time','boolean-time','wrong-request-count','wrong-requested-denominator')){
        $routeReceipt=New-RouteReceipt
        switch($mutation){
            'missing-samples' {$routeReceipt.routes[0].Remove('samples')}
            'short-samples' {$routeReceipt.routes[0].samples=@($routeReceipt.routes[0].samples|Select-Object -First 99)}
            'forged-p95' {$routeReceipt.routes[0].p95Ms=10}
            'forged-max' {$routeReceipt.routes[0].maxMs=10}
            'failed-sample' {$routeReceipt.routes[0].samples[0].statusCode=0}
            'foreign-route' {$routeReceipt.routes[0].samples[0].route='host-detail'}
            'negative-time' {$routeReceipt.routes[0].samples[0].elapsedMs=-1}
            'non-numeric-time' {$routeReceipt.routes[0].samples[0].elapsedMs='NaN'}
            'boolean-time' {$routeReceipt.routes[0].samples[0].elapsedMs=$true}
            'wrong-request-count' {$routeReceipt.requests=199}
            'wrong-requested-denominator' {$routeReceipt.requestedPerRoute=101}
        }
        [IO.File]::WriteAllText($routeReceiptPath,($routeReceipt|ConvertTo-Json -Depth 10),[Text.UTF8Encoding]::new($false))
        $invalidSamples=& $consumer -Action Evaluate -BaseUri $uri -WebSession $session -OutputDirectory $matrixRoot -MatrixRunDirectory $matrixRoot -ProfileName 'controlled-loopback-3000' -ExpectedRunId $evaluationRunId
        Assert (-not$invalidSamples.metricChecksPass -and -not$invalidSamples.observableSubchecksPass) "Route receipt $mutation passed without independently checking the complete raw measurements."
    }
    $routeReceipt=New-RouteReceipt
    for($i=0;$i -lt 100;$i++){$routeReceipt.routes[0].samples[$i].elapsedMs=$i+1}
    $routeReceipt.routes[0].p95Ms=95;$routeReceipt.routes[0].maxMs=100
    [IO.File]::WriteAllText($routeReceiptPath,($routeReceipt|ConvertTo-Json -Depth 10),[Text.UTF8Encoding]::new($false))
    $nearestRank=& $consumer -Action Evaluate -BaseUri $uri -WebSession $session -OutputDirectory $matrixRoot -MatrixRunDirectory $matrixRoot -ProfileName 'controlled-loopback-3000' -ExpectedRunId $evaluationRunId
    Assert ($nearestRank.metricChecksPass -and $nearestRank.routeSamples[0].p95Ms -eq 95) 'Revalidation did not use nearest-rank p95 over all 100 samples.'
    $routeReceipt=New-RouteReceipt;[IO.File]::WriteAllText($routeReceiptPath,($routeReceipt|ConvertTo-Json -Depth 10),[Text.UTF8Encoding]::new($false))
    $badSchedulePath=Join-Path $matrixRoot 'condition-04-netiq-only' 'schedule-complete.json';$badSchedule=Get-Content -LiteralPath $badSchedulePath -Raw|ConvertFrom-Json -Depth 16;$badSchedule.netiqHostDayTiming.status='incomplete';$badSchedule.netiqHostDayTiming.unknownHostDays=1;$badSchedule.netiqHostDayTiming.p95Milliseconds=$null;[IO.File]::WriteAllText($badSchedulePath,($badSchedule|ConvertTo-Json -Depth 16),[Text.UTF8Encoding]::new($false))
    $failedEvaluation=& $consumer -Action Evaluate -BaseUri $uri -WebSession $session -OutputDirectory $matrixRoot -MatrixRunDirectory $matrixRoot -ProfileName 'controlled-loopback-3000' -ExpectedRunId $evaluationRunId
    Assert (-not$failedEvaluation.metricChecksPass -and -not$failedEvaluation.pairs[1].pass -and -not$failedEvaluation.sourceQualification.qualified) 'Incomplete timing or source evidence did not fail closed.'
    "PASS: $passed matrix consumer contract assertions; fixtures used only actual public Web API DTO shapes."
}finally{
    foreach($proc in $processes){try{$current=Get-Process -Id $proc.Id -ErrorAction Stop;if($current.Id -eq $proc.Id){Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue;Wait-Process -Id $proc.Id -Timeout 5 -ErrorAction SilentlyContinue}}catch{}}
    foreach($portFile in $portFiles){if($portFile -match '^.+\\lf-matrix-fixture-port-[0-9a-f]{32}\.txt$'){Remove-Item -LiteralPath $portFile -Force -ErrorAction SilentlyContinue}}
    if(Test-Path -LiteralPath $taskRoot){$resolved=(Resolve-Path -LiteralPath $taskRoot).Path;$parent=(Split-Path -Parent $resolved);$item=Get-Item -LiteralPath $resolved -Force;if($parent -cne $expectedParent -or (Split-Path -Leaf $resolved) -notmatch '^matrix-consumer-contract-[0-9a-f]{32}$' -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Refusing cleanup: test temp root failed its exact parent/name/reparse-point checks.'};Remove-Item -LiteralPath $resolved -Recurse -Force}
}
