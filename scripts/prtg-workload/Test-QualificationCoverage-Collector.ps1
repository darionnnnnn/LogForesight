#Requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$fixture=Join-Path $PSScriptRoot 'qualification_coverage_fixture.py'
$collector=Join-Path $PSScriptRoot 'Read-QualificationCoverage.ps1'
$assembly=Join-Path $PSScriptRoot 'bin/Release/net8.0-windows/PrtgWorkloadGate.dll'
$python=(Get-Command python -ErrorAction Stop).Source
foreach($required in @($fixture,$collector,$assembly)){if(-not(Test-Path -LiteralPath $required -PathType Leaf)){throw "Required isolated 15k coverage test dependency missing: $required"}}
$tempRoot=Join-Path ([IO.Path]::GetTempPath()) ('prtg-qualification-coverage-tests-'+[guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $tempRoot
$manifest=Join-Path $tempRoot 'manifest-15000.json'
@{sensorIds=@(1..15000);jobId='0123456789abcdef0123456789abcdef';scopeFingerprint=('a'*64);settingsRevision='settings-r1';policyRevision='policy-r1';sourceGeneration='source-r1';authorityContextFingerprint=('b'*64)}|ConvertTo-Json -Depth 4 -Compress|Set-Content -Encoding utf8 $manifest
function Get-Port { $l=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0);$l.Start();$port=([Net.IPEndPoint]$l.LocalEndpoint).Port;$l.Stop();$port }
function Invoke-CollectorScenario([string]$scenario,[bool]$expectedReady,[string]$expectedReason='') {
    $port=Get-Port;$stdout=Join-Path $tempRoot "$scenario.stdout";$stderr=Join-Path $tempRoot "$scenario.stderr"
    $psi=[Diagnostics.ProcessStartInfo]::new();$psi.FileName=$python;$psi.UseShellExecute=$false;$psi.CreateNoWindow=$true;$psi.RedirectStandardOutput=$true;$psi.RedirectStandardError=$true
    foreach($arg in @($fixture,'--port',[string]$port,'--scenario',$scenario)){$psi.ArgumentList.Add($arg)}
    $server=[Diagnostics.Process]::Start($psi);$serverOut=$server.StandardOutput.ReadToEndAsync();$serverErr=$server.StandardError.ReadToEndAsync()
    try {
        $ready=$false;for($i=0;$i -lt 100;$i++){try{$tcp=[Net.Sockets.TcpClient]::new();$tcp.Connect('127.0.0.1',$port);$tcp.Close();$ready=$true;break}catch{Start-Sleep -Milliseconds 50}}
        if(-not$ready){throw "15k coverage fixture failed to bind: $($serverErr.Result)"}
        $output=Join-Path $tempRoot "$scenario-evidence";$session=New-Object Microsoft.PowerShell.Commands.WebRequestSession
        $receipt=& $collector -BaseUri "http://127.0.0.1:$port/" -ManifestPath $manifest -AssemblyPath $assembly -OutputDirectory $output -WebSession $session -MaxSeconds 120
        if($receipt.ready -ne $expectedReady){throw "Unexpected actual QualificationCoverage result for '$scenario': $($receipt.reason)"}
        if($expectedReason -and $receipt.reason -cne $expectedReason){throw "Expected reason '$expectedReason', got '$($receipt.reason)' for '$scenario'."}
        if($expectedReady -and ($receipt.expectedSensors -ne 15000 -or $receipt.qualifiedSensors -ne 15000 -or $receipt.readyProfiles -ne 15000)){throw 'Actual 15k verifier did not report exact raw/profile denominator.'}
        if($receipt.nativeSourceVerified -or $receipt.capacityAccepted -or $receipt.retentionAccepted -or $receipt.wholeRoundAccepted){throw 'Coverage-only receipt promoted an unrelated acceptance flag.'}
        $saved=Get-Content (Join-Path $output 'coverage-receipt.json') -Raw|ConvertFrom-Json
        if($saved.ready -ne $expectedReady -or $saved.manifestSha256 -cne $receipt.manifestSha256){throw 'On-disk coverage receipt differs from the result or manifest hash.'}
    } finally {if(-not$server.HasExited){$server.Kill($true);$server.WaitForExit(3000)|Out-Null};$server.Dispose()}
}
try {
    Invoke-CollectorScenario 'ready' $true
    Invoke-CollectorScenario 'partial-profile' $false 'profile-page-row-count-mismatch'
    'PASS: real Read-QualificationCoverage.ps1 + compiled QualificationCoverage.Verify accepted exact 15,000 raw/profile rows across 150 pages and rejected the omitted final profile row; 2 scenarios, 0 failures.'
} finally {
    $parent=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar,[IO.Path]::AltDirectorySeparatorChar)
    $resolved=[IO.Path]::GetFullPath($tempRoot);$item=Get-Item -LiteralPath $resolved
    if([IO.Path]::GetDirectoryName($resolved) -cne $parent -or $item.Name -notmatch '^prtg-qualification-coverage-tests-[0-9a-f]{32}$' -or $item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Refusing unverified collector-test cleanup target.'}
    $reparse=Get-ChildItem -LiteralPath $resolved -Force -Recurse|Where-Object{$_.Attributes -band [IO.FileAttributes]::ReparsePoint}|Select-Object -First 1
    if($reparse){throw 'Refusing collector-test cleanup because a reparse point exists.'}
    [IO.Directory]::Delete($resolved,$true)
}
