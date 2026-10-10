#Requires -Version 7.0
<#
.SYNOPSIS
Protocol contract experiment for Read-QualificationCoverage.ps1 (no Pester required).
.DESCRIPTION
Uses only a synthetic HttpListener bound to an OS-assigned IPv4 loopback port. The listener
is owned and stopped by this process. No production data, credentials, login, or job mutation
is used. Every case writes its manifest, collector snapshots/receipt, and a result record to
a new case directory. Existing evidence directories are never overwritten.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $AssemblyPath,
    [Parameter(Mandatory)][string] $CollectorPath,
    [Parameter(Mandatory)][string] $EvidenceDirectory
)

$ErrorActionPreference = 'Stop'
foreach ($path in @($AssemblyPath, $CollectorPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required input does not exist: $path" }
}
$AssemblyPath = (Resolve-Path -LiteralPath $AssemblyPath).Path
$CollectorPath = (Resolve-Path -LiteralPath $CollectorPath).Path
$EvidenceDirectory = [IO.Path]::GetFullPath($EvidenceDirectory)
if (Test-Path -LiteralPath $EvidenceDirectory) { throw 'EvidenceDirectory must not already exist; refusing to overwrite evidence.' }
$null = New-Item -ItemType Directory -Path $EvidenceDirectory

if (-not ('CoverageProtocolFixtureServer' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

public sealed class CoverageProtocolFixtureServer : IDisposable
{
    private readonly HttpListener listener = new HttpListener();
    private readonly CancellationTokenSource stop = new CancellationTokenSource();
    private readonly Task loop;
    private readonly ConcurrentQueue<string> requests = new ConcurrentQueue<string>();
    private readonly string mode;
    private readonly string beforeJson;
    private readonly string afterJson;
    private readonly string rawJson;
    private readonly string profileJson;
    private int currentCount;
    private int redirectTargetCount;
    private int chunkedBodyCount;

    public string BaseUri { get; }
    public string[] Requests => requests.ToArray();
    public int RedirectTargetCount => Volatile.Read(ref redirectTargetCount);
    public int ChunkedBodyCount => Volatile.Read(ref chunkedBodyCount);

    public CoverageProtocolFixtureServer(string mode, string beforeJson, string afterJson,
        string rawJson, string profileJson)
    {
        this.mode = mode;
        this.beforeJson = beforeJson;
        this.afterJson = afterJson;
        this.rawJson = rawJson;
        this.profileJson = profileJson;
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        BaseUri = "http://127.0.0.1:" + port + "/lf/";
        listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
        listener.Start();
        loop = Task.Run(AcceptLoop);
    }

    private async Task AcceptLoop()
    {
        while (!stop.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync().ConfigureAwait(false); }
            catch (Exception) when (stop.IsCancellationRequested || !listener.IsListening) { break; }
            try { await Respond(context).ConfigureAwait(false); }
            catch (Exception) { try { context.Response.Abort(); } catch { } }
        }
    }

    private async Task Respond(HttpListenerContext context)
    {
        var request = context.Request;
        var path = request.Url.AbsolutePath;
        requests.Enqueue(request.HttpMethod + "\t" + path + "\t" + (request.Headers["Cookie"] ?? ""));
        if (path == "/lf/redirect-target") Interlocked.Increment(ref redirectTargetCount);
        if (!path.StartsWith("/lf/api/prtg/monitoring/trusted-sampling/", StringComparison.Ordinal))
        {
            await Write(context, 404, "application/json", "{}", false, 0).ConfigureAwait(false);
            return;
        }
        if (mode == "unauthorized") { await Write(context, 401, "application/json", "{}", false, 0).ConfigureAwait(false); return; }
        if (mode == "redirect")
        {
            context.Response.StatusCode = 302;
            context.Response.RedirectLocation = "/lf/redirect-target";
            context.Response.Close();
            return;
        }
        if (mode == "timeout" && path.EndsWith("/qualification-jobs/current", StringComparison.Ordinal))
        {
            await Write(context, 200, "application/json", beforeJson, false, 2500).ConfigureAwait(false);
            return;
        }
        if (path.EndsWith("/qualification-jobs/current", StringComparison.Ordinal))
        {
            var call = Interlocked.Increment(ref currentCount);
            await Write(context, 200, mode == "wrong-mime" ? "text/plain" : "application/json",
                call == 1 ? beforeJson : afterJson, false, 0).ConfigureAwait(false);
            return;
        }
        if (path.Contains("/qualification-jobs/", StringComparison.Ordinal) && path.EndsWith("/page", StringComparison.Ordinal))
        {
            if (mode == "oversize-chunked")
            {
                Interlocked.Increment(ref chunkedBodyCount);
                var large = "{" + "\"padding\":\"" + new string('x', 140000) + "\"}";
                await Write(context, 200, "application/json", large, true, 0).ConfigureAwait(false);
            }
            else await Write(context, 200, "application/json", rawJson, false, 0).ConfigureAwait(false);
            return;
        }
        if (path.EndsWith("/profiles", StringComparison.Ordinal))
        {
            await Write(context, 200, "application/json", profileJson, false, 0).ConfigureAwait(false);
            return;
        }
        await Write(context, 404, "application/json", "{}", false, 0).ConfigureAwait(false);
    }

    private async Task Write(HttpListenerContext context, int status, string contentType,
        string body, bool chunked, int delayMs)
    {
        if (delayMs > 0) await Task.Delay(delayMs, stop.Token).ConfigureAwait(false);
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.StatusCode = status;
        context.Response.ContentType = contentType;
        if (chunked) context.Response.SendChunked = true;
        else context.Response.ContentLength64 = bytes.Length;
        if (status >= 300 && status < 400) { context.Response.Close(); return; }
        await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length, stop.Token).ConfigureAwait(false);
        context.Response.Close();
    }

    public void Dispose()
    {
        stop.Cancel();
        try { listener.Stop(); } catch { }
        try { listener.Close(); } catch { }
        try { loop.Wait(TimeSpan.FromSeconds(2)); } catch { }
        stop.Dispose();
    }
}
'@ -ErrorAction Stop | Out-Null
}

$script:JobId = '6f07d6ef44bb46c9a514a59e1c75be4a'
$script:Scope = ('a' * 64)
$script:SettingsRevision = 'settings-r17'
$script:PolicyRevision = 'policy-r23'
$script:SourceGeneration = 'source-gen-9'
$script:Authority = ('b' * 64)
$script:SensorIds = @(41001, 41002)

function New-Job([long] $Version) {
    return [ordered]@{
        jobId=$script:JobId; status='completed'; scopeFingerprint=$script:Scope
        settingsRevision=$script:SettingsRevision; policyRevision=$script:PolicyRevision
        sourceGeneration=$script:SourceGeneration; authorityContextFingerprint=$script:Authority
        version=$Version; wave=1; selected=2; eligible=2; qualified=2; waiting=0; failed=0
        pageCount=1; initializedPages=1; initializationCursor=1; attempts=2; totalFailed=0
        admittedEligibleSensorCount=2; initializedEligibleSensorCount=2; cursor=1; durationHours=24
        maximumAttempts=3; cancelRequested=$false
    }
}
function New-Envelope([object] $Data) { return [ordered]@{success=$true; data=$Data} }
function New-RawPage([string] $Mutation) {
    $rows = @(
        [ordered]@{sensorObjid=41001; bindingRevision=7; bindingFingerprint=('c' * 64); status='qualified'; reason='proof-and-authority-current'; attempts=1; waveAttempts=1; hasQualificationProof=$true},
        [ordered]@{sensorObjid=41002; bindingRevision=9; bindingFingerprint=('d' * 64); status='qualified'; reason='proof-and-authority-current'; attempts=1; waveAttempts=1; hasQualificationProof=$true}
    )
    if ($Mutation -eq 'raw-missing') { $rows = @($rows[0]) }
    if ($Mutation -eq 'raw-foreign') { $rows[1].sensorObjid = 41999 }
    return New-Envelope ([ordered]@{status='completed'; total=2; offset=0; limit=100; nextOffset=$null; rows=$rows})
}
function New-ProfilePage([bool] $Enabled, [string] $Mutation) {
    $rows = @(
        [ordered]@{sensorObjid=41001; status='ready'; missingFacts=@(); bindingStatus='qualified'; bindingRevision=7; bindingFingerprint=('c' * 64); qualificationProofReference='proof-sensor-41001'; currentIdentityEpoch=3; currentChannelGeneration='channel-gen-41001'},
        [ordered]@{sensorObjid=41002; status='ready'; missingFacts=@(); bindingStatus='qualified'; bindingRevision=9; bindingFingerprint=('d' * 64); qualificationProofReference='proof-sensor-41002'; currentIdentityEpoch=5; currentChannelGeneration='channel-gen-41002'}
    )
    return New-Envelope ([ordered]@{settingsRevision=$script:SettingsRevision; policyRevision=$script:PolicyRevision; sourceGeneration=$script:SourceGeneration; authorityContextFingerprint=$script:Authority; prtgEnabled=$Enabled; total=2; offset=0; limit=100; nextOffset=$null; rows=$rows})
}

$cases = [Collections.Generic.List[object]]::new()
function Invoke-ProtocolCase([string] $Name, [string] $Mode = 'normal', [string] $Mutation = '', [bool] $Enabled = $true,
    [bool] $ExpectReady = $false, [string] $ExpectReason = '', [int] $MaxSeconds = 5, [bool] $BadAssembly = $false) {
    $caseRoot = Join-Path $EvidenceDirectory $Name
    $null = New-Item -ItemType Directory -Path $caseRoot
    $manifestPath = Join-Path $caseRoot 'manifest.json'
    $manifest = [ordered]@{
        jobId=$script:JobId; scopeFingerprint=$script:Scope; sensorIds=$script:SensorIds
        settingsRevision=$script:SettingsRevision; policyRevision=$script:PolicyRevision
        sourceGeneration=$script:SourceGeneration; authorityContextFingerprint=$script:Authority
    }
    [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 6 -Compress), [Text.UTF8Encoding]::new($false))
    $before = New-Envelope (New-Job 31)
    $afterJob = if ($Mode -eq 'job-drift') { New-Job 32 } else { New-Job 31 }
    $server = $null
    $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $assembly = $AssemblyPath
    if ($BadAssembly) {
        $assembly = Join-Path $caseRoot 'invalid.dll'
        [IO.File]::WriteAllBytes($assembly, [byte[]]@(0x4D,0x5A,0x00,0x01,0x02))
    }
    $outputDir = Join-Path $caseRoot 'evidence'
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $collected = $null
    $caught = $null
    $serverRequests = @()
    $chunkedCount = 0
    $redirectCount = 0
    try {
        $raw = New-RawPage $Mutation | ConvertTo-Json -Depth 12 -Compress
        $profiles = New-ProfilePage $Enabled $Mutation | ConvertTo-Json -Depth 12 -Compress
        $server = [CoverageProtocolFixtureServer]::new($Mode, ($before | ConvertTo-Json -Depth 12 -Compress),
            ((New-Envelope $afterJob) | ConvertTo-Json -Depth 12 -Compress), $raw, $profiles)
        $cookie = [Net.Cookie]::new('auth', 'synthetic-owned-session', '/lf/')
        $session.Cookies.Add([uri]$server.BaseUri, $cookie)
        try {
            $collected = & $CollectorPath -BaseUri ([uri]$server.BaseUri) -ManifestPath $manifestPath `
                -AssemblyPath $assembly -OutputDirectory $outputDir -WebSession $session -MaxSeconds $MaxSeconds
        } catch { $caught = $_.Exception.Message }
        $clock.Stop()
        $serverRequests = @($server.Requests)
        $chunkedCount = $server.ChunkedBodyCount
        $redirectCount = $server.RedirectTargetCount
    } finally {
        $clock.Stop()
        if ($server) { $server.Dispose() }
        $session = $null
    }

    $receiptPath = Join-Path $outputDir 'coverage-receipt.json'
    $receiptExists = Test-Path -LiteralPath $receiptPath -PathType Leaf
    $receipt = if ($receiptExists) { Get-Content -Raw -LiteralPath $receiptPath | ConvertFrom-Json } else { $null }
    $pass = $true
    $notes = [Collections.Generic.List[string]]::new()
    if (-not $receiptExists) { $pass = $false; $notes.Add('receipt missing') }
    if ($null -ne $receipt -and [bool]$receipt.ready -ne $ExpectReady) { $pass = $false; $notes.Add("ready=$($receipt.ready), expected=$ExpectReady") }
    if ($ExpectReason -and $null -ne $receipt -and $receipt.reason -ne $ExpectReason) { $pass = $false; $notes.Add("reason=$($receipt.reason), expected=$ExpectReason") }
    if ($clock.Elapsed.TotalSeconds -gt 5) { $pass = $false; $notes.Add(('elapsed {0:N2}s > 5s' -f $clock.Elapsed.TotalSeconds)) }
    if ($ExpectReady -and $null -ne $receipt) {
        if ($receipt.expectedSensors -ne 2 -or $receipt.qualifiedSensors -ne 2 -or $receipt.readyProfiles -ne 2) { $pass = $false; $notes.Add('full-scope counts incorrect') }
        if ($receipt.nativeSourceVerified -or $receipt.capacityAccepted -or $receipt.retentionAccepted -or $receipt.wholeRoundAccepted) { $pass = $false; $notes.Add('forbidden acceptance flag true') }
        if ($serverRequests.Count -ne 4) { $pass = $false; $notes.Add("GET count $($serverRequests.Count), expected 4") }
        if (@($serverRequests | Where-Object { $_ -notmatch '^GET\t/lf/api/prtg/monitoring/trusted-sampling/' }).Count -gt 0) { $pass = $false; $notes.Add('method or PathBase mismatch') }
        if (@($serverRequests | Where-Object { $_ -notmatch 'auth=synthetic-owned-session' }).Count -gt 0) { $pass = $false; $notes.Add('owned session cookie missing') }
        if (@($serverRequests | Where-Object { $_ -match '/redirect-target' }).Count -gt 0) { $pass = $false; $notes.Add('unexpected redirect target request') }
    }
    if ($Mode -eq 'redirect' -and ($serverRequests.Count -ne 1 -or $redirectCount -ne 0)) { $pass = $false; $notes.Add('redirect was followed') }
    if ($Mode -eq 'oversize-chunked' -and $chunkedCount -ne 1) { $pass = $false; $notes.Add('oversize response was not sent chunked') }
    if ($BadAssembly -and ($serverRequests.Count -ne 0 -or $null -eq $receipt -or $receipt.ready)) { $pass = $false; $notes.Add('bad DLL did not fail before HTTP with false receipt') }
    $record = [ordered]@{case=$Name; pass=$pass; elapsedSeconds=[Math]::Round($clock.Elapsed.TotalSeconds,3); receiptExists=$receiptExists; ready=$receipt.ready; reason=$receipt.reason; requestCount=$serverRequests.Count; redirectTargetCount=$redirectCount; chunkedBodyCount=$chunkedCount; exception=$caught; notes=@($notes)}
    [IO.File]::WriteAllText((Join-Path $caseRoot 'result.json'), ($record | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
    $cases.Add([pscustomobject]$record)
}

Invoke-ProtocolCase -Name 'success-two-sensors' -ExpectReady $true -ExpectReason 'exact-full-scope-job-and-ready-profile-snapshots'
Invoke-ProtocolCase -Name 'raw-missing-sensor' -Mutation 'raw-missing' -ExpectReason 'qualification-page-row-count-mismatch'
Invoke-ProtocolCase -Name 'raw-foreign-sensor' -Mutation 'raw-foreign' -ExpectReason 'qualification-foreign-sensor'
Invoke-ProtocolCase -Name 'profiles-source-disabled' -Enabled $false -ExpectReason 'profile-source-disabled'
Invoke-ProtocolCase -Name 'before-after-job-version-drift' -Mode 'job-drift' -ExpectReason 'job-revision-drift'
Invoke-ProtocolCase -Name 'http-401' -Mode 'unauthorized' -ExpectReason 'collection-failed'
Invoke-ProtocolCase -Name 'http-302-no-redirect' -Mode 'redirect' -ExpectReason 'collection-failed'
Invoke-ProtocolCase -Name 'wrong-mime' -Mode 'wrong-mime' -ExpectReason 'collection-failed'
Invoke-ProtocolCase -Name 'chunked-body-over-limit' -Mode 'oversize-chunked' -ExpectReason 'collection-failed'
Invoke-ProtocolCase -Name 'overall-deadline-one-second' -Mode 'timeout' -MaxSeconds 1 -ExpectReason 'collection-failed'
Invoke-ProtocolCase -Name 'existing-invalid-dll' -BadAssembly $true -ExpectReason 'collection-failed'

$failed = @($cases | Where-Object { -not $_.pass })
$cases | Select-Object case,pass,elapsedSeconds,ready,reason,requestCount | Format-Table -AutoSize
Write-Output ("Protocol cases: {0} passed, {1} failed. Evidence: {2}" -f ($cases.Count - $failed.Count), $failed.Count, $EvidenceDirectory)
if ($failed.Count -gt 0) { exit 1 }
