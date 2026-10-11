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

function Test-AllowedJsonProperties {
    param([System.Text.Json.JsonElement] $Element, [string[]] $AllowedNames)
    if ($Element.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) { return $false }
    foreach ($property in $Element.EnumerateObject()) {
        if ($property.Name -cnotin $AllowedNames) { return $false }
    }
    return $true
}

function Get-NativePrimaryId {
    param([System.Text.Json.JsonElement] $Element, [bool] $AllowNull = $true)
    if ($Element.ValueKind -eq [System.Text.Json.JsonValueKind]::Null) {
        if ($AllowNull) { return [pscustomobject]@{ Valid = $true; Present = $false; Value = [long]0 } }
        return [pscustomobject]@{ Valid = $false; Present = $false; Value = [long]0 }
    }
    if ($Element.ValueKind -ne [System.Text.Json.JsonValueKind]::String) {
        return [pscustomobject]@{ Valid = $false; Present = $true; Value = [long]0 }
    }
    $text = $Element.GetString()
    $value = [long]0
    $valid = $text -match '^\d{1,20}$' -and
        [long]::TryParse($text, [System.Globalization.NumberStyles]::None,
            [System.Globalization.CultureInfo]::InvariantCulture, [ref] $value) -and $value -ge 0
    return [pscustomobject]@{ Valid = $valid; Present = $true; Value = $value }
}

function Test-OptionalNativeChannelId {
    param([System.Text.Json.JsonElement] $Element)
    if ($Element.ValueKind -eq [System.Text.Json.JsonValueKind]::Null) { return $true }
    if ($Element.ValueKind -ne [System.Text.Json.JsonValueKind]::String) { return $false }
    $text = $Element.GetString()
    if ($text.Length -gt 21 -or $text -notmatch '^(?:0|-?[1-9]\d*)$') { return $false }
    $value = [long]0
    return [long]::TryParse($text, [System.Globalization.NumberStyles]::AllowLeadingSign,
        [System.Globalization.CultureInfo]::InvariantCulture, [ref] $value) -and
        $value.ToString([System.Globalization.CultureInfo]::InvariantCulture) -ceq $text
}

function Test-NullableJsonBoolean {
    param([System.Text.Json.JsonElement] $Element)
    return $Element.ValueKind -eq [System.Text.Json.JsonValueKind]::Null -or (Test-JsonBoolean $Element)
}

function Test-OptionalBoundedString {
    param([System.Text.Json.JsonElement] $Element, [int] $MaximumLength,
        [string[]] $AllowedValues = $null, [bool] $AllowNull = $true)
    if ($Element.ValueKind -eq [System.Text.Json.JsonValueKind]::Null) { return $AllowNull }
    if ($Element.ValueKind -ne [System.Text.Json.JsonValueKind]::String) { return $false }
    $value = $Element.GetString()
    return $value.Length -le $MaximumLength -and
        ($null -eq $AllowedValues -or $value -cin $AllowedValues)
}

function Test-OptionalUtcTimestamp {
    param([System.Text.Json.JsonElement] $Element)
    if ($Element.ValueKind -eq [System.Text.Json.JsonValueKind]::Null) { return $true }
    if ($Element.ValueKind -ne [System.Text.Json.JsonValueKind]::String) { return $false }
    $value = $Element.GetString()
    $parsed = [DateTimeOffset]::MinValue
    return $value.Length -le 40 -and
        [DateTimeOffset]::TryParse($value, [System.Globalization.CultureInfo]::InvariantCulture,
            [System.Globalization.DateTimeStyles]::RoundtripKind, [ref] $parsed) -and
        $parsed.Offset -eq [TimeSpan]::Zero
}

function Test-NativeSnapshotFields {
    param([System.Text.Json.JsonElement] $Snapshot, [ref] $HasNativeFields,
        [ref] $SnapshotId, [ref] $IsIncomplete)
    if ($Snapshot.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) { return $false }
    $allowed = @('status','elapsed_ms','requested_at_utc','received_at_utc','returned_fields',
        'requested_fields','missing_requested_fields','missing_fields','unrecognized_fields_count',
        'fields','has_unfiltered_objects','truncated','error','native_primary_channel_id',
        'primary_channel_field_name','reported_at_sample_time')
    if (-not (Test-AllowedJsonProperties $Snapshot $allowed)) { return $false }
    $id = Get-JsonProperty $Snapshot 'native_primary_channel_id'
    $fieldName = Get-JsonProperty $Snapshot 'primary_channel_field_name'
    $reportedAtSampleTime = Get-JsonProperty $Snapshot 'reported_at_sample_time'
    $HasNativeFields.Value = $null -ne $id -or $null -ne $fieldName -or $null -ne $reportedAtSampleTime
    if (-not $HasNativeFields.Value) { return $true }
    if ($null -eq $reportedAtSampleTime -or -not (Test-JsonBoolean $reportedAtSampleTime) -or
        $reportedAtSampleTime.GetBoolean()) { return $false }
    $parsedId = if ($null -eq $id) {
        [pscustomobject]@{ Valid = $true; Present = $false; Value = [long]0 }
    } else {
        Get-NativePrimaryId $id
    }
    if (-not $parsedId.Valid) { return $false }
    $SnapshotId.Value = $parsedId
    if ($null -ne $fieldName -and -not (Test-OptionalBoundedString $fieldName 32 @('primarychannel','primarychannel_raw'))) { return $false }
    if ($parsedId.Present -and ($null -eq $fieldName -or $fieldName.ValueKind -ne [System.Text.Json.JsonValueKind]::String)) { return $false }
    if (-not $parsedId.Present -or $null -eq $fieldName -or
        $fieldName.ValueKind -eq [System.Text.Json.JsonValueKind]::Null) { $IsIncomplete.Value = $true }
    return $true
}

function Test-NativePrimaryCapability {
    param([System.Text.Json.JsonElement] $Capability, [string] $TargetAlias,
        [bool] $HasSnapshotNativeFields, $SnapshotId, [ref] $IsDiagnosticComplete, [ref] $IsConflict)
    $allowed = @('status','property_support','endpoint','requested_property_name','requested_object_alias',
        'primary_channel_id','matches_raw_observed_channel_ids','matches_snapshot_primary_channel_id',
        'property_value','response_status','http_status','http_date_utc','response_format',
        'requested_at_utc','received_at_utc','elapsed_ms','source_version',
        'authorizes_formal_profile','error')
    if (-not (Test-AllowedJsonProperties $Capability $allowed)) { return $false }

    $status = Get-JsonProperty $Capability 'status'
    $support = Get-JsonProperty $Capability 'property_support'
    $endpoint = Get-JsonProperty $Capability 'endpoint'
    $propertyName = Get-JsonProperty $Capability 'requested_property_name'
    $alias = Get-JsonProperty $Capability 'requested_object_alias'
    $primaryId = Get-JsonProperty $Capability 'primary_channel_id'
    $rawMatch = Get-JsonProperty $Capability 'matches_raw_observed_channel_ids'
    $snapshotMatch = Get-JsonProperty $Capability 'matches_snapshot_primary_channel_id'
    $propertyValue = Get-JsonProperty $Capability 'property_value'
    $responseStatus = Get-JsonProperty $Capability 'response_status'
    $httpStatus = Get-JsonProperty $Capability 'http_status'
    $httpDate = Get-JsonProperty $Capability 'http_date_utc'
    $responseFormat = Get-JsonProperty $Capability 'response_format'
    $requestedAt = Get-JsonProperty $Capability 'requested_at_utc'
    $receivedAt = Get-JsonProperty $Capability 'received_at_utc'
    $elapsed = Get-JsonProperty $Capability 'elapsed_ms'
    $sourceVersion = Get-JsonProperty $Capability 'source_version'
    $authorizes = Get-JsonProperty $Capability 'authorizes_formal_profile'
    $error = Get-JsonProperty $Capability 'error'
    foreach ($value in @($status,$support,$endpoint,$propertyName,$alias,$primaryId,$rawMatch,$snapshotMatch,
            $responseStatus,$httpStatus,$responseFormat,$elapsed,$sourceVersion,$authorizes)) {
        if ($null -eq $value) { return $false }
    }
    if (-not (Test-JsonString $status @('ok','unknown','timeout','error')) -or
        -not (Test-JsonString $support @('supported','unknown')) -or
        -not (Test-JsonString $endpoint @('getobjectproperty.htm')) -or
        -not (Test-JsonString $propertyName @('primarychannel')) -or
        -not (Test-JsonString $alias @($TargetAlias)) -or
        -not (Test-JsonString $responseStatus @('success','timeout','error','unknown')) -or
        -not (Test-JsonString $responseFormat @('xml','malformed_xml','invalid_xml','unexpected_xml','unknown')) -or
        -not (Test-OptionalBoundedString $httpStatus 8 @('unknown','2xx','100','101','102','103','200','201','202','203','204','205','206','300','301','302','303','304','305','307','308','400','401','402','403','404','405','406','407','408','409','410','411','412','413','414','415','416','417','418','421','422','423','424','425','426','428','429','431','451','500','501','502','503','504','505','506','507','508','510','511')) -or
        ($null -ne $httpDate -and -not (Test-OptionalUtcTimestamp $httpDate)) -or
        ($null -ne $requestedAt -and -not (Test-OptionalUtcTimestamp $requestedAt)) -or
        ($null -ne $receivedAt -and -not (Test-OptionalUtcTimestamp $receivedAt)) -or
        $sourceVersion.ValueKind -ne [System.Text.Json.JsonValueKind]::String -or
        -not (Test-NullableJsonBoolean $rawMatch) -or
        -not (Test-NullableJsonBoolean $snapshotMatch) -or
        -not (Test-JsonBoolean $authorizes) -or $authorizes.GetBoolean() -or
        $elapsed.ValueKind -ne [System.Text.Json.JsonValueKind]::Number) { return $false }
    $elapsedValue = 0.0
    if (-not $elapsed.TryGetDouble([ref] $elapsedValue) -or [double]::IsNaN($elapsedValue) -or
        [double]::IsInfinity($elapsedValue) -or $elapsedValue -lt 0) { return $false }
    $sourceVersionText = $sourceVersion.GetString()
    if ($sourceVersionText.Length -gt 32 -or
        ($sourceVersionText -ne 'unknown' -and $sourceVersionText -notmatch '^\d{1,3}(?:\.\d{1,5}){1,3}\+?$')) { return $false }
    if ($null -ne $httpDate -and -not (Test-OptionalUtcTimestamp $httpDate)) { return $false }
    if ($null -ne $error -and -not (Test-OptionalBoundedString $error 256)) { return $false }

    $parsedPrimary = Get-NativePrimaryId $primaryId
    if (-not $parsedPrimary.Valid) { return $false }
    $parsedProperty = if ($null -eq $propertyValue) {
        [pscustomobject]@{ Valid = $true; Present = $false; Value = [long]0 }
    } else {
        Get-NativePrimaryId $propertyValue
    }
    if (-not $parsedProperty.Valid) { return $false }
    if ($parsedPrimary.Present -ne $parsedProperty.Present -or
        $parsedPrimary.Present -and $parsedPrimary.Value -ne $parsedProperty.Value) { return $false }

    if ($status.GetString() -eq 'ok') {
        if ($support.GetString() -ne 'supported' -or $responseStatus.GetString() -ne 'success' -or
            $httpStatus.GetString() -ne '2xx' -or $responseFormat.GetString() -ne 'xml' -or
            -not $parsedPrimary.Present -or -not $parsedProperty.Present -or
            $null -eq $requestedAt -or $null -eq $receivedAt -or
            -not (Test-OptionalUtcTimestamp $requestedAt) -or
            $requestedAt.ValueKind -ne [System.Text.Json.JsonValueKind]::String -or
            $receivedAt.ValueKind -ne [System.Text.Json.JsonValueKind]::String) { return $false }
        if ($HasSnapshotNativeFields -and $SnapshotId.Present) {
            if ($snapshotMatch.ValueKind -eq [System.Text.Json.JsonValueKind]::Null) { return $false }
            if ($snapshotMatch.GetBoolean() -ne ($parsedPrimary.Value -eq $SnapshotId.Value)) { return $false }
        } elseif ($snapshotMatch.ValueKind -ne [System.Text.Json.JsonValueKind]::Null) {
            return $false
        }
        if ($rawMatch.ValueKind -eq [System.Text.Json.JsonValueKind]::False -or
            $rawMatch.ValueKind -eq [System.Text.Json.JsonValueKind]::Null -or
            $snapshotMatch.ValueKind -eq [System.Text.Json.JsonValueKind]::False -or
            $snapshotMatch.ValueKind -eq [System.Text.Json.JsonValueKind]::Null) {
            $IsConflict.Value = $snapshotMatch.ValueKind -eq [System.Text.Json.JsonValueKind]::False -or
                $rawMatch.ValueKind -eq [System.Text.Json.JsonValueKind]::False
            $IsDiagnosticComplete.Value = $false
        } else {
            $IsDiagnosticComplete.Value = $true
        }
    } else {
        if ($support.GetString() -ne 'unknown' -or $parsedPrimary.Present -or
            $parsedProperty.Present -or
            $rawMatch.ValueKind -ne [System.Text.Json.JsonValueKind]::Null -or
            $snapshotMatch.ValueKind -ne [System.Text.Json.JsonValueKind]::Null) { return $false }
        $IsDiagnosticComplete.Value = $false
    }
    # A deadline timeout is evidence, not a schema defect. Keep the measured
    # elapsed duration but never treat a late response as a complete diagnostic.
    if ($elapsedValue -gt 30000) { $IsDiagnosticComplete.Value = $false }
    return $true
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
    $nativePrimaryCapabilityCount = 0
    $nativePrimaryDiagnosticCompleteCount = 0
    $nativePrimaryConflictCount = 0
    $rawShapeValid = $true
    $sourceIncomplete = $false
    foreach ($target in $targets.EnumerateArray()) {
        if ($target.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) { $rawShapeValid = $false; break }
        $targetStatusElement = Get-JsonProperty $target 'status'
        if ($null -eq $targetStatusElement -or $targetStatusElement.ValueKind -ne [System.Text.Json.JsonValueKind]::String) { $rawShapeValid = $false; break }
        $targetStatus = $targetStatusElement.GetString()
        if ($targetStatus -notin @('ok', 'partial', 'missing', 'timeout', 'unknown', 'error', 'truncated')) { $sourceIncomplete = $true }
        elseif ($targetStatus -ne 'ok') { $sourceIncomplete = $true }

        $targetAliasElement = Get-JsonProperty $target 'alias'
        if ($null -ne $targetAliasElement -and $targetAliasElement.ValueKind -ne [System.Text.Json.JsonValueKind]::String) {
            Fail-Safely 'InvalidNativePrimaryCapabilityShape'
        }
        $channelsElement = Get-JsonProperty $target 'channels'
        if ($null -ne $channelsElement -and $channelsElement.ValueKind -ne [System.Text.Json.JsonValueKind]::Null) {
            if ($channelsElement.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) { Fail-Safely 'InvalidChannelMetadataShape' }
            $channelRows = Get-JsonProperty $channelsElement 'rows'
            if ($null -ne $channelRows -and $channelRows.ValueKind -ne [System.Text.Json.JsonValueKind]::Null) {
                if ($channelRows.ValueKind -ne [System.Text.Json.JsonValueKind]::Array -or $channelRows.GetArrayLength() -gt 16) {
                    Fail-Safely 'InvalidChannelMetadataShape'
                }
                foreach ($channelRow in $channelRows.EnumerateArray()) {
                    if ($channelRow.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) { Fail-Safely 'InvalidChannelMetadataShape' }
                    $channelId = Get-JsonProperty $channelRow 'channel_id'
                    if ($null -ne $channelId -and -not (Test-OptionalNativeChannelId $channelId)) {
                        Fail-Safely 'InvalidChannelMetadataShape'
                    }
                    if ($null -ne $channelId -and $channelId.ValueKind -eq [System.Text.Json.JsonValueKind]::Null) {
                        $sourceIncomplete = $true
                    }
                }
            }
        }
        $snapshot = Get-JsonProperty $target 'snapshot'
        $hasSnapshotNativeFields = $false
        $snapshotId = [pscustomobject]@{ Present = $false; Value = [long]0 }
        if ($null -ne $snapshot -and $snapshot.ValueKind -eq [System.Text.Json.JsonValueKind]::Object) {
            $nativeMarker = $null -ne (Get-JsonProperty $snapshot 'native_primary_channel_id') -or
                $null -ne (Get-JsonProperty $snapshot 'primary_channel_field_name') -or
                $null -ne (Get-JsonProperty $snapshot 'reported_at_sample_time')
            if ($nativeMarker) {
                $snapshotIncomplete = $false
                if (-not (Test-NativeSnapshotFields $snapshot ([ref]$hasSnapshotNativeFields) ([ref]$snapshotId) ([ref]$snapshotIncomplete))) {
                    Fail-Safely 'InvalidNativeSnapshotShape'
                }
                if ($snapshotIncomplete) { $sourceIncomplete = $true }
            }
        } elseif ($null -ne $snapshot -and $snapshot.ValueKind -ne [System.Text.Json.JsonValueKind]::Null) {
            Fail-Safely 'InvalidNativeSnapshotShape'
        }

        $nativeCapability = Get-JsonProperty $target 'native_primary_capability'
        if ($null -ne $nativeCapability) {
            if ($nativeCapability.ValueKind -ne [System.Text.Json.JsonValueKind]::Object -or
                $null -eq $targetAliasElement -or $targetAliasElement.ValueKind -ne [System.Text.Json.JsonValueKind]::String) {
                Fail-Safely 'InvalidNativePrimaryCapabilityShape'
            }
            $diagnosticComplete = $false
            $nativeConflict = $false
            if (-not (Test-NativePrimaryCapability $nativeCapability $targetAliasElement.GetString() `
                    $hasSnapshotNativeFields $snapshotId ([ref]$diagnosticComplete) ([ref]$nativeConflict))) {
                Fail-Safely 'InvalidNativePrimaryCapabilityShape'
            }
            $nativePrimaryCapabilityCount++
            if ($diagnosticComplete) { $nativePrimaryDiagnosticCompleteCount++ }
            else { $sourceIncomplete = $true }
            if ($nativeConflict) { $nativePrimaryConflictCount++; $sourceIncomplete = $true }
        }
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

    $batchComplete = $false
    $batchFull100 = $false
    $legacyBatchComplete = $false
    # Keep legacy profile evidence readable and bounded independently from the new snapshot contract.
    $legacyBatch = Get-JsonProperty $root 'sensor_batch_identity'
    if ($null -ne $legacyBatch -and $legacyBatch.ValueKind -ne [System.Text.Json.JsonValueKind]::Null) {
        $legacyRequested = Get-JsonProperty $legacyBatch 'requested_aliases'
        $legacyReturned = Get-JsonProperty $legacyBatch 'returned_aliases'
        $legacyExact = Get-JsonProperty $legacyBatch 'exact_requested_set'
        $legacyAuthority = Get-JsonProperty $legacyBatch 'authorizes_formal_profile'
        if ($legacyBatch.ValueKind -ne [System.Text.Json.JsonValueKind]::Object -or
            $null -eq $legacyRequested -or $legacyRequested.ValueKind -ne [System.Text.Json.JsonValueKind]::Array -or $legacyRequested.GetArrayLength() -gt 5 -or
            $null -eq $legacyReturned -or $legacyReturned.ValueKind -ne [System.Text.Json.JsonValueKind]::Array -or $legacyReturned.GetArrayLength() -gt 6 -or
            $null -eq $legacyExact -or -not (Test-JsonBoolean $legacyExact) -or
            $null -eq $legacyAuthority -or $legacyAuthority.ValueKind -ne [System.Text.Json.JsonValueKind]::False) { Fail-Safely 'InvalidSensorBatchIdentityShape' }
        foreach ($alias in $legacyRequested.EnumerateArray()) {
            if ($alias.ValueKind -ne [System.Text.Json.JsonValueKind]::String -or $alias.GetString() -cnotmatch '^b[1-5]$') { Fail-Safely 'InvalidSensorBatchIdentityShape' }
        }
        foreach ($alias in $legacyReturned.EnumerateArray()) {
            if ($alias.ValueKind -ne [System.Text.Json.JsonValueKind]::String -or $alias.GetString() -cnotmatch '^(b[1-5]|foreign[1-6]|invalid)$') { Fail-Safely 'InvalidSensorBatchIdentityShape' }
        }
        $legacyRequestedSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
        $legacyReturnedSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
        foreach ($alias in $legacyRequested.EnumerateArray()) { [void]$legacyRequestedSet.Add($alias.GetString()) }
        $legacyReturnedUnique = $true
        foreach ($alias in $legacyReturned.EnumerateArray()) { if (-not $legacyReturnedSet.Add($alias.GetString())) { $legacyReturnedUnique = $false } }
        $legacyBatchComplete = $legacyRequestedSet.Count -ge 2 -and $legacyReturnedUnique -and $legacyReturnedSet.SetEquals($legacyRequestedSet) -and $legacyExact.ValueKind -eq [System.Text.Json.JsonValueKind]::True
        if ($legacyExact.ValueKind -eq [System.Text.Json.JsonValueKind]::True -and -not $legacyBatchComplete) { Fail-Safely 'SensorBatchExactSetContradiction' }
    }
    $batch = Get-JsonProperty $root 'snapshot_batch100_identity'
    if ($null -eq $batch -and $null -ne $legacyBatch) { $sourceIncomplete = $true }
    if ($null -ne $batch -and $batch.ValueKind -ne [System.Text.Json.JsonValueKind]::Null) {
        $batchStatus = Get-JsonProperty $batch 'status'
        $batchExact = Get-JsonProperty $batch 'exact_requested_set'
        $batchAuthority = Get-JsonProperty $batch 'authorizes_formal_profile'
        $batchReason = Get-JsonProperty $batch 'reason'
        $requestedAliases = Get-JsonProperty $batch 'requested_aliases'
        $returnedAliases = Get-JsonProperty $batch 'returned_aliases'
        $batchSchema = Get-JsonProperty $batch 'observation_schema_version'
        $isBatch100 = $null -ne $batchSchema -and $batchSchema.ValueKind -eq [System.Text.Json.JsonValueKind]::String -and $batchSchema.GetString() -ceq '2.0.0'
        $requestedLimit = if ($isBatch100) { 100 } else { 5 }
        $returnedLimit = if ($isBatch100) { 101 } else { 6 }
        if ($batch.ValueKind -ne [System.Text.Json.JsonValueKind]::Object -or
            $null -eq $batchStatus -or -not (Test-JsonString $batchStatus @('ok','partial','unknown','error','timeout')) -or
            $null -eq $batchExact -or -not (Test-JsonBoolean $batchExact) -or
            $null -eq $batchAuthority -or $batchAuthority.ValueKind -ne [System.Text.Json.JsonValueKind]::False -or
            $null -eq $batchReason -or -not (Test-JsonString $batchReason) -or
            ($null -ne $batchSchema -and -not $isBatch100) -or
            $null -eq $requestedAliases -or $requestedAliases.ValueKind -ne [System.Text.Json.JsonValueKind]::Array -or $requestedAliases.GetArrayLength() -gt $requestedLimit -or
            $null -eq $returnedAliases -or $returnedAliases.ValueKind -ne [System.Text.Json.JsonValueKind]::Array -or $returnedAliases.GetArrayLength() -gt $returnedLimit) { Fail-Safely 'InvalidSensorBatchIdentityShape' }
        $requestedSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
        foreach ($alias in $requestedAliases.EnumerateArray()) {
            $aliasPattern = if ($isBatch100) { '^b(?:[1-9]|[1-9][0-9]|100)$' } else { '^b[1-5]$' }
            if ($alias.ValueKind -ne [System.Text.Json.JsonValueKind]::String -or $alias.GetString() -cnotmatch $aliasPattern -or -not $requestedSet.Add($alias.GetString())) { Fail-Safely 'InvalidSensorBatchIdentityShape' }
        }
        $returnedSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
        $returnedUnique = $true
        foreach ($alias in $returnedAliases.EnumerateArray()) {
            $returnedPattern = if ($isBatch100) { '^(b(?:[1-9]|[1-9][0-9]|100)|foreign[1-8]|foreign-other|invalid|invalid-other)$' } else { '^(b[1-5]|foreign[1-6]|invalid)$' }
            if ($alias.ValueKind -ne [System.Text.Json.JsonValueKind]::String -or $alias.GetString() -cnotmatch $returnedPattern) { Fail-Safely 'InvalidSensorBatchIdentityShape' }
            if (-not $returnedSet.Add($alias.GetString())) { $returnedUnique = $false }
        }
        $minimumRequested = if ($isBatch100) { 1 } else { 2 }
        $setMatches = $requestedSet.Count -ge $minimumRequested -and $returnedUnique -and $returnedSet.SetEquals($requestedSet)
        if ($batchExact.ValueKind -eq [System.Text.Json.JsonValueKind]::True -and
            (-not $setMatches -or $batchStatus.GetString() -cne 'ok' -or $batchReason.GetString() -cne 'exact-unique-requested-set')) { Fail-Safely 'SensorBatchExactSetContradiction' }
        $batchComplete = $setMatches -and $batchExact.ValueKind -eq [System.Text.Json.JsonValueKind]::True
        if ($isBatch100) {
            $batchContractMatched = $true
            $requestShape = Get-JsonProperty $batch 'request_shape_version'
            $runtimeContract = Get-JsonProperty $batch 'runtime_request_contract_version'
            $requestMethod = Get-JsonProperty $batch 'request_method'
            $nativeColumns = Get-JsonProperty $batch 'native_columns'
            $filterMode = Get-JsonProperty $batch 'filter_mode'
            $requestCountParameter = Get-JsonProperty $batch 'request_count_parameter'
            $sentinelRows = Get-JsonProperty $batch 'sentinel_rows'
            $usesCaption = Get-JsonProperty $batch 'uses_caption'
            $selectionScope = Get-JsonProperty $batch 'selection_scope'
            $requestedCount = Get-JsonProperty $batch 'requested_count'
            $respondedCount = Get-JsonProperty $batch 'responded_count'
            $truncated = Get-JsonProperty $batch 'truncated'
            $responseBytes = Get-JsonProperty $batch 'response_bytes'
            $urlBytes = Get-JsonProperty $batch 'request_url_bytes'
            $maximumResponseBytes = Get-JsonProperty $batch 'maximum_response_bytes'
            $maximumUrlBytes = Get-JsonProperty $batch 'maximum_relative_url_bytes'
            $maximumDepth = Get-JsonProperty $batch 'maximum_json_depth'
            $shapeFingerprint = Get-JsonProperty $batch 'shape_fingerprint'
            $full100 = Get-JsonProperty $batch 'full_batch100_observed'
            $runtimeResponseCompatible = Get-JsonProperty $batch 'runtime_response_compatible'
            $minimumFieldsObserved = Get-JsonProperty $batch 'minimum_fields_observed'
            $runtimeResponseReason = Get-JsonProperty $batch 'runtime_response_reason'
            $profileAuthorized = Get-JsonProperty $batch 'profile_authorized'
            $capacityAccepted = Get-JsonProperty $batch 'capacity_accepted'
            if ($null -eq $requestShape -or -not (Test-JsonString $requestShape) -or
                $null -eq $runtimeContract -or -not (Test-JsonString $runtimeContract) -or
                $null -eq $requestMethod -or -not (Test-JsonString $requestMethod) -or
                $null -eq $nativeColumns -or -not (Test-JsonString $nativeColumns) -or
                $null -eq $filterMode -or -not (Test-JsonString $filterMode) -or
                $null -eq $requestCountParameter -or -not (Test-JsonNonNegativeInteger $requestCountParameter -AllowNull $true -Maximum 101) -or
                $null -eq $sentinelRows -or -not (Test-JsonNonNegativeInteger $sentinelRows -Maximum 10) -or
                $null -eq $usesCaption -or -not (Test-JsonBoolean $usesCaption) -or
                $null -eq $selectionScope -or -not (Test-JsonString $selectionScope) -or
                $null -eq $requestedCount -or -not (Test-JsonNonNegativeInteger $requestedCount -Maximum 100) -or
                $requestedCount.GetInt32() -ne $requestedAliases.GetArrayLength() -or
                $null -eq $respondedCount -or -not (Test-JsonNonNegativeInteger $respondedCount -AllowNull $true -Maximum 101) -or
                ($respondedCount.ValueKind -eq [System.Text.Json.JsonValueKind]::Number -and $respondedCount.GetInt32() -ne $returnedAliases.GetArrayLength()) -or
                $null -eq $truncated -or -not (Test-JsonBoolean $truncated) -or
                $null -eq $responseBytes -or -not (Test-JsonNonNegativeInteger $responseBytes -AllowNull $true -Maximum 524288) -or
                $null -eq $urlBytes -or -not (Test-JsonNonNegativeInteger $urlBytes -AllowNull $true -Maximum 4096) -or
                $null -eq $maximumResponseBytes -or -not (Test-JsonNonNegativeInteger $maximumResponseBytes -Maximum 524288) -or
                $null -eq $maximumUrlBytes -or -not (Test-JsonNonNegativeInteger $maximumUrlBytes -Maximum 4096) -or
                $null -eq $maximumDepth -or -not (Test-JsonNonNegativeInteger $maximumDepth -Maximum 32) -or
                $null -eq $shapeFingerprint -or
                ($requestedCount.GetInt32() -ge 1 -and (-not (Test-JsonString $shapeFingerprint) -or $shapeFingerprint.GetString() -cnotmatch '^[0-9a-f]{64}$')) -or
                ($requestedCount.GetInt32() -lt 1 -and $shapeFingerprint.ValueKind -ne [System.Text.Json.JsonValueKind]::String) -or
                $null -eq $full100 -or -not (Test-JsonBoolean $full100) -or
                $null -eq $profileAuthorized -or $profileAuthorized.ValueKind -ne [System.Text.Json.JsonValueKind]::False -or
                $null -eq $capacityAccepted -or $capacityAccepted.ValueKind -ne [System.Text.Json.JsonValueKind]::False) { Fail-Safely 'InvalidSensorBatch100Metadata' }
            if ($urlBytes.ValueKind -eq [System.Text.Json.JsonValueKind]::Null -and $requestedCount.GetInt32() -ge 1) { Fail-Safely 'InvalidSensorBatch100Metadata' }
            if ($requestedCount.GetInt32() -ge 1) {
                $batchCount = $requestedCount.GetInt32()
                if ($requestCountParameter.ValueKind -ne [System.Text.Json.JsonValueKind]::Number -or $requestCountParameter.GetInt32() -ne $batchCount + 1) { $batchContractMatched = $false }
                $shapeMaterial = "snapshot-filter-batch100-v1`nGET`n/api/table.json?content=sensors&columns=objid,lastvalue,interval,lastcheck,status,primarychannel&filter_objid={$batchCount IDs}&count=$($batchCount + 1)`nfilter=sorted-repeated-filter_objid`nsentinel=1`nusecaption=false`nurl-utf8<=4096`nresponse<=524288`ndepth<=32`nselection=existing-bounded-step3-sample"
                $expectedFingerprint = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData(
                    [System.Text.Encoding]::UTF8.GetBytes($shapeMaterial))).ToLowerInvariant()
                if (-not [string]::Equals($shapeFingerprint.GetString(), $expectedFingerprint, [StringComparison]::Ordinal)) { $batchContractMatched = $false }
                if ($requestShape.GetString() -cne 'snapshot-filter-batch100-v1' -or
                    $runtimeContract.GetString() -cne 'snapshot-filter-batch100-v1' -or
                    $requestMethod.GetString() -cne 'GET' -or
                    $nativeColumns.GetString() -cne 'objid,lastvalue,interval,lastcheck,status,primarychannel' -or
                    $filterMode.GetString() -cne 'sorted-repeated-filter_objid' -or
                    $sentinelRows.GetInt32() -ne 1 -or $usesCaption.GetBoolean() -or
                    $selectionScope.GetString() -cne 'existing-bounded-step3-sample' -or
                    $maximumResponseBytes.GetInt32() -ne 524288 -or $maximumUrlBytes.GetInt32() -ne 4096 -or $maximumDepth.GetInt32() -ne 32) { $batchContractMatched = $false }
                if ($respondedCount.ValueKind -eq [System.Text.Json.JsonValueKind]::Number -and
                    $truncated.GetBoolean() -ne ($respondedCount.GetInt32() -gt $batchCount)) { Fail-Safely 'SensorBatch100TruncationContradiction' }
                if ($batchExact.GetBoolean() -and
                    ($respondedCount.ValueKind -ne [System.Text.Json.JsonValueKind]::Number -or $respondedCount.GetInt32() -ne $batchCount -or $truncated.GetBoolean())) { Fail-Safely 'SensorBatch100ExactSetContradiction' }
                if ($batchStatus.GetString() -ceq 'ok' -and
                    ($respondedCount.ValueKind -ne [System.Text.Json.JsonValueKind]::Number -or $responseBytes.ValueKind -ne [System.Text.Json.JsonValueKind]::Number -or
                     $urlBytes.ValueKind -ne [System.Text.Json.JsonValueKind]::Number)) { Fail-Safely 'InvalidSensorBatch100Metadata' }
            }
            # Older v2 observations did not evaluate the runtime minimum-field contract. They
            # remain parseable for their exact-ID diagnostic but cannot validate a batch-100 shape.
            if ($null -ne $runtimeResponseCompatible -and -not (Test-JsonBoolean $runtimeResponseCompatible)) { Fail-Safely 'InvalidSnapshotRuntimeResponseMetadata' }
            if ($null -ne $minimumFieldsObserved -and -not (Test-JsonBoolean $minimumFieldsObserved)) { Fail-Safely 'InvalidSnapshotRuntimeResponseMetadata' }
            if ($null -ne $runtimeResponseReason -and -not (Test-JsonString $runtimeResponseReason @(
                'not-observed','invalid-request-scope','response-byte-limit','malformed-json',
                'duplicate-or-case-ambiguous-properties','sensors-array-missing','malformed-or-duplicate-sensor-id',
                'sensor-outside-requested-scope','requested-sensor-set-mismatch','minimum-snapshot-fields-missing',
                'runtime-snapshot-contract-compatible'))) { Fail-Safely 'InvalidSnapshotRuntimeResponseMetadata' }
            if ($full100.ValueKind -eq [System.Text.Json.JsonValueKind]::True -and
                ($requestedCount.GetInt32() -ne 100 -or $respondedCount.ValueKind -ne [System.Text.Json.JsonValueKind]::Number -or
                 $respondedCount.GetInt32() -ne 100 -or $batchExact.ValueKind -ne [System.Text.Json.JsonValueKind]::True -or $truncated.GetBoolean())) { Fail-Safely 'SensorBatch100ExactSetContradiction' }
            if (-not $batchContractMatched) { $sourceIncomplete = $true }
            $batchComplete = $batchComplete -and $batchContractMatched
            $runtimeCompatible = $null -ne $runtimeResponseCompatible -and $runtimeResponseCompatible.ValueKind -eq [System.Text.Json.JsonValueKind]::True
            $minimumFields = $null -ne $minimumFieldsObserved -and $minimumFieldsObserved.ValueKind -eq [System.Text.Json.JsonValueKind]::True
            $batchFull100 = $full100.GetBoolean() -and $batchContractMatched -and $batchComplete -and $runtimeCompatible -and $minimumFields
            if (-not $batchFull100) { $sourceIncomplete = $true }
        }
    }

    $sourceStatus = $rootStatus.GetString()
    if ($sourceStatus -notin @('ok', 'partial', 'unknown', 'error', 'truncated')) { $sourceStatus = 'unknown' }

    Write-Output "Version: $version"
    Write-Output "GitRevision: $revision"
    Write-Output "FileSHA256: $sha256"
    Write-Output "TargetsCount: $targetCount"
    Write-Output "RawChannelIdentityTargets: $rawCount"
    Write-Output "RawChannelIds: $channelIdCount"
    Write-Output "NativePrimaryCapabilityDiagnostics: $nativePrimaryDiagnosticCompleteCount/$nativePrimaryCapabilityCount"
    Write-Output "NativePrimaryConflicts: $nativePrimaryConflictCount"
    Write-Output "SensorBatchExactSetObserved: $batchComplete"
    Write-Output "LegacySensorBatchExactSetObserved: $legacyBatchComplete"
    Write-Output "SensorBatchFull100ExactObserved: $batchFull100"
    Write-Output 'SensorBatchProfileAuthorized: false'
    Write-Output 'SensorBatchCapacityAccepted: false'
    Write-Output 'NativePrimaryFormalAuthorization: not-asserted'
    Write-Output "DeploymentProcessorCount: $processorCountValue"
    Write-Output "RuntimeGcAvailableMemoryBytes: $runtimeMemoryValue"
    Write-Output "ProcessWorkingSetBytes: $workingSetValue"
    Write-Output "StorageProvider: $storageProvider"
    Write-Output "StorageMetadataStatus: $statusText"
    Write-Output "StorageQueryCounts: $succeededValue/$attemptedValue"
    Write-Output "StorageFileRows: $($fileRows.GetArrayLength())"
    Write-Output "StorageVolumeRows: $($volumeRows.GetArrayLength())"
    Write-Output "SqlHostResources: $(if ($storageProvider -eq 'Sqlite') { 'not-applicable' } else { 'unknown' })"
    if ($targetCount -eq 0 -or -not $batchComplete -or $sourceIncomplete -or $metadataIncomplete -or $sourceStatus -in @('partial', 'unknown', 'error', 'truncated')) {
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
