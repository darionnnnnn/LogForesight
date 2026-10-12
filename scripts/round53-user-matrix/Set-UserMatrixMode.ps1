[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OwnerFile,
[ValidateSet('netiq-healthy','prtg-healthy','ai-healthy','all-sources-healthy','netiq-events','netiq-risk-events','netiq-audit-cleared-events','netiq-risk-events-ai-hold-prtg-503','late-events-held','late-events-released','netiq-503','prtg-503','prtg-hold-503','ai-hold','ai-hold-prtg-503','all-503')][string]$HttpMode,
    [ValidateSet('accept','reject-next-recipient','drop-after-data')][string]$SmtpMode = 'accept'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ownerPath = [IO.Path]::GetFullPath($OwnerFile)
$owner = Get-Content -LiteralPath $ownerPath -Raw | ConvertFrom-Json
if ($owner.Schema -cne 'lf-round53-user-matrix-process-owner-v1' -or $owner.Status -cne 'running') { throw 'Fixture process owner identity/state invalid.' }
$runRoot = [IO.Path]::GetFullPath([string]$owner.BundlePath)
$runsRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'runs'))
if ([IO.Path]::GetDirectoryName($runRoot).TrimEnd('\') -ine $runsRoot.TrimEnd('\') -or [IO.Path]::GetFileName($runRoot) -cnotmatch '^[0-9a-f]{32}$') { throw 'Owner bundle is outside the canonical owned runs root.' }
$markerPath = Join-Path $runRoot '.lf-round53-user-matrix-owner.json'
$marker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json
if ($marker.Schema -cne 'lf-round53-user-matrix-owner-v1' -or $marker.RunId -cne $owner.RunId -or [IO.Path]::GetFullPath($marker.BundlePath) -ine $runRoot) { throw 'Fixture owner marker mismatch.' }
$sourceOwner = @($owner.Processes | Where-Object { $_.Kind -eq 'sources' })
if ($sourceOwner.Count -ne 1) { throw 'Expected exactly one owned source process.' }
$process = Get-Process -Id ([int]$sourceOwner[0].ProcessId) -ErrorAction Stop
$cim = Get-CimInstance Win32_Process -Filter "ProcessId = $($sourceOwner[0].ProcessId)" -ErrorAction Stop
if ([IO.Path]::GetFullPath([string]$cim.ExecutablePath) -ine [IO.Path]::GetFullPath([string]$owner.DotnetPath) -or
    [string]$cim.CommandLine -cne [string]$sourceOwner[0].CommandLine -or
    $process.StartTime.ToUniversalTime().Ticks -ne [long]$sourceOwner[0].StartTimeUtcTicks -or
    (Get-FileHash -LiteralPath $sourceOwner[0].AssemblyPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne [string]$sourceOwner[0].AssemblySha256) {
    throw 'Controlled source helper process identity changed; refusing to switch modes.'
}
$httpPort = [int]([uri]$owner.SourceHttpBaseUrl).Port
$smtpPort = [int]$owner.SourceSmtpPort
$httpListeners = @(Get-NetTCPConnection -State Listen -LocalPort $httpPort -ErrorAction SilentlyContinue)
$smtpListeners = @(Get-NetTCPConnection -State Listen -LocalPort $smtpPort -ErrorAction SilentlyContinue)
if ($smtpListeners.Count -ne 1 -or [int]$smtpListeners[0].OwningProcess -ne [int]$sourceOwner[0].ProcessId) { throw 'Owned SMTP listener does not match the exact recorded helper process.' }
if ($httpListeners.Count -ne 1) { throw 'Owned HTTP listener is missing or ambiguous.' }
if ([int]$httpListeners[0].OwningProcess -ne [int]$sourceOwner[0].ProcessId) {
    # Windows HttpListener is hosted by HTTP.sys, whose TCP listener is reported as PID 4.
    # Accept that OS-owned socket only when the exact controlled helper is still alive and
    # its loopback health response proves this run's nonce and loopback-only contract.
    if ([int]$httpListeners[0].OwningProcess -ne 4) { throw 'HTTP listener belongs to a foreign process.' }
    try { $health = Invoke-RestMethod -Uri ($owner.SourceHttpBaseUrl.TrimEnd('/') + '/__fixture/health') -TimeoutSec 3 }
    catch { throw 'HTTP.sys listener did not answer the run-owned controlled-source health probe.' }
    if ($health.runId -cne $owner.RunId -or $health.loopbackOnly -ne $true) { throw 'HTTP.sys listener health identity is not this owned source helper.' }
}
[IO.File]::WriteAllText([string]$owner.HttpModeFile, $HttpMode, [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText([string]$owner.SmtpModeFile, $SmtpMode, [Text.UTF8Encoding]::new($false))
$owner | Add-Member -NotePropertyName ModeChangedAtUtc -NotePropertyValue ([DateTimeOffset]::UtcNow.ToString('O')) -Force
$owner | Add-Member -NotePropertyName HttpMode -NotePropertyValue $HttpMode -Force
$owner | Add-Member -NotePropertyName SmtpMode -NotePropertyValue $SmtpMode -Force
[IO.File]::WriteAllText($ownerPath, ($owner | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
[pscustomobject]@{ RunId=$owner.RunId; HttpMode=$HttpMode; SmtpMode=$SmtpMode; SourceRequestsLog=(Join-Path $runRoot 'source-requests.jsonl'); SmtpCaptureLog=(Join-Path $runRoot 'smtp-captures.jsonl') }
