[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion -lt [Version]'7.4') { throw 'Requires PowerShell 7.4+.' }
$stageRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$modeScript = Get-Content -LiteralPath (Join-Path $stageRoot 'Set-UserMatrixMode.ps1') -Raw
if ($modeScript -notmatch "'netiq-audit-cleared-events'") { throw 'Set-UserMatrixMode does not allow the bounded audit-clear mode.' }
$project = Join-Path $stageRoot 'source-stub\Round53ControlledSources.csproj'
$output = Join-Path $stageRoot 'source-stub\out'
$dotnet = (Get-Command dotnet.exe -CommandType Application -ErrorAction Stop).Source
$build = & $dotnet build $project -c Release --nologo -o $output 2>&1
if ($LASTEXITCODE -ne 0) { throw "Source helper build failed: $($build -join ' ')" }
$dll = Join-Path $output 'Round53ControlledSources.dll'
$runId = [guid]::NewGuid().ToString('N')
$runRoot = Join-Path (Join-Path $stageRoot 'runs') "stub-smoke-$runId"
[IO.Directory]::CreateDirectory($runRoot) | Out-Null
$httpListener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0); $httpListener.Start(); $httpPort = ([Net.IPEndPoint]$httpListener.LocalEndpoint).Port; $httpListener.Stop()
do { $smtpListener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0); $smtpListener.Start(); $smtpPort = ([Net.IPEndPoint]$smtpListener.LocalEndpoint).Port; $smtpListener.Stop() } while ($smtpPort -eq $httpPort)
$httpUrl = "http://127.0.0.1:$httpPort"
$httpModeFile = Join-Path $runRoot 'source-mode.txt'; $smtpModeFile = Join-Path $runRoot 'smtp-mode.txt'
[IO.File]::WriteAllText($httpModeFile,'netiq-risk-events',[Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText($smtpModeFile,'accept',[Text.UTF8Encoding]::new($false))
$arguments = @($dll,$runId,$httpUrl,[string]$smtpPort,$httpModeFile,$smtpModeFile,$runRoot)
$argumentLine = ($arguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
$expectedCommandLine = '"' + [IO.Path]::GetFullPath($dotnet) + '" ' + $argumentLine
$process = Start-Process -FilePath $dotnet -ArgumentList $argumentLine -WorkingDirectory $output -WindowStyle Hidden -RedirectStandardOutput (Join-Path $runRoot 'stub.stdout.log') -RedirectStandardError (Join-Path $runRoot 'stub.stderr.log') -PassThru
try {
    $ready = $false
    for ($i = 0; $i -lt 60; $i++) {
        if ($process.HasExited) { break }
        try { $health = Invoke-RestMethod -Uri "$httpUrl/__fixture/health" -TimeoutSec 1; if ($health.runId -eq $runId) { $ready = $true; break } } catch { }
        Start-Sleep -Milliseconds 100
    }
    if (!$ready) { throw 'Loopback HTTP fixture did not become ready.' }
    $cim = Get-CimInstance Win32_Process -Filter "ProcessId = $($process.Id)" -ErrorAction Stop
    if ([IO.Path]::GetFullPath($cim.ExecutablePath) -ine [IO.Path]::GetFullPath($dotnet) -or [string]$cim.CommandLine -cne $expectedCommandLine) { throw 'Smoke helper process identity mismatch.' }
    $auth = Invoke-RestMethod -Method Post -Uri "$httpUrl/SentinelAuthServices/auth/tokens" -Body '{}' -ContentType 'application/json'
    $searchBody = '{"start":"2026-10-10T00:00:00Z","end":"2026-10-11T00:00:00Z","filter":"192.0.2.1"}'
    $null = Invoke-RestMethod -Method Post -Uri "$httpUrl/SentinelRESTServices/objects/event-search" -Body $searchBody -ContentType 'application/json'
    $search = Invoke-RestMethod -Uri "$httpUrl/SentinelRESTServices/objects/event-search/round53-job"
    $events = Invoke-RestMethod -Uri "$httpUrl/SentinelRESTServices/objects/event"
    if ($auth.Token -ne 'round53-loopback-token' -or $search.found -ne 12 -or @($events.events).Count -ne 12) { throw 'NetIQ Sentinel REST protocol fixture contract failed.' }    $riskEvents = @($events.events)
    foreach ($event in $riskEvents) {
        if ($event.rv40 -ne '4625' -or
            $event.repip -ne '192.0.2.1' -or $event.obssvcname -ne 'Microsoft-Windows-Security-Auditing' -or
            $event.rv150 -ne 'Security' -or $event.xdasoutcome -eq '0' -or
            [DateTimeOffset]::Parse($event.dt).ToUniversalTime() -lt [DateTimeOffset]'2026-10-10T00:00:00Z' -or
            [DateTimeOffset]::Parse($event.dt).ToUniversalTime() -ge [DateTimeOffset]'2026-10-11T00:00:00Z') {
            throw 'Risk-event source row does not match the bounded Windows 4625 host/day protocol shape.'
        }
    }

    # This separate mode emits exactly one synthetic Security 1102 audit-clear event;
    # it does not add rows to the existing 12-event 4625 risk scenario.
    [IO.File]::WriteAllText($httpModeFile,'netiq-audit-cleared-events',[Text.UTF8Encoding]::new($false))
    $auditSearchBody = '{"start":"2026-10-10T00:00:00Z","end":"2026-10-11T00:00:00Z","filter":"192.0.2.1"}'
    $null = Invoke-RestMethod -Method Post -Uri "$httpUrl/SentinelRESTServices/objects/event-search" -Body $auditSearchBody -ContentType 'application/json'
    $auditSearch = Invoke-RestMethod -Uri "$httpUrl/SentinelRESTServices/objects/event-search/round53-job"
    $auditResult = Invoke-RestMethod -Uri "$httpUrl/SentinelRESTServices/objects/event"
    $auditEvents = @($auditResult.events)
    if ($auditSearch.found -ne 1 -or $auditSearch.avail -ne 1 -or $auditEvents.Count -ne 1) {
        throw 'Audit-clear mode must return exactly one event in the bounded Sentinel search.'
    }
    $auditEvent = $auditEvents[0]
    if ($auditEvent.rv40 -ne '1102' -or $auditEvent.rv150 -ne 'Security' -or
        $auditEvent.obssvcname -ne 'Microsoft-Windows-Security-Auditing' -or
        $auditEvent.repip -ne '192.0.2.1' -or $auditEvent.r53FixtureSourceKind -ne 'synthetic' -or
        $auditEvent.msg -notmatch 'Synthetic loopback fixture event' -or
        [DateTimeOffset]::Parse($auditEvent.dt).ToUniversalTime() -lt [DateTimeOffset]'2026-10-10T00:00:00Z' -or
        [DateTimeOffset]::Parse($auditEvent.dt).ToUniversalTime() -ge [DateTimeOffset]'2026-10-11T00:00:00Z') {
        throw 'Audit-clear event must preserve the Windows Security 1102 source shape and explicit synthetic marker.'
    }
    $auditRequest = @(Get-Content -LiteralPath (Join-Path $runRoot 'source-requests.jsonl') | ForEach-Object { $_ | ConvertFrom-Json } |
        Where-Object { $_.kind -eq 'netiq' -and $_.mode -eq 'netiq-audit-cleared-events' })
    if ($auditRequest.Count -ne 3) { throw 'Audit-clear HTTP exchange did not record the bounded search/status/result request sequence.' }

    # Exercise the late-arrival source shape as a real request sequence: initially no event,
    # then the same bounded query is eligible to receive an event after the mode is released.
    $lateSearchBody = '{"start":"2026-10-10T00:00:00Z","end":"2026-10-11T00:00:00Z","filter":"192.0.2.2"}'
    [IO.File]::WriteAllText($httpModeFile,'late-events-held',[Text.UTF8Encoding]::new($false))
    $null = Invoke-RestMethod -Method Post -Uri "$httpUrl/SentinelRESTServices/objects/event-search" -Body $lateSearchBody -ContentType 'application/json'
    $heldSearch = Invoke-RestMethod -Uri "$httpUrl/SentinelRESTServices/objects/event-search/round53-job"
    if ($heldSearch.found -ne 0) { throw 'Late-arrival held mode must return a valid zero-event search.' }
    [IO.File]::WriteAllText($httpModeFile,'late-events-released',[Text.UTF8Encoding]::new($false))
    $null = Invoke-RestMethod -Method Post -Uri "$httpUrl/SentinelRESTServices/objects/event-search" -Body $lateSearchBody -ContentType 'application/json'
    $releasedSearch = Invoke-RestMethod -Uri "$httpUrl/SentinelRESTServices/objects/event-search/round53-job"
    if ($releasedSearch.found -ne 1) { throw 'Late-arrival released mode must return one bounded synthetic event.' }

    # Successful loopback source shapes are based on this repository's exercised PRTG
    # JSON/XML parsers and OpenAI chat-completion fixtures. They are protocol harness
    # responses only; this does not qualify a native PRTG or AI service.
    [IO.File]::WriteAllText($httpModeFile,'prtg-healthy',[Text.UTF8Encoding]::new($false))
    $prtgStatus = Invoke-RestMethod -Uri "$httpUrl/api/status.json?id=0"
    $prtgConnection = Invoke-RestMethod -Uri "$httpUrl/api/table.json?content=sensors&columns=objid&count=1"
    $prtgDevice = Invoke-RestMethod -Uri "$httpUrl/api/table.json?content=devices&columns=objid,device,host,group&count=1"
    $prtgSensor = Invoke-RestMethod -Uri "$httpUrl/api/table.json?content=sensors&columns=objid,parentid,type,status,lastvalue_raw,lastcheck,interval,primarychannel&count=1"
    $prtgChannels = Invoke-RestMethod -Uri "$httpUrl/api/table.json?content=channels&id=5301&columns=objid,name,lastvalue_raw,unit&usecaption=1&count=100"
    $prtgProperty = Invoke-WebRequest -Uri "$httpUrl/api/getobjectproperty.htm?id=5301&name=primarychannel"
    $prtgHistory = Invoke-WebRequest -Uri "$httpUrl/api/historicdata.xml?id=5301&sdate=now-1h&edate=now&avg=0&usecaption=1"
    if ($prtgStatus.'prtg-version' -ne '24.1.92.1554+' -or $prtgConnection.treesize -ne 1 -or $prtgConnection.sensors[0].objid -ne 5301 -or
        $prtgDevice.devices[0].host -ne '192.0.2.3' -or $prtgSensor.sensors[0].status -ne 'Up' -or
        $prtgChannels.channels[0].name -ne 'Ping Time' -or $prtgProperty.Content -notmatch '<result>0</result>' -or
        $prtgHistory.Content -notmatch '<histdata totalcount="1">' -or $prtgHistory.Content -notmatch '<datetime_raw>') {
        throw 'Successful bounded synthetic PRTG response contract failed.'
    }
    [IO.File]::WriteAllText($httpModeFile,'ai-healthy',[Text.UTF8Encoding]::new($false))
    $aiBody = '{"model":"round53-loopback","messages":[{"role":"user","content":"ping"}],"max_tokens":1}'
    $aiResponse = Invoke-RestMethod -Uri "$httpUrl/v1/chat/completions" -Method Post -Body $aiBody -ContentType 'application/json'
    if ($aiResponse.choices[0].message.role -ne 'assistant' -or
        $aiResponse.choices[0].message.content -notmatch 'No real model was called' -or
        $aiResponse.usage.total_tokens -ne 2) { throw 'OpenAI-compatible successful AI probe response contract failed.' }
    [IO.File]::WriteAllText($httpModeFile,'all-sources-healthy',[Text.UTF8Encoding]::new($false))
    $allSourcesHealth = Invoke-RestMethod -Uri "$httpUrl/__fixture/health"
    if ($allSourcesHealth.runId -ne $runId -or !$allSourcesHealth.loopbackOnly) { throw 'All-source healthy mode escaped its run-owned loopback fixture.' }
    [IO.File]::WriteAllText($httpModeFile,'netiq-503',[Text.UTF8Encoding]::new($false))
    $netiqFailure = Invoke-WebRequest -Method Post -Uri "$httpUrl/SentinelAuthServices/auth/tokens" -Body '{}' -ContentType 'application/json' -SkipHttpErrorCheck
    if ($netiqFailure.StatusCode -ne 503) { throw 'NetIQ source failure mode did not return the controlled 503 shape.' }
    [IO.File]::WriteAllText($httpModeFile,'prtg-503',[Text.UTF8Encoding]::new($false))
    $prtgFailure = Invoke-WebRequest -Uri "$httpUrl/api/table.json" -SkipHttpErrorCheck
    if ($prtgFailure.StatusCode -ne 503) { throw 'PRTG controlled failure mode did not return 503.' }
    [IO.File]::WriteAllText($httpModeFile,'netiq-healthy',[Text.UTF8Encoding]::new($false))
    $aiFailure = Invoke-WebRequest -Uri "$httpUrl/v1/chat/completions" -Method Post -Body '{}' -ContentType 'application/json' -SkipHttpErrorCheck
    if ($aiFailure.StatusCode -ne 503) { throw 'AI controlled failure mode did not return 503.' }

    [IO.File]::WriteAllText($smtpModeFile,'reject-next-recipient',[Text.UTF8Encoding]::new($false))
    $rejected = $false
    try { [System.Net.Mail.SmtpClient]::new('127.0.0.1',$smtpPort).Send('sender@loopback.invalid','ops@loopback.invalid','refusal probe','synthetic refusal') }
    catch [System.Net.Mail.SmtpException] { $rejected = $true }
    if (!$rejected) { throw 'SMTP transient recipient rejection was not observed.' }
    [IO.File]::WriteAllText($smtpModeFile,'accept',[Text.UTF8Encoding]::new($false))
    $client = [System.Net.Mail.SmtpClient]::new('127.0.0.1',$smtpPort); $client.EnableSsl = $false; $client.Timeout = 4000
    $client.Send('sender@loopback.invalid','ops@loopback.invalid','Round53 SMTP acceptance','controlled capture'); $client.Dispose()
    $realAddressRejected = $false
    try { $client = [System.Net.Mail.SmtpClient]::new('127.0.0.1',$smtpPort); $client.EnableSsl=$false; $client.Timeout=4000; $client.Send('sender@loopback.invalid','ops@example.com','must be rejected','synthetic safety check'); $client.Dispose() }
    catch [System.Net.Mail.SmtpException] { $realAddressRejected = $true }
    if (!$realAddressRejected) { throw 'SMTP fixture accepted a recipient outside the reserved .invalid domain.' }
    [IO.File]::WriteAllText($smtpModeFile,'drop-after-data',[Text.UTF8Encoding]::new($false))
    $unknown = $false
    try { $client = [System.Net.Mail.SmtpClient]::new('127.0.0.1',$smtpPort); $client.EnableSsl=$false; $client.Timeout=4000; $client.Send('sender@loopback.invalid','ops@loopback.invalid','Round53 unknown commit','connection drop'); $client.Dispose() }
    catch [System.Net.Mail.SmtpException] { $unknown = $true }
    if (!$unknown) { throw 'SMTP post-DATA connection drop did not produce unknown client outcome.' }
    Start-Sleep -Milliseconds 150
    $smtpRows = @(Get-Content -LiteralPath (Join-Path $runRoot 'smtp-captures.jsonl') | ForEach-Object { $_ | ConvertFrom-Json })
    $inbox = Get-Content -LiteralPath (Join-Path $runRoot 'smtp-inbox.eml') -Raw
    $httpRows = @(Get-Content -LiteralPath (Join-Path $runRoot 'source-requests.jsonl') | ForEach-Object { $_ | ConvertFrom-Json })
    $badAddressRows = @($smtpRows | Where-Object {
        $addresses = @()
        if ($_.PSObject.Properties['recipients']) { $addresses += @($_.recipients) }
        if ($_.PSObject.Properties['recipient']) { $addresses += [string]$_.recipient }
        @($addresses | Where-Object { $_ -and $_ -notlike '*.invalid' }).Count -gt 0
    })
    if (@($smtpRows | Where-Object { $_.event -eq 'recipient-refused' }).Count -ne 1 -or
        @($smtpRows | Where-Object { $_.event -eq 'accepted' }).Count -ne 1 -or
        @($smtpRows | Where-Object { $_.event -eq 'connection-dropped-after-data' }).Count -ne 1 -or
        $badAddressRows.Count -gt 0 -or
        $inbox -notmatch 'Round53 SMTP acceptance') { throw 'SMTP acceptance/refusal/unknown-commit sink evidence was incomplete or outside .invalid.' }
    if (@($httpRows | Where-Object { $_.path -match '\?' -or $_.PSObject.Properties.Name -contains 'pathAndQuery' }).Count -gt 0 -or
        @($httpRows.kind | Select-Object -Unique).Count -lt 3) { throw 'HTTP source evidence retained query strings or missed a controlled source kind.' }
    [pscustomobject]@{ Result='PASS'; RunId=$runId; RunRoot=$runRoot; HttpEndpoint=$httpUrl; SmtpEndpoint="127.0.0.1:$smtpPort"; NetiqSyntheticEvents=$search.found; NetiqAuditClearEvents=$auditEvents.Count; NetiqAuditClearEventId=$auditEvent.rv40; NetiqAuditClearSyntheticMarker=$auditEvent.r53FixtureSourceKind; NetiqPrtgAi='synthetic responses/failures passed'; LateArrival='held-zero-then-released-one'; SmtpAcceptRejectUnknownCommit='passed'; RealAddressRejected=$realAddressRejected; Evidence='source-requests.jsonl, smtp-captures.jsonl, smtp-inbox.eml' }
}
finally {
    $process.Refresh()
    if (!$process.HasExited) {
        $current = Get-CimInstance Win32_Process -Filter "ProcessId = $($process.Id)" -ErrorAction Stop
        if ([IO.Path]::GetFullPath($current.ExecutablePath) -ieq [IO.Path]::GetFullPath($dotnet) -and
            [string]$current.CommandLine -ceq $expectedCommandLine -and $process.StartTime.ToUniversalTime().Ticks -gt 0) {
            $process.Kill(); $process.WaitForExit(10000)
        } else { throw 'Refusing to stop a changed smoke helper process.' }
    }
}
