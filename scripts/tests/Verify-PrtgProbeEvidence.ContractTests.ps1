#requires -Version 7.0
<#
Standalone contract checks for Verify-PrtgProbeEvidence.ps1. Run with:
  pwsh -NoProfile -File scripts/tests/Verify-PrtgProbeEvidence.ContractTests.ps1
No provider, network, or PRTG calls are made.
#>
$ErrorActionPreference = 'Stop'
$scriptPath = (Resolve-Path (Join-Path $PSScriptRoot '..\Verify-PrtgProbeEvidence.ps1')).Path
$version = '1.0.53.2'
$revision = '0123456789abcdef0123456789abcdef01234567'
$settingsRevision = 'd2719dfc202641048000000000000001'
$remoteSqlHostUnknown = 'unknown (remote database host resources not observable from app process)'

function New-ValidEvidence {
    $facts = @{
        SchemaVersion='local-probe-facts-v1'; Status='observed'; Reason='local-read-only-counts'; AuthorizesFormalProfile=$false
        AuthorityKind='saved-explicit-authority'; SourceTimeZoneId='UTC'; RawTimestampTimeZoneId='UTC'; AnalysisTimeZoneId='UTC'
        SourceCultureName='en-US'; AuthorityFingerprint=('a'*64); PolicyHosts=20; PolicySensors=100
        MirrorDevices=200; MirrorSensors=500; MappedDevices=18; ConflictingDevices=2; GuardOverrideSensors=6; GuardSensorsInMirror=6
        ReadinessSampledSensors=100; SavedBindings=90; QualifiedProfiles=80; ReadinessReason='sampled-profile-qualification-incomplete'
        NetiqDay='2026-10-11'; NetiqReceipts=2; NetiqSuccess=1; NetiqWaiting=1; NetiqParentRows=2; NetiqEligibleParents=1
        NetiqReason='parent-qualification-is-separate-from-receipt-success'; AiProbeStatus='small-request-ready'; AiProbeElapsedMilliseconds=10
        AiProbeTotalTokens=2; AiProbeEvidenceOrigin='current-small-request'; AiCostReason='tokens-observed-price-not-configured'
    }
    $messageTime = @{
        authority_status='saved-explicit-authority'; date_format='ymd-24'; source_timezone='UTC'; source_culture='en-US'
        authority_revision=3; authority_fingerprint=('a'*64); sample_status='sample-compatible';
        sample_source='classic-content-messages-api'; samples_checked=2; samples_compatible=2; remote_account_setting_read=$false
    }
    return [ordered]@{
        schema_version = '1.1.0'
        message_time = $messageTime
        build_version = "$script:version+$script:revision"
        build_identity = @{
            Status='verified'; Reason='matching-source-and-assemblies'; SourceState='clean'; SourceCommit=$script:revision
            SourceFingerprint=('a'*64); Token=('b'*64); WebVersion="$script:version+$script:revision"; CoreVersion="$script:version+$script:revision"
            WebModuleId=('c'*32); CoreModuleId=('d'*32); WebFileSha256=('e'*64); CoreFileSha256=('f'*64)
        }
        status = 'ok'
        sensor_batch_identity = @{ status='ok'; requested_aliases=@('b1','b2'); returned_aliases=@('b2','b1'); exact_requested_set=$true; reason='exact-unique-requested-set'; authorizes_formal_profile=$false }
        settings_revision = $script:settingsRevision
        source_fingerprint = 'a'.PadRight(64, 'a')
        source_timezone = 'UTC'
        source_locale = 'en-US'
        scope_summary = 'policy-hosts=20; policy-sensors=100; mirror-devices=200; mirror-sensors=500; mapped-devices=18; conflicts=2; guard-overrides=6'
        readiness_summary = 'sampled=100; bindings=90; qualified-profiles=80; not-full-scope-verdict'
        local_facts = $facts
        deployment_resources = [ordered]@{
            processor_count = 4
            process_architecture = 'X64'
            os_architecture = 'X64'
            dotnet_framework_description = '.NET 8.0.0'
            total_available_memory_bytes = 8589934592
            working_set_bytes = 524288000
            sql_host_resources = $script:remoteSqlHostUnknown
        }
        storage_provider = 'Sqlite'
        ef_core_provider = 'Microsoft.EntityFrameworkCore.Sqlite'
        storage_environment = [ordered]@{
            status = 'measured'
            provider = 'Sqlite'
            engine_version = '3.45.0'
            edition = 'unknown'
            engine_edition = 'unknown'
            file_status = 'measured'
            volume_status = 'measured'
            local_file_status = 'measured'
            log_status = 'unknown'
            database_log_aggregate = $null
            queries_attempted = 2
            queries_succeeded = 2
            timed_out = $false
            cancelled = $false
            file_rows_truncated = $false
            volume_rows_truncated = $false
            file_capacities = @(
                [ordered]@{ role = 'database-pages'; allocated_bytes = 4096; used_bytes = 2048; maximum_bytes = 8192; maximum_kind = 'bounded'; growth_bytes = $null; growth_percent = $null },
                [ordered]@{ role = 'write-ahead-log'; allocated_bytes = 1024; used_bytes = 512; maximum_bytes = $null; maximum_kind = 'unknown'; growth_bytes = $null; growth_percent = $null }
            )
            volume_capacities = @([ordered]@{ role = 'owned-data-root'; total_bytes = 100000; available_bytes = 40000 })
        }
        targets = @(
            [ordered]@{
                alias = 's1'
                category = 'disk'
                target_type = 'sensor'
                status = 'ok'
                snapshot = [ordered]@{
                    native_primary_channel_id = '3'
                    primary_channel_field_name = 'primarychannel'
                    reported_at_sample_time = $false
                }
                native_primary_capability = [ordered]@{
                    status = 'ok'
                    property_support = 'supported'
                    endpoint = 'getobjectproperty.htm'
                    requested_property_name = 'primarychannel'
                    requested_object_alias = 's1'
                    primary_channel_id = '3'
                    matches_raw_observed_channel_ids = $true
                    matches_snapshot_primary_channel_id = $true
                    property_value = '3'
                    response_status = 'success'
                    http_status = '2xx'
                    response_format = 'xml'
                    requested_at_utc = '2026-10-09T01:02:03.0000000Z'
                    received_at_utc = '2026-10-09T01:02:04.0000000Z'
                    elapsed_ms = 1.0
                    source_version = '24.2.101'
                    authorizes_formal_profile = $false
                }
                channels = [ordered]@{
                    rows = @([ordered]@{ channel_id = '3' })
                }
                identity_preserving_raw_history = [ordered]@{
                    Status = 'ok'
                    Samples = @([ordered]@{ Channels = @([ordered]@{ ChannelId = '3' }) })
                    SampleCount = 1
                }
            }
        )
    }
}

function Set-Batch100Metadata {
    param([object] $Evidence, [int] $Count = 100, [bool] $Full100 = $true)
    $aliases = @(1..$Count | ForEach-Object { "b$_" })
    $shapeMaterial = "snapshot-filter-batch100-v1`nGET`n/api/table.json?content=sensors&columns=objid,lastvalue,interval,lastcheck,status,primarychannel&filter_objid={$Count IDs}&count=$($Count + 1)`nfilter=sorted-repeated-filter_objid`nsentinel=1`nusecaption=false`nurl-utf8<=4096`nresponse<=524288`ndepth<=32`nselection=existing-bounded-step3-sample"
    $shapeFingerprint = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData(
        [System.Text.Encoding]::UTF8.GetBytes($shapeMaterial))).ToLowerInvariant()
    $Evidence.snapshot_batch100_identity = [ordered]@{
        observation_schema_version = '2.0.0'
        request_shape_version = 'snapshot-filter-batch100-v1'
        runtime_request_contract_version = 'snapshot-filter-batch100-v1'
        request_method = 'GET'
        native_columns = 'objid,lastvalue,interval,lastcheck,status,primarychannel'
        filter_mode = 'sorted-repeated-filter_objid'
        request_count_parameter = $Count + 1
        sentinel_rows = 1
        uses_caption = $false
        selection_scope = 'existing-bounded-step3-sample'
        status = 'ok'
        requested_aliases = $aliases
        returned_aliases = $aliases
        requested_count = $Count
        responded_count = $Count
        exact_requested_set = $true
        runtime_response_compatible = $true
        minimum_fields_observed = $true
        runtime_response_reason = 'runtime-snapshot-contract-compatible'
        truncated = $false
        response_bytes = 4096
        request_url_bytes = 3500
        maximum_response_bytes = 524288
        maximum_relative_url_bytes = 4096
        maximum_json_depth = 32
        shape_fingerprint = $shapeFingerprint
        full_batch100_observed = $Full100
        profile_authorized = $false
        capacity_accepted = $false
        reason = 'exact-unique-requested-set'
        authorizes_formal_profile = $false
    }
    return $Evidence
}

function Invoke-ContractCase {
    param([string] $Name, [object] $Evidence, [int] $ExpectedExitCode, [string[]] $ExpectedOutput = @(), [string] $RawJson, [string] $ExpectedSettingsRevision, [string] $ExpectedMessageTimeFormat, [string] $ExpectedMessageTimeFingerprint, [string[]] $ForbiddenOutput = @())
    $tempPath = Join-Path ([System.IO.Path]::GetTempPath()) ("lf-probe-contract-$([guid]::NewGuid().ToString('N')).json")
    try {
        $json = if ($PSBoundParameters.ContainsKey('RawJson')) { $RawJson } else { ConvertTo-Json -InputObject $Evidence -Depth 20 -Compress }
        [System.IO.File]::WriteAllText($tempPath, $json, [System.Text.UTF8Encoding]::new($false))
        $start = [System.Diagnostics.ProcessStartInfo]::new((Get-Command pwsh).Source)
        $start.ArgumentList.Add('-NoProfile')
        $start.ArgumentList.Add('-File')
        $start.ArgumentList.Add($scriptPath)
        $start.ArgumentList.Add('-Path')
        $start.ArgumentList.Add($tempPath)
        $start.ArgumentList.Add('-ExpectedVersion')
        $start.ArgumentList.Add($script:version)
        $start.ArgumentList.Add('-ExpectedRevision')
        $start.ArgumentList.Add($script:revision)
        if ($PSBoundParameters.ContainsKey('ExpectedSettingsRevision')) {
            $start.ArgumentList.Add('-ExpectedSettingsRevision')
            $start.ArgumentList.Add($ExpectedSettingsRevision)
        }
        if ($PSBoundParameters.ContainsKey('ExpectedMessageTimeFormat')) {
            $start.ArgumentList.Add('-ExpectedMessageTimeFormat')
            $start.ArgumentList.Add($ExpectedMessageTimeFormat)
        }
        if ($PSBoundParameters.ContainsKey('ExpectedMessageTimeFingerprint')) {
            $start.ArgumentList.Add('-ExpectedMessageTimeFingerprint')
            $start.ArgumentList.Add($ExpectedMessageTimeFingerprint)
        }
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        $start.UseShellExecute = $false
        $process = [System.Diagnostics.Process]::new()
        $process.StartInfo = $start
        if (-not $process.Start()) { throw "$Name could not start verifier" }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(60000)) { $process.Kill($true); throw "$Name verifier exceeded the local test timeout" }
        $output = $stdoutTask.GetAwaiter().GetResult()
        $errorOutput = $stderrTask.GetAwaiter().GetResult()
        if ($process.ExitCode -ne $ExpectedExitCode) { throw "$Name expected exit $ExpectedExitCode, got $($process.ExitCode): $errorOutput $output" }
        foreach ($expected in $ExpectedOutput) {
            if (-not $output.Contains($expected, [StringComparison]::Ordinal)) { throw "$Name missing fixed summary marker: $expected" }
        }
        if ($output.Contains('Secret-Server-Name-Must-Not-Be-Printed', [StringComparison]::Ordinal) -or
            $output.Contains($script:remoteSqlHostUnknown, [StringComparison]::Ordinal)) {
            throw "$Name leaked a raw deployment string"
        }
        foreach ($forbidden in $ForbiddenOutput) {
            if ($output.Contains($forbidden, [StringComparison]::Ordinal)) { throw "$Name echoed forbidden input" }
        }
        Write-Output "PASS $Name exit=$($process.ExitCode)"
    } finally {
        if (Test-Path -LiteralPath $tempPath) { Remove-Item -LiteralPath $tempPath -Force }
    }
}

function Set-TargetCount {
    param([object] $Evidence, [int] $Count)
    $categories = @('cpu','memory','disk','ping','traffic')
    $targets = @()
    for ($i = 0; $i -lt $Count; $i++) {
        $target = ConvertFrom-Json -InputObject (ConvertTo-Json -InputObject $Evidence.targets[0] -Depth 20 -Compress) -AsHashtable
        $alias = "s$($i + 1)"
        $target.alias = $alias
        $target.category = $categories[$i % $categories.Count]
        $target.native_primary_capability.requested_object_alias = $alias
        $targets += $target
    }
    $Evidence.targets = $targets
    return $Evidence
}

$valid = Set-Batch100Metadata (New-ValidEvidence)
Invoke-ContractCase 'sqlite-complete-and-distinguishes-owned-volume' $valid 0 @(
    'RuntimeGcAvailableMemoryBytes: 8589934592',
    'StorageProvider: Sqlite',
    'SqlHostResources: not-applicable',
    'StorageVolumeRows: 1',
    'NativePrimaryCapabilityDiagnostics: 1/1',
    'NativePrimaryFormalAuthorization: not-asserted',
    'SensorBatchFull100ExactObserved: True',
    'SensorBatchCapacityAccepted: false',
    "SettingsRevision: $settingsRevision",
    'SettingsRevisionReason: exported-no-expected-revision-supplied',
    'SourceFingerprintReason: exported-sha256-shape-only-source-not-independently-verified',
    'SavedTimeAuthority: source_timezone=UTC; source_locale=en-US',
    'SavedTimeAuthorityReason: sampled-profile-qualification-incomplete',
    'LocalScopeSummary: policy-hosts=20; policy-sensors=100; mirror-devices=200; mirror-sensors=500; mapped-devices=18; conflicts=2; guard-overrides=6',
    'LocalReadinessSummary: sampled=100; bindings=90; qualified-profiles=80; not-full-scope-verdict',
    'LocalFactsReason: observed-bounded-local-facts',
    'ReadinessReason: sampled-profile-qualification-incomplete',
    'GuardScopeCounts: overrides=6; in-mirror=6',
    'NetIqReceiptSummary: day=2026-10-11; receipts=2; success=1; waiting=1; eligible-parents=1/2; reason=parent-qualification-is-separate-from-receipt-success',
    'AiSmallProbeSummary: status=small-request-ready; elapsed-ms=10; tokens=2; origin=current-small-request; cost=tokens-observed-price-not-configured',
    'HANDOFF_INTEGRITY_OK'
)

Invoke-ContractCase 'settings-revision-can-be-checked-by-operator' $valid 0 @("SettingsRevision: $settingsRevision",'SettingsRevisionReason: matches-expected-settings-revision') -ExpectedSettingsRevision $settingsRevision
$forgedReady = ConvertFrom-Json -InputObject (ConvertTo-Json -InputObject $valid -Depth 24 -Compress) -AsHashtable
$forgedReady.evidence_ready = $true
Invoke-ContractCase 'compatibility-export-cannot-claim-formal-readiness' $forgedReady 1 @('InvalidEvidenceReadyClaim')
Invoke-ContractCase 'saved-message-time-sample-is-compatibility-only' $valid 0 @('MessageTimeAuthority: authority=saved-explicit-authority; format=ymd-24; sample=sample-compatible','MessageTimeRemoteAccountSettingRead: false','HANDOFF_INTEGRITY_OK') -ExpectedMessageTimeFormat 'ymd-24' -ExpectedMessageTimeFingerprint ('a'*64)
$legacyMessageTime = ConvertFrom-Json -InputObject (ConvertTo-Json -InputObject $valid -Depth 24 -Compress) -AsHashtable
$legacyMessageTime.schema_version = '1.0.0'; $legacyMessageTime.Remove('message_time')
Invoke-ContractCase 'legacy-message-time-is-readable-but-unverified' $legacyMessageTime 2 @('MessageTimeAuthority: authority=legacy-unverified; format=unknown; sample=legacy-message-time-unverified','Result: INCOMPLETE')
$noMessageSamples = ConvertFrom-Json -InputObject (ConvertTo-Json -InputObject $valid -Depth 24 -Compress) -AsHashtable
$noMessageSamples.message_time.sample_status = 'no-samples'; $noMessageSamples.message_time.samples_checked = 0; $noMessageSamples.message_time.samples_compatible = 0
Invoke-ContractCase 'no-message-samples-remain-incomplete' $noMessageSamples 2 @('MessageTimeAuthority: authority=saved-explicit-authority; format=ymd-24; sample=no-samples','Result: INCOMPLETE')
$claimsRemoteRead = ConvertFrom-Json -InputObject (ConvertTo-Json -InputObject $valid -Depth 24 -Compress) -AsHashtable
$claimsRemoteRead.message_time.remote_account_setting_read = $true
Invoke-ContractCase 'probe-cannot-claim-account-setting-readback' $claimsRemoteRead 1 @('InvalidMessageTimeEvidence')
$wrongSampleCounts = ConvertFrom-Json -InputObject (ConvertTo-Json -InputObject $valid -Depth 24 -Compress) -AsHashtable
$wrongSampleCounts.message_time.samples_compatible = 1
Invoke-ContractCase 'sample-compatible-count-must-be-complete' $wrongSampleCounts 1 @('ContradictoryMessageTimeSamples')
$wrongSettingsRevision = ConvertFrom-Json -InputObject (ConvertTo-Json -InputObject $valid -Depth 24 -Compress) -AsHashtable
$wrongSettingsRevision.settings_revision = 'ffffffffffffffffffffffffffffffff'
Invoke-ContractCase 'settings-revision-mismatch-is-rejected' $wrongSettingsRevision 1 @('SettingsRevisionMismatch') -ExpectedSettingsRevision $settingsRevision
$injectedSettingsRevision = ConvertFrom-Json -InputObject (ConvertTo-Json -InputObject $valid -Depth 24 -Compress) -AsHashtable
$injectedSettingsRevision.settings_revision = "revision`nSENTINEL_REVISION_SECRET"
Invoke-ContractCase 'settings-revision-output-injection-is-rejected-without-echo' $injectedSettingsRevision 1 @('InvalidLocalContextShape') -ForbiddenOutput @('SENTINEL_REVISION_SECRET')
Invoke-ContractCase 'expected-settings-revision-output-injection-is-rejected' $valid 1 @('InvalidExpectedSettingsRevision') -ExpectedSettingsRevision "revision`nSENTINEL_REVISION_SECRET" -ForbiddenOutput @('SENTINEL_REVISION_SECRET')
$missingSettingsRevision = ConvertFrom-Json -InputObject (ConvertTo-Json -InputObject $valid -Depth 24 -Compress) -AsHashtable
$missingSettingsRevision.Remove('settings_revision')
Invoke-ContractCase 'missing-settings-revision-is-incomplete' $missingSettingsRevision 2 @('SettingsRevision: unknown','SettingsRevisionReason: settings-revision-not-exported','Result: INCOMPLETE')
$missingSourceFingerprint = ConvertFrom-Json -InputObject (ConvertTo-Json -InputObject $valid -Depth 24 -Compress) -AsHashtable
$missingSourceFingerprint.source_fingerprint = 'unknown'
Invoke-ContractCase 'missing-source-fingerprint-has-specific-incomplete-reason' $missingSourceFingerprint 2 @('SourceFingerprint: unknown','SourceFingerprintReason: source-fingerprint-unavailable','Result: INCOMPLETE')
$missingLocalFacts = ConvertFrom-Json -InputObject (ConvertTo-Json -InputObject $valid -Depth 24 -Compress) -AsHashtable
$missingLocalFacts.Remove('local_facts')
Invoke-ContractCase 'legacy-without-local-provenance-is-incomplete' $missingLocalFacts 2 @('LocalFactsStatus: missing','LocalFactsReason: local-facts-not-exported','SavedTimeAuthorityReason: local-facts-not-exported','Result: INCOMPLETE')
$contradictoryTimezone = ConvertFrom-Json -InputObject (ConvertTo-Json -InputObject $valid -Depth 24 -Compress) -AsHashtable
$contradictoryTimezone.source_timezone = 'America/Los_Angeles'
Invoke-ContractCase 'top-level-timezone-must-match-saved-authority' $contradictoryTimezone 1 @('LocalContextContradiction')
$forgedScope = ConvertFrom-Json -InputObject (ConvertTo-Json -InputObject $valid -Depth 24 -Compress) -AsHashtable
$forgedScope.local_facts.PolicyHosts = 999
Invoke-ContractCase 'scope-summary-must-match-typed-bounded-counts' $forgedScope 1 @('LocalContextContradiction')
$unknownAuthority = ConvertFrom-Json -InputObject (ConvertTo-Json -InputObject $valid -Depth 24 -Compress) -AsHashtable
$unknownAuthority.local_facts.AuthorityKind = 'unknown'
$unknownAuthority.local_facts.SourceTimeZoneId = 'unknown'
$unknownAuthority.local_facts.RawTimestampTimeZoneId = 'unknown'
$unknownAuthority.local_facts.AnalysisTimeZoneId = 'unknown'
$unknownAuthority.local_facts.SourceCultureName = 'unknown'
$unknownAuthority.local_facts.AuthorityFingerprint = 'unknown'
$unknownAuthority.local_facts.ReadinessReason = 'explicit-time-authority-missing-or-invalid'
$unknownAuthority.source_timezone = 'unknown'
$unknownAuthority.source_locale = 'unknown'
Invoke-ContractCase 'missing-saved-time-authority-is-reported-and-incomplete' $unknownAuthority 2 @('SavedTimeAuthority: source_timezone=unknown; source_locale=unknown','LocalFactsReason: explicit-time-authority-missing-or-invalid','Result: INCOMPLETE')

$legacyThreeTargets = Set-TargetCount (New-ValidEvidence) 3
Invoke-ContractCase 'legacy-three-target-evidence-remains-readable' $legacyThreeTargets 2 @(
    'TargetsCount: 3',
    'Result: INCOMPLETE'
)

$currentFiveTargets = Set-TargetCount (New-ValidEvidence) 5
Invoke-ContractCase 'current-five-target-evidence-remains-bounded' $currentFiveTargets 2 @(
    'TargetsCount: 5',
    'Result: INCOMPLETE'
)

$sixTargets = Set-TargetCount (New-ValidEvidence) 6
Invoke-ContractCase 'more-than-five-targets-is-rejected' $sixTargets 1 @('InvalidTargetsCount')

$legacyFiveEvidence = New-ValidEvidence
$legacyFiveEvidence.sensor_batch_identity.requested_aliases = @('b1','b2','b3','b4','b5')
$legacyFiveEvidence.sensor_batch_identity.returned_aliases = @('b1','b2','b3','b4','b5')
Invoke-ContractCase 'legacy-five-id-evidence-remains-readable-but-not-batch100' $legacyFiveEvidence 2 @(
    'LegacySensorBatchExactSetObserved: True',
    'SensorBatchExactSetObserved: False',
    'SensorBatchFull100ExactObserved: False',
    'Result: INCOMPLETE'
)

$exactBatch100 = Set-Batch100Metadata (New-ValidEvidence)
Invoke-ContractCase 'batch100-exact-bounded-shape-is-diagnostic-only' $exactBatch100 0 @(
    'SensorBatchExactSetObserved: True',
    'SensorBatchFull100ExactObserved: True',
    'SensorBatchProfileAuthorized: false',
    'SensorBatchCapacityAccepted: false',
    'HANDOFF_INTEGRITY_OK'
)

$shortExactBatch100 = Set-Batch100Metadata (New-ValidEvidence) -Count 99 -Full100 $false
Invoke-ContractCase 'batch100-contract-with-short-population-remains-incomplete-for-full100' $shortExactBatch100 2 @(
    'SensorBatchExactSetObserved: True',
    'SensorBatchFull100ExactObserved: False',
    'SensorBatchCapacityAccepted: false',
    'Result: INCOMPLETE'
)

$forgedBatch100Authority = Set-Batch100Metadata (New-ValidEvidence)
$forgedBatch100Authority.snapshot_batch100_identity.capacity_accepted = $true
Invoke-ContractCase 'batch100-cannot-claim-capacity-acceptance' $forgedBatch100Authority 1 @('InvalidSensorBatch100Metadata')

$forgedBatch100Fingerprint = Set-Batch100Metadata (New-ValidEvidence)
$forgedBatch100Fingerprint.snapshot_batch100_identity.shape_fingerprint = 'f'.PadRight(64, 'f')
Invoke-ContractCase 'observation-fingerprint-mismatch-is-incomplete' $forgedBatch100Fingerprint 2 @(
    'SensorBatchFull100ExactObserved: False',
    'Result: INCOMPLETE'
)

$driftedQueryContract = Set-Batch100Metadata (New-ValidEvidence)
$driftedQueryContract.snapshot_batch100_identity.native_columns = 'objid,lastvalue,status'
Invoke-ContractCase 'runtime-source-contract-drift-is-incomplete' $driftedQueryContract 2 @(
    'SensorBatchFull100ExactObserved: False',
    'Result: INCOMPLETE'
)

$runtimeIncompatibleBatch100 = Set-Batch100Metadata (New-ValidEvidence)
$runtimeIncompatibleBatch100.snapshot_batch100_identity.runtime_response_compatible = $false
$runtimeIncompatibleBatch100.snapshot_batch100_identity.minimum_fields_observed = $false
$runtimeIncompatibleBatch100.snapshot_batch100_identity.runtime_response_reason = 'minimum-snapshot-fields-missing'
$runtimeIncompatibleBatch100.snapshot_batch100_identity.full_batch100_observed = $false
Invoke-ContractCase 'exact-ids-with-runtime-incompatible-rows-remain-incomplete' $runtimeIncompatibleBatch100 2 @(
    'SensorBatchExactSetObserved: True',
    'SensorBatchFull100ExactObserved: False',
    'Result: INCOMPLETE'
)

$legacyV2MissingRuntimeCheck = Set-Batch100Metadata (New-ValidEvidence)
$legacyV2MissingRuntimeCheck.snapshot_batch100_identity.Remove('runtime_response_compatible')
$legacyV2MissingRuntimeCheck.snapshot_batch100_identity.Remove('minimum_fields_observed')
$legacyV2MissingRuntimeCheck.snapshot_batch100_identity.Remove('runtime_response_reason')
Invoke-ContractCase 'older-v2-without-runtime-response-check-is-incomplete' $legacyV2MissingRuntimeCheck 2 @(
    'SensorBatchFull100ExactObserved: False',
    'Result: INCOMPLETE'
)

$forgedShortFull100 = Set-Batch100Metadata (New-ValidEvidence) -Count 99 -Full100 $true
Invoke-ContractCase 'short-batch-cannot-claim-full100-observed' $forgedShortFull100 1 @('SensorBatch100ExactSetContradiction')

$forgedAuthorization = New-ValidEvidence
$forgedAuthorization.targets[0].native_primary_capability.authorizes_formal_profile = $true
Invoke-ContractCase 'native-property-diagnostic-cannot-claim-formal-authorization' $forgedAuthorization 1 @('InvalidNativePrimaryCapabilityShape')

$wrongNativeIdType = New-ValidEvidence
$wrongNativeIdType.targets[0].native_primary_capability.primary_channel_id = 3
Invoke-ContractCase 'native-channel-id-must-be-bounded-decimal-string' $wrongNativeIdType 1 @('InvalidNativePrimaryCapabilityShape')

$negativeMetadataChannelId = Set-Batch100Metadata (New-ValidEvidence)
$negativeMetadataChannelId.targets[0].channels.rows[0].channel_id = '-1'
Invoke-ContractCase 'negative-native-channel-id-sentinel-is-preserved' $negativeMetadataChannelId 0 @(
    'HANDOFF_INTEGRITY_OK',
    'NativePrimaryFormalAuthorization: not-asserted'
)

$unrecognizedNegativeChannelId = Set-Batch100Metadata (New-ValidEvidence)
$unrecognizedNegativeChannelId.targets[0].channels.rows[0].channel_id = '-5'
Invoke-ContractCase 'undocumented-negative-channel-id-is-rejected' $unrecognizedNegativeChannelId 1 @('InvalidChannelMetadataShape')

$noncanonicalChannelId = Set-Batch100Metadata (New-ValidEvidence)
$noncanonicalChannelId.targets[0].channels.rows[0].channel_id = '03'
Invoke-ContractCase 'noncanonical-channel-id-alias-is-rejected' $noncanonicalChannelId 1 @('InvalidChannelMetadataShape')

$sameIdDifferentQuantities = Set-Batch100Metadata (New-ValidEvidence)
$sameIdDifferentQuantities.targets[0].identity_preserving_raw_history.Samples[0].Channels = @(
    [ordered]@{ ChannelId = '3'; QuantityKind = 'volume' },
    [ordered]@{ ChannelId = '3'; QuantityKind = 'speed' }
)
Invoke-ContractCase 'same-channel-id-separate-traffic-quantities-are-retained' $sameIdDifferentQuantities 0 @('HANDOFF_INTEGRITY_OK')

$duplicateRawQuantity = New-ValidEvidence
$duplicateRawQuantity.targets[0].identity_preserving_raw_history.Samples[0].Channels = @(
    [ordered]@{ ChannelId = '3'; QuantityKind = 'volume' },
    [ordered]@{ ChannelId = '3'; QuantityKind = 'volume' }
)
Invoke-ContractCase 'true-duplicate-channel-id-and-quantity-is-rejected' $duplicateRawQuantity 1 @('RawChannelIdentityMissingOrWrongShape')

$numericChannelIdInEvidence = Set-Batch100Metadata (New-ValidEvidence)
$numericChannelIdInEvidence.targets[0].channels.rows[0].channel_id = 3
Invoke-ContractCase 'channel-id-evidence-must-use-bounded-decimal-string' $numericChannelIdInEvidence 1 @('InvalidChannelMetadataShape')

$overflowChannelIdInEvidence = Set-Batch100Metadata (New-ValidEvidence)
$overflowChannelIdInEvidence.targets[0].channels.rows[0].channel_id = '9223372036854775808'
Invoke-ContractCase 'overflow-channel-id-evidence-is-rejected' $overflowChannelIdInEvidence 1 @('InvalidChannelMetadataShape')

$legacyChannelRows = Set-Batch100Metadata (New-ValidEvidence)
$legacyChannelRows.targets[0].channels.rows[0].Remove('channel_id')
Invoke-ContractCase 'legacy-channel-rows-without-channel-id-remain-readable' $legacyChannelRows 0 @('HANDOFF_INTEGRITY_OK')

$wrongSnapshotTimeSemantics = New-ValidEvidence
$wrongSnapshotTimeSemantics.targets[0].snapshot.reported_at_sample_time = $true
Invoke-ContractCase 'snapshot-channel-is-not-reported-at-sample-time' $wrongSnapshotTimeSemantics 1 @('InvalidNativeSnapshotShape')

$conflictingNativeObservation = New-ValidEvidence
$conflictingNativeObservation.targets[0].native_primary_capability.primary_channel_id = '4'
$conflictingNativeObservation.targets[0].native_primary_capability.property_value = '4'
$conflictingNativeObservation.targets[0].native_primary_capability.matches_raw_observed_channel_ids = $false
$conflictingNativeObservation.targets[0].native_primary_capability.matches_snapshot_primary_channel_id = $false
Invoke-ContractCase 'native-primary-conflict-is-incomplete' $conflictingNativeObservation 2 @(
    'NativePrimaryConflicts: 1',
    'NativePrimaryFormalAuthorization: not-asserted',
    'Result: INCOMPLETE'
)

$unknownNativeObservation = New-ValidEvidence
$unknownNative = $unknownNativeObservation.targets[0].native_primary_capability
$unknownNative.status = 'unknown'
$unknownNative.property_support = 'unknown'
$unknownNative.primary_channel_id = $null
$unknownNative.matches_raw_observed_channel_ids = $null
$unknownNative.matches_snapshot_primary_channel_id = $null
$unknownNative.response_status = 'unknown'
$unknownNative.http_status = 'unknown'
$unknownNative.response_format = 'unknown'
$unknownNative.Remove('property_value')
Invoke-ContractCase 'unknown-native-primary-remains-incomplete-with-null-matches' $unknownNativeObservation 2 @(
    'NativePrimaryCapabilityDiagnostics: 0/1',
    'NativePrimaryFormalAuthorization: not-asserted',
    'Result: INCOMPLETE'
)

$lateSuccessfulNativeObservation = New-ValidEvidence
$lateSuccessfulNativeObservation.targets[0].native_primary_capability.elapsed_ms = 31001.0
Invoke-ContractCase 'late-successful-native-diagnostic-is-incomplete-not-malformed' $lateSuccessfulNativeObservation 2 @(
    'NativePrimaryCapabilityDiagnostics: 0/1',
    'NativePrimaryFormalAuthorization: not-asserted',
    'Result: INCOMPLETE'
)

$lateTimeoutNativeObservation = New-ValidEvidence
$lateTimeoutNative = $lateTimeoutNativeObservation.targets[0].native_primary_capability
$lateTimeoutNative.status = 'timeout'
$lateTimeoutNative.property_support = 'unknown'
$lateTimeoutNative.primary_channel_id = $null
$lateTimeoutNative.matches_raw_observed_channel_ids = $null
$lateTimeoutNative.matches_snapshot_primary_channel_id = $null
$lateTimeoutNative.Remove('property_value')
$lateTimeoutNative.response_status = 'timeout'
$lateTimeoutNative.http_status = 'unknown'
$lateTimeoutNative.response_format = 'unknown'
$lateTimeoutNative.elapsed_ms = 31000.0
Invoke-ContractCase 'late-timeout-with-unknown-proof-remains-incomplete' $lateTimeoutNativeObservation 2 @(
    'NativePrimaryCapabilityDiagnostics: 0/1',
    'NativePrimaryFormalAuthorization: not-asserted',
    'Result: INCOMPLETE'
)

$hugeElapsedEvidenceJson = ConvertTo-Json -InputObject (New-ValidEvidence) -Depth 20 -Compress
$hugeElapsedEvidenceJson = [regex]::Replace($hugeElapsedEvidenceJson, '("elapsed_ms"\s*:\s*)1(?:\.0)?(?=[,}])', '${1}1e999', 1)
Invoke-ContractCase 'non-finite-huge-elapsed-is-malformed' $null 1 @('InvalidNativePrimaryCapabilityShape') -RawJson $hugeElapsedEvidenceJson

$nanElapsedEvidenceJson = ConvertTo-Json -InputObject (New-ValidEvidence) -Depth 20 -Compress
$nanElapsedEvidenceJson = [regex]::Replace($nanElapsedEvidenceJson, '("elapsed_ms"\s*:\s*)1(?:\.0)?(?=[,}])', '${1}NaN', 1)
Invoke-ContractCase 'non-json-nan-elapsed-remains-malformed' $null 1 @('UnreadableOrMalformedJson') -RawJson $nanElapsedEvidenceJson

$legacyPartial = New-ValidEvidence
$legacyPartial.targets[0].Remove('snapshot')
$legacyPartial.targets[0].Remove('native_primary_capability')
$legacyPartial.targets[0].status = 'partial'
$legacyPartial.status = 'partial'
Invoke-ContractCase 'legacy-partial-probe-remains-incomplete' $legacyPartial 2 @('Result: INCOMPLETE')

$missing = New-ValidEvidence
$missing.Remove('storage_environment')
Invoke-ContractCase 'missing-storage-object-is-malformed' $missing 1 @('InvalidStorageEnvironmentShape')

$mismatch = New-ValidEvidence
$mismatch.storage_environment.provider = 'SqlServer'
Invoke-ContractCase 'provider-disagreement-is-malformed' $mismatch 1 @('StorageProviderMismatch')

$partial = New-ValidEvidence
$partial.storage_environment.status = 'partial'
$partial.storage_environment.file_status = 'partial'
$partial.storage_environment.queries_succeeded = 1
Invoke-ContractCase 'partial-metadata-is-incomplete' $partial 2 @('StorageMetadataStatus: partial','Result: INCOMPLETE')

$unknown = New-ValidEvidence
$unknown.storage_environment.status = 'unknown'
$unknown.storage_environment.engine_version = 'unknown'
$unknown.storage_environment.file_status = 'unknown'
$unknown.storage_environment.volume_status = 'unknown'
$unknown.storage_environment.local_file_status = 'unknown'
$unknown.storage_environment.queries_attempted = 0
$unknown.storage_environment.queries_succeeded = 0
$unknown.storage_environment.file_capacities = @()
$unknown.storage_environment.volume_capacities = @()
Invoke-ContractCase 'unknown-or-permission-limited-metadata-is-incomplete' $unknown 2 @('StorageMetadataStatus: unknown','Result: INCOMPLETE')

$badRows = New-ValidEvidence
$badRows.storage_environment.file_capacities = @('raw-file-path-must-not-be-accepted')
Invoke-ContractCase 'wrong-row-shape-is-malformed' $badRows 1 @('InvalidStorageEnvironmentShape')

$shortRevision = New-ValidEvidence
$shortRevision.build_version = "$version+0123456789abcdef"
Invoke-ContractCase 'short-build-revision-is-rejected' $shortRevision 1 @('MissingOrInvalidBuildSuffix')

Invoke-ContractCase 'duplicate-json-properties-are-rejected' $null 1 @('DuplicateJsonProperty') -RawJson '{"schema_version":"1.0.0","schema_version":"1.0.0"}'
$tooLarge = '{"x":"' + ('a' * (64 * 1024)) + '"}'
Invoke-ContractCase '64-kib-limit-is-preserved' $null 1 @('Oversize') -RawJson $tooLarge
$deepJson = '{"x":' + ('[' * 33) + '0' + (']' * 33) + '}'
Invoke-ContractCase 'json-depth-limit-is-preserved' $null 1 @('UnreadableOrMalformedJson') -RawJson $deepJson



$noBatch = New-ValidEvidence
$noBatch.Remove('sensor_batch_identity')
Invoke-ContractCase 'legacy-without-batch-observation-is-incomplete' $noBatch 2 @('SensorBatchExactSetObserved: False','Result: INCOMPLETE')
$duplicateBatch = New-ValidEvidence
$duplicateBatch.sensor_batch_identity.returned_aliases = @('b1','b1')
Invoke-ContractCase 'duplicate-batch-cannot-assert-exact-set' $duplicateBatch 1 @('SensorBatchExactSetContradiction')
$foreignBatch = New-ValidEvidence
$foreignBatch.sensor_batch_identity.returned_aliases = @('b1','foreign1')
$foreignBatch.sensor_batch_identity.exact_requested_set = $false
$foreignBatch.sensor_batch_identity.status = 'partial'
$foreignBatch.sensor_batch_identity.reason = 'missing-foreign-duplicate-or-invalid-sensor'
Invoke-ContractCase 'foreign-batch-remains-incomplete' $foreignBatch 2 @('SensorBatchExactSetObserved: False','Result: INCOMPLETE')
$unsafeBatch = New-ValidEvidence
$unsafeBatch.sensor_batch_identity.authorizes_formal_profile = $true
Invoke-ContractCase 'batch-does-not-authorize-profile' $unsafeBatch 1 @('InvalidSensorBatchIdentityShape')
$legacyIdentity = New-ValidEvidence
$legacyIdentity.Remove('build_identity')
Invoke-ContractCase 'legacy-build-tail-without-assembly-proof-is-incomplete' $legacyIdentity 2 @('Result: INCOMPLETE')
$wrongIdentity = New-ValidEvidence
$wrongIdentity.build_identity.CoreVersion = "$version+ffffffffffffffffffffffffffffffffffffffff"
Invoke-ContractCase 'mixed-web-core-identity-is-rejected' $wrongIdentity 1 @('InvalidBuildIdentity')
$localFacts = @{
    SchemaVersion='local-probe-facts-v1'; Status='observed'; Reason='local-read-only-counts'; AuthorizesFormalProfile=$false
    AuthorityKind='unknown'; SourceTimeZoneId='unknown'; RawTimestampTimeZoneId='unknown'; AnalysisTimeZoneId='unknown'
    SourceCultureName='unknown'; AuthorityFingerprint='unknown'; PolicyHosts=20; PolicySensors=100
    MirrorDevices=6576; MirrorSensors=43762; MappedDevices=18; ConflictingDevices=2; GuardOverrideSensors=6; GuardSensorsInMirror=6
    ReadinessSampledSensors=100; SavedBindings=0; QualifiedProfiles=0; ReadinessReason='explicit-time-authority-missing-or-invalid'
    NetiqDay='2026-10-11'; NetiqReceipts=2; NetiqSuccess=1; NetiqWaiting=1; NetiqParentRows=2; NetiqEligibleParents=1
    NetiqReason='parent-qualification-is-separate-from-receipt-success'; AiProbeStatus='small-request-ready'; AiProbeElapsedMilliseconds=10
    AiProbeTotalTokens=2; AiProbeEvidenceOrigin='current-small-request'; AiCostReason='tokens-observed-price-not-configured'
}
$typedFacts = New-ValidEvidence
$typedFacts = Set-Batch100Metadata $typedFacts
$typedFacts.local_facts = $localFacts
$typedFacts.source_timezone = 'unknown'
$typedFacts.source_locale = 'unknown'
$typedFacts.scope_summary = 'policy-hosts=20; policy-sensors=100; mirror-devices=6576; mirror-sensors=43762; mapped-devices=18; conflicts=2; guard-overrides=6'
$typedFacts.readiness_summary = 'sampled=100; bindings=0; qualified-profiles=0; not-full-scope-verdict'
Invoke-ContractCase 'bounded-local-facts-are-readable-but-unknown-authority-incomplete' $typedFacts 2 @('Version: 1.0.53.2','SavedTimeAuthorityReason: explicit-time-authority-missing-or-invalid','Result: INCOMPLETE')
$localFacts.AuthorizesFormalProfile = $true
Invoke-ContractCase 'local-facts-cannot-authorize-profile' $typedFacts 1 @('InvalidLocalProbeFacts')
$localFacts.AuthorizesFormalProfile = $false
$localFacts.QualifiedProfiles = 101
Invoke-ContractCase 'local-facts-cannot-overclaim-bounded-page' $typedFacts 1 @('InvalidLocalProbeFacts')
$localFacts.QualifiedProfiles = 0
$localFacts.Secret = 'private-host-must-not-appear'
Invoke-ContractCase 'local-facts-reject-unbounded-private-field' $typedFacts 1 @('InvalidLocalProbeFacts')
$missingRepresentative = New-ValidEvidence
$missingRepresentative.targets += @{ alias='s5'; category='traffic'; status='missing'; reason='No sensor sample found for type in step 3' }
Invoke-ContractCase -Name 'missing-representative-without-raw-receipt-is-incomplete' -Evidence $missingRepresentative -ExpectedExitCode 2
$missingRepresentative.targets[-1].status = 'ok'
Invoke-ContractCase -Name 'observed-representative-cannot-omit-raw-receipt' -Evidence $missingRepresentative -ExpectedExitCode 1 -ExpectedOutput @('RawChannelIdentityMissingOrWrongShape')
Write-Output 'All bounded PRTG evidence verifier contracts passed.'
