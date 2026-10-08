#Requires -Version 7.0
<#
用途：只核對完整環境探測 JSON 的版本、build revision、raw identity 與部署／儲存 metadata 形狀。
用法：pwsh -File .\Verify-PrtgProbeEvidence.ps1 -Path .\evidence.json [-ExpectedVersion 1.0.53.2] [-ExpectedRevision <40 位 hex>]
輸出為固定摘要；不代表來源、風險資格或 fleet capacity 驗收。需要 PowerShell 7。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Path,
    [string] $ExpectedVersion = '1.0.53.2',
    [string] $ExpectedRevision
)

$ErrorActionPreference = 'Stop'
$MaxBytes = 64 * 1024
$MaxDepth = 32

function Get-JsonProperty {
    param([System.Text.Json.JsonElement] $Element, [string] $Name)
    $value = [System.Text.Json.JsonElement]::new()
    if ($Element.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) { return $null }
    if ($Element.TryGetProperty($Name, [ref] $value)) { return $value }
    return $null
}

function Test-NoDuplicateJsonProperties {
    param([System.Text.Json.JsonElement] $Element)
    if ($Element.ValueKind -eq [System.Text.Json.JsonValueKind]::Object) {
        $names = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
        foreach ($property in $Element.EnumerateObject()) {
            if (-not $names.Add($property.Name)) { return $false }
            if (-not (Test-NoDuplicateJsonProperties $property.Value)) { return $false }
        }
    } elseif ($Element.ValueKind -eq [System.Text.Json.JsonValueKind]::Array) {
        foreach ($item in $Element.EnumerateArray()) {
            if (-not (Test-NoDuplicateJsonProperties $item)) { return $false }
        }
    }
    return $true
}

function Test-JsonString {
    param([System.Text.Json.JsonElement] $Element, [string[]] $AllowedValues)
    if ($Element.ValueKind -ne [System.Text.Json.JsonValueKind]::String) { return $false }
    $value = $Element.GetString()
    return -not [string]::IsNullOrWhiteSpace($value) -and
        ($null -eq $AllowedValues -or $value -cin $AllowedValues)
}

function Test-JsonNonNegativeInteger {
    param([System.Text.Json.JsonElement] $Element, [bool] $AllowNull = $false,
        [long] $Maximum = [long]::MaxValue)
    if ($Element.ValueKind -eq [System.Text.Json.JsonValueKind]::Null) { return $AllowNull }
    if ($Element.ValueKind -ne [System.Text.Json.JsonValueKind]::Number) { return $false }
    $value = [long]0
    return $Element.TryGetInt64([ref] $value) -and $value -ge 0 -and $value -le $Maximum
}

function Test-JsonBoolean {
    param([System.Text.Json.JsonElement] $Element)
    return $Element.ValueKind -eq [System.Text.Json.JsonValueKind]::True -or
        $Element.ValueKind -eq [System.Text.Json.JsonValueKind]::False
}

function Test-StorageRowShape {
    param([System.Text.Json.JsonElement] $Rows, [string] $Kind)
    if ($Rows.ValueKind -ne [System.Text.Json.JsonValueKind]::Array -or $Rows.GetArrayLength() -gt 16) { return $false }
    foreach ($row in $Rows.EnumerateArray()) {
        if ($row.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) { return $false }
        if ($Kind -eq 'file') {
            $role = Get-JsonProperty $row 'role'
            $maximumKind = Get-JsonProperty $row 'maximum_kind'
            if ($null -eq $role -or -not (Test-JsonString $role @('database-pages','database-file','write-ahead-log','shared-memory','data','log','unknown')) -or
                $null -eq $maximumKind -or -not (Test-JsonString $maximumKind @('bounded','unbounded','fixed-at-current','unknown'))) { return $false }
            foreach ($name in @('allocated_bytes','used_bytes','maximum_bytes','growth_bytes','growth_percent')) {
                $value = Get-JsonProperty $row $name
                $maximum = if ($name -eq 'growth_percent') { [long]100 } else { [long]::MaxValue }
                if ($null -eq $value -or -not (Test-JsonNonNegativeInteger $value -AllowNull:$true -Maximum $maximum)) { return $false }
            }
        } else {
            $role = Get-JsonProperty $row 'role'
            if ($null -eq $role -or -not (Test-JsonString $role @('owned-data-root','data','log','unknown'))) { return $false }
            foreach ($name in @('total_bytes','available_bytes')) {
                $value = Get-JsonProperty $row $name
                if ($null -eq $value -or -not (Test-JsonNonNegativeInteger $value -AllowNull:$true)) { return $false }
            }
        }
    }
    return $true
}

function Fail-Safely {
    param([string] $Code)
    Write-Output "Result: FAIL ($Code)"
    exit 1
}

if ($ExpectedVersion -notmatch '^\d+\.\d+(?:\.\d+){0,2}$') {
    Fail-Safely 'InvalidExpectedVersion'
}
if ($ExpectedRevision -and $ExpectedRevision -notmatch '^[0-9a-fA-F]{40}$') {
    Fail-Safely 'InvalidExpectedRevision'
}

try {
    $file = Get-Item -LiteralPath $Path -ErrorAction Stop
    if ($file.PSIsContainer) { Fail-Safely 'InputIsNotAFile' }
    if ($file.Length -gt $MaxBytes) { Fail-Safely 'Oversize' }
    $stream = [System.IO.FileStream]::new($file.FullName, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
    try {
        $buffer = [byte[]]::new($MaxBytes + 1)
        $offset = 0
        while ($offset -lt $buffer.Length) {
            $read = $stream.Read($buffer, $offset, $buffer.Length - $offset)
            if ($read -eq 0) { break }
            $offset += $read
        }
        if ($offset -gt $MaxBytes) { Fail-Safely 'Oversize' }
        $bytes = [byte[]]::new($offset)
        [Array]::Copy($buffer, $bytes, $offset)
    } finally {
        $stream.Dispose()
    }
    $sha256 = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
    $utf8 = [System.Text.UTF8Encoding]::new($false, $true)
    $jsonText = $utf8.GetString($bytes)
    if ($jsonText.Length -gt 0 -and $jsonText[0] -eq [char]0xFEFF) { Fail-Safely 'InvalidUtf8Json' }
    $options = [System.Text.Json.JsonDocumentOptions]::new()
    $options.MaxDepth = $MaxDepth
    $document = [System.Text.Json.JsonDocument]::Parse($jsonText, $options)
} catch {
    Fail-Safely 'UnreadableOrMalformedJson'
}

try {
    $root = $document.RootElement
    if ($root.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) { Fail-Safely 'RootMustBeObject' }
    if (-not (Test-NoDuplicateJsonProperties $root)) { Fail-Safely 'DuplicateJsonProperty' }

    $schema = Get-JsonProperty $root 'schema_version'
    $build = Get-JsonProperty $root 'build_version'
    $targets = Get-JsonProperty $root 'targets'
    $rootStatus = Get-JsonProperty $root 'status'
    if ($null -eq $schema -or $schema.ValueKind -ne [System.Text.Json.JsonValueKind]::String -or $schema.GetString() -ne '1.0.0') { Fail-Safely 'UnsupportedSchema' }
    if ($null -eq $build -or $build.ValueKind -ne [System.Text.Json.JsonValueKind]::String) { Fail-Safely 'MissingBuildVersion' }
    if ($null -eq $targets -or $targets.ValueKind -ne [System.Text.Json.JsonValueKind]::Array) { Fail-Safely 'InvalidTargetsShape' }
    if ($null -eq $rootStatus -or $rootStatus.ValueKind -ne [System.Text.Json.JsonValueKind]::String) { Fail-Safely 'InvalidRootStatus' }

    $buildText = $build.GetString()
    if ($buildText -notmatch '^(?<version>\d+\.\d+(?:\.\d+){0,2})\+(?<revision>[0-9a-fA-F]{40})$') { Fail-Safely 'MissingOrInvalidBuildSuffix' }
    $version = $Matches.version
    $revision = $Matches.revision.ToLowerInvariant()
    if ($version -ne $ExpectedVersion) { Fail-Safely 'VersionMismatch' }
    if ($ExpectedRevision -and $revision -ne $ExpectedRevision.ToLowerInvariant()) { Fail-Safely 'RevisionMismatch' }

    $metadataIncomplete = $false
    $deployment = Get-JsonProperty $root 'deployment_resources'
    if ($null -eq $deployment -or $deployment.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) { Fail-Safely 'InvalidDeploymentResourcesShape' }
    $processorCount = Get-JsonProperty $deployment 'processor_count'
    $processArchitecture = Get-JsonProperty $deployment 'process_architecture'
    $osArchitecture = Get-JsonProperty $deployment 'os_architecture'
    $dotnetDescription = Get-JsonProperty $deployment 'dotnet_framework_description'
    $runtimeMemory = Get-JsonProperty $deployment 'total_available_memory_bytes'
    $workingSet = Get-JsonProperty $deployment 'working_set_bytes'
    $sqlHostResources = Get-JsonProperty $deployment 'sql_host_resources'
    if ($null -eq $processorCount -or -not (Test-JsonNonNegativeInteger $processorCount) -or
        $null -eq $processArchitecture -or -not (Test-JsonString $processArchitecture $null) -or
        $null -eq $osArchitecture -or -not (Test-JsonString $osArchitecture $null) -or
        $null -eq $dotnetDescription -or -not (Test-JsonString $dotnetDescription $null) -or
        $null -eq $runtimeMemory -or -not (Test-JsonNonNegativeInteger $runtimeMemory) -or
        $null -eq $workingSet -or -not (Test-JsonNonNegativeInteger $workingSet) -or
        $null -eq $sqlHostResources -or -not (Test-JsonString $sqlHostResources $null)) {
        Fail-Safely 'InvalidDeploymentResourcesShape'
    }
    $processorCountValue = [long]0
    $runtimeMemoryValue = [long]0
    $workingSetValue = [long]0
    [void]$processorCount.TryGetInt64([ref] $processorCountValue)
    [void]$runtimeMemory.TryGetInt64([ref] $runtimeMemoryValue)
    [void]$workingSet.TryGetInt64([ref] $workingSetValue)
    if ($processorCountValue -eq 0 -or $runtimeMemoryValue -eq 0 -or $workingSetValue -eq 0 -or
        $processArchitecture.GetString() -eq 'unknown' -or $osArchitecture.GetString() -eq 'unknown' -or
        $dotnetDescription.GetString() -eq 'unknown') { $metadataIncomplete = $true }
    if ($processArchitecture.GetString() -notin @('X86','X64','Arm','Arm64','Wasm','unknown') -or
        $osArchitecture.GetString() -notin @('X86','X64','Arm','Arm64','Wasm','unknown') -or
        ($dotnetDescription.GetString() -ne 'unknown' -and $dotnetDescription.GetString() -notmatch '^\.NET(?: Framework)? \d+(?:\.\d+){1,3}$')) {
        Fail-Safely 'InvalidDeploymentResourcesShape'
    }
    $expectedSqlHostResources = 'unknown (remote database host resources not observable from app process)'
    if ($sqlHostResources.GetString() -ne $expectedSqlHostResources) { Fail-Safely 'InvalidSqlHostResourcesClaim' }

    $storageProviderElement = Get-JsonProperty $root 'storage_provider'
    if ($null -eq $storageProviderElement -or -not (Test-JsonString $storageProviderElement @('Sqlite','SqlServer','unknown'))) { Fail-Safely 'InvalidStorageProviderShape' }
    $storageProvider = $storageProviderElement.GetString()
    $efProviderElement = Get-JsonProperty $root 'ef_core_provider'
    $expectedEfProvider = switch ($storageProvider) {
        'Sqlite' { 'Microsoft.EntityFrameworkCore.Sqlite' }
        'SqlServer' { 'Microsoft.EntityFrameworkCore.SqlServer' }
        default { $null }
    }
    if ($null -eq $expectedEfProvider) {
        if ($null -ne $efProviderElement -and $efProviderElement.ValueKind -ne [System.Text.Json.JsonValueKind]::Null) { Fail-Safely 'StorageProviderMismatch' }
    } elseif ($null -eq $efProviderElement -or -not (Test-JsonString $efProviderElement @($expectedEfProvider))) {
        Fail-Safely 'StorageProviderMismatch'
    }

    $storage = Get-JsonProperty $root 'storage_environment'
    if ($null -eq $storage -or $storage.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) { Fail-Safely 'InvalidStorageEnvironmentShape' }
    $storageStatus = Get-JsonProperty $storage 'status'
    $factsProvider = Get-JsonProperty $storage 'provider'
    $engineVersion = Get-JsonProperty $storage 'engine_version'
    $edition = Get-JsonProperty $storage 'edition'
    $engineEdition = Get-JsonProperty $storage 'engine_edition'
    $fileStatus = Get-JsonProperty $storage 'file_status'
    $volumeStatus = Get-JsonProperty $storage 'volume_status'
    $localFileStatus = Get-JsonProperty $storage 'local_file_status'
    $logStatus = Get-JsonProperty $storage 'log_status'
    $attempted = Get-JsonProperty $storage 'queries_attempted'
    $succeeded = Get-JsonProperty $storage 'queries_succeeded'
    $timedOut = Get-JsonProperty $storage 'timed_out'
    $cancelled = Get-JsonProperty $storage 'cancelled'
    $fileTruncated = Get-JsonProperty $storage 'file_rows_truncated'
    $volumeTruncated = Get-JsonProperty $storage 'volume_rows_truncated'
    $fileRows = Get-JsonProperty $storage 'file_capacities'
    $volumeRows = Get-JsonProperty $storage 'volume_capacities'
    $logAggregate = Get-JsonProperty $storage 'database_log_aggregate'
    if ($null -eq $storageStatus -or -not (Test-JsonString $storageStatus @('measured','partial','timeout','cancelled','unknown')) -or
        $null -eq $factsProvider -or -not (Test-JsonString $factsProvider @('Sqlite','SqlServer','unknown')) -or
        $null -eq $engineVersion -or -not (Test-JsonString $engineVersion $null) -or
        $null -eq $edition -or -not (Test-JsonString $edition @('Enterprise','Standard','Developer','Express','Web','Evaluation','Azure','unknown')) -or
        $null -eq $engineEdition -or -not (Test-JsonString $engineEdition @('personal-or-desktop','standard','enterprise','express','azure-sql-database','azure-synapse','azure-sql-managed-instance','unknown')) -or
        $null -eq $fileStatus -or -not (Test-JsonString $fileStatus @('measured','partial','unknown')) -or
        $null -eq $volumeStatus -or -not (Test-JsonString $volumeStatus @('measured','partial','unknown')) -or
        $null -eq $localFileStatus -or -not (Test-JsonString $localFileStatus @('measured','partial','unknown')) -or
        $null -eq $logStatus -or -not (Test-JsonString $logStatus @('measured','partial','unknown')) -or
        $null -eq $attempted -or -not (Test-JsonNonNegativeInteger $attempted) -or
        $null -eq $succeeded -or -not (Test-JsonNonNegativeInteger $succeeded) -or
        $null -eq $timedOut -or -not (Test-JsonBoolean $timedOut) -or
        $null -eq $cancelled -or -not (Test-JsonBoolean $cancelled) -or
        $null -eq $fileTruncated -or -not (Test-JsonBoolean $fileTruncated) -or
        $null -eq $volumeTruncated -or -not (Test-JsonBoolean $volumeTruncated) -or
        $null -eq $fileRows -or -not (Test-StorageRowShape $fileRows 'file') -or
        $null -eq $volumeRows -or -not (Test-StorageRowShape $volumeRows 'volume') -or
        $null -eq $logAggregate) { Fail-Safely 'InvalidStorageEnvironmentShape' }
    $attemptedValue = [long]0
    $succeededValue = [long]0
    [void]$attempted.TryGetInt64([ref] $attemptedValue)
    [void]$succeeded.TryGetInt64([ref] $succeededValue)
    if ($attemptedValue -gt 3 -or $succeededValue -gt 3 -or $succeededValue -gt $attemptedValue) { Fail-Safely 'InvalidStorageQueryCounts' }
    if ($logAggregate.ValueKind -ne [System.Text.Json.JsonValueKind]::Null) {
        if ($logAggregate.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) { Fail-Safely 'InvalidStorageEnvironmentShape' }
        foreach ($name in @('allocated_bytes','used_bytes')) {
            $value = Get-JsonProperty $logAggregate $name
            if ($null -eq $value -or -not (Test-JsonNonNegativeInteger $value -AllowNull:$true)) { Fail-Safely 'InvalidStorageEnvironmentShape' }
        }
    }
    if ($factsProvider.GetString() -ne $storageProvider) { Fail-Safely 'StorageProviderMismatch' }
    $statusText = $storageStatus.GetString()
    $engineVersionText = $engineVersion.GetString()
    if ($engineVersionText -ne 'unknown' -and $engineVersionText -notmatch '^\d{1,3}(?:\.\d{1,5}){1,3}$') { Fail-Safely 'InvalidStorageEnvironmentShape' }
    if ($storageProvider -eq 'Sqlite' -and ($edition.GetString() -ne 'unknown' -or $engineEdition.GetString() -ne 'unknown')) { Fail-Safely 'StorageProviderMismatch' }
    if ($storageProvider -eq 'SqlServer' -and ($localFileStatus.GetString() -ne 'unknown' -or
        $edition.GetString() -eq 'unknown' -or $engineEdition.GetString() -eq 'unknown')) { $metadataIncomplete = $true }
    if (($statusText -eq 'measured' -and ($attemptedValue -eq 0 -or $succeededValue -ne $attemptedValue)) -or
        ($statusText -eq 'timeout' -and -not $timedOut.GetBoolean()) -or
        ($statusText -eq 'cancelled' -and -not $cancelled.GetBoolean())) { Fail-Safely 'InvalidStorageMetadataState' }
    $storageIncomplete = $statusText -ne 'measured' -or $timedOut.GetBoolean() -or $cancelled.GetBoolean() -or
        $fileTruncated.GetBoolean() -or $volumeTruncated.GetBoolean() -or
        $fileStatus.GetString() -ne 'measured' -or $volumeStatus.GetString() -ne 'measured' -or
        $fileRows.GetArrayLength() -eq 0 -or $volumeRows.GetArrayLength() -eq 0
    if ($storageProvider -eq 'Sqlite') {
        $storageIncomplete = $storageIncomplete -or $localFileStatus.GetString() -ne 'measured'
    } elseif ($storageProvider -eq 'SqlServer') {
        $storageIncomplete = $storageIncomplete -or $logStatus.GetString() -ne 'measured'
        $metadataIncomplete = $true # remote SQL host physical resources remain explicitly unobservable
    } else {
        $metadataIncomplete = $true
    }
    foreach ($row in $fileRows.EnumerateArray()) {
        $roleText = (Get-JsonProperty $row 'role').GetString()
        if ($roleText -eq 'unknown') { $metadataIncomplete = $true }
        elseif ($storageProvider -eq 'Sqlite' -and $roleText -notin @('database-pages','database-file','write-ahead-log','shared-memory')) { Fail-Safely 'StorageProviderMismatch' }
        elseif ($storageProvider -eq 'SqlServer' -and $roleText -notin @('data','log')) { Fail-Safely 'StorageProviderMismatch' }
    }
    foreach ($row in $volumeRows.EnumerateArray()) {
        $roleText = (Get-JsonProperty $row 'role').GetString()
        if ($roleText -eq 'unknown') { $metadataIncomplete = $true }
        elseif ($storageProvider -eq 'Sqlite' -and $roleText -ne 'owned-data-root') { Fail-Safely 'StorageProviderMismatch' }
        elseif ($storageProvider -eq 'SqlServer' -and $roleText -notin @('data','log')) { Fail-Safely 'StorageProviderMismatch' }
    }
    if ($engineVersionText -eq 'unknown') { $metadataIncomplete = $true }
    if ($storageIncomplete) { $metadataIncomplete = $true }

    if ($targets.GetArrayLength() -gt 3) { Fail-Safely 'InvalidTargetsCount' }
    $targetCount = $targets.GetArrayLength()
    $rawCount = 0
    $channelIdCount = 0
    $rawShapeValid = $true
    $sourceIncomplete = $false
    foreach ($target in $targets.EnumerateArray()) {
        if ($target.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) { $rawShapeValid = $false; break }
        $targetStatusElement = Get-JsonProperty $target 'status'
        if ($null -eq $targetStatusElement -or $targetStatusElement.ValueKind -ne [System.Text.Json.JsonValueKind]::String) { $rawShapeValid = $false; break }
        $targetStatus = $targetStatusElement.GetString()
        if ($targetStatus -notin @('ok', 'partial', 'missing', 'timeout', 'unknown', 'error', 'truncated')) { $sourceIncomplete = $true }
        elseif ($targetStatus -ne 'ok') { $sourceIncomplete = $true }
        $raw = Get-JsonProperty $target 'identity_preserving_raw_history'
        if ($null -eq $raw) { $rawShapeValid = $false; break }
        if ($raw.ValueKind -eq [System.Text.Json.JsonValueKind]::Null -and $targetStatus -eq 'missing') {
            $sourceIncomplete = $true
            continue
        }
        if ($raw.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) { $rawShapeValid = $false; break }
        $status = Get-JsonProperty $raw 'Status'
        $samples = Get-JsonProperty $raw 'Samples'
        $sampleCount = Get-JsonProperty $raw 'SampleCount'
        $sampleCountValue = 0
        if ($null -eq $status -or $status.ValueKind -ne [System.Text.Json.JsonValueKind]::String -or
            $null -eq $samples -or $samples.ValueKind -ne [System.Text.Json.JsonValueKind]::Array -or
            $null -eq $sampleCount -or $sampleCount.ValueKind -ne [System.Text.Json.JsonValueKind]::Number -or
            -not $sampleCount.TryGetInt32([ref] $sampleCountValue) -or $sampleCountValue -lt 0) {
            $rawShapeValid = $false; break
        }
        $rawStatus = $status.GetString()
        if ($rawStatus -notin @('ok', 'partial', 'missing', 'timeout', 'unknown', 'error', 'truncated')) { $rawShapeValid = $false; break }
        $rawSampleCount = $samples.GetArrayLength()
        if ($rawSampleCount -gt 3 -or $rawSampleCount -ne [Math]::Min($sampleCountValue, 3)) { $rawShapeValid = $false; break }
        if ($rawStatus -ne 'ok') { $sourceIncomplete = $true }
        if ($rawStatus -eq 'ok' -and $rawSampleCount -eq 0) { $sourceIncomplete = $true }
        $rawCount++
        foreach ($sample in $samples.EnumerateArray()) {
            if ($sample.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) { $rawShapeValid = $false; break }
            $channels = Get-JsonProperty $sample 'Channels'
            if ($null -eq $channels -or $channels.ValueKind -ne [System.Text.Json.JsonValueKind]::Array -or $channels.GetArrayLength() -gt 8) { $rawShapeValid = $false; break }
            if ($channels.GetArrayLength() -eq 0 -and ($rawStatus -eq 'ok' -or $sampleCountValue -gt 0)) { $sourceIncomplete = $true }
            foreach ($channel in $channels.EnumerateArray()) {
                if ($channel.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) { $rawShapeValid = $false; break }
                $channelId = Get-JsonProperty $channel 'ChannelId'
                if ($null -eq $channelId -or $channelId.ValueKind -ne [System.Text.Json.JsonValueKind]::String -or [string]::IsNullOrWhiteSpace($channelId.GetString())) { $rawShapeValid = $false; break }
                $channelIdCount++
            }
            if (-not $rawShapeValid) { break }
        }
        if (-not $rawShapeValid) { break }
    }
    if (-not $rawShapeValid) { Fail-Safely 'RawChannelIdentityMissingOrWrongShape' }

    $sourceStatus = $rootStatus.GetString()
    if ($sourceStatus -notin @('ok', 'partial', 'unknown', 'error', 'truncated')) { $sourceStatus = 'unknown' }

    Write-Output "Version: $version"
    Write-Output "GitRevision: $revision"
    Write-Output "FileSHA256: $sha256"
    Write-Output "TargetsCount: $targetCount"
    Write-Output "RawChannelIdentityTargets: $rawCount"
    Write-Output "RawChannelIds: $channelIdCount"
    Write-Output "DeploymentProcessorCount: $processorCountValue"
    Write-Output "RuntimeGcAvailableMemoryBytes: $runtimeMemoryValue"
    Write-Output "ProcessWorkingSetBytes: $workingSetValue"
    Write-Output "StorageProvider: $storageProvider"
    Write-Output "StorageMetadataStatus: $statusText"
    Write-Output "StorageQueryCounts: $succeededValue/$attemptedValue"
    Write-Output "StorageFileRows: $($fileRows.GetArrayLength())"
    Write-Output "StorageVolumeRows: $($volumeRows.GetArrayLength())"
    Write-Output "SqlHostResources: $(if ($storageProvider -eq 'Sqlite') { 'not-applicable' } else { 'unknown' })"
    if ($targetCount -eq 0 -or $sourceIncomplete -or $metadataIncomplete -or $sourceStatus -in @('partial', 'unknown', 'error', 'truncated')) {
        Write-Output 'Result: INCOMPLETE (來源核對未齊；此結果只核對探測交接完整性，不代表來源、風險資格或 fleet capacity 驗收)'
        exit 2
    }
    Write-Output 'Result: HANDOFF_INTEGRITY_OK (僅核對探測交接完整性；不代表來源、風險資格或 fleet capacity 驗收)'
    exit 0
} catch {
    Fail-Safely 'ValidationFailed'
} finally {
    if ($null -ne $document) { $document.Dispose() }
}
