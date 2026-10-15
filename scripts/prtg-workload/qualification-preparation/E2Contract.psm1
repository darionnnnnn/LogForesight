Set-StrictMode -Version Latest

function Test-E2SafeTimeBasisReference([object]$Value) {
    if($Value -isnot [string] -or [string]::IsNullOrWhiteSpace($Value) -or $Value.Length -gt 128){return $false}
    foreach($character in $Value.ToCharArray()){
        if([char]::IsControl($character)){return $false}
    }
    foreach($separator in @('://','/','\','?','#','@')){
        if($Value.IndexOf($separator,[StringComparison]::Ordinal) -ge 0){return $false}
    }
    return $true
}

function ConvertTo-E2Sha256([object]$Value,[string]$Label='SHA-256') {
    $text=[string]$Value
    if($text -notmatch '^[A-Fa-f0-9]{64}$'){throw "$Label is not a 64-character SHA-256 digest."}
    return $text.ToUpperInvariant()
}

function Get-E2ListenerProcessId([object]$Listener) {
    $listenerProcessId=[int]$Listener.OwningProcess
    if($listenerProcessId -le 0){throw 'Listener process identity is invalid.'}
    return $listenerProcessId
}

function Set-E2ReceiptRevisionFence([System.Collections.IDictionary]$Receipt,[System.Collections.IDictionary]$Current,
    [switch]$AllowPolicyRevisionProgress) {
    $keys=@('settingsRevision','policyRevision','scopeFingerprint','sourceGeneration','authorityContextFingerprint')
    if($AllowPolicyRevisionProgress){$keys=@('settingsRevision','scopeFingerprint','sourceGeneration','authorityContextFingerprint')}
    foreach($key in $keys) {
        $saved=[string]$Receipt[$key]
        if(-not[string]::IsNullOrWhiteSpace($saved) -and $saved -cne [string]$Current[$key]) {
            throw "Saved partial receipt has a stale $key; no binding or pilot writes were attempted."
        }
    }
    foreach($key in $Current.Keys){
        if($AllowPolicyRevisionProgress -and $key -ceq 'policyRevision'){continue}
        $Receipt[$key]=[string]$Current[$key]
    }
}

function Get-E2SettingsWriteDecision([string]$CurrentRevision,[string]$ExpectedRevision,[bool]$MatchesRequestedValues) {
    if($MatchesRequestedValues){return 'Current'}
    if($CurrentRevision -ceq $ExpectedRevision){return 'Save'}
    throw 'Saved settings revision differs from the operator settings plan; no settings write was attempted.'
}

function Test-E2BootstrapRequired([string]$Action,[bool]$HasSavedSettingsRevision,[bool]$BootstrapCompleted) {
    return $Action -in @('Start','Pilot') -and -not$HasSavedSettingsRevision -and -not$BootstrapCompleted
}

function Assert-E2ActionMayWrite([string]$Action,[ValidateSet('Settings','SourcePolicy','Binding','AdmissionPilot','QualificationPilot','QualificationStart','QualificationResume')][string]$Operation) {
    if($Operation -in @('Settings','SourcePolicy','Binding','AdmissionPilot','QualificationPilot') -and $Action -in @('Resume','Check')) {
        throw "Action $Action is read-only for $Operation; no write was attempted."
    }
    if($Operation -eq 'QualificationStart' -and $Action -cne 'Start') {throw 'Only Start may create a qualification job.'}
    if($Operation -eq 'QualificationResume' -and $Action -cne 'Resume') {throw 'Only Resume may resume a qualification job.'}
    if($Operation -eq 'QualificationStart' -and $Action -in @('Resume','Check') -or
       $Operation -eq 'QualificationResume' -and $Action -eq 'Check') {throw "Action $Action cannot perform $Operation."}
}

function Assert-E2BootstrapReadback([System.Text.Json.JsonElement]$Current,[System.Collections.IDictionary]$ExpectedFields,
    [bool]$ExpectedEnabled,[string]$ExpectedRevision='') {
    if($Current.ValueKind -ne 'Object' -or $Current.GetProperty('prtgEnabled').GetBoolean() -ne $ExpectedEnabled -or
       -not(Test-E2SettingsFieldsMatch $Current $ExpectedFields) -or
       -not[string]::IsNullOrWhiteSpace($ExpectedRevision) -and $Current.GetProperty('revision').GetString() -cne $ExpectedRevision) {
        throw 'Bootstrap outcome does not match an exact saved settings readback.'
    }
}

function Test-E2SettingsFieldsMatch([System.Text.Json.JsonElement]$Current,[System.Collections.IDictionary]$Fields) {
    foreach($key in $Fields.Keys){
        if($key -eq 'expectedRevision'){continue}
        $actual=$Current.GetProperty([string]$key).GetRawText()|ConvertFrom-Json -Depth 12
        if((ConvertTo-Json -InputObject $actual -Depth 12 -Compress) -cne (ConvertTo-Json -InputObject $Fields[$key] -Depth 12 -Compress)){return $false}
    }
    return $true
}

function Test-E2MonitoringPolicyMatches([System.Text.Json.JsonElement]$Current,[object]$Plan,
    [bool]$AllowMessageDateFormatDrift=$false) {
    if($Current.ValueKind -ne 'Object'){return $false}
    $pairs=@(@('coreSystemId','coreSystemId'),@('sourceTimeZoneId','sourceTimeZoneId'),
        @('sourceCultureName','sourceCultureName'),
        @('rawTimestampTimeZoneId','rawTimestampTimeZoneId'),@('analysisTimeZoneId','analysisTimeZoneId'),
        @('timeBasisEvidenceReference','timeBasisEvidenceReference'))
    if(-not$AllowMessageDateFormatDrift){$pairs+=,@('messageDateFormat','messageDateFormat')}
    foreach($pair in $pairs){
        $property=$Current.GetProperty($pair[0])
        if($property.ValueKind -ne 'String' -or $property.GetString() -cne [string]$Plan.($pair[1])){return $false}
    }
    foreach($pair in @(@('hostIds','hostIds'),@('sensorIds','sensorIds'))){
        $actual=@($Current.GetProperty($pair[0]).EnumerateArray()|ForEach-Object{$_.GetInt64()}|Sort-Object)
        $expected=@($Plan.($pair[1])|ForEach-Object{[long]$_}|Sort-Object)
        if(($actual -join ',') -cne ($expected -join ',')){return $false}
    }
    return $true
}

function Assert-E2SealedResumeManifest([System.Collections.IDictionary]$Manifest,
    [System.Collections.IDictionary]$Receipt,[long[]]$SensorIds,[System.Collections.IDictionary]$Current) {
    if($Manifest.schema -cne 'qualification-job-manifest-v1' -or
       [string]::IsNullOrWhiteSpace([string]$Manifest.policyRevision) -or
       [string]$Manifest.policyRevision -cne [string]$Receipt.policyRevision -or
       [string]$Manifest.settingsRevision -cne [string]$Receipt.settingsRevision -or
       [string]$Manifest.scopeFingerprint -cne [string]$Receipt.scopeFingerprint -or
       [string]$Manifest.sourceGeneration -cne [string]$Receipt.sourceGeneration -or
       [string]$Manifest.authorityContextFingerprint -cne [string]$Receipt.authorityContextFingerprint) {
        throw 'Sealed qualification manifest does not match its original receipt fences.'
    }
    foreach($key in @('settingsRevision','scopeFingerprint','sourceGeneration','authorityContextFingerprint')) {
        if([string]$Manifest[$key] -cne [string]$Current[$key]) {
            throw "Current $key differs from the sealed qualification manifest."
        }
    }
    $manifestIds=@($Manifest.sensorIds|ForEach-Object{[long]$_})
    $fences=@($Manifest.sensorFences)
    if($manifestIds.Count -ne $SensorIds.Count -or $fences.Count -ne $SensorIds.Count -or
       ($manifestIds -join ',') -cne ($SensorIds -join ',')) {
        throw 'Sealed qualification manifest does not contain the exact current sensor scope.'
    }
    for($i=0;$i -lt $SensorIds.Count;$i++) {
        $fence=$fences[$i]
        if([long]$fence.sensorObjid -ne $SensorIds[$i] -or [long]$fence.identityEpoch -le 0 -or
           [string]::IsNullOrWhiteSpace([string]$fence.channelGeneration) -or
           [long]$fence.bindingRevision -le 0 -or
           ([string]$fence.bindingFingerprint -notmatch '^[A-Fa-f0-9]{64}$')) {
            throw "Sealed identity/binding fence is incomplete for sensor $($SensorIds[$i])."
        }
    }
}

function Assert-E2ResumeProfileProgress([System.Text.Json.JsonElement[]]$Profiles,
    [System.Collections.IDictionary]$Manifest,[object[]]$Plans,[System.Text.Json.JsonElement[]]$JobRows) {
    if($Profiles.Count -ne $Manifest.sensorIds.Count -or $Profiles.Count -ne $Plans.Count) {
        throw 'Current profile catalog is incomplete for sealed resume validation.'
    }
    $fences=@($Manifest.sensorFences)
    $jobById=@{}
    foreach($row in $JobRows) {
        if($row.ValueKind -ne 'Object'){throw 'Qualification job page contains an invalid row.'}
        $id=$row.GetProperty('sensorObjid').GetInt64()
        if($jobById.ContainsKey([string]$id)){throw 'Qualification job pages contain a duplicate sensor row.'}
        $jobById[[string]$id]=$row
    }
    for($i=0;$i -lt $Profiles.Count;$i++) {
        $profile=$Profiles[$i];$fence=$fences[$i];$plan=$Plans[$i]
        $id=[long]$Manifest.sensorIds[$i]
        if($profile.ValueKind -ne 'Object' -or $profile.GetProperty('sensorObjid').GetInt64() -ne $id -or
           $profile.GetProperty('currentIdentityEpoch').ValueKind -ne 'Number' -or
           $profile.GetProperty('currentIdentityEpoch').GetInt64() -ne [long]$fence.identityEpoch -or
           -not(Test-E2ProfileBindingMatches $profile $plan)) {
            throw "Current profile identity or reviewed binding semantics differ from the sealed manifest for sensor $id."
        }
        $identityEpoch=$profile.GetProperty('currentIdentityEpoch').GetInt64()
        $channelGeneration=$profile.GetProperty('currentChannelGeneration').GetString()
        if([string]::IsNullOrWhiteSpace($channelGeneration)) {throw "Current channel generation is missing for sensor $id."}
        $bindingRevision=$profile.GetProperty('bindingRevision').GetInt64()
        $bindingFingerprint=$profile.GetProperty('bindingFingerprint').GetString()
        $proof=$profile.GetProperty('qualificationProofReference')
        $hasProof=$proof.ValueKind -eq 'String' -and -not[string]::IsNullOrWhiteSpace($proof.GetString())
        if($jobById.ContainsKey([string]$id)) {
            $jobRow=$jobById[[string]$id]
            $jobHasProof=$jobRow.GetProperty('hasQualificationProof').GetBoolean()
            $jobStatus=$jobRow.GetProperty('status').GetString()
            $qualifiedIdentityEpoch=[System.Text.Json.JsonElement]::new()
            $qualifiedChannelGeneration=[System.Text.Json.JsonElement]::new()
            $hasQualifiedIdentityEpoch=$jobRow.TryGetProperty('qualifiedIdentityEpoch',[ref]$qualifiedIdentityEpoch)
            $hasQualifiedChannelGeneration=$jobRow.TryGetProperty('qualifiedChannelGeneration',[ref]$qualifiedChannelGeneration)
            if($jobHasProof -ne ($jobStatus -ceq 'qualified')) {
                throw "Qualification job page has an inconsistent proof status for sensor $id."
            }
            if($jobRow.GetProperty('bindingRevision').GetInt64() -ne $bindingRevision -or
               $jobRow.GetProperty('bindingFingerprint').GetString() -cne $bindingFingerprint -or
               $jobHasProof -ne $hasProof -or
               $hasProof -and (
                   -not$hasQualifiedIdentityEpoch -or
                   $qualifiedIdentityEpoch.ValueKind -ne 'Number' -or
                   $qualifiedIdentityEpoch.GetInt64() -ne [long]$fence.identityEpoch -or
                   $identityEpoch -ne [long]$fence.identityEpoch -or
                   -not$hasQualifiedChannelGeneration -or
                   $qualifiedChannelGeneration.ValueKind -ne 'String' -or
                   [string]::IsNullOrWhiteSpace($qualifiedChannelGeneration.GetString()) -or
                   $channelGeneration -cne $qualifiedChannelGeneration.GetString()) -or
               -not$hasProof -and ($identityEpoch -ne [long]$fence.identityEpoch -or
                   $channelGeneration -cne [string]$fence.channelGeneration -or
                   $bindingRevision -ne [long]$fence.bindingRevision -or
                   $bindingFingerprint -cne [string]$fence.bindingFingerprint)) {
                throw "Current binding progress is not proven by the sealed qualification job page for sensor $id."
            }
        } elseif($identityEpoch -ne [long]$fence.identityEpoch -or
            $channelGeneration -cne [string]$fence.channelGeneration -or
            $bindingRevision -ne [long]$fence.bindingRevision -or
            $bindingFingerprint -cne [string]$fence.bindingFingerprint) {
            throw "Binding changed without a matching sealed qualification job page for sensor $id."
        }
    }
    foreach($id in $jobById.Keys){if($id -notin @($Manifest.sensorIds|ForEach-Object{[string]$_})){throw 'Qualification job page contains a sensor outside the sealed scope.'}}
}

function Assert-E2StableJobSnapshot([System.Text.Json.JsonElement]$Before,[System.Text.Json.JsonElement]$After,
    [string]$JobId,[long]$Version,[int]$Wave) {
    Assert-E2JobIdentity $Before $JobId $Version $Wave
    Assert-E2JobIdentity $After $JobId $Version $Wave
    $beforeReadState=[System.Text.Json.JsonElement]::new();$afterReadState=[System.Text.Json.JsonElement]::new()
    if(-not$Before.TryGetProperty('readStateVersion',[ref]$beforeReadState) -or
       -not$After.TryGetProperty('readStateVersion',[ref]$afterReadState) -or
       $beforeReadState.ValueKind -ne 'Number' -or $afterReadState.ValueKind -ne 'Number' -or
       $beforeReadState.GetInt64() -le 0 -or $beforeReadState.GetInt64() -ne $afterReadState.GetInt64()) {
        throw 'Qualification job pages changed or lack a valid read-state version; retry Resume/Check with the current snapshot.'
    }
    foreach($key in @('selected','pageCount','initializedPages','settingsRevision','policyRevision','scopeFingerprint','sourceGeneration','authorityContextFingerprint')) {
        if($Before.GetProperty($key).GetRawText() -cne $After.GetProperty($key).GetRawText()) {
            throw 'Qualification job changed while its pages were being checked; retry Resume/Check with the current version.'
        }
    }
}

function Test-E2IdSequenceMatches([System.Text.Json.JsonElement]$Actual,[object[]]$Expected) {
    if($Actual.ValueKind -ne 'Array'){return $false}
    $actualIds=@($Actual.EnumerateArray()|ForEach-Object{$_.GetInt64()}|Sort-Object)
    $expectedIds=@($Expected|ForEach-Object{[long]$_}|Sort-Object)
    return $actualIds.Count -eq $expectedIds.Count -and ($actualIds -join ',') -ceq ($expectedIds -join ',')
}

function New-E2MonitoringRequest([object]$Plan,[string]$CurrentRevision,[string]$CatalogueToken,[string]$EstimateToken=$null) {
    return [ordered]@{revision=$CurrentRevision;coreSystemId=[string]$Plan.coreSystemId;sourceTimeZoneId=[string]$Plan.sourceTimeZoneId;
        sourceCultureName=[string]$Plan.sourceCultureName;messageDateFormat=[string]$Plan.messageDateFormat;
        rawTimestampTimeZoneId=[string]$Plan.rawTimestampTimeZoneId;analysisTimeZoneId=[string]$Plan.analysisTimeZoneId;
        timeBasisEvidenceReference=[string]$Plan.timeBasisEvidenceReference;
        sourceChangeMode=[string]$Plan.sourceChangeMode;continuityConfirmed=($Plan.continuityConfirmed -eq $true);
        continuityEvidenceReference=[string]$Plan.continuityEvidenceReference;identityConfirmed=($Plan.identityConfirmed -eq $true);
        hostIds=@($Plan.hostIds|ForEach-Object{[long]$_});sensorIds=@($Plan.sensorIds|ForEach-Object{[long]$_});
        catalogueToken=$CatalogueToken;estimateToken=$EstimateToken}
}

function Invoke-E2JsonApi([System.Net.Http.HttpClient]$Client,[System.Net.Http.HttpRequestMessage]$Request,
    [System.Threading.CancellationToken]$CancellationToken,[int]$MaxBytes=524288) {
    $response=$Client.SendAsync($Request,[System.Net.Http.HttpCompletionOption]::ResponseHeadersRead,$CancellationToken).GetAwaiter().GetResult()
    try {
        if([int]$response.StatusCode -ge 300 -and [int]$response.StatusCode -lt 400){throw 'Web API redirect refused.'}
        if(-not$response.IsSuccessStatusCode){throw "Web API returned HTTP $([int]$response.StatusCode)."}
        if($response.Content.Headers.ContentType.MediaType -cne 'application/json' -or $response.Content.Headers.ContentLength -gt $MaxBytes){throw 'Web API content type/length violates its response cap.'}
        $stream=$response.Content.ReadAsStreamAsync($CancellationToken).GetAwaiter().GetResult();$memory=[IO.MemoryStream]::new()
        try {
            $buffer=[byte[]]::new(8192)
            while(($count=$stream.ReadAsync($buffer,0,$buffer.Length,$CancellationToken).GetAwaiter().GetResult()) -gt 0){
                if($memory.Length+$count -gt $MaxBytes){throw 'Web API response exceeded its streaming byte cap.'}
                $memory.Write($buffer,0,$count)
            }
            $document=[System.Text.Json.JsonDocument]::Parse([ReadOnlyMemory[byte]]$memory.ToArray())
            try {
                $root=$document.RootElement
                if(-not$root.GetProperty('success').GetBoolean()){throw 'Web API returned an unsuccessful ApiResponse.'}
                return $root.GetProperty('data').Clone()
            } finally {$document.Dispose()}
        } finally {$stream.Dispose();$memory.Dispose()}
    } finally {$response.Dispose()}
}

function Test-E2ProfileBindingMatches([System.Text.Json.JsonElement]$Profile,[object]$Plan) {
    if($Profile.ValueKind -ne 'Object' -or $Profile.GetProperty('sensorObjid').GetInt64() -ne [long]$Plan.sensorObjid -or
       $Profile.GetProperty('bindingRevision').ValueKind -ne 'Number' -or $Profile.GetProperty('bindingRevision').GetInt64() -le 0 -or
       [string]::IsNullOrWhiteSpace($Profile.GetProperty('bindingFingerprint').GetString())){return $false}
    $pairs=@(
        @('boundChannelObjectId','channelObjectId'),@('boundChannelCaption','expectedCaption'),
        @('bindingQuantity','quantity'),@('bindingUnit','unit'),@('bindingDirection','direction'),
        @('bindingIntervalRawUnit','intervalRawUnit'),@('bindingRawTimestampTimeZoneId','rawTimestampTimeZoneId'),
        @('bindingAnalysisTimeZoneId','analysisTimeZoneId'),@('timeBasisEvidenceReference','timeBasisEvidenceReference'))
    foreach($pair in $pairs){if($Profile.GetProperty($pair[0]).GetString() -cne [string]$Plan.($pair[1])){return $false}}
    if([Math]::Abs($Profile.GetProperty('bindingScale').GetDouble()-[double]$Plan.scale) -ge 1e-12){return $false}
    return $true
}

function Get-E2BindingBatchOutcome([long[]]$ExpectedSensorIds,[object[]]$Rows) {
    if($null -eq $Rows -or $Rows.Count -ne $ExpectedSensorIds.Count) {
        return [pscustomobject]@{OutcomeUnknown=$true;Committed=@();Rejected=@()}
    }
    $committed=[Collections.Generic.List[long]]::new();$rejected=[Collections.Generic.List[long]]::new()
    for($i=0;$i -lt $ExpectedSensorIds.Count;$i++) {
        if([long]$Rows[$i].sensorObjid -ne $ExpectedSensorIds[$i]) {
            return [pscustomobject]@{OutcomeUnknown=$true;Committed=@();Rejected=@()}
        }
        if([string]$Rows[$i].status -in @('waiting','qualified')){$committed.Add($ExpectedSensorIds[$i])}
        else{$rejected.Add($ExpectedSensorIds[$i])}
    }
    return [pscustomobject]@{OutcomeUnknown=$false;Committed=$committed.ToArray();Rejected=$rejected.ToArray()}
}

function Set-E2BindingBatchReceipt([System.Collections.IDictionary]$Receipt,[long[]]$ExpectedSensorIds,[object[]]$Rows) {
    $outcome=Get-E2BindingBatchOutcome $ExpectedSensorIds $Rows
    if($outcome.OutcomeUnknown){$Receipt.stages+=@('binding-batch-outcome-unknown');return $outcome}
    $Receipt.bindingCommitted=@($Receipt.bindingCommitted)+@($outcome.Committed)
    if($outcome.Rejected.Count -gt 0){$Receipt.bindingRejected=@($Receipt.bindingRejected)+@($outcome.Rejected);$Receipt.stages+=@('binding-batch-partial')}
    else{$Receipt.stages+=@('binding-batch-committed')}
    return $outcome
}

function Assert-E2ResumeBindingsComplete([System.Text.Json.JsonElement[]]$Profiles,[object[]]$Plans) {
    if($Profiles.Count -ne $Plans.Count){throw 'Resume profile catalog is incomplete; no binding writes were attempted.'}
    for($i=0;$i -lt $Profiles.Count;$i++){
        if(-not(Test-E2ProfileBindingMatches $Profiles[$i] $Plans[$i])){
            throw "Resume requires every current binding to match the sealed plan; sensor $($Plans[$i].sensorObjid) is missing or changed."
        }
    }
}

function Assert-E2PilotMayProceed([string]$Action,[bool]$IsCurrent) {
    if(-not$IsCurrent -and $Action -in @('Resume','Check')) {
        throw 'Resume/Check require the saved capacity pilot to remain current; no replacement pilot is started.'
    }
}

function Assert-E2PilotQualified([string]$Status) {
    if($Status -cne 'qualified'){throw 'Capacity pilot did not qualify; durable job was not started.'}
}

function Assert-E2JobIdentity([System.Text.Json.JsonElement]$Current,[string]$JobId,[long]$Version,[int]$Wave) {
    if($Current.ValueKind -eq 'Null' -or $Current.GetProperty('jobId').GetString() -cne $JobId -or
       $Current.GetProperty('version').GetInt64() -ne $Version -or $Current.GetProperty('wave').GetInt32() -ne $Wave) {
        throw 'Current qualification job does not match the exact supplied ID, version, and wave.'
    }
}

function New-E2QualificationJobRequest([string]$SettingsRevision,[string]$PolicyRevision,[string]$ScopeFingerprint) {
    return @{durationHours=720;maximumAttempts=1;expectedSettingsRevision=$SettingsRevision;
        expectedPolicyRevision=$PolicyRevision;expectedScopeFingerprint=$ScopeFingerprint}
}

function New-E2QualificationResumeRequest([long]$ExpectedVersion) {
    if($ExpectedVersion -le 0){throw 'Resume requires a positive exact expected version.'}
    return @{expectedVersion=$ExpectedVersion;durationHours=720;maximumAttempts=1}
}

Export-ModuleMember -Function Test-E2SafeTimeBasisReference,ConvertTo-E2Sha256,Get-E2ListenerProcessId,Set-E2ReceiptRevisionFence,Get-E2SettingsWriteDecision,Test-E2BootstrapRequired,Assert-E2ActionMayWrite,Assert-E2BootstrapReadback,Test-E2SettingsFieldsMatch,Test-E2MonitoringPolicyMatches,Test-E2IdSequenceMatches,New-E2MonitoringRequest,Invoke-E2JsonApi,Test-E2ProfileBindingMatches,Get-E2BindingBatchOutcome,Set-E2BindingBatchReceipt,Assert-E2ResumeBindingsComplete,Assert-E2SealedResumeManifest,Assert-E2ResumeProfileProgress,Assert-E2StableJobSnapshot,Assert-E2PilotMayProceed,Assert-E2PilotQualified,Assert-E2JobIdentity,New-E2QualificationJobRequest,New-E2QualificationResumeRequest
