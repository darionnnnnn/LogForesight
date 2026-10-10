#Requires -Version 7.0
<#
.SYNOPSIS
Creates an owned, isolated qualification-matrix run descriptor from live public Web API data.
.DESCRIPTION
The operator supplies a provider declaration, saved sensor manifest, explicit numeric-loopback
fixture endpoints, authenticated WebRequestSession, and an empty matrix run directory. This
initializer reads current settings and api/admin/hosts/all, then writes the host IDs and owned
profile/preflight descriptors. Provider and 180-day retention details remain operator declarations
until normal qualification and exact coverage checks succeed. Every native-source, formal, capacity,
retention-acceptance, and whole-round acceptance flag remains false. It does not start jobs, alter
settings, create bindings/proofs, or claim that a fixture is a physical source.
.EXAMPLE
Initialize-QualificationMatrixRun.ps1 -Profile fullstore-3000 -BaseUri http://127.0.0.1:5100/ `
  -PrtgFixtureUri http://127.0.0.1:5101/ -SentinelFixtureUri http://127.0.0.1:5102/ `
  -ManifestPath .\owned\sensor-manifest.json -ProviderDescriptorPath .\owned\provider.json `
  -MatrixRunDirectory .\owned\run-001 -WebSession $session
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('fullstore-3000','controlled-loopback-3000')][string]$Profile,
    [Parameter(Mandatory)][uri]$BaseUri,
    [Parameter(Mandatory)][uri]$PrtgFixtureUri,
    [Parameter(Mandatory)][uri]$SentinelFixtureUri,
    [Parameter(Mandatory)][string]$ManifestPath,
    [Parameter(Mandatory)][string]$ProviderDescriptorPath,
    [Parameter(Mandatory)][string]$MatrixRunDirectory,
    [Parameter(Mandatory)][Microsoft.PowerShell.Commands.WebRequestSession]$WebSession,
    [string]$CookieName='lf_auth',
    [ValidateRange(1,120)][int]$MaxSeconds=30
)
$ErrorActionPreference='Stop'
$candidateRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
function Assert-Loopback([uri]$Value,[string]$Label) {
    $ip=$null
    if($Value.Scheme -cnotin @('http','https') -or -not[Net.IPAddress]::TryParse($Value.DnsSafeHost.Trim('[',']'),[ref]$ip) -or
       -not[Net.IPAddress]::IsLoopback($ip) -or $Value.Port -lt 1 -or $Value.UserInfo -or $Value.Query -or $Value.Fragment) {
        throw "$Label must be an explicit numeric-loopback fixture URL without credentials, query, or fragment (received '$($Value.AbsoluteUri)')."
    }
}
function Write-JsonAtomic([string]$Path,[object]$Value) {
    $tmp=$Path+'.tmp';[IO.File]::WriteAllText($tmp,($Value|ConvertTo-Json -Depth 16),[Text.UTF8Encoding]::new($false));[IO.File]::Move($tmp,$Path,$true)
}
function Get-Sha256Text([string]$Text) { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Text))) }
function Assert-NoReparseAncestor([string]$Path,[string]$Label) {
    $cursor=[IO.Path]::GetFullPath($Path)
    while(-not[string]::IsNullOrWhiteSpace($cursor)) {
        if(Test-Path -LiteralPath $cursor){$item=Get-Item -LiteralPath $cursor -Force;if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw "$Label or one of its existing ancestors is a reparse point; refusing lexical path escape."}}
        $parent=[IO.Path]::GetDirectoryName($cursor);if([string]::IsNullOrWhiteSpace($parent) -or $parent -ceq $cursor){break};$cursor=$parent
    }
}
Assert-Loopback $BaseUri 'Web fixture URL';Assert-Loopback $PrtgFixtureUri 'PRTG fixture URL';Assert-Loopback $SentinelFixtureUri 'Sentinel fixture URL'
$manifestFull=[IO.Path]::GetFullPath($ManifestPath);$providerFull=[IO.Path]::GetFullPath($ProviderDescriptorPath);$runFull=[IO.Path]::GetFullPath($MatrixRunDirectory)
foreach($p in @($manifestFull,$providerFull,$runFull)){if(-not$p.StartsWith($candidateRoot.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Manifest, provider descriptor, and run directory must remain inside the isolated candidate.'}}
Assert-NoReparseAncestor $manifestFull 'Qualification manifest';Assert-NoReparseAncestor $providerFull 'Provider descriptor';Assert-NoReparseAncestor $runFull 'Matrix run directory'
if((Get-Item -LiteralPath $manifestFull).Length -gt 8MB){throw 'Qualification manifest exceeds the bounded 8 MiB limit.'}
if((Get-Item -LiteralPath $providerFull).Length -gt 64KB){throw 'Provider descriptor exceeds the bounded 64 KiB limit.'}
if(Test-Path -LiteralPath $runFull){if(@(Get-ChildItem -LiteralPath $runFull -Force).Count -ne 0){throw 'Matrix run directory must be new or empty; existing evidence is preserved.'}}else{$null=New-Item -ItemType Directory -Path $runFull}
$manifest=Get-Content -LiteralPath $manifestFull -Raw|ConvertFrom-Json -Depth 32
$sensorIds=@($manifest.sensorIds|ForEach-Object{[long]$_});$sensorHash=Get-Sha256Text ($sensorIds -join "`n")
if($manifest.schema -cne 'qualification-job-manifest-v1' -or $sensorIds.Count -ne 15000 -or @($sensorIds|Sort-Object -Unique).Count -ne 15000 -or
   -not([Linq.Enumerable]::SequenceEqual([long[]]$sensorIds,[long[]]@($sensorIds|Sort-Object))) -or $manifest.sensorFences.Count -ne 15000){throw 'Manifest must contain the exact ordered 15,000-sensor public qualification scope and one fence per sensor.'}
$provider=Get-Content -LiteralPath $providerFull -Raw|ConvertFrom-Json -Depth 12
if($provider.schema -cne 'round53-owned-run-descriptor-v1' -or ([string]$provider.provider).ToLowerInvariant() -notin @('sqlite','localdb') -or [int]$provider.retentionDays -ne 180){throw 'Operator provider declaration must identify sqlite/localdb and explicitly declare 180 retention days.'}
$dataRootText=[string]$provider.dataRoot
if(-not[IO.Path]::IsPathFullyQualified($dataRootText)){throw 'Operator dataRoot must be an absolute path inside the isolated candidate.'}
$dataRoot=[IO.Path]::GetFullPath($dataRootText);$dataRootItem=Get-Item -LiteralPath $dataRoot -Force -ErrorAction SilentlyContinue;if(-not$dataRoot.StartsWith($candidateRoot.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase) -or -not$dataRootItem -or ($dataRootItem.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Provider dataRoot must exist as an owned, non-reparse directory under the isolated candidate.'}
$providerName=([string]$provider.provider).ToLowerInvariant();$databaseFile=$null;$instanceName=$null;$databaseName=$null
if($providerName -eq 'sqlite') {
    $databaseFile=[IO.Path]::GetFullPath([string]$provider.databaseFile)
    if(-not$databaseFile.StartsWith($dataRoot.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase) -or -not(Test-Path -LiteralPath $databaseFile -PathType Leaf)){throw 'Declared SQLite database file must exist under its owned dataRoot.'}
    $file=Get-Item -LiteralPath $databaseFile -Force;if(($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $file.Length -lt 16){throw 'SQLite database file must be an owned regular file with enough bytes to validate its header.'}
    $stream=[IO.File]::OpenRead($databaseFile);try{$header=[byte[]]::new(16);if($stream.Read($header,0,16) -ne 16 -or [Text.Encoding]::ASCII.GetString($header) -cne "SQLite format 3`0"){throw 'Declared SQLite file does not have a valid SQLite format header.'}}finally{$stream.Dispose()}
} else {
    $instanceName=[string]$provider.instanceName;$databaseName=[string]$provider.databaseName
    if([string]::IsNullOrWhiteSpace($instanceName) -or [string]::IsNullOrWhiteSpace($databaseName)){throw 'LocalDB declaration must explicitly name its instance and database.'}
}
$handler=[Net.Http.HttpClientHandler]::new();$handler.AllowAutoRedirect=$false;$handler.UseCookies=$true;$handler.CookieContainer=$WebSession.Cookies
$client=[Net.Http.HttpClient]::new($handler);$client.Timeout=[Threading.Timeout]::InfiniteTimeSpan;$deadline=[Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds($MaxSeconds));$jsonOptions=[Text.Json.JsonDocumentOptions]::new();$jsonOptions.MaxDepth=24
function Get-ApiData([string]$Path,[int]$Cap=2MB) {
    $linked=[Threading.CancellationTokenSource]::CreateLinkedTokenSource($deadline.Token);$request=$null;$response=$null;$stream=$null;$memory=[IO.MemoryStream]::new()
    try{$request=[Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get,[uri]::new([uri]($BaseUri.AbsoluteUri.TrimEnd('/')+'/'),$Path));$request.Headers.Add('X-Requested-By','LogForesight');$request.Headers.Accept.ParseAdd('application/json');$linked.CancelAfter([TimeSpan]::FromSeconds(15));$response=$client.SendAsync($request,[Net.Http.HttpCompletionOption]::ResponseHeadersRead,$linked.Token).GetAwaiter().GetResult();if([int]$response.StatusCode -ge 300 -and [int]$response.StatusCode -lt 400){throw 'Public Web API redirected; refusing to follow.'};if(-not$response.IsSuccessStatusCode){throw "Public Web API returned HTTP $([int]$response.StatusCode) for $Path."};if($response.Content.Headers.ContentType.MediaType -cne 'application/json' -or $response.Content.Headers.ContentLength -gt $Cap){throw "Public Web API returned an invalid MIME type or oversized response for $Path."};$stream=$response.Content.ReadAsStreamAsync($linked.Token).GetAwaiter().GetResult();$buffer=[byte[]]::new(8192);while(($n=$stream.ReadAsync($buffer,0,$buffer.Length,$linked.Token).GetAwaiter().GetResult()) -gt 0){if($memory.Length+$n -gt $Cap){throw "Public Web API response exceeded byte limit for $Path."};$memory.Write($buffer,0,$n)};$doc=[Text.Json.JsonDocument]::Parse([ReadOnlyMemory[byte]]$memory.ToArray(),$jsonOptions);try{$root=$doc.RootElement;if($root.ValueKind -ne 'Object') {throw "Public Web API envelope is malformed for $Path."};return $root.GetProperty('data').Clone()}finally{$doc.Dispose()}}
    finally{if($stream){$stream.Dispose()};if($response){$response.Dispose()};if($request){$request.Dispose()};$memory.Dispose();$linked.Dispose()}
}
try {
    $settings=Get-ApiData 'api/admin/settings' 256KB
    if($settings.GetProperty('prtgEnabled').ValueKind -ne 'True' -or $settings.GetProperty('revision').ValueKind -ne 'String'){throw 'Initializer requires current enabled PRTG settings with an explicit revision.'}
    $savedPrtg=[uri]$settings.GetProperty('prtgUrl').GetString();Assert-Loopback $savedPrtg 'Saved PRTG URL';if($savedPrtg.AbsoluteUri.TrimEnd('/') -cne $PrtgFixtureUri.AbsoluteUri.TrimEnd('/')){throw 'Saved PRTG endpoint differs from the explicit loopback fixture URI.'}
    if($manifest.settingsRevision -cne $settings.GetProperty('revision').GetString()){throw 'Manifest settings revision differs from the live public settings API.'}
    $hosts=Get-ApiData 'api/admin/hosts/all' 2MB;if($hosts.ValueKind -ne 'Array' -or $hosts.GetArrayLength() -ne 3000){throw 'Actual public api/admin/hosts/all must return exactly 3,000 hosts.'}
    $hostIds=[Collections.Generic.List[long]]::new();foreach($hostRow in $hosts.EnumerateArray()){$id=$hostRow.GetProperty('hostId').GetInt64();if($id -le 0){throw 'Actual public host IDs must be positive.'};$hostIds.Add($id)}
    if(@($hostIds|Sort-Object -Unique).Count -ne 3000){throw 'Actual public host list contains duplicate IDs.'};$hostIds=[Collections.Generic.List[long]]::new();foreach($id in @($hosts.EnumerateArray()|ForEach-Object{$_.GetProperty('hostId').GetInt64()}|Sort-Object)){$hostIds.Add([long]$id)}
    $hostIdsPath=Join-Path $runFull 'host-ids.txt';[IO.File]::WriteAllText($hostIdsPath,(($hostIds|ForEach-Object{$_.ToString([Globalization.CultureInfo]::InvariantCulture)}) -join "`n")+"`n",[Text.UTF8Encoding]::new($false))
    $hostHash=Get-Sha256Text (($hostIds|ForEach-Object{$_.ToString([Globalization.CultureInfo]::InvariantCulture)}) -join "`n")
    $runId=[guid]::NewGuid().ToString('N');$manifestHash=(Get-FileHash -LiteralPath $manifestFull -Algorithm SHA256).Hash;$providerHash=(Get-FileHash -LiteralPath $providerFull -Algorithm SHA256).Hash
    $profileSchema=if($Profile -eq 'fullstore-3000'){'round53-full-consumer-profile-v1'}else{'round53-controlled-loopback-profile-v1'}
    $profileDocument=[ordered]@{schema=$profileSchema;profile=$Profile;runId=$runId;netiqHosts=3000;prtgSensors=15000;retentionDays=180;provider=$providerName;dataRoot=$dataRoot;databaseFile=$databaseFile;instanceName=$instanceName;databaseName=$databaseName;providerDeclaration='operator-declared';providerDescriptorPath=$providerFull;providerDescriptorSha256=$providerHash;manifestPath=$manifestFull;manifestSha256=$manifestHash;settingsRevision=$settings.GetProperty('revision').GetString();policyRevision=[string]$manifest.policyRevision;sourceGeneration=[string]$manifest.sourceGeneration;scopeFingerprint=[string]$manifest.scopeFingerprint;authorityContextFingerprint=[string]$manifest.authorityContextFingerprint;sensorIdsSha256=$sensorHash;hostIdsSha256=$hostHash;sourceEndpoints=@{web=$BaseUri.AbsoluteUri.TrimEnd('/')+'/';prtg=$PrtgFixtureUri.AbsoluteUri.TrimEnd('/');sentinel=$SentinelFixtureUri.AbsoluteUri.TrimEnd('/')};nativeSourceVerified=$false;formalAcceptance=$false;wholeRoundAccepted=$false}
    $owner=[ordered]@{schema='round53-mixed-owner-v1';runId=$runId;profile=$Profile;dataRoot=$dataRoot;createdUtc=[DateTimeOffset]::UtcNow.ToString('o');providerDescriptorSha256=$providerHash;manifestSha256=$manifestHash;hostIdsSha256=$hostHash;nativeSourceVerified=$false;formalAcceptance=$false;wholeRoundAccepted=$false}
    $preflight=[ordered]@{schema='round53-full-consumer-authority-preflight-v1';status='owned-fixture-declared-awaiting-qualification';profile=$Profile;hosts=3000;sensors=15000;retentionDays=180;settingsEnabled=$true;settingsRevision=$settings.GetProperty('revision').GetString();provider=$providerName;providerDeclaration='operator-declared';providerDescriptorSha256=$providerHash;dataRoot=$dataRoot;databaseFile=$databaseFile;instanceName=$instanceName;databaseName=$databaseName;manifestSha256=$manifestHash;hostIdsSha256=$hostHash;qualificationJobId=$null;qualificationJobVersion=$null;qualificationJobWave=$null;coverageReceiptPath=$null;coverageReceiptSha256=$null;qualifiedSensors=0;readyProfiles=0;qualificationObservedUtc=$null;nativeSourceVerified=$false;formalAcceptance=$false;wholeRoundAccepted=$false}
    Write-JsonAtomic (Join-Path $runFull 'run-owner.json') $owner;Write-JsonAtomic (Join-Path $runFull 'round53-profile.json') $profileDocument;Write-JsonAtomic (Join-Path $runFull 'full-consumer-authority-preflight.json') $preflight
    [pscustomobject]@{status=$preflight.status;repositoryRoot=$candidateRoot;matrixRunDirectory=$runFull;runId=$runId;hostCount=3000;hostIdsSha256=$hostHash;sensorCount=15000;sensorIdsSha256=$sensorHash;provider=$providerName;providerDeclaration='operator-declared';retentionDaysDeclared=180;settingsRevision=$preflight.settingsRevision;nativeSourceVerified=$false;formalAcceptance=$false;wholeRoundAccepted=$false}
} finally {$client.Dispose();$deadline.Dispose()}
