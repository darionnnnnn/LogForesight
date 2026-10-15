$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'E2Contract.psm1') -Force

if(-not(Test-E2SafeTimeBasisReference 'x') -or -not(Test-E2SafeTimeBasisReference ('x'*128)) -or
   (Test-E2SafeTimeBasisReference ('x'*129)) -or (Test-E2SafeTimeBasisReference 'x://y') -or
   (Test-E2SafeTimeBasisReference 'x/y') -or (Test-E2SafeTimeBasisReference 'x\y') -or
   (Test-E2SafeTimeBasisReference 'x?y') -or (Test-E2SafeTimeBasisReference 'x#y') -or
   (Test-E2SafeTimeBasisReference 'x@y') -or (Test-E2SafeTimeBasisReference ([string]('x'+[char]0x85+'y'))) -or
   (Test-E2SafeTimeBasisReference '   ')){throw 'Time-basis evidence reference does not match the public API boundary contract.'}

$listenerPid=Get-E2ListenerProcessId ([pscustomobject]@{OwningProcess=4242})
if($listenerPid -ne 4242){throw 'listener PID extraction failed'}
try { Get-E2ListenerProcessId ([pscustomobject]@{OwningProcess=0}); throw 'invalid listener PID accepted' } catch { if($_.Exception.Message -notmatch 'Listener process identity is invalid'){throw} }
if((ConvertTo-E2Sha256 ('a'*64) 'test digest') -cne ('A'*64) -or (ConvertTo-E2Sha256 ('A'*64) 'test digest') -cne ('a'*64).ToUpperInvariant()){throw 'SHA-256 comparisons are not invariant to hex casing'}
try { ConvertTo-E2Sha256 ('g'*64); throw 'invalid SHA-256 digest accepted' } catch { if($_.Exception.Message -notmatch '64-character SHA-256'){throw} }

$receipt=[ordered]@{settingsRevision='s1';policyRevision='p1';scopeFingerprint='scope1';sourceGeneration='g1';authorityContextFingerprint='a1'}
$same=[ordered]@{settingsRevision='s1';policyRevision='p1';scopeFingerprint='scope1';sourceGeneration='g1';authorityContextFingerprint='a1'}
Set-E2ReceiptRevisionFence $receipt $same
$stale=[ordered]@{settingsRevision='s2';policyRevision='p1';scopeFingerprint='scope1';sourceGeneration='g1';authorityContextFingerprint='a1'}
try { Set-E2ReceiptRevisionFence $receipt $stale; throw 'stale settings revision was accepted' } catch { if($_.Exception.Message -notmatch 'stale settingsRevision'){throw} }
if((Get-E2SettingsWriteDecision 'r1' 'r1' $false) -ne 'Save' -or (Get-E2SettingsWriteDecision 'r2' 'r1' $true) -ne 'Current'){throw 'public settings CAS/reconciliation decision is incorrect'}
try { Get-E2SettingsWriteDecision 'r2' 'r1' $false; throw 'stale settings write was accepted' } catch { if($_.Exception.Message -notmatch 'differs from the operator settings plan'){throw} }
if(-not(Test-E2BootstrapRequired 'Start' $false $false) -or -not(Test-E2BootstrapRequired 'Pilot' $false $false) -or
   (Test-E2BootstrapRequired 'Resume' $false $false) -or (Test-E2BootstrapRequired 'Check' $false $false) -or
   (Test-E2BootstrapRequired 'Start' $true $false) -or (Test-E2BootstrapRequired 'Pilot' $false $true)){throw 'disabled bootstrap action/revision fence is incorrect'}
$resumeReadOnly=@('Settings','SourcePolicy','Binding','AdmissionPilot','QualificationPilot','QualificationStart')
foreach($action in @('Resume','Check')){foreach($operation in $resumeReadOnly){try{Assert-E2ActionMayWrite $action $operation;throw "$action unexpectedly allowed $operation"}catch{if($_.Exception.Message -notmatch "Action $action is read-only|Only Start may create"){throw}}}}
Assert-E2ActionMayWrite 'Start' 'Settings';Assert-E2ActionMayWrite 'Pilot' 'AdmissionPilot';Assert-E2ActionMayWrite 'Resume' 'QualificationResume'
foreach($pair in @(@('Check','QualificationResume'),@('Pilot','QualificationStart'))){try{Assert-E2ActionMayWrite $pair[0] $pair[1];throw "unexpected $($pair[0]) $($pair[1]) permission"}catch{if($_.Exception.Message -notmatch 'Only Resume may resume|Only Start may create|cannot perform'){throw}}}
$desiredSettings=[ordered]@{prtgEnabled=$true;prtgUrl='http://127.0.0.1:5101/'}
$enabledReadback=[System.Text.Json.JsonDocument]::Parse('{"revision":"r9","prtgEnabled":true,"prtgUrl":"http://127.0.0.1:5101/"}')
$disabledReadback=[System.Text.Json.JsonDocument]::Parse('{"revision":"r8","prtgEnabled":false,"prtgUrl":"http://127.0.0.1:5101/"}')
try {
    Assert-E2BootstrapReadback $enabledReadback.RootElement $desiredSettings $true
    $disabledFields=[ordered]@{prtgEnabled=$false;prtgUrl='http://127.0.0.1:5101/'}
    Assert-E2BootstrapReadback $disabledReadback.RootElement $disabledFields $false 'r8'
    try{Assert-E2BootstrapReadback $enabledReadback.RootElement $disabledFields $false 'r8';throw 'changed enable outcome was accepted by disabled readback'}catch{if($_.Exception.Message -notmatch 'exact saved settings readback'){throw}}
    try{Assert-E2BootstrapReadback $disabledReadback.RootElement $disabledFields $false 'stale';throw 'stale disabled revision was accepted'}catch{if($_.Exception.Message -notmatch 'exact saved settings readback'){throw}}
} finally {$enabledReadback.Dispose();$disabledReadback.Dispose()}

$bindingPlan=[pscustomobject]@{sensorObjid=11L;channelObjectId='54321';expectedCaption='Disk Used Percent';quantity='DiskUsedPercent';unit='%';scale=1.0;direction='direct';intervalRawUnit='seconds';rawTimestampTimeZoneId='UTC';analysisTimeZoneId='UTC';timeBasisEvidenceReference='review-17'}
$profileJson='{"sensorObjid":11,"bindingRevision":2,"bindingFingerprint":"fp-11","boundChannelObjectId":"54321","boundChannelCaption":"Disk Used Percent","bindingQuantity":"DiskUsedPercent","bindingUnit":"%","bindingScale":1,"bindingDirection":"direct","bindingIntervalRawUnit":"seconds","bindingRawTimestampTimeZoneId":"UTC","bindingAnalysisTimeZoneId":"UTC","timeBasisEvidenceReference":"review-17"}'
$profileDoc=[System.Text.Json.JsonDocument]::Parse($profileJson)
try {
    if(-not(Test-E2ProfileBindingMatches $profileDoc.RootElement $bindingPlan)){throw 'public profile/binding JSON was not accepted'}
    $tamperedPlan=$bindingPlan.PSObject.Copy();$tamperedPlan.expectedCaption='Wrong caption'
    if(Test-E2ProfileBindingMatches $profileDoc.RootElement $tamperedPlan){throw 'tampered public binding caption was accepted'}
} finally {$profileDoc.Dispose()}
$monitoringPlan=[pscustomobject]@{coreSystemId='core-17';sourceTimeZoneId='UTC';sourceCultureName='en-US';messageDateFormat='';rawTimestampTimeZoneId='UTC';analysisTimeZoneId='UTC';timeBasisEvidenceReference='review-17';sourceChangeMode='new';continuityConfirmed=$false;continuityEvidenceReference='';identityConfirmed=$true;hostIds=@(2L,3L);sensorIds=@(11L,12L,13L)}
$monitoringJson='{"revision":"m1","coreSystemId":"core-17","sourceTimeZoneId":"UTC","sourceCultureName":"en-US","messageDateFormat":"","rawTimestampTimeZoneId":"UTC","analysisTimeZoneId":"UTC","timeBasisEvidenceReference":"review-17","hostIds":[2,3],"sensorIds":[11,12,13]}'
$monitoringDoc=[System.Text.Json.JsonDocument]::Parse($monitoringJson)
try {
    if(-not(Test-E2MonitoringPolicyMatches $monitoringDoc.RootElement $monitoringPlan)){throw 'normal public monitoring source JSON did not match explicit plan'}
    $tamperedMonitoring=$monitoringPlan.PSObject.Copy();$tamperedMonitoring.sensorIds=@(11L,12L)
    if(Test-E2MonitoringPolicyMatches $monitoringDoc.RootElement $tamperedMonitoring){throw 'public monitoring policy with a partial sensor scope was accepted'}
    if(-not(Test-E2IdSequenceMatches $monitoringDoc.RootElement.GetProperty('hostIds') @(3L,2L)) -or (Test-E2IdSequenceMatches $monitoringDoc.RootElement.GetProperty('sensorIds') @(11L,12L))){throw 'source preview identity check accepted a missing or changed ID set'}
} finally {$monitoringDoc.Dispose()}
$monitorRequest=New-E2MonitoringRequest $monitoringPlan 'm1' 'catalog-token'
$monitorRequestJson=$monitorRequest|ConvertTo-Json -Depth 8 -Compress
$monitorRequestRoundtrip=$monitorRequestJson|ConvertFrom-Json
if($monitorRequestRoundtrip.revision -cne 'm1' -or $monitorRequestRoundtrip.catalogueToken -cne 'catalog-token' -or $monitorRequestRoundtrip.identityConfirmed -ne $true -or $monitorRequestRoundtrip.hostIds.Count -ne 2 -or $monitorRequestRoundtrip.sensorIds.Count -ne 3){throw 'normal monitoring estimate/source-confirmation request shape changed'}
$profile12Json=$profileJson.Replace('"sensorObjid":11','"sensorObjid":12').Replace('fp-11','fp-12')
$plan12=$bindingPlan.PSObject.Copy();$plan12.sensorObjid=12L
$profile12Doc=[System.Text.Json.JsonDocument]::Parse($profile12Json)
$resumePlans=@($bindingPlan,$plan12)
$completeResumeProfiles=@([System.Text.Json.JsonDocument]::Parse($profileJson),$profile12Doc)
try {Assert-E2ResumeBindingsComplete @($completeResumeProfiles|ForEach-Object{$_.RootElement}) $resumePlans} finally {$completeResumeProfiles[0].Dispose();$profile12Doc.Dispose()}
$missing12Json='{"sensorObjid":12,"bindingRevision":null,"bindingFingerprint":null}'
$profile11Doc=[System.Text.Json.JsonDocument]::Parse($profileJson);$missing12Doc=[System.Text.Json.JsonDocument]::Parse($missing12Json)
try {
    try {Assert-E2ResumeBindingsComplete @($profile11Doc.RootElement,$missing12Doc.RootElement) $resumePlans;throw 'partial binding state was accepted for resume'}
    catch {if($_.Exception.Message -notmatch 'every current binding to match the sealed plan'){throw}}
} finally {$profile11Doc.Dispose();$missing12Doc.Dispose()}

# Proof-backed binding progress may advance; raw/scope identity and the sealed
# manifest remain fixed, and page reads are fenced by the exact job version.
$fp11=('A'*64);$fp12=('B'*64);$fp11New=('C'*64)
$sealed=[ordered]@{schema='qualification-job-manifest-v1';sensorIds=@(11L,12L);policyRevision='p1';settingsRevision='s1';scopeFingerprint='scope1';sourceGeneration='g1';authorityContextFingerprint='a1';sensorFences=@(@{sensorObjid=11L;identityEpoch=5L;channelGeneration='ch11';bindingRevision=2L;bindingFingerprint=$fp11},@{sensorObjid=12L;identityEpoch=7L;channelGeneration='ch12';bindingRevision=4L;bindingFingerprint=$fp12})}
$receiptFence=[ordered]@{policyRevision='p1';settingsRevision='s1';scopeFingerprint='scope1';sourceGeneration='g1';authorityContextFingerprint='a1'}
$liveFence=[ordered]@{settingsRevision='s1';policyRevision='p2';scopeFingerprint='scope1';sourceGeneration='g1';authorityContextFingerprint='a1'}
Assert-E2SealedResumeManifest $sealed $receiptFence @(11L,12L) $liveFence
$qualifiedProfileJson=([ordered]@{sensorObjid=11L;currentIdentityEpoch=5L;currentChannelGeneration='ch11-qualified';bindingRevision=3L;bindingFingerprint=$fp11New;qualificationProofReference='proof11';boundChannelObjectId='54321';boundChannelCaption='Disk Used Percent';bindingQuantity='DiskUsedPercent';bindingUnit='%';bindingScale=1.0;bindingDirection='direct';bindingIntervalRawUnit='seconds';bindingRawTimestampTimeZoneId='UTC';bindingAnalysisTimeZoneId='UTC';timeBasisEvidenceReference='review-17'}|ConvertTo-Json -Compress)
$stableProfileJson=([ordered]@{sensorObjid=12L;currentIdentityEpoch=7L;currentChannelGeneration='ch12';bindingRevision=4L;bindingFingerprint=$fp12;qualificationProofReference=$null;boundChannelObjectId='54321';boundChannelCaption='Disk Used Percent';bindingQuantity='DiskUsedPercent';bindingUnit='%';bindingScale=1.0;bindingDirection='direct';bindingIntervalRawUnit='seconds';bindingRawTimestampTimeZoneId='UTC';bindingAnalysisTimeZoneId='UTC';timeBasisEvidenceReference='review-17'}|ConvertTo-Json -Compress)
$profile11New=[System.Text.Json.JsonDocument]::Parse($qualifiedProfileJson);$profile12Stable=[System.Text.Json.JsonDocument]::Parse($stableProfileJson)
$page11Json=([ordered]@{sensorObjid=11L;bindingRevision=3L;bindingFingerprint=$fp11New;hasQualificationProof=$true;status='qualified';qualifiedIdentityEpoch=5L;qualifiedChannelGeneration='ch11-qualified'}|ConvertTo-Json -Compress)
$page11=[System.Text.Json.JsonDocument]::Parse($page11Json)
$page12=[System.Text.Json.JsonDocument]::Parse((([ordered]@{sensorObjid=12L;bindingRevision=4L;bindingFingerprint=$fp12;hasQualificationProof=$false;status='pending'}|ConvertTo-Json -Compress)))
try {
    $twoProfiles=@($profile11New.RootElement,$profile12Stable.RootElement);$twoRows=@($page11.RootElement,$page12.RootElement)
    Assert-E2ResumeProfileProgress $twoProfiles $sealed @($bindingPlan,$plan12) $twoRows
    $arbitraryProfile=[System.Text.Json.JsonDocument]::Parse($stableProfileJson.Replace($fp12,('D'*64)))
    try {try {Assert-E2ResumeProfileProgress @($profile11New.RootElement,$arbitraryProfile.RootElement) $sealed @($bindingPlan,$plan12) $twoRows;throw 'arbitrary binding replacement was accepted'} catch {if($_.Exception.Message -notmatch 'not proven|Binding changed'){throw}}} finally {$arbitraryProfile.Dispose()}
    $channelDrift=[System.Text.Json.JsonDocument]::Parse($qualifiedProfileJson.Replace('ch11-qualified','ch11-arbitrary'))
    try {try {Assert-E2ResumeProfileProgress @($channelDrift.RootElement,$profile12Stable.RootElement) $sealed @($bindingPlan,$plan12) $twoRows;throw 'unproven channel rotation after qualification was accepted'} catch {if($_.Exception.Message -notmatch 'not proven|Current profile identity'){throw}}} finally {$channelDrift.Dispose()}
    $oldQualifiedPage=[System.Text.Json.JsonDocument]::Parse((([ordered]@{sensorObjid=11L;bindingRevision=3L;bindingFingerprint=$fp11New;hasQualificationProof=$true;status='qualified'}|ConvertTo-Json -Compress)))
    try {try {Assert-E2ResumeProfileProgress $twoProfiles $sealed @($bindingPlan,$plan12) @($oldQualifiedPage.RootElement,$page12.RootElement);throw 'legacy qualified page without post-qualification channel fence was accepted'} catch {if($_.Exception.Message -notmatch 'not proven'){throw}}} finally {$oldQualifiedPage.Dispose()}
} finally {$profile11New.Dispose();$profile12Stable.Dispose();$page11.Dispose();$page12.Dispose()}
$rawDrift=[ordered]@{settingsRevision='s1';policyRevision='p2';scopeFingerprint='scope1';sourceGeneration='g1';authorityContextFingerprint='a2'}
try {try {Assert-E2SealedResumeManifest $sealed $receiptFence @(11L,12L) $rawDrift;throw 'raw authority drift was accepted'} catch {if($_.Exception.Message -notmatch 'authorityContextFingerprint'){throw}}} catch {throw}
$jobBefore=[System.Text.Json.JsonDocument]::Parse('{"jobId":"job1","version":8,"readStateVersion":41,"wave":2,"selected":2,"pageCount":1,"initializedPages":1,"settingsRevision":"s1","policyRevision":"p1","scopeFingerprint":"scope1","sourceGeneration":"g1","authorityContextFingerprint":"a1"}')
$jobSame=[System.Text.Json.JsonDocument]::Parse($jobBefore.RootElement.GetRawText());$jobChanged=[System.Text.Json.JsonDocument]::Parse($jobBefore.RootElement.GetRawText().Replace('"version":8','"version":9'))
$jobPageRace=[System.Text.Json.JsonDocument]::Parse($jobBefore.RootElement.GetRawText().Replace('"readStateVersion":41','"readStateVersion":42'))
$jobLegacy=[System.Text.Json.JsonDocument]::Parse($jobBefore.RootElement.GetRawText().Replace('"readStateVersion":41,',''))
try {
    Assert-E2StableJobSnapshot $jobBefore.RootElement $jobSame.RootElement 'job1' 8 2
    try {Assert-E2StableJobSnapshot $jobBefore.RootElement $jobChanged.RootElement 'job1' 8 2;throw 'job lease-version drift was accepted'} catch {if($_.Exception.Message -notmatch 'exact|changed while|read-state'){throw}}
    try {Assert-E2StableJobSnapshot $jobBefore.RootElement $jobPageRace.RootElement 'job1' 8 2;throw 'page mutation with a stable lease version was accepted'} catch {if($_.Exception.Message -notmatch 'read-state'){throw}}
    try {Assert-E2StableJobSnapshot $jobLegacy.RootElement $jobLegacy.RootElement 'job1' 8 2;throw 'job snapshot without read-state version was accepted'} catch {if($_.Exception.Message -notmatch 'read-state'){throw}}
} finally {$jobBefore.Dispose();$jobSame.Dispose();$jobChanged.Dispose();$jobPageRace.Dispose();$jobLegacy.Dispose()}
$dateDrift=[System.Text.Json.JsonDocument]::Parse($monitoringJson.Replace('"messageDateFormat":""','"messageDateFormat":"yyyy-MM-dd"'))
try {if(Test-E2MonitoringPolicyMatches $dateDrift.RootElement $monitoringPlan){throw 'strict policy comparison ignored DateFormat drift'};if(-not(Test-E2MonitoringPolicyMatches $dateDrift.RootElement $monitoringPlan $true)){throw 'Resume/Check rejected DateFormat-only drift'}} finally {$dateDrift.Dispose()}
$policyReceipt=[ordered]@{settingsRevision='s1';policyRevision='p1';scopeFingerprint='scope1';sourceGeneration='g1';authorityContextFingerprint='a1'}
Set-E2ReceiptRevisionFence $policyReceipt ([ordered]@{settingsRevision='s1';policyRevision='p2';scopeFingerprint='scope1';sourceGeneration='g1';authorityContextFingerprint='a1'}) -AllowPolicyRevisionProgress
if($policyReceipt.policyRevision -cne 'p1'){throw 'Resume rewrote the original sealed policy revision'}
try {Set-E2ReceiptRevisionFence $policyReceipt ([ordered]@{settingsRevision='s1';policyRevision='p3';scopeFingerprint='scope1';sourceGeneration='g1';authorityContextFingerprint='a2'}) -AllowPolicyRevisionProgress;throw 'raw drift passed the receipt fence'} catch {if($_.Exception.Message -notmatch 'authorityContextFingerprint'){throw}}

Add-Type -TypeDefinition @'
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
public sealed class E2ContractHttpHandler : HttpMessageHandler {
    readonly string fallbackBody;
    public readonly System.Collections.Generic.Dictionary<string,string> Routes = new System.Collections.Generic.Dictionary<string,string>();
    public string LastPath { get; private set; } = "";
    public string LastMethod { get; private set; } = "";
    public string LastBody { get; private set; } = "";
    public E2ContractHttpHandler(string value) { fallbackBody = value; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) {
        LastPath = request.RequestUri!.PathAndQuery;
        LastMethod = request.Method.Method;
        LastBody = request.Content == null ? "" : await request.Content.ReadAsStringAsync(token);
        string body = Routes.TryGetValue(LastMethod + " " + LastPath, out var value) ? value : fallbackBody;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
'@
$profilePage='{"success":true,"data":{"settingsRevision":"s1","policyRevision":"p1","sourceGeneration":"g1","authorityContextFingerprint":"a1","prtgEnabled":true,"total":1,"offset":0,"limit":1,"rows":['+$profileJson+']}}'
$handler=[E2ContractHttpHandler]::new($profilePage);$http=[Net.Http.HttpClient]::new($handler)
$request=[Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get,'http://127.0.0.1:5100/api/prtg/monitoring/trusted-sampling/profiles?offset=0&limit=1')
try {
    $page=Invoke-E2JsonApi $http $request ([Threading.CancellationToken]::None) 16384
    if($handler.LastPath -cne '/api/prtg/monitoring/trusted-sampling/profiles?offset=0&limit=1' -or $page.GetProperty('settingsRevision').GetString() -cne 's1' -or $page.GetProperty('rows').GetArrayLength() -ne 1){throw 'public profile route/ApiResponse pipeline shape changed'}
    if(-not(Test-E2ProfileBindingMatches $page.GetProperty('rows')[0] $bindingPlan)){throw 'HTTP profile row did not satisfy explicit reviewed binding plan'}
} finally {$request.Dispose();$http.Dispose()}
$pipelineHandler=[E2ContractHttpHandler]::new('{}')
$settingsBefore='{"success":true,"data":{"revision":"r7","prtgEnabled":false,"prtgUrl":"http://127.0.0.1:5101/"}}'
$settingsAfter='{"success":true,"data":{"revision":"r8","prtgEnabled":true,"prtgUrl":"http://127.0.0.1:5101/"}}'
$pipelineHandler.Routes['GET /api/admin/settings']=$settingsBefore
$pipelineHandler.Routes['PUT /api/admin/settings/prtg']=$settingsAfter
$pipelineHandler.Routes['GET /api/prtg/monitoring']='{"success":true,"data":'+$monitoringJson.Substring(0,$monitoringJson.Length-1)+',"catalogueToken":"catalog-token"}}'
$pipelineHandler.Routes['POST /api/prtg/monitoring/estimate']='{"success":true,"data":{"estimateToken":"estimate-token","hostCount":2,"selectedSensorCount":3}}'
$pipelineHandler.Routes['POST /api/prtg/monitoring/source-preview']='{"success":true,"data":{"proposedHostIds":[2,3],"proposedSensorIds":[11,12,13]}}'
$pipelineHandler.Routes['PUT /api/prtg/monitoring']='{"success":true,"data":"m2"}'
$pipelineHandler.Routes['GET /api/prtg/monitoring/trusted-sampling/profiles?offset=0&limit=1']=$profilePage
$pipelineHttp=[Net.Http.HttpClient]::new($pipelineHandler)
try {
    $get=[Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get,'http://127.0.0.1:5100/api/admin/settings')
    try {$before=Invoke-E2JsonApi $pipelineHttp $get ([Threading.CancellationToken]::None) 16384} finally {$get.Dispose()}
    if($before.GetProperty('revision').GetString() -cne 'r7' -or $before.GetProperty('prtgEnabled').GetBoolean()){throw 'public settings GET JSON contract changed'}
    $putBody=@{expectedRevision='r7';prtgEnabled=$true;prtgUrl='http://127.0.0.1:5101/'}
    $put=[Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Put,'http://127.0.0.1:5100/api/admin/settings/prtg')
    $put.Content=[Net.Http.StringContent]::new(($putBody|ConvertTo-Json -Compress),[Text.Encoding]::UTF8,'application/json')
    try {$after=Invoke-E2JsonApi $pipelineHttp $put ([Threading.CancellationToken]::None) 16384} finally {$put.Dispose()}
    $posted=$pipelineHandler.LastBody|ConvertFrom-Json
    if($pipelineHandler.LastMethod -cne 'PUT' -or $posted.expectedRevision -cne 'r7' -or $posted.prtgEnabled -ne $true -or $after.GetProperty('revision').GetString() -cne 'r8'){throw 'public settings compare-and-save route/request contract changed'}
    $sourceGet=[Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get,'http://127.0.0.1:5100/api/prtg/monitoring')
    try {$sourceCurrent=Invoke-E2JsonApi $pipelineHttp $sourceGet ([Threading.CancellationToken]::None) 16384} finally {$sourceGet.Dispose()}
    $sourceReq=New-E2MonitoringRequest $monitoringPlan $sourceCurrent.GetProperty('revision').GetString() $sourceCurrent.GetProperty('catalogueToken').GetString()
    $estimateReq=[Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post,'http://127.0.0.1:5100/api/prtg/monitoring/estimate')
    $estimateReq.Content=[Net.Http.StringContent]::new(($sourceReq|ConvertTo-Json -Depth 8 -Compress),[Text.Encoding]::UTF8,'application/json')
    try {$estimateResult=Invoke-E2JsonApi $pipelineHttp $estimateReq ([Threading.CancellationToken]::None) 16384} finally {$estimateReq.Dispose()}
    if($estimateResult.GetProperty('estimateToken').GetString() -cne 'estimate-token' -or $pipelineHandler.LastPath -cne '/api/prtg/monitoring/estimate'){throw 'public monitoring estimate route/response contract changed'}
    $sourceReq.estimateToken=$estimateResult.GetProperty('estimateToken').GetString()
    $previewReq=[Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post,'http://127.0.0.1:5100/api/prtg/monitoring/source-preview')
    $previewReq.Content=[Net.Http.StringContent]::new(($sourceReq|ConvertTo-Json -Depth 8 -Compress),[Text.Encoding]::UTF8,'application/json')
    try {$previewResult=Invoke-E2JsonApi $pipelineHttp $previewReq ([Threading.CancellationToken]::None) 16384} finally {$previewReq.Dispose()}
    if($previewResult.GetProperty('proposedSensorIds').GetArrayLength() -ne 3){throw 'public source preview did not return its complete selected scope'}
    $saveReq=[Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Put,'http://127.0.0.1:5100/api/prtg/monitoring')
    $saveReq.Content=[Net.Http.StringContent]::new(($sourceReq|ConvertTo-Json -Depth 8 -Compress),[Text.Encoding]::UTF8,'application/json')
    try {$saveResult=Invoke-E2JsonApi $pipelineHttp $saveReq ([Threading.CancellationToken]::None) 16384} finally {$saveReq.Dispose()}
    if($saveResult.GetString() -cne 'm2' -or $pipelineHandler.LastMethod -cne 'PUT'){throw 'public monitoring compare-and-save route contract changed'}
    $profileRequest=[Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get,'http://127.0.0.1:5100/api/prtg/monitoring/trusted-sampling/profiles?offset=0&limit=1')
    try {$page=Invoke-E2JsonApi $pipelineHttp $profileRequest ([Threading.CancellationToken]::None) 16384} finally {$profileRequest.Dispose()}
    if($page.GetProperty('sourceGeneration').GetString() -cne 'g1' -or $page.GetProperty('authorityContextFingerprint').GetString() -cne 'a1' -or -not(Test-E2ProfileBindingMatches $page.GetProperty('rows')[0] $bindingPlan)){throw 'public sampling profile contract lost its source fence or binding fields'}
} finally {$pipelineHttp.Dispose()}
$failureHandler=[E2ContractHttpHandler]::new('{"success":false,"data":null}')
$failureHttp=[Net.Http.HttpClient]::new($failureHandler);$failureRequest=[Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get,'http://127.0.0.1:5100/api/prtg/monitoring/trusted-sampling/qualification-jobs/contract')
try { Invoke-E2JsonApi $failureHttp $failureRequest ([Threading.CancellationToken]::None) 16384 | Out-Null; throw 'unsuccessful ApiResponse was accepted' } catch { if($_.Exception.Message -notmatch 'unsuccessful ApiResponse'){throw} } finally {$failureRequest.Dispose();$failureHttp.Dispose()}

$mixed=@(@{sensorObjid=11L;status='waiting'},@{sensorObjid=12L;status='qualified'},@{sensorObjid=13L;status='rejected'})
$outcome=Get-E2BindingBatchOutcome @(11L,12L,13L) $mixed
if($outcome.OutcomeUnknown -or ($outcome.Committed -join ',') -ne '11,12' -or ($outcome.Rejected -join ',') -ne '13'){throw 'partial binding outcomes were not recorded exactly'}
$partialReceipt=[ordered]@{bindingCommitted=@();bindingRejected=@();stages=@()}
$partial=Set-E2BindingBatchReceipt $partialReceipt @(11L,12L,13L) $mixed
if($partial.OutcomeUnknown -or ($partialReceipt.bindingCommitted -join ',') -ne '11,12' -or ($partialReceipt.bindingRejected -join ',') -ne '13' -or $partialReceipt.stages[-1] -cne 'binding-batch-partial'){throw 'partial binding response was not durably represented by the production receipt helper'}
$bindingApi=[E2ContractHttpHandler]::new('{}')
$bindingApi.Routes['POST /api/prtg/monitoring/trusted-sampling/bindings/batch']='{"success":true,"data":[{"sensorObjid":11,"status":"waiting"},{"sensorObjid":12,"status":"qualified"},{"sensorObjid":13,"status":"rejected"}]}'
$bindingHttp=[Net.Http.HttpClient]::new($bindingApi);$bindingRequest=[Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post,'http://127.0.0.1:5100/api/prtg/monitoring/trusted-sampling/bindings/batch')
$bindingRequest.Content=[Net.Http.StringContent]::new('{"rows":[{"sensorObjid":11},{"sensorObjid":12},{"sensorObjid":13}]}',[Text.Encoding]::UTF8,'application/json')
try {$bindingResult=Invoke-E2JsonApi $bindingHttp $bindingRequest ([Threading.CancellationToken]::None) 16384} finally {$bindingRequest.Dispose();$bindingHttp.Dispose()}
$httpRows=@($bindingResult.EnumerateArray()|ForEach-Object{@{sensorObjid=$_.GetProperty('sensorObjid').GetInt64();status=$_.GetProperty('status').GetString()}})
$persistedPartial=[ordered]@{bindingCommitted=@();bindingRejected=@();stages=@()}
$null=Set-E2BindingBatchReceipt $persistedPartial @(11L,12L,13L) $httpRows
$partialRoundtrip=($persistedPartial|ConvertTo-Json -Depth 8 -Compress)|ConvertFrom-Json
if(($partialRoundtrip.bindingCommitted -join ',') -ne '11,12' -or ($partialRoundtrip.bindingRejected -join ',') -ne '13'){throw 'mock public binding partial response did not survive durable-receipt JSON roundtrip'}
$reordered=Get-E2BindingBatchOutcome @(11L,12L,13L) @($mixed[1],$mixed[0],$mixed[2])
if(-not$reordered.OutcomeUnknown){throw 'reordered binding response was treated as a known partial commit'}

Assert-E2PilotMayProceed 'Pilot' $false
try { Assert-E2PilotMayProceed 'Resume' $false; throw 'stale pilot allowed resume' } catch { if($_.Exception.Message -notmatch 'pilot to remain current'){throw} }
try { Assert-E2PilotQualified 'waiting-capacity'; throw 'rejected pilot allowed job start' } catch { if($_.Exception.Message -notmatch 'did not qualify'){throw} }

$start=New-E2QualificationJobRequest 's1' 'p1' 'scope1'
$resume=New-E2QualificationResumeRequest 8
if($start.durationHours -ne 720 -or $start.maximumAttempts -ne 1 -or $resume.durationHours -ne 720 -or $resume.maximumAttempts -ne 1){throw 'qualification window or attempts changed'}
if((New-E2QualificationJobRequest 's1' 'p1' 'scope1').maximumAttempts -ne 1){throw 'new job attempts are not fixed to one'}
try { New-E2QualificationResumeRequest 0; throw 'resume accepted a missing expected version' } catch { if($_.Exception.Message -notmatch 'positive exact expected version'){throw} }

$doc=[System.Text.Json.JsonDocument]::Parse('{"jobId":"job-a","version":8,"wave":2}')
try {
    Assert-E2JobIdentity $doc.RootElement 'job-a' 8 2
    try { Assert-E2JobIdentity $doc.RootElement 'job-a' 8 3; throw 'wrong wave accepted' } catch { if($_.Exception.Message -notmatch 'exact supplied ID, version, and wave'){throw} }
} finally {$doc.Dispose()}

'E2 PowerShell contract checks passed.'
