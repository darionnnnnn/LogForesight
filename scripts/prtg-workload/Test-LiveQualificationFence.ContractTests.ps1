#Requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$scriptRoot=$PSScriptRoot
$fencePath=Join-Path $scriptRoot 'Test-LiveQualificationFence.ps1'
$fixturePath=Join-Path $scriptRoot 'live_fence_fixture.py'
$taskRoot=Join-Path ([IO.Path]::GetTempPath()) ('lf-live-fence-'+[guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $taskRoot -Force
$manifestPath=Join-Path $taskRoot 'manifest.json'
$proofPath=Join-Path $taskRoot 'matrix-proof-fence.json'
$manifest=[ordered]@{schema='qualification-job-manifest-v1';sensorIds=@(1..15000);sensorFences=@();settingsRevision='settings-r1';policyRevision='policy-r1';scopeFingerprint='scope-r1';sourceGeneration='source-r1';authorityContextFingerprint='authority-r1'}
foreach($id in $manifest.sensorIds){$manifest.sensorFences+=@{sensorObjid=$id;identityEpoch=1;channelGeneration="channel-$id";bindingRevision=1;bindingFingerprint=('b'*64)}}
[IO.File]::WriteAllText($manifestPath,($manifest|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
$session=[Microsoft.PowerShell.Commands.WebRequestSession]::new()
function Assert([bool]$condition,[string]$message){if(-not$condition){throw $message}}
function Start-Fixture([string]$mode){
    $listener=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0);$listener.Start();$port=([Net.IPEndPoint]$listener.LocalEndpoint).Port;$listener.Stop()
    $start=[Diagnostics.ProcessStartInfo]::new();$start.FileName='python';$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    $start.ArgumentList.Add($fixturePath);$start.ArgumentList.Add('--port');$start.ArgumentList.Add([string]$port);$start.ArgumentList.Add('--mode');$start.ArgumentList.Add($mode)
    $process=[Diagnostics.Process]::Start($start)
    $deadline=[Diagnostics.Stopwatch]::StartNew()
    while($deadline.Elapsed.TotalSeconds -lt 10){if($process.HasExited){throw "Fixture exited: $($process.StandardError.ReadToEnd())"};try{$probe=[Net.Sockets.TcpClient]::new();$probe.Connect('127.0.0.1',$port);$probe.Dispose();return @{process=$process;port=$port}}catch{Start-Sleep -Milliseconds 100}}
    $process.Kill($true);throw 'Live fence fixture did not start.'
}
function Stop-Fixture($fixture){if($fixture.process -and -not$fixture.process.HasExited){$fixture.process.Kill($true);$fixture.process.WaitForExit(5000)|Out-Null;$fixture.process.Dispose()}}
function Invoke-Fence($fixture){& $fencePath -BaseUri ([uri]"http://127.0.0.1:$($fixture.port)/") -PrtgFixtureUri ([uri]'http://127.0.0.1:9999/') -WebSession $session -ManifestPath $manifestPath -ProofFencePath $proofPath -JobId ('a'*32) -JobVersion 1 -JobWave 1 -ExpectedPrtgEnabled:$false -MaxSeconds 600}
try{
    $fixture=Start-Fixture 'valid';try{$result=Invoke-Fence $fixture;Assert ($result.status -ceq 'current' -and $result.prtgEnabled -eq $false -and $result.rawRows -eq 15000 -and $result.profileRows -eq 15000 -and (Test-Path $proofPath)) 'Disabled baseline read-only contract or exact proof fence failed.'}finally{Stop-Fixture $fixture}
    $fixture=Start-Fixture 'proof-change';try{$null=Invoke-Fence $fixture;throw 'Changed proof reference unexpectedly passed.'}catch{if($_.Exception.Message -eq 'Changed proof reference unexpectedly passed.'){throw}}finally{Stop-Fixture $fixture}
    $fixture=Start-Fixture 'source-drift-final';try{$null=Invoke-Fence $fixture;throw 'Final source/policy drift unexpectedly passed.'}catch{if($_.Exception.Message -eq 'Final source/policy drift unexpectedly passed.'){throw}}finally{Stop-Fixture $fixture}
    $fixture=Start-Fixture 'settings-drift-final';try{$null=Invoke-Fence $fixture;throw 'Final-page settings drift unexpectedly passed.'}catch{if($_.Exception.Message -eq 'Final-page settings drift unexpectedly passed.'){throw}}finally{Stop-Fixture $fixture}
    [pscustomobject]@{status='passed';scenarios=@('disabled-baseline-contract','exact-15000-live-pages','changed-proof-reference','final-source-policy-drift','final-page-settings-drift');nativeSourceVerified=$false;formalAcceptance=$false;wholeRoundAccepted=$false}
}finally{Get-Process python -ErrorAction SilentlyContinue | Out-Null;try{Remove-Item -LiteralPath $taskRoot -Recurse -Force}catch{}}
