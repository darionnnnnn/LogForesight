[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$WebOutputDirectory,
    [Parameter(Mandatory = $true)][string]$WebAssetsDirectory,
    [Parameter(Mandatory = $true)][string]$SourceFixtureBundlePath,
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9a-f]{40}$')][string]$ExpectedSourceCommit
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion -lt [Version]'7.4') { throw 'Requires PowerShell 7.4+.' }
$stageRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$startScript = Join-Path $stageRoot 'Start-UserMatrixFixture.ps1'
$setModeScript = Join-Path $stageRoot 'Set-UserMatrixMode.ps1'
$stopScript = Join-Path $stageRoot 'Stop-UserMatrixFixture.ps1'
$started = $null
try {
    $started = & $startScript -WebOutputDirectory $WebOutputDirectory -WebAssetsDirectory $WebAssetsDirectory -SourceFixtureBundlePath $SourceFixtureBundlePath -ExpectedSourceCommit $ExpectedSourceCommit
    $manifest = Get-Content -LiteralPath $started.ManifestPath -Raw | ConvertFrom-Json
    if ($manifest.Status -cne 'running' -or $manifest.SyntheticOnly -ne $true -or $manifest.Runtime.ExternalConnectionsEnabled -ne $false -or $manifest.Runtime.ScheduleEnabled -ne $false -or $manifest.Runtime.LocalHostAnalysisEnabled -ne $false) { throw 'Started fixture does not preserve synthetic/offline safety settings, including disabled local host analysis.' }
    $source = $manifest.SourceFixture
    if ($source.DatabaseSha256Before -cne $source.DatabaseSha256After -or $source.KeySha256Before -cne $source.KeySha256After -or
        [string]::IsNullOrWhiteSpace([string]$source.CloneDatabaseSha256BeforeLaunch) -or [string]::IsNullOrWhiteSpace([string]$source.CloneKeySha256BeforeLaunch)) { throw 'Source fixture DB/key immutability or prelaunch clone hash is unproven.' }
    $login = Invoke-WebRequest -Uri $started.LoginUrl -UseBasicParsing -TimeoutSec 10
    if ($login.StatusCode -ge 500) { throw 'Run-owned Web login did not return a usable page.' }
    $health = Invoke-RestMethod -Uri "$($started.SourceHttpBaseUrl)/__fixture/health" -TimeoutSec 5
    if ($health.runId -ne $started.RunId -or $health.loopbackOnly -ne $true) { throw 'Run-owned controlled source health identity mismatch.' }

    # Verify the interactive source control used by the browser operator: a valid held result
    # followed by a released result, without fabricating an application run or persisted status.
    $null = & $setModeScript -OwnerFile $started.OwnerFile -HttpMode late-events-held -SmtpMode reject-next-recipient
    $searchBody = '{"start":"2026-10-10T00:00:00Z","end":"2026-10-11T00:00:00Z","filter":"192.0.2.1"}'
    $null = Invoke-RestMethod -Method Post -Uri "$($started.SourceHttpBaseUrl)/SentinelAuthServices/auth/tokens" -Body '{}' -ContentType 'application/json'
    $null = Invoke-RestMethod -Method Post -Uri "$($started.SourceHttpBaseUrl)/SentinelRESTServices/objects/event-search" -Body $searchBody -ContentType 'application/json'
    $held = Invoke-RestMethod -Uri "$($started.SourceHttpBaseUrl)/SentinelRESTServices/objects/event-search/round53-job"
    if ($held.found -ne 0) { throw 'Held late-arrival mode did not return zero events.' }
    $null = & $setModeScript -OwnerFile $started.OwnerFile -HttpMode late-events-released -SmtpMode accept
    $null = Invoke-RestMethod -Method Post -Uri "$($started.SourceHttpBaseUrl)/SentinelRESTServices/objects/event-search" -Body $searchBody -ContentType 'application/json'
    $released = Invoke-RestMethod -Uri "$($started.SourceHttpBaseUrl)/SentinelRESTServices/objects/event-search/round53-job"
    if ($released.found -ne 1) { throw 'Released late-arrival mode did not return one event.' }

    # The SMTP server is local and refuses non-reserved addresses; verify 451 then accepted capture.
    $null = & $setModeScript -OwnerFile $started.OwnerFile -HttpMode netiq-healthy -SmtpMode reject-next-recipient
    $smtpRejected = $false
    try { [System.Net.Mail.SmtpClient]::new('127.0.0.1',[int]$manifest.Runtime.SourceSmtpEndpoint.Split(':')[-1]).Send('sender@loopback.invalid','ops@loopback.invalid','fixture rejection','synthetic retry test') }
    catch [System.Net.Mail.SmtpException] { $smtpRejected = $true }
    if (!$smtpRejected) { throw 'SMTP sink did not expose the controlled recipient refusal.' }
    $null = & $setModeScript -OwnerFile $started.OwnerFile -HttpMode netiq-healthy -SmtpMode accept
    $smtpPort = [int]$manifest.Runtime.SourceSmtpEndpoint.Split(':')[-1]
    $smtpClient = [System.Net.Mail.SmtpClient]::new('127.0.0.1',$smtpPort); $smtpClient.EnableSsl=$false; $smtpClient.Timeout=4000
    $smtpClient.Send('sender@loopback.invalid','ops@loopback.invalid','fixture accepted','synthetic local capture'); $smtpClient.Dispose()
    $smtpRows = @(Get-Content -LiteralPath (Join-Path $started.BundlePath 'smtp-captures.jsonl') | ForEach-Object { $_ | ConvertFrom-Json })
    if (@($smtpRows | Where-Object { $_.event -eq 'recipient-refused' }).Count -ne 1 -or @($smtpRows | Where-Object { $_.event -eq 'accepted' }).Count -ne 1) { throw 'SMTP refuse/accept capture records were incomplete.' }

    $runId = $started.RunId
    $ownerPath = $started.OwnerFile
    $manifestPath = $started.ManifestPath
    $bundlePath = $started.BundlePath
    $null = & $stopScript -OwnerFile $ownerPath
    $stoppedOwner = Get-Content -LiteralPath $ownerPath -Raw | ConvertFrom-Json
    if ($stoppedOwner.Status -cne 'stopped') { throw 'Exact-owner stop did not persist stopped state.' }
    [pscustomobject]@{ Result='PASS'; RunId=$runId; BundlePath=$bundlePath; ManifestPath=$manifestPath; ExpectedSourceCommit=$manifest.SourceBuild.ExpectedSourceCommit; BuildSourceStamp=$manifest.SourceBuild.SourceStamp; WebLogin='ready'; Source='held-zero-then-released-one'; Smtp='451-then-accepted'; LocalHostAnalysis='disabled'; OwnerCheckedStop='stopped'; BrowserAcceptance='not-performed' }
}
finally {
    if ($null -ne $started -and (Test-Path -LiteralPath $started.OwnerFile)) {
        try {
            $currentOwner = Get-Content -LiteralPath $started.OwnerFile -Raw | ConvertFrom-Json
            if ($currentOwner.Status -ceq 'running') { & $stopScript -OwnerFile $started.OwnerFile | Out-Null }
        } catch { }
    }
}
