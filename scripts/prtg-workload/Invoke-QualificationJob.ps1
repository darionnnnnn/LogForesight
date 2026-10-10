#Requires -Version 7.0
<#
Explicit durable qualification job coordinator for an isolated numeric-loopback Web fixture.
This script never creates bindings or qualification proof. Start/Resume require an exact
manifest and a current capacity pilot; Check is read-only. Every call has bounded bytes,
depth, per-request and whole-invocation deadlines. Existing WebRequestSession is reused.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Start','Resume','Check')][string]$Action,
    [Parameter(Mandatory)][uri]$BaseUri,
    [Parameter(Mandatory)][uri]$PrtgFixtureUri,
    [Parameter(Mandatory)][uri]$SentinelFixtureUri,
    [Parameter(Mandatory)][string]$ManifestPath,
    [Parameter(Mandatory)][string]$ReceiptPath,
    [Parameter(Mandatory)][Microsoft.PowerShell.Commands.WebRequestSession]$WebSession,
    [string]$JobId,
    [long]$ExpectedVersion = 0,
    [int]$ExpectedWave = 0,
    [ValidateRange(1,720)][int]$DurationHours = 720,
    [ValidateRange(1,3)][int]$MaximumAttempts = 1,
    [ValidateRange(1,600)][int]$MaxSeconds = 60,
    [ValidateRange(1,60)][int]$PollSeconds = 5,
    [switch]$RequireMatrixReady
)
$ErrorActionPreference = 'Stop'
$address = $null
if ($BaseUri.Scheme -notin @('http','https') -or -not [Net.IPAddress]::TryParse($BaseUri.DnsSafeHost.Trim('[',']'),[ref]$address) -or
    -not [Net.IPAddress]::IsLoopback($address) -or $BaseUri.Port -lt 1 -or $BaseUri.UserInfo -or $BaseUri.Query -or $BaseUri.Fragment) {
    throw 'Only explicit numeric-loopback fixture URLs without credentials, query, or fragment are allowed.'
}
$base = [uri]($BaseUri.AbsoluteUri.TrimEnd('/') + '/')
$manifestItem = Get-Item -LiteralPath $ManifestPath
if ($manifestItem.Length -gt 8MB) { throw 'Manifest exceeds 8 MiB.' }
$manifestBytes = [IO.File]::ReadAllBytes($manifestItem.FullName)
$jsonOptions = [Text.Json.JsonDocumentOptions]::new(); $jsonOptions.MaxDepth = 32
$manifestDoc = [Text.Json.JsonDocument]::Parse([ReadOnlyMemory[byte]]$manifestBytes,$jsonOptions)
try {
    $manifest = $manifestDoc.RootElement
    $idsElement = $manifest.GetProperty('sensorIds')
    if ($idsElement.ValueKind -ne 'Array' -or $idsElement.GetArrayLength() -lt 1 -or $idsElement.GetArrayLength() -gt 15000) { throw 'Manifest must contain 1..15000 exact sensor IDs.' }
    $ids = [Collections.Generic.List[long]]::new(); foreach ($item in $idsElement.EnumerateArray()) { $id = $item.GetInt64(); if ($id -le 0) { throw 'Manifest sensor IDs must be positive.' }; $ids.Add($id) }
    if (@($ids | Sort-Object -Unique).Count -ne $ids.Count -or -not [Linq.Enumerable]::SequenceEqual([long[]]$ids.ToArray(),[long[]]@($ids | Sort-Object))) { throw 'Manifest IDs must be unique and in ascending order.' }
    foreach ($field in @('settingsRevision','policyRevision','scopeFingerprint','sourceGeneration','authorityContextFingerprint')) {
        $null=$manifest.GetProperty($field)
    }
    $fenceArray=$manifest.GetProperty('sensorFences')
    if($fenceArray.ValueKind -ne 'Array' -or $fenceArray.GetArrayLength() -ne $ids.Count) { throw 'Manifest must contain one explicit binding/identity fence for every selected sensor.' }
    $fences=[Collections.Generic.List[object]]::new();foreach($fence in $fenceArray.EnumerateArray()){$fences.Add(@{sensorObjid=$fence.GetProperty('sensorObjid').GetInt64();identityEpoch=$fence.GetProperty('identityEpoch').GetInt64();channelGeneration=$fence.GetProperty('channelGeneration').GetString();bindingRevision=$fence.GetProperty('bindingRevision').GetInt64();bindingFingerprint=$fence.GetProperty('bindingFingerprint').GetString()})}
    if(-not [Linq.Enumerable]::SequenceEqual([long[]]@($fences|ForEach-Object sensorObjid),[long[]]$ids.ToArray()) -or $fences.Where({$_.identityEpoch -le 0 -or $_.bindingRevision -le 0 -or [string]::IsNullOrWhiteSpace($_.channelGeneration) -or [string]::IsNullOrWhiteSpace($_.bindingFingerprint)}).Count) { throw 'Manifest binding and identity fences are incomplete, duplicate, or unordered.' }
    $ctx = [ordered]@{ settingsRevision=$manifest.GetProperty('settingsRevision').GetString(); policyRevision=$manifest.GetProperty('policyRevision').GetString(); scopeFingerprint=$manifest.GetProperty('scopeFingerprint').GetString(); sourceGeneration=$manifest.GetProperty('sourceGeneration').GetString(); authorityContextFingerprint=$manifest.GetProperty('authorityContextFingerprint').GetString(); selectedSensors=$ids.Count; sensorIds=$ids.ToArray(); sensorFences=$fences.ToArray() }
    if (@($ctx.settingsRevision,$ctx.policyRevision,$ctx.scopeFingerprint,$ctx.sourceGeneration,$ctx.authorityContextFingerprint | Where-Object { [string]::IsNullOrWhiteSpace($_) }).Count) { throw 'Manifest context fields cannot be empty.' }
    $handler = [Net.Http.HttpClientHandler]::new(); $handler.AllowAutoRedirect=$false; $handler.UseCookies=$true; $handler.CookieContainer=$WebSession.Cookies
    $client = [Net.Http.HttpClient]::new($handler); $client.Timeout=[Threading.Timeout]::InfiniteTimeSpan
    $invocation = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds($MaxSeconds))
    $wholeClock=[Diagnostics.Stopwatch]::StartNew()
    function Invoke-FixtureApi([string]$method,[string]$path,[object]$body=$null,[int]$cap=256KB) {
        $cts=[Threading.CancellationTokenSource]::CreateLinkedTokenSource($invocation.Token); $cts.CancelAfter([TimeSpan]::FromSeconds(30)); $req=$null;$res=$null;$stream=$null;$memory=[IO.MemoryStream]::new()
        try {
            $req=[Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::new($method),[uri]::new($base,$path))
            if($method -cne 'GET'){$req.Headers.Add('X-Requested-By','LogForesight')}
            if ($null -ne $body) { $json=ConvertTo-Json -InputObject $body -Depth 32 -Compress; $req.Content=[Net.Http.StringContent]::new($json,[Text.Encoding]::UTF8,'application/json') }
            $res=$client.SendAsync($req,[Net.Http.HttpCompletionOption]::ResponseHeadersRead,$cts.Token).GetAwaiter().GetResult()
            if ([int]$res.StatusCode -ge 300 -and [int]$res.StatusCode -lt 400) { throw 'Fixture API redirected; refusing to follow.' }
            if (-not $res.IsSuccessStatusCode) { throw "Fixture API returned HTTP $([int]$res.StatusCode)." }
            if ($res.Content.Headers.ContentType.MediaType -cne 'application/json') { throw 'Fixture API returned non-JSON content.' }
            if ($res.Content.Headers.ContentLength -gt $cap) { throw 'Fixture API response exceeds its byte limit.' }
            $stream=$res.Content.ReadAsStreamAsync($cts.Token).GetAwaiter().GetResult();$buffer=[byte[]]::new(8192)
            while (($n=$stream.ReadAsync($buffer,0,$buffer.Length,$cts.Token).GetAwaiter().GetResult()) -gt 0) { if ($memory.Length+$n -gt $cap) { throw 'Fixture API response exceeds its streaming byte limit.' };$memory.Write($buffer,0,$n) }
            $doc=[Text.Json.JsonDocument]::Parse([ReadOnlyMemory[byte]]$memory.ToArray(),$jsonOptions)
            try { $root=$doc.RootElement; if (-not $root.GetProperty('success').GetBoolean()) { throw 'Fixture API rejected the request.' }; return $root.GetProperty('data').Clone() } finally { $doc.Dispose() }
        } finally { if($stream){$stream.Dispose()};if($res){$res.Dispose()};if($req){$req.Dispose()};$memory.Dispose();$cts.Dispose() }
    }
    function Assert-NumericLoopback([uri]$value,[string]$label) {
        $parsed=$null
        if($value.Scheme -notin @('http','https') -or -not [Net.IPAddress]::TryParse($value.DnsSafeHost.Trim('[',']'),[ref]$parsed) -or -not [Net.IPAddress]::IsLoopback($parsed) -or $value.Port -lt 1 -or $value.UserInfo -or $value.Query -or $value.Fragment) { throw "$label must be an explicit numeric loopback fixture URI without credentials, query, or fragment." }
    }
    Assert-NumericLoopback $PrtgFixtureUri 'PRTG fixture endpoint';Assert-NumericLoopback $SentinelFixtureUri 'Sentinel fixture endpoint'
    $settings=Invoke-FixtureApi 'GET' 'api/admin/settings' $null 64KB
    $configuredPrtg=[uri]$settings.GetProperty('prtgUrl').GetString();Assert-NumericLoopback $configuredPrtg 'Configured PRTG URL'
    if($configuredPrtg.AbsoluteUri.TrimEnd('/') -cne $PrtgFixtureUri.AbsoluteUri.TrimEnd('/')) { throw 'Saved configured PRTG URL does not exactly match the explicit loopback fixture endpoint.' }
    function Assert-CurrentContext([System.Text.Json.JsonElement]$contract) {
        if ($contract.GetProperty('settingsRevision').GetString() -cne $ctx.settingsRevision -or $contract.GetProperty('policyRevision').GetString() -cne $ctx.policyRevision -or $contract.GetProperty('scopeFingerprint').GetString() -cne $ctx.scopeFingerprint -or $contract.GetProperty('selectedSensors').GetInt32() -ne $ctx.selectedSensors -or -not $contract.GetProperty('capacityPilotCurrent').GetBoolean()) { throw 'Current settings, policy, exact scope, or saved capacity pilot differs from the manifest.' }
    }
    function Assert-AllSelectedBindingsCurrent([switch]$RequireMatrixReady) {
        $missingProof=0
        for($offset=0;$offset -lt $ctx.selectedSensors;$offset+=100) {
            $limit=[Math]::Min(100,$ctx.selectedSensors-$offset);$page=Invoke-FixtureApi 'GET' "api/prtg/monitoring/trusted-sampling/profiles?offset=$offset&limit=$limit" $null 512KB
            if($page.GetProperty('offset').GetInt32() -ne $offset -or $page.GetProperty('total').GetInt32() -ne $ctx.selectedSensors -or $page.GetProperty('limit').GetInt32() -ne $limit) { throw 'Profile page metadata does not match the complete manifest scope.' }
            if($page.GetProperty('settingsRevision').GetString() -cne $ctx.settingsRevision -or $page.GetProperty('policyRevision').GetString() -cne $ctx.policyRevision -or $page.GetProperty('sourceGeneration').GetString() -cne $ctx.sourceGeneration -or $page.GetProperty('authorityContextFingerprint').GetString() -cne $ctx.authorityContextFingerprint -or -not $page.GetProperty('prtgEnabled').GetBoolean()) { throw 'Profile page source, settings, policy, authority, or enabled fence differs from the manifest.' }
            $nextOffset=$page.GetProperty('nextOffset');$expectedNext=if($offset+$limit -lt $ctx.selectedSensors){$offset+$limit}else{$null};if($expectedNext -eq $null){if($nextOffset.ValueKind -ne 'Null'){throw 'Final profile page unexpectedly advertises another page.'}}elseif($nextOffset.GetInt32() -ne $expectedNext){throw 'Profile page nextOffset is missing or stale.'}
            $rows=$page.GetProperty('rows').EnumerateArray();$actual=[Collections.Generic.List[long]]::new()
            foreach($row in $rows) {
                $id=$row.GetProperty('sensorObjid').GetInt64();$actual.Add($id);$fence=$ctx.sensorFences[$offset+$actual.Count-1]
                $status=$row.GetProperty('status').GetString();$bindingStatus=$row.GetProperty('bindingStatus').GetString()
                if($row.GetProperty('currentIdentityEpoch').GetInt64() -ne $fence.identityEpoch -or $row.GetProperty('currentChannelGeneration').GetString() -cne $fence.channelGeneration -or $row.GetProperty('bindingRevision').GetInt64() -ne $fence.bindingRevision -or $row.GetProperty('bindingFingerprint').GetString() -cne $fence.bindingFingerprint -or [string]::IsNullOrWhiteSpace($row.GetProperty('boundChannelObjectId').GetString()) -or $status -notin @('ready','waiting','source_authority_incomplete') -or $bindingStatus -notin @('waiting','qualified')) { throw 'A selected sensor is missing its saved profile/binding or current identity fence.' }
                $proof=$row.GetProperty('qualificationProofReference').GetString();if([string]::IsNullOrWhiteSpace($proof)){$missingProof++;if($status -ceq 'source_authority_incomplete'){$facts=@($row.GetProperty('missingFacts').EnumerateArray()|ForEach-Object{$_.GetString()});if($bindingStatus -cne 'waiting' -or $facts -notcontains 'qualification:raw_channel_id_time_proof_required' -or $facts.Count -ne 1){throw 'Unqualified sensor lacks an explicit saved binding/current profile-directory marker.'}}}
                if($RequireMatrixReady -and ($row.GetProperty('status').GetString() -cne 'ready' -or $row.GetProperty('bindingStatus').GetString() -cne 'qualified' -or [string]::IsNullOrWhiteSpace($proof))) { throw 'Matrix gate requires every selected profile to be ready with raw qualification proof.' }
            }
            $expected=$ctx.sensorIds[$offset..($offset+$limit-1)];if($actual.Count -ne $limit -or -not [Linq.Enumerable]::SequenceEqual([long[]]$actual.ToArray(),[long[]]$expected)) { throw 'Profile page IDs are duplicate, partial, reordered, or outside the exact manifest scope.' }
        }
        return $missingProof
    }
    $prefix='api/prtg/monitoring/trusted-sampling/qualification-jobs/'
    $contract=Invoke-FixtureApi 'GET' ($prefix+'contract'); Assert-CurrentContext $contract
    $missingProof=if($Action -eq 'Check' -and -not $RequireMatrixReady){-1}else{Assert-AllSelectedBindingsCurrent -RequireMatrixReady:($Action -eq 'Check' -and $RequireMatrixReady)}
    $current=Invoke-FixtureApi 'GET' ($prefix+'current')
    if($Action -eq 'Start') {
        if($missingProof -eq 0) { throw 'Every selected sensor already has raw proof; use Check with the matrix gate instead of starting another job.' }
        if($current.ValueKind -ne 'Null' -and $current.GetProperty('status').GetString() -in @('initializing','running','waiting-capacity')) { throw 'An active durable job already exists; explicit Resume or Check is required.' }
        $payload=@{durationHours=$DurationHours;maximumAttempts=$MaximumAttempts;expectedSettingsRevision=$ctx.settingsRevision;expectedPolicyRevision=$ctx.policyRevision;expectedScopeFingerprint=$ctx.scopeFingerprint}
        $started=Invoke-FixtureApi 'POST' ($prefix+'start') $payload
        if($started.GetProperty('status').GetString() -cne 'initializing' -or $started.GetProperty('job').ValueKind -eq 'Null') { throw 'Server did not accept a durable qualification job.' }
        $job=$started.GetProperty('job');$JobId=$job.GetProperty('jobId').GetString()
    } elseif($Action -eq 'Resume') {
        if($ExpectedVersion -le 0 -or $ExpectedWave -le 0) { throw 'Resume requires positive expected version and wave CAS fences.' }
        if([string]::IsNullOrWhiteSpace($JobId) -or $current.ValueKind -eq 'Null' -or $current.GetProperty('jobId').GetString() -cne $JobId -or $current.GetProperty('version').GetInt64() -ne $ExpectedVersion -or ($ExpectedWave -gt 0 -and $current.GetProperty('wave').GetInt32() -ne $ExpectedWave)) { throw 'Resume requires the exact current job ID, version, and optional wave.' }
        foreach($field in @('scopeFingerprint','settingsRevision','policyRevision','sourceGeneration','authorityContextFingerprint')) { $wire=if($field -eq 'authorityContextFingerprint'){'authorityContextFingerprint'}else{$field};if($current.GetProperty($wire).GetString() -cne $ctx[$field]) { throw 'Stored job context is stale; refusing automatic restart.' } }
        $payload=@{expectedVersion=$ExpectedVersion;durationHours=$DurationHours;maximumAttempts=$MaximumAttempts}
        $resumed=Invoke-FixtureApi 'POST' ($prefix+$JobId+'/resume') $payload
        if($resumed.GetProperty('status').GetString() -cne 'running') { throw 'Server did not explicitly resume the existing durable job.' }
        $job=$resumed.GetProperty('job')
        $ExpectedWave=$job.GetProperty('wave').GetInt32()
    } else {
        if([string]::IsNullOrWhiteSpace($JobId) -or $current.ValueKind -eq 'Null' -or $current.GetProperty('jobId').GetString() -cne $JobId) { throw 'Check requires the exact current durable job ID.' }
        if($ExpectedVersion -gt 0 -and $current.GetProperty('version').GetInt64() -ne $ExpectedVersion) { throw 'Check current job version differs from its initial CAS fence.' }
        if($ExpectedWave -gt 0 -and $current.GetProperty('wave').GetInt32() -ne $ExpectedWave) { throw 'Check current job wave differs from its initial CAS fence.' }
        $job=$current
    }
    foreach($field in @('scopeFingerprint','settingsRevision','policyRevision','sourceGeneration','authorityContextFingerprint')) {
        if($job.GetProperty($field).GetString() -cne $ctx[$field]) { throw "Durable job $field does not match the manifest fence." }
    }
    $receiptPathFull=[IO.Path]::GetFullPath($ReceiptPath)
    $script:pollDeadlineReached=$false
    function Save-Receipt([System.Text.Json.JsonElement]$state) {
        $receiptValue=[ordered]@{schema='prtg-qualification-job-receipt-v1';action=$Action;jobId=$JobId;version=$state.GetProperty('version').GetInt64();wave=$state.GetProperty('wave').GetInt32();status=$state.GetProperty('status').GetString();pollDeadlineReached=$script:pollDeadlineReached;settingsRevision=$ctx.settingsRevision;policyRevision=$ctx.policyRevision;scopeFingerprint=$ctx.scopeFingerprint;sourceGeneration=$ctx.sourceGeneration;authorityContextFingerprint=$ctx.authorityContextFingerprint;selectedSensors=$ctx.selectedSensors;manifestSha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($manifestBytes));observedUtc=[DateTimeOffset]::UtcNow.ToString('o');nativeSourceVerified=$false;capacityAccepted=$false;retentionAccepted=$false;wholeRoundAccepted=$false}
        $temp=$receiptPathFull+'.tmp';[IO.File]::WriteAllText($temp,($receiptValue|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false));[IO.File]::Move($temp,$receiptPathFull,$true);return [pscustomobject]$receiptValue
    }
    $acceptedWave=$job.GetProperty('wave').GetInt32()
    $receipt=Save-Receipt $job
    $last=$job
    while($true) {
        if($last.GetProperty('status').GetString() -notin @('initializing','running','waiting-capacity')) { break }
        $remaining=[Math]::Max(0,$MaxSeconds-[int][Math]::Ceiling($wholeClock.Elapsed.TotalSeconds))
        if($remaining -le 0 -or $invocation.IsCancellationRequested) { $script:pollDeadlineReached=$true;break }
        if($PollSeconds -gt 0) { Start-Sleep -Seconds ([Math]::Min($PollSeconds,$remaining)) }
        if($invocation.IsCancellationRequested -or $wholeClock.Elapsed.TotalSeconds -ge $MaxSeconds) { $script:pollDeadlineReached=$true;break }
        try { $polled=Invoke-FixtureApi 'GET' ($prefix+'current') }
        catch [OperationCanceledException] { if($invocation.IsCancellationRequested){$script:pollDeadlineReached=$true;break};throw }
        $last=$polled
        if($last.ValueKind -eq 'Null' -or $last.GetProperty('jobId').GetString() -cne $JobId) { throw 'Current job disappeared or changed identity.' }
        if($last.GetProperty('wave').GetInt32() -ne $acceptedWave) { throw 'Current durable job wave differs from the accepted wave fence; refusing to adopt a concurrent resume.' }
        foreach($field in @('scopeFingerprint','settingsRevision','policyRevision','sourceGeneration','authorityContextFingerprint')) { if($last.GetProperty($field).GetString() -cne $ctx[$field]) { throw 'Durable job context changed; refusing to continue polling.' } }
        $receipt=Save-Receipt $last
    }
    if($last.GetProperty('wave').GetInt32() -ne $acceptedWave) { throw 'Final receipt wave differs from the accepted durable job wave.' }
    if($last.GetProperty('status').GetString() -in @('initializing','running','waiting-capacity') -and ($invocation.IsCancellationRequested -or $wholeClock.Elapsed.TotalSeconds -ge $MaxSeconds)) { $script:pollDeadlineReached=$true }
    if($Action -eq 'Check' -and $RequireMatrixReady) { if($last.GetProperty('status').GetString() -cne 'completed') { throw 'Matrix gate requires a completed durable qualification job.' };$null=Assert-AllSelectedBindingsCurrent -RequireMatrixReady }
    $receipt=Save-Receipt $last
    [pscustomobject]$receipt
} finally { if($client){$client.Dispose()};if($invocation){$invocation.Dispose()};$manifestDoc.Dispose() }
