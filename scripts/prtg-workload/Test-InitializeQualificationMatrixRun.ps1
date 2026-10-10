#Requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$fixture=Join-Path $PSScriptRoot 'matrix_consumer_fixture.py'
$initializer=Join-Path $PSScriptRoot 'Initialize-QualificationMatrixRun.ps1'
$taskRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot ('initializer-contract-'+[guid]::NewGuid().ToString('N'))))
$expectedParent=[IO.Path]::GetFullPath($PSScriptRoot);$processes=[Collections.Generic.List[Diagnostics.Process]]::new();$portFiles=[Collections.Generic.List[string]]::new();$passed=0
function Assert([bool]$condition,[string]$message){if(-not$condition){throw $message};$script:passed++}
function Start-Fixture([string]$Mode='ok'){
    $portFile=Join-Path ([IO.Path]::GetTempPath()) ('lf-init-fixture-port-'+[guid]::NewGuid().ToString('N')+'.txt');$script:portFiles.Add($portFile)
    $argumentLine='"{0}" --port 0 --port-file "{1}" --mode "{2}"' -f $fixture,$portFile,$Mode
    $proc=Start-Process -FilePath 'python' -ArgumentList $argumentLine -WindowStyle Hidden -PassThru -RedirectStandardError (Join-Path $taskRoot "$Mode.stderr.log") -RedirectStandardOutput (Join-Path $taskRoot "$Mode.stdout.log");$script:processes.Add($proc)
    $until=[DateTime]::UtcNow.AddSeconds(10);while(-not(Test-Path -LiteralPath $portFile) -and [DateTime]::UtcNow -lt $until){if($proc.HasExited){throw "Initializer fixture exited: $Mode"};Start-Sleep -Milliseconds 20};if(-not(Test-Path -LiteralPath $portFile)){throw "Initializer fixture did not start: $Mode"}
    $port=[int](Get-Content -LiteralPath $portFile -Raw);return [uri]"http://127.0.0.1:$port/"
}
function New-Session([uri]$Uri){$session=[Microsoft.PowerShell.Commands.WebRequestSession]::new();$session.Cookies.Add($Uri,[Net.Cookie]::new('lf_auth','fixture'));return $session}
try{
    $null=New-Item -ItemType Directory -Path $taskRoot
    $ids=1..15000;$fences=@(foreach($id in $ids){@{sensorObjid=$id}})
    $manifestPath=Join-Path $taskRoot 'manifest.json';$manifest=[ordered]@{schema='qualification-job-manifest-v1';sensorIds=$ids;sensorFences=$fences;settingsRevision='1';policyRevision='policy-7';sourceGeneration='source-9';scopeFingerprint='scope-11';authorityContextFingerprint='authority-13'}
    [IO.File]::WriteAllText($manifestPath,($manifest|ConvertTo-Json -Depth 6 -Compress),[Text.UTF8Encoding]::new($false))
    $dataRoot=Join-Path $taskRoot 'owned-data';$null=New-Item -ItemType Directory -Path $dataRoot
    $dbPath=Join-Path $dataRoot 'fixture.db';[IO.File]::WriteAllBytes($dbPath,[Text.Encoding]::ASCII.GetBytes("SQLite format 3`0"+'0123456789'))
    $providerPath=Join-Path $taskRoot 'provider.json';$provider=@{schema='round53-owned-run-descriptor-v1';provider='sqlite';dataRoot=$dataRoot;databaseFile=$dbPath;retentionDays=180}
    [IO.File]::WriteAllText($providerPath,($provider|ConvertTo-Json -Compress),[Text.UTF8Encoding]::new($false))
    $uri=Start-Fixture;$runDirectory=Join-Path $taskRoot 'run-valid';$result=& $initializer -Profile fullstore-3000 -BaseUri $uri -PrtgFixtureUri ([uri]($uri.AbsoluteUri.TrimEnd('/')+'/prtg')) -SentinelFixtureUri ([uri]($uri.AbsoluteUri.TrimEnd('/')+'/sentinel')) -ManifestPath $manifestPath -ProviderDescriptorPath $providerPath -MatrixRunDirectory $runDirectory -WebSession (New-Session $uri)
    $owner=Get-Content -LiteralPath (Join-Path $runDirectory 'run-owner.json') -Raw|ConvertFrom-Json;$profile=Get-Content -LiteralPath (Join-Path $runDirectory 'round53-profile.json') -Raw|ConvertFrom-Json;$preflight=Get-Content -LiteralPath (Join-Path $runDirectory 'full-consumer-authority-preflight.json') -Raw|ConvertFrom-Json
    $expectedHostHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes((1..3000 -join "`n"))))
    $expectedRepositoryRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
    Assert ($result.repositoryRoot -ceq $expectedRepositoryRoot -and $result.status -eq 'owned-fixture-declared-awaiting-qualification' -and $owner.runId -match '^[0-9a-f]{32}$' -and $profile.netiqHosts -eq 3000 -and $profile.prtgSensors -eq 15000) 'Initializer did not resolve the delivered two-parent repository root or exact owned 3,000/15,000 run descriptors.'
    Assert ($profile.hostIdsSha256 -ceq $expectedHostHash -and $result.hostIdsSha256 -ceq $expectedHostHash -and (Get-Content -LiteralPath (Join-Path $runDirectory 'host-ids.txt')).Count -eq 3000) 'Host ID file/hash was not derived from the actual all-hosts DTO.'
    Assert ($preflight.status -ceq 'owned-fixture-declared-awaiting-qualification' -and $preflight.providerDeclaration -ceq 'operator-declared' -and $preflight.retentionDays -eq 180) 'Initializer incorrectly promoted operator provider/retention declarations to qualification.'
    Assert (-not$owner.nativeSourceVerified -and -not$profile.formalAcceptance -and -not$preflight.wholeRoundAccepted) 'Initializer promoted a source, formal, or whole-round evidence flag.'
    $matrixPath=Join-Path $PSScriptRoot 'Invoke-QualificationMatrix.ps1';$awaitingRejected=$false
    try{$null=& $matrixPath -Action Matrix -Profile fullstore-3000 -BaseUri $uri -PrtgFixtureUri ([uri]($uri.AbsoluteUri.TrimEnd('/')+'/prtg')) -SentinelFixtureUri ([uri]($uri.AbsoluteUri.TrimEnd('/')+'/sentinel')) -ManifestPath $manifestPath -WebSession (New-Session $uri) -CoverageAssemblyPath (Join-Path $PSScriptRoot 'Initialize-QualificationMatrixRun.ps1') -MatrixRunDirectory $runDirectory -EvidenceRoot $runDirectory -ConditionIndex 1}catch{$awaitingRejected=$true}
    Assert ($awaitingRejected) 'Matrix accepted the initializer declaration before a completed public Check/coverage gate.'
    foreach($scenario in @(@{mode='wrong-host-count';name='wrong-count'},@{mode='wrong-prtg-url';name='wrong-source'})){
        $badUri=Start-Fixture $scenario.mode;$badRun=Join-Path $taskRoot $scenario.name;$failed=$false
        try{$null=& $initializer -Profile fullstore-3000 -BaseUri $badUri -PrtgFixtureUri ([uri]($badUri.AbsoluteUri.TrimEnd('/')+'/prtg')) -SentinelFixtureUri ([uri]($badUri.AbsoluteUri.TrimEnd('/')+'/sentinel')) -ManifestPath $manifestPath -ProviderDescriptorPath $providerPath -MatrixRunDirectory $badRun -WebSession (New-Session $badUri)}catch{$failed=$true}
        Assert ($failed -and -not(Test-Path -LiteralPath (Join-Path $badRun 'full-consumer-authority-preflight.json'))) "Initializer accepted a public API fence violation: $($scenario.mode)."
    }
    $changedManifestPath=Join-Path $taskRoot 'manifest-stale-settings.json';$manifest.settingsRevision='stale-settings';[IO.File]::WriteAllText($changedManifestPath,($manifest|ConvertTo-Json -Depth 6 -Compress),[Text.UTF8Encoding]::new($false));$manifest.settingsRevision='1';$badRun=Join-Path $taskRoot 'bad-manifest-settings';$bad=$false
    try{$null=& $initializer -Profile fullstore-3000 -BaseUri $uri -PrtgFixtureUri ([uri]($uri.AbsoluteUri.TrimEnd('/')+'/prtg')) -SentinelFixtureUri ([uri]($uri.AbsoluteUri.TrimEnd('/')+'/sentinel')) -ManifestPath $changedManifestPath -ProviderDescriptorPath $providerPath -MatrixRunDirectory $badRun -WebSession (New-Session $uri)}catch{$bad=$true}
    Assert ($bad -and -not(Test-Path -LiteralPath (Join-Path $badRun 'full-consumer-authority-preflight.json'))) 'Initializer accepted a manifest settings revision that differs from the live Web API.'
    $badProvider=Join-Path $taskRoot 'provider-bad.json';$provider.databaseFile=Join-Path $taskRoot 'missing.db';[IO.File]::WriteAllText($badProvider,($provider|ConvertTo-Json -Compress),[Text.UTF8Encoding]::new($false));$badRun=Join-Path $taskRoot 'bad-provider';$bad=$false
    try{$null=& $initializer -Profile fullstore-3000 -BaseUri $uri -PrtgFixtureUri ([uri]($uri.AbsoluteUri.TrimEnd('/')+'/prtg')) -SentinelFixtureUri ([uri]($uri.AbsoluteUri.TrimEnd('/')+'/sentinel')) -ManifestPath $manifestPath -ProviderDescriptorPath $badProvider -MatrixRunDirectory $badRun -WebSession (New-Session $uri)}catch{$bad=$true}
    Assert ($bad -and -not(Test-Path -LiteralPath (Join-Path $badRun 'round53-profile.json'))) 'Initializer accepted a missing provider database file.'
    $junctionTarget=Join-Path $taskRoot 'junction-target';$junctionPath=Join-Path $taskRoot 'junction-run';$null=New-Item -ItemType Directory -Path $junctionTarget
    $junctionTargetFull=[IO.Path]::GetFullPath($junctionTarget);$junctionPathFull=[IO.Path]::GetFullPath($junctionPath)
    if(-not$junctionTargetFull.StartsWith($taskRoot.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase) -or -not$junctionPathFull.StartsWith($taskRoot.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Refusing to create a junction outside the verified initializer test root.'}
    $junctionCreated=$false
    try{
        $null=New-Item -ItemType Junction -Path $junctionPathFull -Target $junctionTargetFull;$junctionCreated=$true
        $junctionItem=Get-Item -LiteralPath $junctionPathFull -Force
        if(-not($junctionItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -or [string]$junctionItem.Target -cne $junctionTargetFull){throw 'Test junction did not resolve to the exact verified owned target.'}
        $reparseRun=Join-Path $junctionPathFull 'run';$reparseRejected=$false
        try{$null=& $initializer -Profile fullstore-3000 -BaseUri $uri -PrtgFixtureUri ([uri]($uri.AbsoluteUri.TrimEnd('/')+'/prtg')) -SentinelFixtureUri ([uri]($uri.AbsoluteUri.TrimEnd('/')+'/sentinel')) -ManifestPath $manifestPath -ProviderDescriptorPath $providerPath -MatrixRunDirectory $reparseRun -WebSession (New-Session $uri)}catch{$reparseRejected=$_.Exception.Message -like '*reparse point*'}
        Assert ($reparseRejected -and -not(Test-Path -LiteralPath (Join-Path $junctionTargetFull 'run')) ) 'Initializer accepted a run path below a reparse ancestor or wrote through the junction.'
    } finally {
        if($junctionCreated){$junctionCheck=Get-Item -LiteralPath $junctionPathFull -Force;if($junctionCheck.Attributes -band [IO.FileAttributes]::ReparsePoint){Remove-Item -LiteralPath $junctionPathFull -Force}else{throw 'Refusing junction cleanup because the path is no longer the verified reparse point.'}}
    }
    "PASS: $passed public initialization contract assertions; provider/180-day claims remain operator-declared."
}finally{
    foreach($proc in $processes){try{if(Get-Process -Id $proc.Id -ErrorAction Stop){Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue;Wait-Process -Id $proc.Id -Timeout 5 -ErrorAction SilentlyContinue}}catch{}}
    foreach($portFile in $portFiles){if((Split-Path -Leaf $portFile) -match '^lf-init-fixture-port-[0-9a-f]{32}\.txt$'){Remove-Item -LiteralPath $portFile -Force -ErrorAction SilentlyContinue}}
    if(Test-Path -LiteralPath $taskRoot){$resolved=(Resolve-Path -LiteralPath $taskRoot).Path;$parent=(Split-Path -Parent $resolved);$item=Get-Item -LiteralPath $resolved -Force;if($parent -cne $expectedParent -or (Split-Path -Leaf $resolved) -notmatch '^initializer-contract-[0-9a-f]{32}$' -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Refusing cleanup: initializer test temp root failed exact parent/name/reparse-point checks.'};Remove-Item -LiteralPath $resolved -Recurse -Force}
}
