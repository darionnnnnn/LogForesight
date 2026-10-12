[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$OwnerFile)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion -lt [Version]'7.4') { throw 'Requires PowerShell 7.4+.' }

function Get-FileSha256([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256 -ErrorAction Stop).Hash.ToLowerInvariant()
}
function Write-JsonAtomic([string]$Path, [object]$Value, [int]$Depth) {
    $tmp = "$Path.$([guid]::NewGuid().ToString('N')).tmp"
    [IO.File]::WriteAllText($tmp, ($Value | ConvertTo-Json -Depth $Depth), [Text.UTF8Encoding]::new($false))
    [IO.File]::Move($tmp, $Path, $true)
}
function Get-TreeSha256([string]$Root) {
    $entries = foreach ($file in Get-ChildItem -LiteralPath $Root -File -Recurse | Sort-Object FullName) {
        $relative = [IO.Path]::GetRelativePath($Root, $file.FullName).Replace('\', '/')
        "$relative`n$(Get-FileSha256 $file.FullName)`n"
    }
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($entries -join '')))).ToLowerInvariant()
}
function Get-ProcessIdentity([object]$Record, [string]$DotnetPath, [string]$RunRoot) {
    $process = Get-Process -Id ([int]$Record.ProcessId) -ErrorAction Stop
    $process.Refresh()
    if ($process.HasExited) { throw "Owned $($Record.Kind) process has exited." }
    $cim = Get-CimInstance Win32_Process -Filter "ProcessId = $($Record.ProcessId)" -ErrorAction Stop
    if ($null -eq $cim -or [IO.Path]::GetFullPath([string]$cim.ExecutablePath) -ine $DotnetPath -or
        [string]$cim.CommandLine -cne [string]$Record.CommandLine -or
        $process.StartTime.ToUniversalTime().Ticks -ne [long]$Record.StartTimeUtcTicks -or
        [IO.Path]::GetFullPath([string]$Record.AssemblyPath) -notlike "$RunRoot\*") {
        throw "Owned $($Record.Kind) process identity no longer matches its owner record."
    }
    if ((Get-FileSha256 ([string]$Record.AssemblyPath)) -cne ([string]$Record.AssemblySha256).ToLowerInvariant()) {
        throw "Owned $($Record.Kind) assembly hash no longer matches its owner record."
    }
    [pscustomobject]@{ Process=$process; Cim=$cim }
}
function Assert-OwnedPorts([object]$Record, [int]$OwnerProcessId, [string]$SourceHttpUrl, [string]$RunId) {
    foreach ($port in @($Record.Ports)) {
        $listeners = @(Get-NetTCPConnection -State Listen -LocalPort ([int]$port) -ErrorAction SilentlyContinue)
        if ($listeners.Count -ne 1) { throw "Expected exactly one owned $($Record.Kind) listener on port $port." }
        if ([int]$listeners[0].OwningProcess -eq $OwnerProcessId) { continue }
        $httpPort = if ($Record.Kind -eq 'sources') { ([uri]$SourceHttpUrl).Port } else { -1 }
        if ($Record.Kind -ne 'sources' -or [int]$port -ne $httpPort -or [int]$listeners[0].OwningProcess -ne 4) {
            throw "Port $port is not owned by the expected $($Record.Kind) process."
        }
        $health = Invoke-RestMethod -Uri ($SourceHttpUrl.TrimEnd('/') + '/__fixture/health') -TimeoutSec 3
        if ($health.runId -cne $RunId -or $health.loopbackOnly -ne $true) { throw 'HTTP.sys source health identity mismatch.' }
    }
}

$ownerPath = [IO.Path]::GetFullPath($OwnerFile)
$owner = Get-Content -LiteralPath $ownerPath -Raw | ConvertFrom-Json
if ($owner.Schema -cne 'lf-round53-user-matrix-process-owner-v1' -or $owner.Status -cne 'running') { throw 'Fixture owner must be running before a Web-only restart.' }
$runRoot = [IO.Path]::GetFullPath([string]$owner.BundlePath)
$runsRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'runs'))
if ([IO.Path]::GetDirectoryName($runRoot).TrimEnd('\') -ine $runsRoot.TrimEnd('\') -or
    [IO.Path]::GetFileName($runRoot) -cnotmatch '^[0-9a-f]{32}$' -or
    [IO.Path]::GetDirectoryName($ownerPath) -ine $runRoot -or
    $ownerPath -ine [IO.Path]::GetFullPath((Join-Path $runRoot 'process-owner.json'))) { throw 'Owner file is not the canonical process-owner.json for this run-owned fixture.' }
$markerPath = Join-Path $runRoot '.lf-round53-user-matrix-owner.json'
$manifestPath = Join-Path $runRoot 'fixture-manifest.json'
$marker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($marker.Schema -cne 'lf-round53-user-matrix-owner-v1' -or $marker.RunId -cne $owner.RunId -or
    [IO.Path]::GetFullPath([string]$marker.BundlePath) -ine $runRoot -or $marker.Status -cne 'running' -or
    $manifest.Schema -cne 'lf-round53-user-matrix-fixture-v1' -or $manifest.RunId -cne $owner.RunId -or
    [IO.Path]::GetFullPath([string]$manifest.BundlePath) -ine $runRoot -or $manifest.Status -cne 'running' -or
    $manifest.SyntheticOnly -ne $true) { throw 'Run owner, marker, and manifest do not describe the same active owned fixture.' }
$dataRoot = [IO.Path]::GetFullPath((Join-Path $runRoot 'data'))
if ([IO.Path]::GetFullPath([string]$manifest.Runtime.DataRoot) -ine $dataRoot -or
    [IO.Path]::GetFullPath((Join-Path $dataRoot 'Db\logforesight.db')) -ine [IO.Path]::GetFullPath((Join-Path $runRoot 'data\Db\logforesight.db'))) {
    throw 'Fixture data root/database path is not the canonical same-run location.'
}
$dbPath = Join-Path $dataRoot 'Db\logforesight.db'
$keyPath = Join-Path $dataRoot 'keys\lf-crypto.key'
foreach ($path in @($dbPath,$keyPath)) { if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Owned fixture state missing: $path" } }
$appRoot = Join-Path $runRoot 'app'
$webDll = Join-Path $appRoot 'LogForesight.Web.dll'
$coreDll = Join-Path $appRoot 'LogForesight.Core.dll'
$assetsRoot = Join-Path $appRoot 'wwwroot'
$identityPath = Join-Path $appRoot 'build-identity.json'
foreach ($path in @($webDll,$coreDll,$assetsRoot,$identityPath)) { if (!(Test-Path -LiteralPath $path)) { throw "Same-run runtime artifact missing: $path" } }
$build = $manifest.SourceBuild
if ((Get-FileSha256 $webDll) -cne ([string]$build.WebDllSha256).ToLowerInvariant() -or
    (Get-FileSha256 $coreDll) -cne ([string]$build.CoreDllSha256).ToLowerInvariant() -or
    (Get-FileSha256 $identityPath) -cne ([string]$build.IdentitySha256).ToLowerInvariant() -or
    (Get-TreeSha256 $assetsRoot) -cne ([string]$build.StaticAssetsRuntimeSha256).ToLowerInvariant()) {
    throw 'Same-run Web/Core/assets no longer match the frozen fixture build identity.'
}
$identity = Get-Content -LiteralPath $identityPath -Raw | ConvertFrom-Json
$entries = @($identity.Assemblies)
$webEntry = @($entries | Where-Object Name -CEQ 'LogForesight.Web')
$coreEntry = @($entries | Where-Object Name -CEQ 'LogForesight.Core')
if ($webEntry.Count -ne 1 -or $coreEntry.Count -ne 1 -or
    $webEntry[0].SourceStamp -cne $coreEntry[0].SourceStamp -or
    $webEntry[0].SourceStamp -cne [string]$build.SourceStamp -or
    $webEntry[0].FileSha256 -cne [string]$build.WebDllSha256 -or $coreEntry[0].FileSha256 -cne [string]$build.CoreDllSha256) {
    throw 'Same-run copied build identity is mixed or inconsistent with the fixture manifest.'
}
$processRows = @($owner.Processes)
$webRows = @($processRows | Where-Object Kind -CEQ 'web')
$sourceRows = @($processRows | Where-Object Kind -CEQ 'sources')
if ($webRows.Count -ne 1 -or $sourceRows.Count -ne 1 -or @($manifest.Processes).Count -ne 2) { throw 'Expected one owned Web process and one retained source helper.' }
$webRecord = $webRows[0]
$sourceRecord = $sourceRows[0]
$manifestWeb = @($manifest.Processes | Where-Object Kind -CEQ 'web')
$manifestSource = @($manifest.Processes | Where-Object Kind -CEQ 'sources')
foreach ($pair in @(@($webRecord,$manifestWeb[0]),@($sourceRecord,$manifestSource[0]))) {
    foreach ($field in @('ProcessId','StartTimeUtcTicks','Executable','CommandLine','AssemblyPath','AssemblySha256')) {
        if ([string]$pair[0].$field -cne [string]$pair[1].$field) { throw "Owner and manifest process identity differ ($field)." }
    }
}
$dotnet = [IO.Path]::GetFullPath([string]$owner.DotnetPath)
if (!(Test-Path -LiteralPath $dotnet -PathType Leaf) -or
    [IO.Path]::GetFullPath([string]$webRecord.Executable) -ine $dotnet -or
    [IO.Path]::GetFullPath([string]$sourceRecord.Executable) -ine $dotnet -or
    [IO.Path]::GetFullPath([string]$webRecord.AssemblyPath) -ine [IO.Path]::GetFullPath($webDll) -or
    [IO.Path]::GetFullPath([string]$sourceRecord.AssemblyPath) -ine [IO.Path]::GetFullPath((Join-Path $runRoot 'helper\Round53ControlledSources.dll'))) {
    throw 'Owned Web/source executable or assembly path differs from this run.'
}
$webIdentity = Get-ProcessIdentity $webRecord $dotnet $runRoot
$sourceIdentity = Get-ProcessIdentity $sourceRecord $dotnet $runRoot
Assert-OwnedPorts $webRecord ([int]$webRecord.ProcessId) ([string]$owner.SourceHttpBaseUrl) ([string]$owner.RunId)
Assert-OwnedPorts $sourceRecord ([int]$sourceRecord.ProcessId) ([string]$owner.SourceHttpBaseUrl) ([string]$owner.RunId)
$sourceHealthBefore = Invoke-RestMethod -Uri ($owner.SourceHttpBaseUrl.TrimEnd('/') + '/__fixture/health') -TimeoutSec 3
if ($sourceHealthBefore.runId -cne $owner.RunId -or $sourceHealthBefore.loopbackOnly -ne $true) { throw 'Controlled source helper health is not this run.' }

$lockPath = Join-Path $runRoot '.lifecycle.lock'
$lock = $null
$newProcess = $null
$expectedCommandLine = $null
$restartCommitted = $false
try { $lock = [IO.File]::Open($lockPath,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None) }
catch { throw 'Another fixture lifecycle operation is active; refusing a concurrent restart.' }
try {
    # Re-read the durable fence after taking the lock. Another lifecycle operation may have won the race.
    $owner = Get-Content -LiteralPath $ownerPath -Raw | ConvertFrom-Json
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $currentWebRows = @($owner.Processes | Where-Object Kind -CEQ 'web')
    $currentSourceRows = @($owner.Processes | Where-Object Kind -CEQ 'sources')
    if ($owner.Status -cne 'running' -or $manifest.Status -cne 'running' -or $currentWebRows.Count -ne 1 -or
        $currentSourceRows.Count -ne 1 -or $owner.RunId -cne $marker.RunId -or
        [IO.Path]::GetFullPath([string]$owner.BundlePath) -ine $runRoot -or
        [IO.Path]::GetFullPath([string]$manifest.Runtime.DataRoot) -ine $dataRoot -or
        [int]$currentWebRows[0].ProcessId -ne [int]$webRecord.ProcessId -or
        [int]$currentSourceRows[0].ProcessId -ne [int]$sourceRecord.ProcessId -or
        [long]$currentSourceRows[0].StartTimeUtcTicks -ne [long]$sourceRecord.StartTimeUtcTicks) {
        throw 'Fixture state changed while the lifecycle lock was being acquired.'
    }
    $dbShaBefore = $null; $dbShaBeforeError = $null
    try { $dbShaBefore = Get-FileSha256 $dbPath } catch { $dbShaBeforeError = $_.Exception.GetType().FullName }
    $keyShaBefore = Get-FileSha256 $keyPath
    $webPort = [int]@($webRecord.Ports)[0]
    $receipt = [ordered]@{
        Schema='lf-round53-user-matrix-web-restart-v1'; RestartId=[guid]::NewGuid().ToString('N');
        StartedAtUtc=[DateTimeOffset]::UtcNow.ToString('O'); Status='in-progress'; RunId=$owner.RunId
        DataRoot=$dataRoot; DatabasePath=$dbPath; DatabaseSha256Before=$dbShaBefore; DatabaseSha256BeforeUnavailableReason=$dbShaBeforeError
        KeyPath=$keyPath; KeySha256Before=$keyShaBefore; WebPort=$webPort; SourceProcessId=[int]$sourceRecord.ProcessId
        SourceStartTimeUtcTicks=[long]$sourceRecord.StartTimeUtcTicks; PriorWebProcessId=[int]$webRecord.ProcessId
        PriorWebStartTimeUtcTicks=[long]$webRecord.StartTimeUtcTicks; WebDllSha256=Get-FileSha256 $webDll
        CoreDllSha256=Get-FileSha256 $coreDll; RuntimeAssetsSha256=Get-TreeSha256 $assetsRoot; BuildSourceStamp=[string]$build.SourceStamp
    }
    if ($null -eq $owner.PSObject.Properties['RestartReceipts']) { $owner | Add-Member -NotePropertyName RestartReceipts -NotePropertyValue @() -Force }
    if ($null -eq $manifest.PSObject.Properties['RestartReceipts']) { $manifest | Add-Member -NotePropertyName RestartReceipts -NotePropertyValue @() -Force }
    if ($null -eq $owner.PSObject.Properties['RestartState']) { $owner | Add-Member -NotePropertyName RestartState -NotePropertyValue 'in-progress' -Force }
    if ($null -eq $manifest.PSObject.Properties['RestartState']) { $manifest | Add-Member -NotePropertyName RestartState -NotePropertyValue 'in-progress' -Force }
    $owner.RestartReceipts = @($owner.RestartReceipts) + @($receipt)
    $manifest.RestartReceipts = @($manifest.RestartReceipts) + @($receipt)
    $owner.RestartState='in-progress'; $manifest.RestartState='in-progress'
    Write-JsonAtomic $ownerPath $owner 24
    Write-JsonAtomic $manifestPath $manifest 60

    # Revalidate immediately before terminating exactly the owned Web process. Source helper stays alive.
    $webIdentity = Get-ProcessIdentity $webRecord $dotnet $runRoot
    Assert-OwnedPorts $webRecord ([int]$webRecord.ProcessId) ([string]$owner.SourceHttpBaseUrl) ([string]$owner.RunId)
    $webIdentity.Process.Kill(); $webIdentity.Process.WaitForExit(10000) | Out-Null
    if (!$webIdentity.Process.HasExited) { throw 'Owned Web process did not stop within 10 seconds.' }
    foreach ($port in @($webRecord.Ports)) {
        if (@(Get-NetTCPConnection -State Listen -LocalPort ([int]$port) -ErrorAction SilentlyContinue).Count -gt 0) { throw "Web port $port remains bound after stopping the owned Web process." }
    }
    $dbShaBeforeClosed = Get-FileSha256 $dbPath
    $receipt.DatabaseSha256BeforeClosed = $dbShaBeforeClosed

    $webArgs = @($webDll,'--contentRoot',$appRoot,'--urls',[string]$owner.WebBaseUrl)
    $webArgLine = ($webArgs | ForEach-Object { '"' + $_ + '"' }) -join ' '
    $expectedCommandLine = '"' + $dotnet + '" ' + $webArgLine
    $environment = @{
        ASPNETCORE_ENVIRONMENT='Development'; DOTNET_ENVIRONMENT='Development'; ASPNETCORE_URLS=[string]$owner.WebBaseUrl
        Auth__Provider='Stub'; Storage__Type='Sqlite'; Storage__DataRoot=$dataRoot; Storage__ConnectionString=''
        Server__PathBase='/browser-fixture'; DOTNET_NOLOGO='1'; PATH=(Join-Path $appRoot 'runtimes\win-x64\native') + ';' + $env:PATH
    }
    $stdout = Join-Path $runRoot 'web-restart.stdout.log'; $stderr = Join-Path $runRoot 'web-restart.stderr.log'
    $newProcess = Start-Process -FilePath $dotnet -ArgumentList $webArgLine -WorkingDirectory $appRoot -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -Environment $environment -PassThru
    $ready = $false
    $loginUrl = ([string]$owner.WebBaseUrl).TrimEnd('/') + '/browser-fixture/login'
    for ($attempt=0; $attempt -lt 100; $attempt++) {
        $newProcess.Refresh()
        if ($newProcess.HasExited) { break }
        try {
            $health = Invoke-RestMethod -Uri ($owner.SourceHttpBaseUrl.TrimEnd('/') + '/__fixture/health') -TimeoutSec 1
            $login = Invoke-WebRequest -Uri $loginUrl -UseBasicParsing -TimeoutSec 1
            if ($health.runId -ceq $owner.RunId -and $health.loopbackOnly -eq $true -and $login.StatusCode -lt 500) { $ready=$true; break }
        } catch { }
        Start-Sleep -Milliseconds 200
    }
    if (!$ready) { throw 'Same-run Web process did not become ready while its owned source helper remained available.' }
    $newProcess.Refresh()
    $newCim = Get-CimInstance Win32_Process -Filter "ProcessId = $($newProcess.Id)" -ErrorAction Stop
    if ($newProcess.HasExited -or [IO.Path]::GetFullPath([string]$newCim.ExecutablePath) -ine $dotnet -or [string]$newCim.CommandLine -cne $expectedCommandLine) {
        throw 'Restarted Web process executable/command line differs from the same-run launch contract.'
    }
    $newWebRecord = [ordered]@{
        Kind='web'; ProcessId=$newProcess.Id; StartTimeUtcTicks=$newProcess.StartTime.ToUniversalTime().Ticks
        Executable=[IO.Path]::GetFullPath([string]$newCim.ExecutablePath); CommandLine=[string]$newCim.CommandLine
        Ports=@($webPort); AssemblyPath=$webDll; AssemblySha256=Get-FileSha256 $webDll
    }
    if ($newWebRecord.AssemblySha256 -cne [string]$receipt.WebDllSha256) { throw 'Web assembly changed during the restart.' }
    $sourceIdentityAfter = Get-ProcessIdentity $sourceRecord $dotnet $runRoot
    Assert-OwnedPorts $sourceRecord ([int]$sourceRecord.ProcessId) ([string]$owner.SourceHttpBaseUrl) ([string]$owner.RunId)
    $sourceHealthAfter = Invoke-RestMethod -Uri ($owner.SourceHttpBaseUrl.TrimEnd('/') + '/__fixture/health') -TimeoutSec 3
    if ($sourceHealthAfter.runId -cne $owner.RunId -or $sourceHealthAfter.loopbackOnly -ne $true -or
        $sourceIdentityAfter.Process.StartTime.ToUniversalTime().Ticks -ne [long]$receipt.SourceStartTimeUtcTicks) {
        throw 'Controlled source helper did not retain its original process identity across Web restart.'
    }
    $dbShaAfter = $null; $dbShaAfterError = $null
    try { $dbShaAfter = Get-FileSha256 $dbPath } catch { $dbShaAfterError = $_.Exception.GetType().FullName }
    $keyShaAfter = Get-FileSha256 $keyPath
    if ($keyShaAfter -cne $keyShaBefore) { throw 'Encryption key changed across same-run Web restart.' }
    $receipt.DatabaseSha256After = $dbShaAfter; $receipt.DatabaseSha256AfterUnavailableReason=$dbShaAfterError
    $receipt.KeySha256After=$keyShaAfter; $receipt.FinishedAtUtc=[DateTimeOffset]::UtcNow.ToString('O'); $receipt.Status='completed'
    $receipt.NewWebProcessId=$newWebRecord.ProcessId; $receipt.NewWebStartTimeUtcTicks=$newWebRecord.StartTimeUtcTicks
    $owner = Get-Content -LiteralPath $ownerPath -Raw | ConvertFrom-Json
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $ownerWeb = @($owner.Processes | Where-Object Kind -CEQ 'web')
    $manifestWeb = @($manifest.Processes | Where-Object Kind -CEQ 'web')
    if ($ownerWeb.Count -ne 1 -or $manifestWeb.Count -ne 1 -or
        [int]$ownerWeb[0].ProcessId -ne [int]$webRecord.ProcessId -or [int]$manifestWeb[0].ProcessId -ne [int]$webRecord.ProcessId) {
        throw 'Owner/manifest Web identity drifted while the lifecycle lock was held.'
    }
    $owner.Processes = @($owner.Processes | Where-Object Kind -CNE 'web') + @($newWebRecord)
    $manifest.Processes = @($manifest.Processes | Where-Object Kind -CNE 'web') + @($newWebRecord)
    $owner.RestartReceipts = @($owner.RestartReceipts | Where-Object RestartId -CNE $receipt.RestartId) + @($receipt)
    $manifest.RestartReceipts = @($manifest.RestartReceipts | Where-Object RestartId -CNE $receipt.RestartId) + @($receipt)
    $owner.RestartState='completed'; $manifest.RestartState='completed'
    Write-JsonAtomic $ownerPath $owner 24
    Write-JsonAtomic $manifestPath $manifest 60
    $restartCommitted = $true
    [pscustomobject]@{ Result='restarted'; RunId=$owner.RunId; RestartId=$receipt.RestartId; OwnerFile=$ownerPath; ManifestPath=$manifestPath; DataRoot=$dataRoot; DatabasePath=$dbPath; DatabaseSha256Before=$dbShaBefore; DatabaseSha256BeforeClosed=$dbShaBeforeClosed; DatabaseSha256After=$dbShaAfter; KeySha256=$keyShaAfter; SourceProcessId=$sourceRecord.ProcessId; WebProcessId=$newWebRecord.ProcessId; WebStartTimeUtcTicks=$newWebRecord.StartTimeUtcTicks; LoginUrl=$loginUrl; BuildSourceStamp=$build.SourceStamp; OwnerStatus=$owner.Status; ManifestStatus=$manifest.Status }
}
finally {
    if (!$restartCommitted -and $null -ne $newProcess -and ![string]::IsNullOrWhiteSpace([string]$expectedCommandLine)) {
        try {
            $newProcess.Refresh()
            if (!$newProcess.HasExited) {
                $cim = Get-CimInstance Win32_Process -Filter "ProcessId = $($newProcess.Id)" -ErrorAction Stop
                if ([IO.Path]::GetFullPath([string]$cim.ExecutablePath) -ieq $dotnet -and
                    [string]$cim.CommandLine -ceq $expectedCommandLine -and
                    $newProcess.StartTime.ToUniversalTime().Ticks -gt 0) {
                    $newProcess.Kill(); $newProcess.WaitForExit(10000) | Out-Null
                }
            }
        } catch { Write-Warning 'A failed restart left a process whose identity could not be safely verified for cleanup; owner state remains fail-closed.' }
    }
    if ($null -ne $lock) { $lock.Dispose() }
}
