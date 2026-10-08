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
    'HANDOFF_INTEGRITY_OK'
)

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
