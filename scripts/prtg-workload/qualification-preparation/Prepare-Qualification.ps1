#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Pilot','Start','Resume','Check')][string]$Action,
    [Parameter(Mandatory)][uri]$BaseUri,
    [Parameter(Mandatory)][string]$SourceRoot,
    [Parameter(Mandatory)][string]$ProviderDescriptorPath,
    [Parameter(Mandatory)][string]$SettingsPlanPath,
    [Parameter(Mandatory)][string]$MonitoringPlanPath,
    [Parameter(Mandatory)][string]$BindingPlanPath,
    [Parameter(Mandatory)][string]$WebAssemblyPath,
    [Parameter(Mandatory)][string]$ManifestPath,
    [Parameter(Mandatory)][string]$ReceiptPath,
    [Parameter(Mandatory)][Microsoft.PowerShell.Commands.WebRequestSession]$WebSession,
    [string]$JobId,
    [long]$ExpectedVersion=0,
    [int]$ExpectedWave=0,
    [ValidateRange(1,600)][int]$MaxSeconds=90
)
$ErrorActionPreference='Stop'
$null=Import-Module (Join-Path $PSScriptRoot 'E2Contract.psm1') -Force
$FixedSensors=15000
$MaxBytes=512KB
function Assert-NoReparse([string]$Path,[string]$Label) {
    $p=[IO.Path]::GetFullPath($Path)
    while($p) {
        if(Test-Path -LiteralPath $p){$i=Get-Item -LiteralPath $p -Force;if($i.Attributes -band [IO.FileAttributes]::ReparsePoint){throw "$Label has a reparse-point path component."}}
        $next=[IO.Path]::GetDirectoryName($p);if(-not$next -or $next -ceq $p){break};$p=$next
    }
}
function Assert-Loopback([uri]$u) {
    $ip=$null
    if($u.Scheme -cnotin @('http','https') -or -not[Net.IPAddress]::TryParse($u.DnsSafeHost.Trim('[',']'),[ref]$ip) -or -not[Net.IPAddress]::IsLoopback($ip) -or $u.Port -lt 1 -or $u.UserInfo -or $u.Query -or $u.Fragment){throw 'Web endpoint must be an explicit numeric-loopback URI without credentials/query/fragment.'}
}
function Get-Argv([string]$CommandLine) {
    if(-not('E2CommandLine' -as [type])){Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class E2CommandLine {
 [DllImport("shell32.dll", SetLastError=true)] static extern IntPtr CommandLineToArgvW(string commandLine, out int argc);
 [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);
 public static string[] Parse(string line) {
  int n; IntPtr p=CommandLineToArgvW(line,out n); if(p==IntPtr.Zero) throw new InvalidOperationException("Could not parse process command line.");
  try { var a=new string[n]; for(int i=0;i<n;i++) a[i]=Marshal.PtrToStringUni(Marshal.ReadIntPtr(p,i*IntPtr.Size)) ?? ""; return a; }
  finally { LocalFree(p); }
 }
}
'@ | Out-Null}
    return [E2CommandLine]::Parse($CommandLine)
}
function Assert-OwnedListener([string]$ExpectedRoot,[string]$ExpectedDatabase,[string]$Provider,[string]$DescriptorHash) {
    $connections=@(Get-NetTCPConnection -State Listen -LocalPort $BaseUri.Port -ErrorAction SilentlyContinue)
    if($connections.Count -ne 1){throw 'Expected exactly one Web listener on the requested port; wildcard, foreign, or dual listeners are refused.'}
    $listenerProcessId=Get-E2ListenerProcessId $connections[0]
    $proc=Get-CimInstance Win32_Process -Filter "ProcessId=$listenerProcessId" -ErrorAction Stop
    $live=[Diagnostics.Process]::GetProcessById($listenerProcessId)
    $tokens=@(Get-Argv ([string]$proc.CommandLine))
    $assemblyToken=$tokens|Where-Object {$_ -match '(?i)(^|[\\/])LogForesight\.Web\.dll$'}|Select-Object -First 1
    if(-not$assemblyToken){throw 'Listener process does not name the expected Web assembly as an exact argv token.'}
    $assembly=[IO.Path]::GetFullPath($assemblyToken)
    $assemblyHash=(Get-FileHash -LiteralPath $assembly -Algorithm SHA256).Hash
    if($assembly -cne [IO.Path]::GetFullPath($WebAssemblyPath)){throw 'Listener Web assembly path differs from the selected release.'}
    $coreAssembly=[IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $assembly) 'LogForesight.Core.dll'))
    if(-not(Test-Path -LiteralPath $coreAssembly -PathType Leaf)){throw 'Selected release has no adjacent LogForesight.Core.dll.'}
    $coreAssemblyHash=(Get-FileHash -LiteralPath $coreAssembly -Algorithm SHA256).Hash
    $runtimeRequest=$null;$runtimeCts=[Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(10))
    try {
        $runtimeRequest=[Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get,[uri]::new([uri]($BaseUri.AbsoluteUri.TrimEnd('/')+'/'),'api/admin/prtg-runtime-ownership/status'))
        $runtimeElement=Invoke-E2JsonApi $client $runtimeRequest $runtimeCts.Token 32KB
        $runtimeMetadata=$runtimeElement.GetRawText()|ConvertFrom-Json -Depth 8 -AsHashtable
    } finally {if($runtimeRequest){$runtimeRequest.Dispose()};$runtimeCts.Dispose()}
    $request=[ordered]@{processId=$listenerProcessId;processStartUtc=$live.StartTime.ToUniversalTime().ToString('o');processStartUtcTicks=$live.StartTime.ToUniversalTime().Ticks;expectedPort=$BaseUri.Port;expectedAddress=$BaseUri.DnsSafeHost.Trim('[',']');listeners=@($connections|ForEach-Object{@{address=$_.LocalAddress;port=$_.LocalPort;processId=$_.OwningProcess}});expectedAssembly=[IO.Path]::GetFullPath($WebAssemblyPath);assemblySha256=$assemblyHash;expectedCoreAssembly=$coreAssembly;coreAssemblySha256=$coreAssemblyHash;expectedDataRoot=[IO.Path]::GetFullPath($ExpectedRoot);expectedProvider=$Provider;expectedDatabase=[IO.Path]::GetFullPath($ExpectedDatabase);descriptorSha256=$DescriptorHash;commandTokens=$tokens;runtimeMetadata=$runtimeMetadata}
    $ownerVerifier=Join-Path $PSScriptRoot 'verify_runtime_owner.py'
    $json=ConvertTo-Json -InputObject $request -Depth 12 -Compress
    $verified=$json|& python $ownerVerifier
    if($LASTEXITCODE -ne 0){throw 'Live Web listener failed exact process/assembly/argv/provider ownership verification.'}
    $owner=$verified|ConvertFrom-Json -AsHashtable -Depth 8
    $owner.observedUtc=[DateTimeOffset]::UtcNow.ToString('o')
    return $owner
}
function Write-Receipt([System.Collections.IDictionary]$State) {
    $full=[IO.Path]::GetFullPath($ReceiptPath);Assert-NoReparse $full 'Receipt'
    $dir=Split-Path -Parent $full;$null=New-Item -ItemType Directory -Path $dir -Force
    $State['updatedUtc']=[DateTimeOffset]::UtcNow.ToString('o');$tmp=$full+'.tmp-'+[guid]::NewGuid().ToString('N')
    $bytes=[Text.UTF8Encoding]::new($false).GetBytes(($State|ConvertTo-Json -Depth 20))
    $fs=[IO.FileStream]::new($tmp,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try{$fs.Write($bytes,0,$bytes.Length);$fs.Flush($true)}finally{$fs.Dispose()}
    [IO.File]::Move($tmp,$full,$true)
}
Assert-Loopback $BaseUri
$root=[IO.Path]::GetFullPath($SourceRoot);$descriptor=[IO.Path]::GetFullPath($ProviderDescriptorPath);$settingsPlanPath=[IO.Path]::GetFullPath($SettingsPlanPath);$monitoringPlanPath=[IO.Path]::GetFullPath($MonitoringPlanPath);$bindingPlanPath=[IO.Path]::GetFullPath($BindingPlanPath);$assemblyPath=[IO.Path]::GetFullPath($WebAssemblyPath)
foreach($path in @($root,$descriptor,$settingsPlanPath,$monitoringPlanPath,$bindingPlanPath,$assemblyPath,[IO.Path]::GetFullPath($ManifestPath),[IO.Path]::GetFullPath($ReceiptPath))){Assert-NoReparse $path 'E2 path'}
if(-not(Test-Path -LiteralPath $root -PathType Container) -or -not(Test-Path -LiteralPath $descriptor -PathType Leaf) -or -not(Test-Path -LiteralPath $settingsPlanPath -PathType Leaf) -or -not(Test-Path -LiteralPath $monitoringPlanPath -PathType Leaf) -or -not(Test-Path -LiteralPath $bindingPlanPath -PathType Leaf) -or -not(Test-Path -LiteralPath $assemblyPath -PathType Leaf)){throw 'Source root, provider descriptor, settings plan, monitoring source plan, binding plan, or Web assembly is missing.'}
if((Get-Item -LiteralPath $settingsPlanPath).Length -gt 64KB){throw 'Operator settings plan exceeds 64 KiB.'}
if((Get-Item -LiteralPath $monitoringPlanPath).Length -gt 2MB){throw 'Operator monitoring source plan exceeds 2 MiB.'}
if((Get-Item -LiteralPath $bindingPlanPath).Length -gt 8MB){throw 'Explicit binding plan exceeds 8 MiB.'}
$settingsPlan=Get-Content -LiteralPath $settingsPlanPath -Raw|ConvertFrom-Json -Depth 12
if($settingsPlan.schema -cne 'round54-e2-settings-plan-v1' -or [string]::IsNullOrWhiteSpace([string]$settingsPlan.evidenceReference) -or [string]::IsNullOrWhiteSpace([string]$settingsPlan.expectedRevision) -or $settingsPlan.prtgEnabled -ne $true){throw 'Settings plan must carry operator evidence, an exact expected revision, and explicit PRTG enablement.'}
$monitoringPlan=Get-Content -LiteralPath $monitoringPlanPath -Raw|ConvertFrom-Json -Depth 12
if($monitoringPlan.schema -cne 'round54-e2-monitoring-plan-v1' -or [string]::IsNullOrWhiteSpace([string]$monitoringPlan.evidenceReference) -or
   [string]::IsNullOrWhiteSpace([string]$monitoringPlan.expectedRevision) -or [string]::IsNullOrWhiteSpace([string]$monitoringPlan.coreSystemId) -or
   [string]::IsNullOrWhiteSpace([string]$monitoringPlan.sourceTimeZoneId) -or [string]::IsNullOrWhiteSpace([string]$monitoringPlan.sourceCultureName) -or
   [string]::IsNullOrWhiteSpace([string]$monitoringPlan.rawTimestampTimeZoneId) -or [string]::IsNullOrWhiteSpace([string]$monitoringPlan.analysisTimeZoneId) -or
   -not(Test-E2SafeTimeBasisReference $monitoringPlan.timeBasisEvidenceReference) -or
   ([string]$monitoringPlan.rawTimestampTimeZoneId).Length -gt 128 -or ([string]$monitoringPlan.analysisTimeZoneId).Length -gt 128 -or
   $monitoringPlan.PSObject.Properties.Name -notcontains 'messageDateFormat' -or ([string]$monitoringPlan.messageDateFormat).Length -gt 16 -or
   ([string]$monitoringPlan.messageDateFormat).Length -gt 0 -and [string]$monitoringPlan.messageDateFormat -notin @('dmy-24','dmy-12','mdy-24','mdy-12','ymd-24','ymd-12') -or
   $monitoringPlan.identityConfirmed -ne $true -or
   $monitoringPlan.sourceChangeMode -notin @('new','continue') -or @($monitoringPlan.hostIds).Count -ne 3000 -or @($monitoringPlan.sensorIds).Count -ne $FixedSensors){throw 'Monitoring plan must contain operator evidence, exact current-revision fence, explicit source identity/time basis, and exactly 3,000 hosts plus 15,000 sensors.'}
if(@($monitoringPlan.hostIds|Sort-Object -Unique).Count -ne 3000 -or @($monitoringPlan.sensorIds|Sort-Object -Unique).Count -ne $FixedSensors -or @($monitoringPlan.hostIds|Where-Object{[long]$_ -le 0}).Count -gt 0 -or @($monitoringPlan.sensorIds|Where-Object{[long]$_ -le 0}).Count -gt 0){throw 'Monitoring plan IDs must be positive, unique, complete, and operator-selected; no sensor/host is inferred or truncated.'}
if($monitoringPlan.sourceChangeMode -eq 'continue' -and ($monitoringPlan.continuityConfirmed -ne $true -or [string]::IsNullOrWhiteSpace([string]$monitoringPlan.continuityEvidenceReference))){throw 'Continuity requires explicit confirmation and an evidence reference; unknown source identity cannot be re-attested.'}
if($monitoringPlan.sourceChangeMode -eq 'new' -and ($monitoringPlan.continuityConfirmed -eq $true -or -not[string]::IsNullOrWhiteSpace([string]$monitoringPlan.continuityEvidenceReference))){throw 'New-source mode cannot carry continuity confirmation or evidence.'}
if(@($monitoringPlan.PSObject.Properties.Name|Where-Object{$_ -notin @('schema','evidenceReference','expectedRevision','coreSystemId','sourceTimeZoneId','sourceCultureName','messageDateFormat','rawTimestampTimeZoneId','analysisTimeZoneId','timeBasisEvidenceReference','sourceChangeMode','continuityConfirmed','continuityEvidenceReference','identityConfirmed','hostIds','sensorIds')}).Count -gt 0){throw 'Monitoring plan contains unknown fields; provide only the normal public source-confirmation request inputs.'}
$bindingPlan=Get-Content -LiteralPath $bindingPlanPath -Raw|ConvertFrom-Json -Depth 12
if($bindingPlan.schema -cne 'round54-e2-binding-plan-v1' -or @($bindingPlan.rows).Count -ne $FixedSensors){throw 'Binding plan must contain exactly one operator-reviewed semantic row for all 15,000 selected sensors.'}
foreach($row in $bindingPlan.rows){
    foreach($field in @('rawTimestampTimeZoneId','analysisTimeZoneId','timeBasisEvidenceReference')){
        if([string]$row.$field -cne [string]$monitoringPlan.$field){throw 'Binding time basis differs from the reviewed monitoring policy; no API writes were attempted.'}
    }
}
$providerDescriptor=Get-Content -LiteralPath $descriptor -Raw|ConvertFrom-Json -Depth 8
$descriptorUri=[uri]$providerDescriptor.prtgUrl;Assert-Loopback $descriptorUri
$planEndpoint=[uri]$settingsPlan.prtgUrl;Assert-Loopback $planEndpoint
if($planEndpoint.AbsoluteUri.TrimEnd('/') -cne $descriptorUri.AbsoluteUri.TrimEnd('/')){throw 'Operator settings plan URL must equal the saved provider descriptor endpoint.'}
$settingsKeys=@('expectedRevision','prtgEnabled','prtgUrl','prtgIgnoreSslErrors','prtgTimeoutSeconds','prtgFetchConcurrency','prtgBackfillDays','prtgRetentionDays','prtgSensorTypeWhitelist','prtgSensorTypeCategoryOverrides','prtgValueFetchScope','prtgFetchStrategy','prtgValueFetchExtraHosts','prtgResourceGuardEnabled','prtgResourceGuardSensorObjids','prtgResourceGuardCpuPercent','prtgResourceGuardMemoryFreePercent','prtgResourceGuardCheckSeconds','prtgResourceGuardPauseMinutes','prtgResourceGuardStrikes','prtgResourceGuardMaxPauseMinutes','allowDisabledBootstrap','schema','evidenceReference')
if(@($settingsPlan.PSObject.Properties.Name|Where-Object{$_ -notin $settingsKeys}).Count -gt 0){throw 'Settings plan contains an unknown or secret-bearing property; only reviewed public PRTG settings are accepted.'}
$settingsFields=[ordered]@{}
foreach($property in $settingsPlan.PSObject.Properties){if($property.Name -notin @('schema','evidenceReference','allowDisabledBootstrap')){$settingsFields[$property.Name]=$property.Value}}
$settingsPlanHash=ConvertTo-E2Sha256 (Get-FileHash -LiteralPath $settingsPlanPath -Algorithm SHA256).Hash 'settings-plan SHA-256'
$monitoringPlanHash=ConvertTo-E2Sha256 (Get-FileHash -LiteralPath $monitoringPlanPath -Algorithm SHA256).Hash 'monitoring source plan SHA-256'
$py=Join-Path $PSScriptRoot 'e2_preflight.py'
$preflightJson=& python $py --source-root $root --provider-descriptor $descriptor --monitoring-plan $monitoringPlanPath
if($LASTEXITCODE -ne 0){throw 'Read-only full receipt/catalog preflight failed; no Web API request was made.'}
$preflight=($preflightJson -join "`n")|ConvertFrom-Json -Depth 16
$descriptorHash=ConvertTo-E2Sha256 (Get-FileHash -LiteralPath $descriptor -Algorithm SHA256).Hash 'provider descriptor SHA-256'
if($preflight.status -cne 'complete-owned-source' -or $preflight.catalog.values -ne 64800000 -or (ConvertTo-E2Sha256 $preflight.descriptorSha256 'preflight descriptor SHA-256') -cne $descriptorHash){throw 'Full owned E1 receipt/catalog is not current or exact.'}
$handler=[Net.Http.HttpClientHandler]::new();$handler.AllowAutoRedirect=$false;$handler.UseCookies=$true;$handler.CookieContainer=$WebSession.Cookies
$client=[Net.Http.HttpClient]::new($handler);$client.Timeout=[Threading.Timeout]::InfiniteTimeSpan
$owner=Assert-OwnedListener ([string]$preflight.dataRoot) ([string]$preflight.databasePath) 'Sqlite' $descriptorHash
$whole=[Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds($MaxSeconds))
function Invoke-Api([string]$Method,[string]$Path,[object]$Body=$null,[int]$Cap=$MaxBytes) {
    $cts=[Threading.CancellationTokenSource]::CreateLinkedTokenSource($whole.Token);$cts.CancelAfter([TimeSpan]::FromSeconds(30));$req=$null
    try {
        $currentOwner=Assert-OwnedListener ([string]$preflight.dataRoot) ([string]$preflight.databasePath) 'Sqlite' $descriptorHash
        foreach($name in @('processId','processStartUtcTicks','assemblyPath','assemblySha256','webAssemblyMvid','coreAssemblyPath','coreAssemblySha256','coreAssemblyMvid','commandTokenCount','commandTokensSha256','dataRoot','provider','databasePath','descriptorSha256','localAddress','port')){
            if([string]$currentOwner[$name] -cne [string]$owner[$name]){throw 'Owned Web listener identity changed during the preparation run; refusing to continue against a replacement process.'}
        }
        $req=[Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::new($Method),[uri]::new([uri]($BaseUri.AbsoluteUri.TrimEnd('/')+'/'),$Path))
        if($Method -ne 'GET'){$req.Headers.Add('X-Requested-By','LogForesight')}
        if($null -ne $Body){$req.Content=[Net.Http.StringContent]::new(($Body|ConvertTo-Json -Depth 12 -Compress),[Text.Encoding]::UTF8,'application/json')}
        return Invoke-E2JsonApi $client $req $cts.Token $Cap
    } finally {if($req){$req.Dispose()};$cts.Dispose()}
}
function Assert-E2CurrentJobPages([string]$JobId,[System.Text.Json.JsonElement]$Before,[long]$Version,[int]$Wave,
    [System.Text.Json.JsonElement[]]$Profiles,[System.Collections.IDictionary]$Manifest,[object[]]$Plans) {
    Assert-E2JobIdentity $Before $JobId $Version $Wave
    foreach($tuple in @(@('settingsRevision',[string]$Manifest.settingsRevision),
        @('policyRevision',[string]$Manifest.policyRevision),@('scopeFingerprint',[string]$Manifest.scopeFingerprint),
        @('sourceGeneration',[string]$Manifest.sourceGeneration),@('authorityContextFingerprint',[string]$Manifest.authorityContextFingerprint))) {
        if($Before.GetProperty($tuple[0]).GetString() -cne $tuple[1]){throw "Current durable job has stale $($tuple[0]) context."}
    }
    $selected=$Before.GetProperty('selected').GetInt32()
    $pageCount=$Before.GetProperty('pageCount').GetInt32()
    $initializedPages=$Before.GetProperty('initializedPages').GetInt32()
    $expectedPages=[int][Math]::Ceiling($selected/100.0)
    if($selected -ne $Manifest.sensorIds.Count -or $pageCount -ne $expectedPages -or
       $initializedPages -lt 0 -or $initializedPages -gt $pageCount) {
        throw 'Current durable job page shape differs from the sealed sensor scope.'
    }
    $rows=[Collections.Generic.List[System.Text.Json.JsonElement]]::new()
    for($pageIndex=0;$pageIndex -lt $initializedPages;$pageIndex++) {
        $offset=$pageIndex*100;$limit=[Math]::Min(100,$selected-$offset)
        $path="api/prtg/monitoring/trusted-sampling/qualification-jobs/$JobId/page?offset=$offset&limit=$limit"
        $page=Invoke-Api GET $path $null 128KB
        if($page.GetProperty('total').GetInt32() -ne $selected -or
           $page.GetProperty('offset').GetInt32() -ne $offset -or
           $page.GetProperty('limit').GetInt32() -ne $limit -or
           $page.GetProperty('rows').GetArrayLength() -ne $limit) {
            throw 'Qualification job page is incomplete or not aligned to its durable snapshot; retry Resume/Check.'
        }
        foreach($row in $page.GetProperty('rows').EnumerateArray()) {$rows.Add($row.Clone())}
    }
    $After=Invoke-Api GET 'api/prtg/monitoring/trusted-sampling/qualification-jobs/current' $null 128KB
    Assert-E2StableJobSnapshot $Before $After $JobId $Version $Wave
    Assert-E2ResumeProfileProgress $Profiles $Manifest $Plans $rows.ToArray()
}
$sourcePreflightHash=ConvertTo-E2Sha256 $preflight.sourceIdentitySha256 'stable source identity SHA-256'
$planHash=ConvertTo-E2Sha256 (Get-FileHash -LiteralPath $bindingPlanPath -Algorithm SHA256).Hash 'binding-plan SHA-256'
$receiptFull=[IO.Path]::GetFullPath($ReceiptPath)
if(Test-Path -LiteralPath $receiptFull -PathType Leaf){
    $receipt=Get-Content -LiteralPath $receiptFull -Raw|ConvertFrom-Json -Depth 24 -AsHashtable
    if($receipt.schema -cne 'round54-e2-qualification-preparation-v1' -or (ConvertTo-E2Sha256 $receipt.sourcePreflightSha256 'receipt source identity SHA-256') -cne $sourcePreflightHash -or (ConvertTo-E2Sha256 $receipt.descriptorSha256 'receipt provider descriptor SHA-256') -cne $descriptorHash -or (ConvertTo-E2Sha256 $receipt.settingsPlanSha256 'receipt settings plan SHA-256') -cne $settingsPlanHash -or (ConvertTo-E2Sha256 $receipt.monitoringPlanSha256 'receipt monitoring plan SHA-256') -cne $monitoringPlanHash -or (ConvertTo-E2Sha256 $receipt.bindingPlanSha256 'receipt binding plan SHA-256') -cne $planHash){throw 'Existing E2 receipt belongs to another source/provider/settings/monitoring/binding-plan snapshot; it is preserved and cannot be overwritten.'}
    $explicitUnknownStartReconcile=$Action -in @('Resume','Check') -and $receipt.jobStartOutcomeUnknown -eq $true -and [string]::IsNullOrWhiteSpace([string]$receipt.jobId)
    if($Action -in @('Resume','Check') -and -not$explicitUnknownStartReconcile -and ($receipt.jobId -cne $JobId -or $receipt.jobVersion -ne $ExpectedVersion -or $receipt.jobWave -ne $ExpectedWave)){throw 'Resume/Check inputs must match the exact durable job ID, version, and wave in the existing receipt.'}
    if($explicitUnknownStartReconcile -and ([string]::IsNullOrWhiteSpace($JobId) -or $ExpectedVersion -le 0 -or $ExpectedWave -le 0)){throw 'An unknown start outcome can be reconciled only with the operator-supplied exact current job ID, version, and wave.'}
    if($Action -eq 'Start' -and -not[string]::IsNullOrWhiteSpace([string]$receipt.jobId)){throw 'An earlier receipt already names a job; preserve it and use explicit Resume or Check.'}
    if(-not[string]::IsNullOrWhiteSpace([string]$receipt.manifestSha256) -and (-not(Test-Path -LiteralPath $receipt.manifestPath -PathType Leaf) -or (ConvertTo-E2Sha256 (Get-FileHash -LiteralPath $receipt.manifestPath -Algorithm SHA256).Hash) -cne (ConvertTo-E2Sha256 $receipt.manifestSha256 'receipt manifest SHA-256'))){throw 'Existing manifest is missing or differs from its sealed partial receipt; preserve both and refuse replay.'}
    if($Action -in @('Resume','Check')){
        if([string]::IsNullOrWhiteSpace([string]$receipt.manifestSha256) -or
           [IO.Path]::GetFullPath([string]$receipt.manifestPath) -cne [IO.Path]::GetFullPath($ManifestPath)){
            throw 'Resume/Check require the exact original sealed manifest path and digest; the original receipt is preserved.'
        }
    }
    $receipt.action=$Action
} else {
    if($Action -in @('Resume','Check')){throw 'Resume/Check require a durable E2 partial receipt; no receipt is created from guessed job identity.'}
    $receipt=[ordered]@{schema='round54-e2-qualification-preparation-v1';status='preflight-complete';action=$Action;sourcePreflightSha256=$sourcePreflightHash;descriptorSha256=$descriptorHash;settingsPlanSha256=$settingsPlanHash;monitoringPlanSha256=$monitoringPlanHash;monitoringExpectedRevision=[string]$monitoringPlan.expectedRevision;bindingPlanSha256=$planHash;runtimeOwner=$owner;settingsRevision=$null;policyRevision=$null;scopeFingerprint=$null;sourceGeneration=$null;authorityContextFingerprint=$null;manifestPath=[IO.Path]::GetFullPath($ManifestPath);manifestSha256=$null;bindingCommitted=@();bindingRejected=@();pilotStatus=$null;jobId=$null;jobVersion=$null;jobWave=$null;jobStatus=$null;durationHours=720;maximumAttempts=1;nativeSourceVerified=$false;capacityAccepted=$false;retentionAccepted=$false;formalAcceptance=$false;wholeRoundAccepted=$false;stages=@()}
}
$receipt.runtimeOwner=$owner
Write-Receipt $receipt
try {
    $desiredSettingsFields=$settingsFields
    $bootstrapRequired=Test-E2BootstrapRequired $Action (-not[string]::IsNullOrWhiteSpace([string]$receipt.settingsRevision)) ($receipt.bootstrapCompleted -eq $true)
    if($bootstrapRequired){
        if($settingsPlan.allowDisabledBootstrap -ne $true){throw 'Initial Start requires explicit allowDisabledBootstrap=true in the reviewed settings plan; source preparation saves disabled settings, runs both admission pilots, then enables through the normal guarded API.'}
        $settingsFields=[ordered]@{};foreach($key in $desiredSettingsFields.Keys){$settingsFields[$key]=$desiredSettingsFields[$key]}
        $settingsFields.prtgEnabled=$false
    }
    $settings=Invoke-Api GET 'api/admin/settings' $null 128KB
    if($receipt.bootstrapEnableOutcomeUnknown -eq $true){
        if($settings.GetProperty('prtgEnabled').GetBoolean()){
            Assert-E2BootstrapReadback $settings $desiredSettingsFields $true
            $receipt.bootstrapEnableOutcomeUnknown=$false;$receipt.bootstrapCompleted=$true
            $receipt.stages+=@('guarded-source-enable-reconciled-by-exact-readback');Write-Receipt $receipt
            $bootstrapRequired=$false;$settingsFields=$desiredSettingsFields
        } else {
            $disabledFields=[ordered]@{};foreach($key in $desiredSettingsFields.Keys){$disabledFields[$key]=$desiredSettingsFields[$key]};$disabledFields.prtgEnabled=$false
            if([string]::IsNullOrWhiteSpace([string]$receipt.bootstrapSettingsRevision)){
                throw 'Unknown source-enable outcome has no saved disabled-bootstrap revision; preserve the receipt and inspect current settings.'
            }
            Assert-E2BootstrapReadback $settings $disabledFields $false ([string]$receipt.bootstrapSettingsRevision)
            $receipt.bootstrapEnableOutcomeUnknown=$false
            $receipt.stages+=@('guarded-source-enable-not-applied-by-exact-disabled-readback');Write-Receipt $receipt
        }
    }
    $settingsMatches=$true
    foreach($property in $settingsFields.Keys){
        if($property -eq 'expectedRevision'){continue}
        $actual=$settings.GetProperty([string]$property).GetRawText()|ConvertFrom-Json -Depth 12
        $expectedJson=ConvertTo-Json -InputObject $settingsFields[$property] -Depth 12 -Compress
        $actualJson=ConvertTo-Json -InputObject $actual -Depth 12 -Compress
        if($actualJson -cne $expectedJson){$settingsMatches=$false;break}
    }
    $settingsDecision=Get-E2SettingsWriteDecision $settings.GetProperty('revision').GetString() ([string]$settingsPlan.expectedRevision) $settingsMatches
    if($receipt.settingsUpdateOutcomeUnknown -eq $true -and $settingsMatches){$receipt.settingsUpdateOutcomeUnknown=$false;$receipt.stages+=@('public-settings-update-reconciled-by-exact-readback');Write-Receipt $receipt}
    if($settingsDecision -eq 'Save'){
        if($Action -in @('Resume','Check')){throw 'Resume/Check cannot change saved source settings; current settings must exactly match the sealed plan.'}
        Assert-E2ActionMayWrite $Action 'Settings'
        $receipt.settingsUpdateOutcomeUnknown=$true;$receipt.stages+=@('public-settings-update-submitting');Write-Receipt $receipt
        $settings=Invoke-Api PUT 'api/admin/settings/prtg' $settingsFields 128KB
        $settingsMatchAfter=$true
        foreach($property in $settingsFields.Keys){if($property -eq 'expectedRevision'){continue};$actual=$settings.GetProperty([string]$property).GetRawText()|ConvertFrom-Json -Depth 12;$expectedJson=ConvertTo-Json -InputObject $settingsFields[$property] -Depth 12 -Compress;$actualJson=ConvertTo-Json -InputObject $actual -Depth 12 -Compress;if($actualJson -cne $expectedJson){$settingsMatchAfter=$false;break}}
        if(-not$settingsMatchAfter){throw 'Public settings save returned without the exact operator-requested PRTG settings; receipt preserves an unresolved update.'}
        $receipt.settingsUpdateOutcomeUnknown=$false;$receipt.stages+=@('public-settings-updated');Write-Receipt $receipt
    } elseif($settings.GetProperty('revision').GetString() -cne [string]$settingsPlan.expectedRevision){
        $receipt.settingsUpdateOutcomeUnknown=$false;$receipt.stages+=@('public-settings-update-reconciled-current-values');Write-Receipt $receipt
    }
    if(-not$bootstrapRequired -and -not$settings.GetProperty('prtgEnabled').GetBoolean()){throw 'Saved PRTG settings are disabled; preparation remains pending.'}
    $revision=$settings.GetProperty('revision').GetString();$savedUri=[uri]$settings.GetProperty('prtgUrl').GetString();Assert-Loopback $savedUri
    if($savedUri.AbsoluteUri.TrimEnd('/') -cne $descriptorUri.AbsoluteUri.TrimEnd('/')){throw 'Saved public PRTG settings endpoint differs from the owned provider descriptor.'}
    $monitoring=Invoke-Api GET 'api/prtg/monitoring' $null 256KB
    $allowMessageDateFormatDrift=$Action -in @('Resume','Check')
    $monitoringMatches=Test-E2MonitoringPolicyMatches $monitoring $monitoringPlan $allowMessageDateFormatDrift
    $policyRevisionNow=$monitoring.GetProperty('revision').GetString()
    $reconcileMonitoringSave=$monitoringMatches -and $receipt.monitoringUpdateOutcomeUnknown -eq $true
    $monitoringSaveAlreadySealed=$monitoringMatches -and -not$reconcileMonitoringSave -and
        -not[string]::IsNullOrWhiteSpace([string]$receipt.monitoringPolicyRevision) -and
        ($allowMessageDateFormatDrift -or $receipt.monitoringPolicyRevision -ceq $policyRevisionNow)
    if(-not$reconcileMonitoringSave -and -not$monitoringSaveAlreadySealed){
        if($Action -in @('Resume','Check')){throw 'Resume/Check cannot write or repair the saved monitoring source policy; a prior partial source save must be reconciled by exact public readback.'}
        Assert-E2ActionMayWrite $Action 'SourcePolicy'
        if($policyRevisionNow -cne [string]$monitoringPlan.expectedRevision){throw 'Public monitoring policy revision differs from the reviewed plan; refresh the policy and re-approve before source changes.'}
        $monitorRequest=New-E2MonitoringRequest $monitoringPlan $policyRevisionNow ([string]$monitoring.GetProperty('catalogueToken').GetString())
        $estimate=Invoke-Api POST 'api/prtg/monitoring/estimate' $monitorRequest 128KB
        if($estimate.GetProperty('hostCount').GetInt32() -ne 3000 -or $estimate.GetProperty('selectedSensorCount').GetInt32() -ne $FixedSensors){throw 'Normal public monitoring estimate did not accept the exact reviewed 3,000-host / 15,000-sensor scope.'}
        $monitorRequest.estimateToken=$estimate.GetProperty('estimateToken').GetString()
        $preview=Invoke-Api POST 'api/prtg/monitoring/source-preview' $monitorRequest 256KB
        if(-not(Test-E2IdSequenceMatches $preview.GetProperty('proposedHostIds') $monitoringPlan.hostIds) -or -not(Test-E2IdSequenceMatches $preview.GetProperty('proposedSensorIds') $monitoringPlan.sensorIds)){throw 'Normal public source preview did not return the exact reviewed host/sensor IDs.'}
        if($monitoringPlan.sourceChangeMode -eq 'continue' -and -not$preview.GetProperty('continuationAllowed').GetBoolean()){throw 'Normal public source preview does not authorize continuity for the supplied identity and time basis.'}
        $receipt.monitoringEstimateHostCount=$estimate.GetProperty('hostCount').GetInt32();$receipt.monitoringEstimateSensorCount=$estimate.GetProperty('selectedSensorCount').GetInt32();$receipt.stages+=@('public-monitoring-estimate-and-source-preview-accepted');Write-Receipt $receipt
        $receipt.monitoringUpdateOutcomeUnknown=$true;$receipt.stages+=@('public-monitoring-source-update-submitting');Write-Receipt $receipt
        $null=Invoke-Api PUT 'api/prtg/monitoring' $monitorRequest 256KB
        $monitoring=Invoke-Api GET 'api/prtg/monitoring' $null 256KB
        if(-not(Test-E2MonitoringPolicyMatches $monitoring $monitoringPlan)){throw 'Public monitoring source save did not read back as the exact reviewed source identity and scope; receipt preserves the unresolved update.'}
        $receipt.monitoringPolicyRevision=$monitoring.GetProperty('revision').GetString();$receipt.monitoringUpdateOutcomeUnknown=$false;$receipt.stages+=@('public-monitoring-source-updated-and-readback');Write-Receipt $receipt
    } elseif($reconcileMonitoringSave){
        $receipt.monitoringPolicyRevision=$policyRevisionNow;$receipt.monitoringUpdateOutcomeUnknown=$false;$receipt.stages+=@('public-monitoring-source-update-reconciled-by-readback');Write-Receipt $receipt
    } else {$receipt.stages+=@('public-monitoring-source-already-sealed-by-receipt');Write-Receipt $receipt}
    if($bootstrapRequired){
        if($settings.GetProperty('prtgEnabled').GetBoolean()){throw 'Bootstrap must remain disabled through both admission pilots.'}
        $receipt.bootstrapSettingsRevision=$revision;$receipt.stages+=@('disabled-source-and-time-basis-prepared');Write-Receipt $receipt
        Assert-E2ActionMayWrite $Action 'AdmissionPilot'
        $snapshotPilot=Invoke-Api POST 'api/admin/settings/prtg-capacity-pilot' @{} 128KB
        if($snapshotPilot.GetProperty('status').GetString() -cne 'capacity-qualified' -or $snapshotPilot.GetProperty('targetCount').GetInt32() -le 0){throw 'Snapshot admission pilot did not qualify a nonempty actual source; source remains disabled.'}
        $receipt.snapshotAdmissionPilot=$snapshotPilot.GetRawText()|ConvertFrom-Json -Depth 12;$receipt.stages+=@('snapshot-admission-pilot-qualified');Write-Receipt $receipt
        $profilePilot=$null
        for($sample=0;$sample -lt 5;$sample++){
            $currentSettings=Invoke-Api GET 'api/admin/settings' $null 128KB
            $currentPolicy=Invoke-Api GET 'api/prtg/monitoring' $null 256KB
            if($currentSettings.GetProperty('revision').GetString() -cne $revision -or $currentSettings.GetProperty('prtgEnabled').GetBoolean() -or $currentPolicy.GetProperty('revision').GetString() -cne [string]$receipt.monitoringPolicyRevision){throw 'Source settings or policy changed during disabled admission pilots.'}
            Assert-E2ActionMayWrite $Action 'AdmissionPilot'
            $profilePilot=Invoke-Api POST 'api/prtg/monitoring/trusted-sampling/profile-capacity-pilot' @{} 128KB
            $receipt.profileAdmissionPilot=$profilePilot.GetRawText()|ConvertFrom-Json -Depth 12;$receipt.stages+=@("profile-admission-pilot-sample-$($sample+1)");Write-Receipt $receipt
            if($profilePilot.GetProperty('targetSensorCount').GetInt32() -ne $FixedSensors){throw 'Profile transport pilot does not cover the exact reviewed policy denominator.'}
            $pilotStatus=$profilePilot.GetProperty('status').GetString()
            if($pilotStatus -ceq 'capacity-qualified'){break}
            if($pilotStatus -ceq 'capacity-exceeded'){throw 'Profile transport admission exceeds the budget; source remains disabled.'}
        }
        if($null -eq $profilePilot -or $profilePilot.GetProperty('status').GetString() -cne 'capacity-qualified'){throw 'Five bounded profile transport samples did not qualify admission; source remains disabled.'}
        $enableFields=[ordered]@{};foreach($key in $desiredSettingsFields.Keys){$enableFields[$key]=$desiredSettingsFields[$key]};$enableFields.expectedRevision=$revision
        Assert-E2ActionMayWrite $Action 'Settings'
        $receipt.bootstrapEnableOutcomeUnknown=$true;$receipt.stages+=@('guarded-source-enable-submitting');Write-Receipt $receipt
        $settings=Invoke-Api PUT 'api/admin/settings/prtg' $enableFields 128KB
        Assert-E2BootstrapReadback $settings $desiredSettingsFields $true
        $revision=$settings.GetProperty('revision').GetString();$receipt.bootstrapEnableOutcomeUnknown=$false;$receipt.bootstrapCompleted=$true;$receipt.stages+=@('both-admission-pilots-qualified-and-source-enabled');Write-Receipt $receipt
        $settingsFields=$desiredSettingsFields
    }
    $contract=Invoke-Api GET 'api/prtg/monitoring/trusted-sampling/qualification-jobs/contract' $null 128KB
    $sensorCount=$contract.GetProperty('selectedSensors').GetInt32();if($sensorCount -ne $FixedSensors -or $contract.GetProperty('settingsRevision').GetString() -cne $revision){throw 'Public qualification contract is not the exact current 15,000 sensor scope.'}
    $policyRevision=$contract.GetProperty('policyRevision').GetString()
    $ids=[Collections.Generic.List[long]]::new();$fences=[Collections.Generic.List[object]]::new();$profileRows=[Collections.Generic.List[object]]::new();$contextFence=$null
    for($offset=0;$offset -lt $FixedSensors;$offset+=100){
        $limit=[Math]::Min(100,$FixedSensors-$offset);$page=Invoke-Api GET "api/prtg/monitoring/trusted-sampling/profiles?offset=$offset&limit=$limit" $null 512KB
        if($page.GetProperty('offset').GetInt32() -ne $offset -or $page.GetProperty('limit').GetInt32() -ne $limit -or $page.GetProperty('total').GetInt32() -ne $FixedSensors -or $page.GetProperty('settingsRevision').GetString() -cne $revision -or $page.GetProperty('policyRevision').GetString() -cne $contract.GetProperty('policyRevision').GetString() -or -not$page.GetProperty('prtgEnabled').GetBoolean()){throw 'Public profile catalog is partial or its settings/policy fence changed.'}
        $pageContext=@{sourceGeneration=$page.GetProperty('sourceGeneration').GetString();authority=$page.GetProperty('authorityContextFingerprint').GetString()}
        if($null -eq $contextFence){$contextFence=$pageContext}elseif($contextFence.sourceGeneration -cne $pageContext.sourceGeneration -or $contextFence.authority -cne $pageContext.authority){throw 'Source generation or authority context changed between profile pages.'}
        $rows=$page.GetProperty('rows');if($rows.GetArrayLength() -ne $limit){throw 'Public profile catalog page is incomplete.'}
        foreach($row in $rows.EnumerateArray()){$id=$row.GetProperty('sensorObjid').GetInt64();$ids.Add($id);$profileRows.Add($row.Clone());$fences.Add(@{sensorObjid=$id;identityEpoch=$row.GetProperty('currentIdentityEpoch').GetInt64();channelGeneration=$row.GetProperty('currentChannelGeneration').GetString();bindingRevision=$(if($row.GetProperty('bindingRevision').ValueKind -eq 'Null'){0}else{$row.GetProperty('bindingRevision').GetInt64()});bindingFingerprint=$(if($row.GetProperty('bindingFingerprint').ValueKind -eq 'Null'){''}else{$row.GetProperty('bindingFingerprint').GetString()})})}
    }
    if($ids.Count -ne $FixedSensors -or @($ids|Sort-Object -Unique).Count -ne $FixedSensors){throw 'Public profile catalog has duplicate or missing sensor identities.'}
    if($Action -in @('Resume','Check')){Assert-E2ResumeBindingsComplete $profileRows.ToArray() @($bindingPlan.rows)}
    $sourceGeneration=$contextFence.sourceGeneration;$authority=$contextFence.authority
    $currentReceiptFence=[ordered]@{settingsRevision=$revision;policyRevision=$policyRevision;scopeFingerprint=$contract.GetProperty('scopeFingerprint').GetString();sourceGeneration=$sourceGeneration;authorityContextFingerprint=$authority}
    Set-E2ReceiptRevisionFence $receipt $currentReceiptFence -AllowPolicyRevisionProgress:$allowMessageDateFormatDrift
    $planRows=@($bindingPlan.rows)
    for($i=0;$i -lt $FixedSensors;$i++){
        $plan=$planRows[$i];$profile=$profileRows[$i]
        if([long]$plan.sensorObjid -ne $ids[$i] -or [string]::IsNullOrWhiteSpace([string]$plan.channelObjectId) -or [string]::IsNullOrWhiteSpace([string]$plan.expectedCaption) -or
           [string]::IsNullOrWhiteSpace([string]$plan.quantity) -or [string]::IsNullOrWhiteSpace([string]$plan.unit) -or [double]$plan.scale -le 0 -or
           [string]::IsNullOrWhiteSpace([string]$plan.direction) -or [string]::IsNullOrWhiteSpace([string]$plan.intervalRawUnit) -or
           [string]::IsNullOrWhiteSpace([string]$plan.rawTimestampTimeZoneId) -or [string]::IsNullOrWhiteSpace([string]$plan.analysisTimeZoneId) -or
           [string]::IsNullOrWhiteSpace([string]$plan.timeBasisEvidenceReference)){throw "Binding plan row $i is incomplete, unordered, or outside the actual public sensor catalog."}
    }
    $receipt.stages+=@('operator-reviewed-binding-plan-validated','live-revision-fences-sealed');Write-Receipt $receipt
    for($start=0;$start -lt $FixedSensors;$start+=100){
        $batch=[Collections.Generic.List[object]]::new();$batchPlan=[Collections.Generic.List[object]]::new()
        for($i=$start;$i -lt [Math]::Min($start+100,$FixedSensors);$i++){
            $plan=$planRows[$i];$profile=$profileRows[$i];$boundRevision=$profile.GetProperty('bindingRevision')
            if($boundRevision.ValueKind -ne 'Null'){
                if(-not(Test-E2ProfileBindingMatches $profile $plan)){throw "Existing binding for sensor $($ids[$i]) differs from the explicit plan; refusing silent overwrite. Partial receipt remains available."}
                continue
            }
            if($Action -in @('Resume','Check')){throw "Resume/Check cannot create a missing binding for sensor $($ids[$i]); preserved receipt requires exact current bindings."}
            $epoch=$profile.GetProperty('currentIdentityEpoch');$generation=$profile.GetProperty('currentChannelGeneration')
            if($epoch.ValueKind -ne 'Number' -or $epoch.GetInt64() -le 0 -or $generation.ValueKind -ne 'String' -or [string]::IsNullOrWhiteSpace($generation.GetString())){throw "Sensor $($ids[$i]) lacks a current identity fence; refusing to create binding."}
            $batch.Add(@{sensorObjid=$ids[$i];expectedSettingsRevision=$revision;expectedPolicyRevision=$policyRevision;expectedIdentityEpoch=$epoch.GetInt64();expectedChannelGeneration=$generation.GetString();expectedBindingRevision=0;channelObjectId=[string]$plan.channelObjectId;expectedCaption=[string]$plan.expectedCaption;quantity=[string]$plan.quantity;unit=[string]$plan.unit;scale=[double]$plan.scale;direction=[string]$plan.direction;intervalRawUnit=[string]$plan.intervalRawUnit;rawTimestampTimeZoneId=[string]$plan.rawTimestampTimeZoneId;analysisTimeZoneId=[string]$plan.analysisTimeZoneId;timeBasisEvidenceReference=[string]$plan.timeBasisEvidenceReference})
            $batchPlan.Add($ids[$i])
        }
        if($batch.Count -eq 0){continue}
        Assert-E2ActionMayWrite $Action 'Binding'
        $answer=Invoke-Api POST 'api/prtg/monitoring/trusted-sampling/bindings/batch' @{rows=$batch.ToArray()} 512KB
        if($answer.ValueKind -eq 'Object' -and @($answer.EnumerateObject()|Where-Object{$_.Name -ceq 'committed'}).Count -gt 0){
            $committed=@($answer.GetProperty('committedSensorObjids').EnumerateArray()|ForEach-Object{$_.GetInt64()});$receipt.bindingCommitted=@($receipt.bindingCommitted)+$committed;Write-Receipt $receipt
            throw "Binding API returned a partial commit for $($committed.Count) rows; receipt saved. Re-run only after re-reading exact current revisions."
        }
        if($answer.ValueKind -ne 'Array' -or $answer.GetArrayLength() -ne $batch.Count){$receipt.stages+=@("binding-batch-$([int]($start/100)+1)-outcome-unknown");Write-Receipt $receipt;throw 'Binding API response was not an exact per-row result array; receipt records the batch as outcome-unknown.'}
        $outcomeRows=[Collections.Generic.List[object]]::new();$j=0
        foreach($row in $answer.EnumerateArray()){
            $outcomeRows.Add(@{sensorObjid=$row.GetProperty('sensorObjid').GetInt64();status=$row.GetProperty('status').GetString()})
            $j++
        }
        $outcome=Set-E2BindingBatchReceipt $receipt @($batch|ForEach-Object{[long]$_.sensorObjid}) $outcomeRows.ToArray()
        if($outcome.OutcomeUnknown){$receipt.stages+=@("binding-batch-$([int]($start/100)+1)-outcome-unknown");Write-Receipt $receipt;throw 'Binding API response row order/identity is unknown; receipt preserves the unresolved batch.'}
        if($outcome.Rejected.Count -gt 0){$receipt.stages+=@("binding-batch-$([int]($start/100)+1)-partial");Write-Receipt $receipt;throw "Binding batch committed $($outcome.Committed.Count) rows and rejected $($outcome.Rejected.Count); exact row outcomes are recorded in the partial receipt."}
        $receipt.stages+=@("binding-batch-$([int]($start/100)+1)-committed");Write-Receipt $receipt
    }
    # Re-read after all binding commits. Never seal a manifest from the pre-write revisions.
    $profileRows=[Collections.Generic.List[object]]::new();$fences=[Collections.Generic.List[object]]::new()
    for($offset=0;$offset -lt $FixedSensors;$offset+=100){
        $limit=[Math]::Min(100,$FixedSensors-$offset);$page=Invoke-Api GET "api/prtg/monitoring/trusted-sampling/profiles?offset=$offset&limit=$limit" $null 512KB
        if($page.GetProperty('settingsRevision').GetString() -cne $revision -or $page.GetProperty('policyRevision').GetString() -cne $contract.GetProperty('policyRevision').GetString() -or $page.GetProperty('sourceGeneration').GetString() -cne $sourceGeneration -or $page.GetProperty('authorityContextFingerprint').GetString() -cne $authority -or $page.GetProperty('rows').GetArrayLength() -ne $limit){throw 'Settings/source/profile catalog changed during binding writes; manifest was not sealed.'}
        foreach($row in $page.GetProperty('rows').EnumerateArray()){
            $i=$profileRows.Count;$plan=$planRows[$i]
            if($row.GetProperty('sensorObjid').GetInt64() -ne $ids[$i] -or -not(Test-E2ProfileBindingMatches $row $plan)){throw "Post-write binding catalog does not match explicit plan at sensor $($ids[$i]); preserve partial evidence and re-audit."}
            $profileRows.Add($row.Clone());$fences.Add(@{sensorObjid=$ids[$i];identityEpoch=$row.GetProperty('currentIdentityEpoch').GetInt64();channelGeneration=$row.GetProperty('currentChannelGeneration').GetString();bindingRevision=$row.GetProperty('bindingRevision').GetInt64();bindingFingerprint=$row.GetProperty('bindingFingerprint').GetString()})
        }
    }
    $manifestFull=[IO.Path]::GetFullPath($ManifestPath)
    if($allowMessageDateFormatDrift){
        $manifest=Get-Content -LiteralPath $manifestFull -Raw|ConvertFrom-Json -Depth 16 -AsHashtable
        Assert-E2SealedResumeManifest $manifest $receipt $ids.ToArray() $currentReceiptFence
    } else {
        $manifest=[ordered]@{schema='qualification-job-manifest-v1';sensorIds=$ids.ToArray();sensorFences=$fences.ToArray();settingsRevision=$revision;policyRevision=$policyRevision;scopeFingerprint=$contract.GetProperty('scopeFingerprint').GetString();sourceGeneration=$sourceGeneration;authorityContextFingerprint=$authority}
        $manifestJson=$manifest|ConvertTo-Json -Depth 12;$manifestBytes=[Text.UTF8Encoding]::new($false).GetBytes($manifestJson);$prospectiveManifestHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($manifestBytes))
        if(-not[string]::IsNullOrWhiteSpace([string]$receipt.manifestSha256) -and (ConvertTo-E2Sha256 $receipt.manifestSha256 'receipt manifest SHA-256') -cne (ConvertTo-E2Sha256 $prospectiveManifestHash)){throw 'Live settings/source/binding context differs from the sealed receipt manifest; preserve partial job and refuse stale revision.'}
        if(Test-Path -LiteralPath $manifestFull -PathType Leaf){if((ConvertTo-E2Sha256 (Get-FileHash -LiteralPath $manifestFull -Algorithm SHA256).Hash) -cne (ConvertTo-E2Sha256 $prospectiveManifestHash)){throw 'Existing manifest path contains a different snapshot; preserve it and choose a new owned run path.'}}
        else{$null=New-Item -ItemType Directory -Path (Split-Path -Parent $manifestFull) -Force;$tmp=$manifestFull+'.tmp-'+[guid]::NewGuid().ToString('N');$fs=[IO.FileStream]::new($tmp,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None);try{$fs.Write($manifestBytes,0,$manifestBytes.Length);$fs.Flush($true)}finally{$fs.Dispose()};[IO.File]::Move($tmp,$manifestFull,$true)}
        $receipt.settingsRevision=$revision;$receipt.policyRevision=$manifest.policyRevision;$receipt.scopeFingerprint=$manifest.scopeFingerprint;$receipt.sourceGeneration=$sourceGeneration;$receipt.authorityContextFingerprint=$authority;$receipt.manifestSha256=(Get-FileHash -LiteralPath $manifestFull -Algorithm SHA256).Hash;$receipt.stages+=@('source-receipt-catalog','public-settings','exact-profile-catalog','binding-fence-snapshot');Write-Receipt $receipt
    }
    $pilotCurrent=$contract.GetProperty('capacityPilotCurrent').GetBoolean()
    Assert-E2PilotMayProceed $Action $pilotCurrent
    if(-not$pilotCurrent){
        $pilotIds=@($ids|Select-Object -First ([Math]::Min(5,$ids.Count)))
        Assert-E2ActionMayWrite $Action 'QualificationPilot'
        $pilot=Invoke-Api POST 'api/prtg/monitoring/trusted-sampling/qualification-jobs/pilot' @{sensorObjids=$pilotIds} 256KB
        $receipt.pilotStatus=$pilot.GetProperty('status').GetString();$receipt.stages+=@('capacity-pilot');Write-Receipt $receipt
        Assert-E2PilotQualified ([string]$receipt.pilotStatus)
    } else {$receipt.pilotStatus='already-current'}
    if($Action -eq 'Pilot'){$receipt.status='pilot-ready';Write-Receipt $receipt;[pscustomobject]$receipt;return}
    $current=Invoke-Api GET 'api/prtg/monitoring/trusted-sampling/qualification-jobs/current' $null 128KB
    if($Action -eq 'Start'){
        Assert-E2ActionMayWrite $Action 'QualificationStart'
        if($current.ValueKind -ne 'Null' -and $current.GetProperty('status').GetString() -in @('initializing','running','waiting-capacity')){throw 'An existing durable job is active; preserve its receipt and resume only with its exact ID/version/wave.'}
        $receipt.jobStartOutcomeUnknown=$true;$receipt.stages+=@('qualification-start-submitting');$receipt.status='qualification-start-outcome-unknown';Write-Receipt $receipt
        $start=Invoke-Api POST 'api/prtg/monitoring/trusted-sampling/qualification-jobs/start' (New-E2QualificationJobRequest $revision $manifest.policyRevision $manifest.scopeFingerprint) 256KB
        $jobElement=$start.GetProperty('job')
        if($jobElement.ValueKind -eq 'Null'){$receipt.jobStartOutcomeUnknown=$false;$receipt.jobStatus=$start.GetProperty('status').GetString();$receipt.jobStartReason=$start.GetProperty('reason').GetString();$receipt.status='qualification-waiting-capacity';$receipt.stages+=@('qualification-start-not-admitted');Write-Receipt $receipt;[pscustomobject]$receipt;return}
        $job=$jobElement
    } elseif($Action -eq 'Resume'){
        Assert-E2ActionMayWrite $Action 'QualificationResume'
        if($ExpectedVersion -le 0 -or $ExpectedWave -le 0){throw 'Resume requires the exact current job ID, version, and wave.'};Assert-E2JobIdentity $current $JobId $ExpectedVersion $ExpectedWave
        foreach($tuple in @(@('settingsRevision',$revision),@('policyRevision',$manifest.policyRevision),@('scopeFingerprint',$manifest.scopeFingerprint),@('sourceGeneration',$sourceGeneration),@('authorityContextFingerprint',$authority))){if($current.GetProperty($tuple[0]).GetString() -cne $tuple[1]){throw 'Current durable job has stale source/authority context.'}}
        Assert-E2CurrentJobPages $JobId $current $ExpectedVersion $ExpectedWave $profileRows.ToArray() $manifest @($bindingPlan.rows)
        $answer=Invoke-Api POST "api/prtg/monitoring/trusted-sampling/qualification-jobs/$JobId/resume" (New-E2QualificationResumeRequest $ExpectedVersion) 256KB;$job=$answer.GetProperty('job')
    } else {
        Assert-E2JobIdentity $current $JobId $ExpectedVersion $ExpectedWave;$job=$current
        Assert-E2CurrentJobPages $JobId $current $ExpectedVersion $ExpectedWave $profileRows.ToArray() $manifest @($bindingPlan.rows)
    }
    if($job.GetProperty('scopeFingerprint').GetString() -cne $manifest.scopeFingerprint -or $job.GetProperty('settingsRevision').GetString() -cne $revision -or $job.GetProperty('policyRevision').GetString() -cne $manifest.policyRevision -or $job.GetProperty('sourceGeneration').GetString() -cne $sourceGeneration -or $job.GetProperty('authorityContextFingerprint').GetString() -cne $authority){throw 'Qualification job response is not bound to current exact source/settings authority.'}
    if($explicitUnknownStartReconcile){$receipt.jobStartOutcomeUnknown=$false;$receipt.stages+=@('unknown-start-explicitly-reconciled')}
    $receipt.jobId=$job.GetProperty('jobId').GetString();$receipt.jobVersion=$job.GetProperty('version').GetInt64();$receipt.jobWave=$job.GetProperty('wave').GetInt32();$receipt.jobStatus=$job.GetProperty('status').GetString();$receipt.stages+=@("qualification-$Action");$receipt.status='qualification-pending-or-current';Write-Receipt $receipt
    [pscustomobject]$receipt
} catch { $receipt.status='partial-failed';$receipt.safeFailure=$_.Exception.GetType().Name;$receipt.stages+=@('failure-receipt-preserved');Write-Receipt $receipt;throw }
finally {$client.Dispose();$whole.Dispose()}
