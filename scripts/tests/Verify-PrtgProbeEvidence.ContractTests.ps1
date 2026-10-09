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
$remoteSqlHostUnknown = 'unknown (remote database host resources not observable from app process)'

function New-ValidEvidence {
    return [ordered]@{
        schema_version = '1.0.0'
        build_version = "$script:version+$script:revision"
        status = 'ok'
        source_fingerprint = 'a'.PadRight(64, 'a')
        scope_summary = 'Secret-Server-Name-Must-Not-Be-Printed'
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
                identity_preserving_raw_history = [ordered]@{
                    Status = 'ok'
                    Samples = @([ordered]@{ Channels = @([ordered]@{ ChannelId = '3' }) })
                    SampleCount = 1
                }
            }
        )
    }
}

function Invoke-ContractCase {
    param([string] $Name, [object] $Evidence, [int] $ExpectedExitCode, [string[]] $ExpectedOutput = @(), [string] $RawJson)
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
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        $start.UseShellExecute = $false
        $process = [System.Diagnostics.Process]::new()
        $process.StartInfo = $start
        if (-not $process.Start()) { throw "$Name could not start verifier" }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(15000)) { $process.Kill($true); throw "$Name verifier exceeded the local test timeout" }
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
        Write-Output "PASS $Name exit=$($process.ExitCode)"
    } finally {
        if (Test-Path -LiteralPath $tempPath) { Remove-Item -LiteralPath $tempPath -Force }
    }
}

$valid = New-ValidEvidence
Invoke-ContractCase 'sqlite-complete-and-distinguishes-owned-volume' $valid 0 @(
    'RuntimeGcAvailableMemoryBytes: 8589934592',
    'StorageProvider: Sqlite',
    'SqlHostResources: not-applicable',
    'StorageVolumeRows: 1',
    'NativePrimaryCapabilityDiagnostics: 1/1',
    'NativePrimaryFormalAuthorization: not-asserted',
    'HANDOFF_INTEGRITY_OK'
)

$forgedAuthorization = New-ValidEvidence
$forgedAuthorization.targets[0].native_primary_capability.authorizes_formal_profile = $true
Invoke-ContractCase 'native-property-diagnostic-cannot-claim-formal-authorization' $forgedAuthorization 1 @('InvalidNativePrimaryCapabilityShape')

$wrongNativeIdType = New-ValidEvidence
$wrongNativeIdType.targets[0].native_primary_capability.primary_channel_id = 3
Invoke-ContractCase 'native-channel-id-must-be-bounded-decimal-string' $wrongNativeIdType 1 @('InvalidNativePrimaryCapabilityShape')

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

Write-Output 'All bounded PRTG evidence verifier contracts passed.'
