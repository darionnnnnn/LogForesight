#Requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
Import-Module Microsoft.PowerShell.Utility
$matrix=Join-Path $PSScriptRoot 'Invoke-QualificationMatrix.ps1'
$runRoot=Join-Path $PSScriptRoot ('runtime-fence-test-'+[guid]::NewGuid().ToString('N'))
$session=[Microsoft.PowerShell.Commands.WebRequestSession]::new()
$common=@{
    Profile='controlled-loopback-3000'
    BaseUri=[uri]'http://127.0.0.1:1/'
    PrtgFixtureUri=[uri]'http://127.0.0.1:1/prtg'
    SentinelFixtureUri=[uri]'http://127.0.0.1:1/sentinel'
    ManifestPath=(Join-Path $runRoot 'manifest.json')
    WebSession=$session
    CoverageAssemblyPath=(Join-Path $runRoot 'QualificationCoverage.dll')
    MatrixRunDirectory=$runRoot
    EvidenceRoot=(Join-Path $runRoot 'evidence')
    MaxSeconds=1
    PollSeconds=1
}
$passed=0
foreach($action in @('Start','Resume','Matrix')) {
    $rejected=$false;$message=''
    try { $null=& $matrix @common -Action $action -JobId ('a'*32) -ExpectedVersion 1 -ExpectedWave 1 -ConditionIndex 1 }
    catch { $rejected=$true;$message=$_.Exception.Message }
    if(-not$rejected -or $message -notlike 'D54-02: refusing public lifecycle mutation because the verified Web runtime owner receipt is missing*') {
        throw "$action did not fail closed on the missing exact Web runtime owner receipt: $message"
    }
    if(Test-Path -LiteralPath $runRoot) { throw "$action wrote matrix artifacts before rejecting the missing runtime owner receipt." }
    $passed++
}
$listener=$null;$tcp=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0);$receiptRoot=$runRoot+'-receipt-shaped'
try {
    $tcp.Start();$port=([Net.IPEndPoint]$tcp.LocalEndpoint).Port;$tcp.Stop()
    $listener=[Net.HttpListener]::new();$listener.Prefixes.Add("http://127.0.0.1:$port/");$listener.Start()
    $requestTask=$listener.GetContextAsync()
    $null=New-Item -ItemType Directory -Path $receiptRoot
    $receiptPath=Join-Path $receiptRoot 'verified-web-runtime-owner.json'
    [IO.File]::WriteAllText($receiptPath,(@{schema='round53-web-runtime-owner-v1';processId=$PID;startTimeUtcTicks=[DateTime]::UtcNow.Ticks;assemblyPath='C:\fake\LogForesight.Web.dll';arguments='--data-root C:\fake';dataRoot='C:\fake';provider='sqlite';descriptorSha256=('A'*64)}|ConvertTo-Json -Compress),[Text.UTF8Encoding]::new($false))
    $common.MatrixRunDirectory=$receiptRoot;$common.EvidenceRoot=Join-Path $receiptRoot 'evidence'
    $common.BaseUri=[uri]"http://127.0.0.1:$port/";$common.PrtgFixtureUri=[uri]"http://127.0.0.1:$port/prtg";$common.SentinelFixtureUri=[uri]"http://127.0.0.1:$port/sentinel"
    foreach($action in @('Start','Resume','Matrix')) {
        $rejected=$false;$message=''
        try { $null=& $matrix @common -Action $action -JobId ('a'*32) -ExpectedVersion 1 -ExpectedWave 1 -ConditionIndex 1 }
        catch { $rejected=$true;$message=$_.Exception.Message }
        if(-not$rejected -or $message -notlike 'D54-02: refusing public lifecycle mutation because*has no implemented exact process/listener verifier*') {
            throw "$action accepted a receipt-shaped file without verifying the actual listener/process identity: $message"
        }
        if($requestTask.Wait(150)) { $context=$requestTask.Result;try{$context.Response.Close()}catch{};throw "$action contacted a foreign loopback listener before refusing the unverified receipt." }
        $passed++
    }
} finally {
    if($listener){try{$listener.Stop();$listener.Close()}catch{}}
    if($tcp){try{$tcp.Stop()}catch{}}
    if(Test-Path -LiteralPath $receiptRoot) {
        $resolved=(Resolve-Path -LiteralPath $receiptRoot).Path;$parent=(Split-Path -Parent $resolved);$item=Get-Item -LiteralPath $resolved -Force
        if($parent -cne $PSScriptRoot -or (Split-Path -Leaf $resolved) -notmatch '^runtime-fence-test-[0-9a-f]{32}-receipt-shaped$' -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Refusing cleanup: receipt-shaped fixture escaped its exact owned test directory.' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
"PASS: $passed lifecycle checks refused missing and receipt-shaped ownership evidence before filesystem or HTTP effects; a foreign loopback listener received no request (D54-02)."
