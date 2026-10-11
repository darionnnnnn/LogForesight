#Requires -Version 7.0
<#
Bounded public-Web-API consumer for the fixed round53 schedule matrix. The caller
supplies an existing authenticated WebRequestSession and explicit numeric-loopback
Web URL. This script never opens a provider database, logs in, contacts PRTG or
Sentinel directly, redirects requests, or promotes native/retention/round claims.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('SetCondition','Schedule','MeasureRoutes','Evaluate')][string]$Action,
    [Parameter(Mandatory)][uri]$BaseUri,
    [Parameter(Mandatory)][Microsoft.PowerShell.Commands.WebRequestSession]$WebSession,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$Condition,
    [string]$ProfilePath,
    [string]$HostIdsPath,
    [string]$MatrixRunDirectory,
    [string]$ProfileName,
    [string]$ExpectedRunId,
    [string]$ExpectedSettingsRevision,
    [string]$CookieName='lf_auth',
    [ValidateRange(1,3600)][int]$MaxSeconds=600,
    [ValidateRange(100,10000)][int]$SamplesPerRoute=100,
    [ValidateRange(1,300000)][int]$RequestDeadlineMs=2000
)
$ErrorActionPreference='Stop'
$ip=$null
if($BaseUri.Scheme -cnotin @('http','https') -or -not[Net.IPAddress]::TryParse($BaseUri.DnsSafeHost.Trim('[',']'),[ref]$ip) -or
   -not[Net.IPAddress]::IsLoopback($ip) -or $BaseUri.Port -lt 1 -or $BaseUri.UserInfo -or $BaseUri.Query -or $BaseUri.Fragment){throw 'Web consumer requires an explicit numeric-loopback URL without credentials, query, or fragment.'}
$base=[uri]($BaseUri.AbsoluteUri.TrimEnd('/')+'/')
$out=[IO.Path]::GetFullPath($OutputDirectory);$null=New-Item -ItemType Directory -Path $out -Force
$handler=[Net.Http.HttpClientHandler]::new();$handler.AllowAutoRedirect=$false;$handler.UseCookies=$true;$handler.CookieContainer=$WebSession.Cookies
$client=[Net.Http.HttpClient]::new($handler);$client.Timeout=[Threading.Timeout]::InfiniteTimeSpan
$whole=[Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds($MaxSeconds))
$jsonOptions=[Text.Json.JsonDocumentOptions]::new();$jsonOptions.MaxDepth=32
function New-ApiUri([string]$Path){[uri]::new($base,[string]$Path.TrimStart('/'))}
function Test-IntegerNumber([object]$Value){
    $parsed=0L
    return ($Value -is [ValueType] -and $Value -isnot [bool] -and [long]::TryParse([string]$Value,[Globalization.NumberStyles]::Integer,[Globalization.CultureInfo]::InvariantCulture,[ref]$parsed))
}
function Invoke-Api([string]$Method,[string]$Path,[object]$Body=$null,[int]$Limit=1048576,[int]$RequestMs=30000){
    $req=$null;$resp=$null;$linked=[Threading.CancellationTokenSource]::CreateLinkedTokenSource($whole.Token);$linked.CancelAfter([TimeSpan]::FromSeconds(30));$memory=[IO.MemoryStream]::new();$stream=$null
    try{
        $linked.CancelAfter([Math]::Min(30000,[Math]::Max(1,$RequestMs)))
        $req=[Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::new($Method),(New-ApiUri $Path));$req.Headers.Add('X-Requested-By','LogForesight');$req.Headers.Accept.ParseAdd('application/json')
        if($null -ne $Body){$req.Content=[Net.Http.StringContent]::new(($Body|ConvertTo-Json -Depth 16 -Compress),[Text.Encoding]::UTF8,'application/json')}
        try{$resp=$client.SendAsync($req,[Net.Http.HttpCompletionOption]::ResponseHeadersRead,$linked.Token).GetAwaiter().GetResult()}catch{throw "Web consumer API $Method $Path request failed: $($_.Exception.ToString())"}
        if([int]$resp.StatusCode -ge 300 -and [int]$resp.StatusCode -lt 400){throw 'Web consumer refuses HTTP redirects.'}
        if(-not$resp.IsSuccessStatusCode){throw "Web consumer API $Method $Path returned HTTP $([int]$resp.StatusCode)."}
        if($resp.Content.Headers.ContentType.MediaType -cne 'application/json'){throw 'Web consumer API returned a non-JSON response.'}
        if($resp.Content.Headers.ContentLength -gt $Limit){throw 'Web consumer API declared a response above the bounded byte limit.'}
        $bodyWatch=[Diagnostics.Stopwatch]::StartNew()
        try{$stream=$resp.Content.ReadAsStreamAsync($linked.Token).GetAwaiter().GetResult();$buf=[byte[]]::new(8192)
            $expectedLength=$resp.Content.Headers.ContentLength
            while($null -eq $expectedLength -or $memory.Length -lt $expectedLength){$readCount=$buf.Length;if($null -ne $expectedLength){$readCount=[int][Math]::Min($buf.Length,$expectedLength-$memory.Length)};$n=$stream.ReadAsync($buf,0,$readCount,$linked.Token).GetAwaiter().GetResult();if($n -eq 0){if($null -ne $expectedLength -and $memory.Length -ne $expectedLength){throw 'Web consumer API response ended before its declared Content-Length.'};break};if($memory.Length+$n -gt $Limit){throw 'Web consumer API response exceeded its streaming byte limit.'};$memory.Write($buf,0,$n)}
        }catch{throw "Web consumer API $Method $Path response body read failed after $($bodyWatch.ElapsedMilliseconds)ms ($($memory.Length) bytes of $($resp.Content.Headers.ContentLength)): $($_.Exception.Message)"}
        $doc=[Text.Json.JsonDocument]::Parse([ReadOnlyMemory[byte]]$memory.ToArray(),$jsonOptions);try{return $doc.RootElement.Clone()}finally{$doc.Dispose()}
    }finally{if($stream){$stream.Dispose()};$memory.Dispose();if($resp){$resp.Dispose()};if($req){$req.Dispose()};$linked.Dispose()}
}
function Get-Data($Document){if($Document.ValueKind -ne 'Object'){throw 'Web API response omitted its actual data envelope.'};try{return $Document.GetProperty('data')}catch{throw 'Web API response omitted its actual data envelope.'}}
function Save-Receipt([string]$Name,[object]$Value){$path=Join-Path $out $Name;$temp=$path+'.tmp';[IO.File]::WriteAllText($temp,($Value|ConvertTo-Json -Depth 20),[Text.UTF8Encoding]::new($false));[IO.File]::Move($temp,$path,$true);return $path}
function Get-Segment([string]$ProfileFile,[string]$HostIdFile){
    $profile=Get-Content -LiteralPath $ProfileFile -Raw|ConvertFrom-Json -Depth 32
    $expectedIds=@(Get-Content -LiteralPath $HostIdFile|ForEach-Object{[long]::Parse($_,[Globalization.CultureInfo]::InvariantCulture)}|Sort-Object -Unique)
    if($expectedIds.Count -ne 3000){throw 'Owned matrix host list must contain exactly 3,000 unique IDs.'}
    $current=Get-Data (Invoke-Api 'GET' 'api/admin/hosts/all' $null 2MB);if($current.ValueKind -ne 'Array'){throw 'Actual Admin hosts/all response is not the expected HostOptionDto array.'}
    $rows=@($current.EnumerateArray());if($rows.Count -ne 3000){throw 'Current Web host catalogue does not contain exactly 3,000 active hosts.'}
    $actualIds=@($rows|ForEach-Object{$_.GetProperty('hostId').GetInt64()}|Sort-Object -Unique)
    if(($actualIds -join ',') -cne ($expectedIds -join ',')){throw 'Current Web host IDs differ from the owned 3,000-host profile.'}
    $addresses=[Collections.Generic.List[uint32]]::new()
    foreach($row in $rows){$address=$row.GetProperty('ipAddress');$parsed=$null;if($address.ValueKind -ne 'String' -or -not[Net.IPAddress]::TryParse($address.GetString(),[ref]$parsed) -or $parsed.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork){throw 'Current owned host list has a missing or non-IPv4 address.'};$bytes=$parsed.GetAddressBytes();$addresses.Add([uint32](([uint64]$bytes[0] -shl 24) -bor ([uint64]$bytes[1] -shl 16) -bor ([uint64]$bytes[2] -shl 8) -bor $bytes[3]))}
    $first=$addresses[0];$common=32
    foreach($value in $addresses){for($bits=0;$bits -lt $common;$bits++){ $shift=31-$bits;if((($first -shr $shift)-band 1) -ne (($value -shr $shift)-band 1)){$common=$bits;break} }}
    if($common -lt 1){throw 'Owned host addresses have no non-empty shared IPv4 segment.'}
    $mask=if($common -eq 0){[uint32]0}elseif($common -eq 32){[uint32]::MaxValue}else{[uint32](([uint64]4294967295 -shl (32-$common))-band [uint64]4294967295)};$network=[uint32]($first -band $mask)
    $segment="{0}.{1}.{2}.{3}/{4}" -f (($network -shr 24)-band 255),(($network -shr 16)-band 255),(($network -shr 8)-band 255),($network-band 255),$common
    $previewPath='api/admin/schedule/run-preview?scope=segment&segment='+$segment+'&onlyMissingOrFailed=false'
    $preview=Get-Data (Invoke-Api 'GET' $previewPath $null 64KB);$count=$preview.GetProperty('hostCount').GetInt32()
    if($count -ne 3000){throw "Current explicit CIDR $segment resolves to $count hosts, not the exact owned 3,000; refuse to schedule."}
    return [pscustomobject]@{segment=$segment;hostCount=$count;hostIdsSha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($expectedIds -join "`n"))));profile=$profile}
}
function Assert-ApiEnvelope($Document){if($Document.ValueKind -ne 'Object'){throw 'Actual Web API envelope is not an object.'};$data=$Document.GetProperty('data');if($data.ValueKind -ne 'Object' -and $data.ValueKind -ne 'Array'){throw 'Actual Web API data envelope has an unexpected DTO shape.'};return $data}
try{
    switch($Action){
        'SetCondition' {
            if($Condition -notin @('netiq-only','combined')){throw 'Condition must be one of the two fixed matrix values.'}
            $enabled=$Condition -ceq 'combined';$current=Assert-ApiEnvelope (Invoke-Api 'GET' 'api/admin/settings' $null 128KB)
            $revision=$current.GetProperty('revision').GetString();$currentEnabled=$current.GetProperty('prtgEnabled').GetBoolean()
            if([string]::IsNullOrWhiteSpace($revision)){throw 'Current Settings DTO omitted its revision.'}
            if([string]::IsNullOrWhiteSpace($ExpectedSettingsRevision) -or $revision -cne $ExpectedSettingsRevision){throw 'Settings revision changed since the previous qualification fence; refuse this matrix condition.'}
            if($currentEnabled -ne $enabled){$updated=Assert-ApiEnvelope (Invoke-Api 'PUT' 'api/admin/settings/prtg' @{expectedRevision=$revision;prtgEnabled=$enabled} 256KB);if($updated.GetProperty('prtgEnabled').GetBoolean() -ne $enabled){throw 'Settings CAS did not save the requested PRTG condition.'};$revision=$updated.GetProperty('revision').GetString()}
            [pscustomobject]@{schema='round53-condition-settings-v1';condition=$Condition;settingsRevision=$revision;prtgEnabled=$enabled;changed=($currentEnabled -ne $enabled);nativeSourceVerified=$false;capacityAccepted=$false;retentionAccepted=$false;wholeRoundAccepted=$false}
        }
        'Schedule' {
            if(-not(Test-Path -LiteralPath $ProfilePath -PathType Leaf) -or -not(Test-Path -LiteralPath $HostIdsPath -PathType Leaf)){throw 'Schedule requires its current owned profile and exact host list.'}
            $target=Get-Segment $ProfilePath $HostIdsPath
            $before=Assert-ApiEnvelope (Invoke-Api 'GET' 'api/admin/schedule/status' $null 128KB)
            $previous=if($before.GetProperty('lastRunEndedAt').ValueKind -eq 'String'){$before.GetProperty('lastRunEndedAt').GetString()}else{''}
            $previousTimingRunId=0L;$priorTiming=$before.GetProperty('netiqHostDayTiming');if($priorTiming.ValueKind -eq 'Object'){$previousTimingRunId=$priorTiming.GetProperty('runId').GetInt64()}
            $options=Assert-ApiEnvelope (Invoke-Api 'GET' 'api/admin/schedule/options' $null 128KB);$minutes=0.0
            foreach($window in $options.GetProperty('windows').EnumerateArray()){$start=[TimeOnly]::ParseExact($window.GetProperty('start').GetString(),'HH:mm',[Globalization.CultureInfo]::InvariantCulture);$end=[TimeOnly]::ParseExact($window.GetProperty('end').GetString(),'HH:mm',[Globalization.CultureInfo]::InvariantCulture);$delta=($end.ToTimeSpan()-$start.ToTimeSpan()).TotalMinutes;if($delta -le 0){$delta+=1440};$minutes+=$delta}
            if($minutes -le 0){throw 'Schedule options contain no valid configured window.'}
            if($before.GetProperty('isRunning').GetBoolean()){throw 'Schedule API reports an existing run; no matrix run was submitted.'}
            $started=[DateTimeOffset]::UtcNow;$watch=[Diagnostics.Stopwatch]::StartNew()
            $request=@{scope='segment';segment=$target.segment;onlyMissingOrFailed=$false;rerunMode='All';includePrtgValues=$false}
            $accepted=Assert-ApiEnvelope (Invoke-Api 'POST' 'api/admin/schedule/run' $request 128KB)
            if(-not$accepted.GetProperty('started').GetBoolean()){throw 'Schedule API refused to start this matrix cell.'}
            $null=Save-Receipt 'schedule-start.json' ([ordered]@{schema='round53-schedule-run-v1';startedUtc=$started.ToString('o');segment=$target.segment;hostCount=$target.hostCount;hostIdsSha256=$target.hostIdsSha256;configuredWindowMinutes=$minutes;allowed75PercentMs=$minutes*60000*0.75;response=$accepted.GetRawText();nativeSourceVerified=$false;capacityAccepted=$false;retentionAccepted=$false;wholeRoundAccepted=$false})
            $complete=$null
            while($watch.Elapsed.TotalSeconds -lt $MaxSeconds){$whole.Token.ThrowIfCancellationRequested();Start-Sleep -Milliseconds 500;$status=Assert-ApiEnvelope (Invoke-Api 'GET' 'api/admin/schedule/status' $null 128KB);$ended=if($status.GetProperty('lastRunEndedAt').ValueKind -eq 'String'){$status.GetProperty('lastRunEndedAt').GetString()}else{''};$running=$status.GetProperty('isRunning').GetBoolean();if(-not$running -and -not[string]::IsNullOrWhiteSpace($ended) -and $ended -cne $previous){$complete=$status;break}}
            if($null -eq $complete){throw "Schedule did not finish within $MaxSeconds seconds; keep this cell receipt and inspect before explicit retry."}
            if(-not$complete.GetProperty('lastRunSuccess').GetBoolean()){throw 'Schedule completed unsuccessfully.'}
            $timing=$complete.GetProperty('netiqHostDayTiming');if($timing.ValueKind -ne 'Object'){throw 'Completed schedule omitted this run aggregate NetIQ timing snapshot.'}
            $timingRunId=$timing.GetProperty('runId').GetInt64();$outcomeRunId=0L;if($complete.GetProperty('lastRunBatchRunId').ValueKind -eq 'Number'){$outcomeRunId=$complete.GetProperty('lastRunBatchRunId').GetInt64()};if($timingRunId -le 0 -or $timingRunId -eq $previousTimingRunId -or $outcomeRunId -le 0 -or $outcomeRunId -ne $timingRunId){throw 'Completed schedule outcome must expose matching persistent BatchRun and timing IDs for this trigger.'}
            $timingStarted=[DateTimeOffset]::MinValue;$timingCompleted=[DateTimeOffset]::MinValue;$outcomeEnded=[DateTimeOffset]::MinValue
            if(-not[DateTimeOffset]::TryParse($timing.GetProperty('startedAtUtc').GetString(),[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::RoundtripKind,[ref]$timingStarted) -or -not[DateTimeOffset]::TryParse($timing.GetProperty('completedAtUtc').GetString(),[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::RoundtripKind,[ref]$timingCompleted) -or -not[DateTimeOffset]::TryParse($complete.GetProperty('lastRunEndedAt').GetString(),[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::RoundtripKind,[ref]$outcomeEnded)){throw 'Current schedule outcome omitted a valid timing or end timestamp.'}
            if($timingStarted -lt $started -or $timingCompleted -lt $started -or $timingCompleted -lt $timingStarted -or $outcomeEnded -lt $timingCompleted -or [string]::IsNullOrWhiteSpace($complete.GetProperty('lastRunTriggerText').GetString())){throw "Timing and last-run outcome do not correlate to this explicit trigger boundary (trigger=$($started.ToString('o')); timingStart=$($timingStarted.ToString('o')); timingEnd=$($timingCompleted.ToString('o')); outcomeEnd=$($outcomeEnded.ToString('o')); triggerText=$($complete.GetProperty('lastRunTriggerText').GetString()))."}
            $watch.Stop();$stateData=$complete.GetRawText()|ConvertFrom-Json -Depth 32;$schedule=[ordered]@{schema='round53-schedule-run-v1';startedUtc=$started.ToString('o');completedUtc=[DateTimeOffset]::UtcNow.ToString('o');batchRunId=$timingRunId;elapsedMs=$watch.ElapsedMilliseconds;configuredWindowMinutes=$minutes;allowed75PercentMs=$minutes*60000*0.75;segment=$target.segment;hostCount=$target.hostCount;hostIdsSha256=$target.hostIdsSha256;state=$stateData;status='observed-idle';netiqHostDayTiming=$stateData.netiqHostDayTiming;nativeSourceVerified=$false;capacityAccepted=$false;retentionAccepted=$false;wholeRoundAccepted=$false};$null=Save-Receipt 'schedule-complete.json' $schedule
            [pscustomobject]$schedule
        }
        'MeasureRoutes' {
            $ids=@(Get-Content -LiteralPath $HostIdsPath|ForEach-Object{[long]::Parse($_,[Globalization.CultureInfo]::InvariantCulture)});if($ids.Count -ne 3000 -or @($ids|Sort-Object -Unique).Count -ne 3000){throw 'Route measurement requires the exact 3,000 owned host IDs.'}
            $ownedIds=[Collections.Generic.HashSet[long]]::new([long[]]$ids);$rows=[Collections.Generic.List[object]]::new();$watch=[Diagnostics.Stopwatch]::StartNew()
            for($index=0;$index -lt $SamplesPerRoute;$index++){foreach($route in @('host-list','host-detail')){$watch.Restart();$code=0;$path=if($route -eq 'host-list'){'api/hosts'}else{'api/host-detail/'+$ids[$index%$ids.Count]};try{$routeResponse=Invoke-Api 'GET' $path $null 2MB $RequestDeadlineMs
                if($routeResponse.ValueKind -ne 'Object' -or $routeResponse.GetProperty('success').ValueKind -ne 'True'){throw 'Route API did not report success.'}
                $routeData=Assert-ApiEnvelope $routeResponse;if($routeData.ValueKind -ne 'Object'){throw 'Route API omitted its object DTO.'}
                if($route -eq 'host-list'){
                    $items=$routeData.GetProperty('items');if($routeData.GetProperty('total').GetInt32() -ne 3000 -or $routeData.GetProperty('truncated').ValueKind -ne 'False' -or $items.ValueKind -ne 'Array' -or $items.GetArrayLength() -ne 3000){throw 'Host list is partial or truncated.'}
                    $seen=[Collections.Generic.HashSet[long]]::new();foreach($item in $items.EnumerateArray()){$routeId=$item.GetProperty('hostId').GetInt64();if(-not$ownedIds.Contains($routeId) -or -not$seen.Add($routeId)){throw 'Host list identities differ from the owned scope.'}}
                }elseif($routeData.GetProperty('hostId').GetInt64() -ne $ids[$index%$ids.Count]){throw 'Host detail identity differs from the requested host.'}
                $code=200}catch{$code=0};$elapsed=$watch.ElapsedMilliseconds;$rows.Add([pscustomobject]@{route=$route;statusCode=$code;elapsedMs=$elapsed})}}
            $routes=@($rows|Group-Object route|ForEach-Object{$samples=@($_.Group|Sort-Object elapsedMs);$n=$samples.Count;$p95=[int][Math]::Ceiling($n*0.95)-1;[pscustomobject]@{route=$_.Name;denominator=$n;successes=@($_.Group|Where-Object{$_.statusCode -eq 200}).Count;failures=@($_.Group|Where-Object{$_.statusCode -ne 200}).Count;p95Ms=$samples[$p95].elapsedMs;maxMs=($samples|Measure-Object elapsedMs -Maximum).Maximum;deadlineMs=$RequestDeadlineMs;samples=$samples}})
            $measurement=[ordered]@{schema='round53-route-measurement-v1';startedUtc=[DateTimeOffset]::UtcNow.ToString('o');requestedPerRoute=$SamplesPerRoute;requests=$rows.Count;routes=$routes;status='measured';nativeSourceVerified=$false;capacityAccepted=$false;retentionAccepted=$false;wholeRoundAccepted=$false};$null=Save-Receipt 'route-measurement.json' $measurement;[pscustomobject]$measurement
        }
        'Evaluate' {
            $matrixRoot=[IO.Path]::GetFullPath($MatrixRunDirectory);$conditions=@('netiq-only','combined','combined','netiq-only','netiq-only','combined');$samples=[Collections.Generic.List[object]]::new()
            if([string]::IsNullOrWhiteSpace($ExpectedRunId)){throw 'Evaluate requires the exact owned run ID; it never adopts prior timing or matrix artifacts.'}
            for($i=0;$i -lt 6;$i++){$dir=Join-Path $matrixRoot ("condition-{0:D2}-{1}" -f ($i+1),$conditions[$i]);$ownerPath=Join-Path $dir 'condition-owner.json';if(-not(Test-Path -LiteralPath $ownerPath -PathType Leaf)){throw "Matrix condition $($i+1) has no owner receipt."};$owner=Get-Content -LiteralPath $ownerPath -Raw|ConvertFrom-Json -Depth 16;if($owner.schema -cne 'round53-condition-attempt-v1' -or $owner.runId -cne $ExpectedRunId -or $owner.profile -cne $ProfileName -or $owner.conditionIndex -ne ($i+1) -or $owner.condition -cne $conditions[$i] -or $owner.state -cne 'complete'){throw "Matrix condition $($i+1) owner receipt does not match this exact run/profile/condition."};$schedule=Get-Content -LiteralPath (Join-Path $dir 'schedule-complete.json') -Raw|ConvertFrom-Json -Depth 32;$routes=Get-Content -LiteralPath (Join-Path $dir 'route-measurement.json') -Raw|ConvertFrom-Json -Depth 32;$fencePath=Join-Path $dir 'live-qualification-fence-final.json';$fence=if(Test-Path -LiteralPath $fencePath -PathType Leaf){Get-Content -LiteralPath $fencePath -Raw|ConvertFrom-Json -Depth 32}else{$null};$fenceValid=($null -ne $fence -and $fence.status -eq 'current' -and $fence.rawRows -eq 15000 -and $fence.profileRows -eq 15000 -and -not[string]::IsNullOrWhiteSpace([string]$fence.proofFenceSha256) -and -not$fence.nativeSourceVerified -and -not$fence.formalAcceptance -and -not$fence.wholeRoundAccepted);$fraction=$schedule.elapsedMs/($schedule.configuredWindowMinutes*60000);$timing=$schedule.netiqHostDayTiming;$timingValid=($null -ne $timing -and $timing.status -eq 'complete' -and $timing.expectedHostDays -gt 0 -and $timing.observedHostDays -eq $timing.expectedHostDays -and $timing.committedHostDays -eq $timing.expectedHostDays -and $timing.durationSampleCount -eq $timing.expectedHostDays -and $timing.sourceFailedHostDays -eq 0 -and $timing.unknownHostDays -eq 0 -and $timing.duplicateHostDays -eq 0 -and -not $timing.overflow -and $null -ne $timing.p95Milliseconds -and [double]::IsFinite([double]$timing.p95Milliseconds));$samples.Add([pscustomobject]@{condition=$conditions[$i];elapsedMs=$schedule.elapsedMs;scheduleFraction=$fraction;success=($schedule.status -eq 'observed-idle' -and $schedule.state.lastRunSuccess -eq $true -and $schedule.hostCount -eq 3000);timingValid=$timingValid;liveFenceValid=$fenceValid;expectedHostDays=if($timing){$timing.expectedHostDays}else{0};hostDayP95Ms=if($timingValid){[double]$timing.p95Milliseconds}else{$null};routeReceiptValid=($routes.schema -ceq 'round53-route-measurement-v1' -and $routes.status -ceq 'measured' -and (Test-IntegerNumber $routes.requestedPerRoute) -and $routes.requestedPerRoute -ge 100 -and $routes.requestedPerRoute -le 10000 -and (Test-IntegerNumber $routes.requests) -and $routes.requests -eq ($routes.requestedPerRoute*2));requestedPerRoute=$routes.requestedPerRoute;routes=@($routes.routes)})}
            $pairs=@(@(0,1),@(3,2),@(4,5)|ForEach-Object{$baseSample=$samples[$_[0]];$combinedSample=$samples[$_[1]];$relative=$null;$pass=$false;if($baseSample.timingValid -and $combinedSample.timingValid -and $baseSample.expectedHostDays -eq $combinedSample.expectedHostDays -and $baseSample.hostDayP95Ms -gt 0){$relative=($combinedSample.hostDayP95Ms-$baseSample.hostDayP95Ms)/$baseSample.hostDayP95Ms;$pass=($relative -le 0.10)};[pscustomobject]@{baselineIndex=$_[0]+1;combinedIndex=$_[1]+1;baselineScheduleMs=$baseSample.elapsedMs;combinedScheduleMs=$combinedSample.elapsedMs;baselineExpectedHostDays=$baseSample.expectedHostDays;combinedExpectedHostDays=$combinedSample.expectedHostDays;baselineHostDayP95Ms=$baseSample.hostDayP95Ms;combinedHostDayP95Ms=$combinedSample.hostDayP95Ms;relativeP95Increase=$relative;pass=$pass}})
            $routeRows=[Collections.Generic.List[object]]::new();$routeSetRows=[Collections.Generic.List[object]]::new();$expectedRouteNames=@('host-list','host-detail')
            for($i=0;$i -lt 6;$i++){
                $cellRoutes=@($samples[$i].routes);$names=@($cellRoutes|ForEach-Object{[string]$_.route})
                $routeSetValid=($samples[$i].routeReceiptValid -and $cellRoutes.Count -eq 2 -and @($names|Sort-Object -Unique).Count -eq 2 -and $names -contains 'host-list' -and $names -contains 'host-detail')
                $routeSetRows.Add([pscustomobject]@{conditionIndex=$i+1;condition=$conditions[$i];routeCount=$cellRoutes.Count;pass=$routeSetValid})
                foreach($routeName in $expectedRouteNames){
                    $matches=@($cellRoutes|Where-Object{[string]$_.route -ceq $routeName})
                    if($matches.Count -ne 1){$routeRows.Add([pscustomobject]@{conditionIndex=$i+1;condition=$conditions[$i];route=$routeName;denominator=0;successes=0;failures=0;p95Ms=$null;pass=$false});continue}
                    $route=$matches[0];$denominator=0L;$successes=0L;$failures=0L;$p95=[double]::NaN
                    $denominatorProperty=$route.PSObject.Properties['denominator'];$successesProperty=$route.PSObject.Properties['successes'];$failuresProperty=$route.PSObject.Properties['failures'];$p95Property=$route.PSObject.Properties['p95Ms']
                    $denominatorValid=($null -ne $denominatorProperty -and $denominatorProperty.Value -is [ValueType] -and $denominatorProperty.Value -isnot [bool] -and [long]::TryParse([string]$denominatorProperty.Value,[Globalization.NumberStyles]::Integer,[Globalization.CultureInfo]::InvariantCulture,[ref]$denominator))
                    $successesValid=($null -ne $successesProperty -and $successesProperty.Value -is [ValueType] -and $successesProperty.Value -isnot [bool] -and [long]::TryParse([string]$successesProperty.Value,[Globalization.NumberStyles]::Integer,[Globalization.CultureInfo]::InvariantCulture,[ref]$successes))
                    $failuresValid=($null -ne $failuresProperty -and $failuresProperty.Value -is [ValueType] -and $failuresProperty.Value -isnot [bool] -and [long]::TryParse([string]$failuresProperty.Value,[Globalization.NumberStyles]::Integer,[Globalization.CultureInfo]::InvariantCulture,[ref]$failures))
                    $p95Valid=($null -ne $p95Property -and $null -ne $p95Property.Value -and $p95Property.Value -is [ValueType] -and $p95Property.Value -isnot [bool] -and [double]::TryParse([string]$p95Property.Value,[Globalization.NumberStyles]::Float,[Globalization.CultureInfo]::InvariantCulture,[ref]$p95) -and [double]::IsFinite($p95))
                    $consistent=($denominatorValid -and $successesValid -and $failuresValid -and $p95Valid -and $denominator -ge 100 -and $successes -ge 0 -and $failures -ge 0 -and $successes+$failures -eq $denominator -and $failures -eq 0 -and $successes -eq $denominator -and $p95 -ge 0 -and $p95 -le 2000)
                    $rawSamples=@($route.samples);$elapsedValues=[Collections.Generic.List[long]]::new();$rawSuccesses=0L
                    $rawValid=($denominatorValid -and $denominator -eq $samples[$i].requestedPerRoute -and $rawSamples.Count -eq $denominator -and (Test-IntegerNumber $route.maxMs) -and (Test-IntegerNumber $route.deadlineMs) -and $route.deadlineMs -ge 1 -and $route.deadlineMs -le 300000)
                    foreach($raw in $rawSamples){
                        if($null -eq $raw -or $raw.route -cne $routeName -or -not(Test-IntegerNumber $raw.elapsedMs) -or $raw.elapsedMs -lt 0 -or -not(Test-IntegerNumber $raw.statusCode) -or $raw.statusCode -notin @(0,200)){$rawValid=$false;continue}
                        $elapsedValues.Add([long]$raw.elapsedMs);if($raw.statusCode -eq 200){$rawSuccesses++}
                    }
                    $computedP95=$null
                    if($rawValid -and $elapsedValues.Count -gt 0){
                        $sorted=@($elapsedValues|Sort-Object);$computedP95=$sorted[[int][Math]::Ceiling($sorted.Count*0.95)-1]
                        $rawValid=($rawSuccesses -eq $successes -and ($sorted.Count-$rawSuccesses) -eq $failures -and $computedP95 -eq $p95 -and $sorted[-1] -eq $route.maxMs)
                    }
                    $routeRows.Add([pscustomobject]@{conditionIndex=$i+1;condition=$conditions[$i];route=$routeName;denominator=$denominator;successes=$successes;failures=$failures;p95Ms=if($p95Valid){$p95}else{$null};rawSampleCount=$rawSamples.Count;rawSamplesValid=$rawValid;computedP95Ms=$computedP95;pass=($routeSetValid -and $consistent -and $rawValid)})
                }
            }
            $routePairs=[Collections.Generic.List[object]]::new()
            foreach($pair in @(@(0,1),@(3,2),@(4,5))){foreach($routeName in $expectedRouteNames){
                $baseline=@($routeRows|Where-Object { $_.conditionIndex -eq ($pair[0]+1) -and $_.route -ceq $routeName })[0]
                $combined=@($routeRows|Where-Object { $_.conditionIndex -eq ($pair[1]+1) -and $_.route -ceq $routeName })[0]
                $relative=$null;$pass=$false;$reason='route-samples-invalid'
                if($baseline.pass -and $combined.pass){
                    if($baseline.p95Ms -gt 0){$relative=($combined.p95Ms-$baseline.p95Ms)/$baseline.p95Ms;$pass=($relative -le 0.10);$reason=if($pass){'within-relative-p95-limit'}else{'route-relative-p95-limit-exceeded'}}
                    else{$reason='route-baseline-unusable'}
                }
                $routePairs.Add([pscustomobject]@{baselineIndex=$pair[0]+1;combinedIndex=$pair[1]+1;route=$routeName;baselineP95Ms=$baseline.p95Ms;combinedP95Ms=$combined.p95Ms;relativeP95Increase=$relative;reason=$reason;pass=$pass})
            }}
            $windowRows=@(for($i=0;$i -lt 6;$i++){[pscustomobject]@{conditionIndex=$i+1;elapsedMs=$samples[$i].elapsedMs;fraction=$samples[$i].scheduleFraction;pass=($samples[$i].scheduleFraction -le 0.75)}})
            $result=[ordered]@{schema='round53-mixed-matrix-evaluation-v1';workloadProfile=$ProfileName;fixtureOnly=($ProfileName -eq 'controlled-loopback-3000');nativeSourceVerified=$false;formalAcceptance=$false;wholeRoundAccepted=$false;n6Overall=$false;fixedOrder=$conditions;pairs=$pairs;routePairs=$routePairs;routeSetChecks=$routeSetRows;routeSamples=$routeRows;scheduleWindows=$windowRows;metricThresholds=@{relativeP95IncreaseMax=0.10;windowFractionMax=0.75;routeP95MaxMs=2000;routeDenominatorMin=100};observableSubchecksPass=($routeRows.pass -notcontains $false -and $routeSetRows.pass -notcontains $false -and $routeRows.Count -eq 12 -and $routePairs.Count -eq 6 -and $routePairs.pass -notcontains $false -and $windowRows.pass -notcontains $false -and $samples.success -notcontains $false -and $samples.timingValid -notcontains $false -and $samples.liveFenceValid -notcontains $false);metricChecksPass=($pairs.pass -notcontains $false -and $routeRows.pass -notcontains $false -and $routeSetRows.pass -notcontains $false -and $routeRows.Count -eq 12 -and $routePairs.Count -eq 6 -and $routePairs.pass -notcontains $false -and $windowRows.pass -notcontains $false -and $samples.liveFenceValid -notcontains $false);netiqP95Gate='public-aggregate-hostday-append-duration-exact-denominator';sourceQualification=@{qualified=$false;reason='current profile proofs and six schedule measurements do not establish historical native-source coverage or 180-day retention'}}
            $null=Save-Receipt 'matrix-evaluation.json' $result;[pscustomobject]$result
        }
    }
}finally{$client.Dispose();$whole.Dispose()}
