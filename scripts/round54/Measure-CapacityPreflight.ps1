[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$GeneratorRoot,
    [Parameter(Mandatory)][string]$OutputPath,
    [ValidateRange(1,10)][int]$AdditionalFullCopies = 3,
    [ValidateRange(0,10)][int]$AdditionalWalCopies = 1,
    [ValidateRange(0,100)][double]$ReservePercent = 25
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($GeneratorRoot)
$output = [IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $output) { throw 'Refusing to overwrite a preflight report.' }
for ($ancestor = [IO.DirectoryInfo]::new($root); $null -ne $ancestor; $ancestor=$ancestor.Parent) {
    if ($ancestor.Exists -and ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Generator root has a reparse ancestor.' }
}
if ($output.StartsWith($root + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Report must not be written into the generator source.' }
$manifestPath=Join-Path $root 'n6-run-manifest.json'
$checkpointPath=Join-Path $root 'n6-checkpoint.json'
$manifest=Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$checkpoint=Get-Content -LiteralPath $checkpointPath -Raw | ConvertFrom-Json
if ($manifest.schema -cne 'LogForesight.N6StorageRunManifest' -or $checkpoint.schema -cne 'LogForesight.N6StorageCheckpoint' -or
    $manifest.runId -cne $checkpoint.runId -or $manifest.manifestSha256 -cne $checkpoint.manifestSha256 -or
    $manifest.hosts -ne 3000 -or $manifest.sensors -ne 15000 -or $manifest.days -ne 180 -or
    $manifest.requestedRows -ne 64800000 -or $manifest.typedSlotsPerRow -ne 4 -or $manifest.provider -cne 'sqlite') { throw 'Preflight requires the fixed-shape SQLite manifest and matching checkpoint.' }
if ($checkpoint.committedRows -le 0 -or $checkpoint.committedRows -gt $manifest.requestedRows) { throw 'Checkpoint row count is invalid.' }
$db=[IO.Path]::GetFullPath($manifest.databasePath)
if (!$db.StartsWith($root + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or !(Test-Path -LiteralPath $db -PathType Leaf)) { throw 'Database must remain inside the generator root.' }
if ((Get-Item -LiteralPath $db).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Generator database must not be a reparse point.' }
$before=@((Get-FileHash -LiteralPath $manifestPath).Hash,(Get-FileHash -LiteralPath $checkpointPath).Hash)
$databaseBytes=(Get-Item -LiteralPath $db).Length
$sidecarBytes=0L
foreach($suffix in @('-wal','-shm')) { if(Test-Path -LiteralPath ($db+$suffix)) { $sidecarBytes+=(Get-Item -LiteralPath ($db+$suffix)).Length } }
$volume=[IO.DriveInfo]::new([IO.Path]::GetPathRoot($output))
if ([IO.Path]::GetPathRoot($db) -cne [IO.Path]::GetPathRoot($output)) { throw 'This forecast requires source and output on the same volume.' }
$projected=[long][Math]::Ceiling($databaseBytes / [double]$checkpoint.committedRows * $manifest.requestedRows)
$growth=[Math]::Max(0L,$projected-$databaseBytes)
$reserve=[long][Math]::Ceiling($volume.TotalSize*$ReservePercent/100)
$additional=[long]($growth + $projected*($AdditionalFullCopies+$AdditionalWalCopies))
$after=@((Get-FileHash -LiteralPath $manifestPath).Hash,(Get-FileHash -LiteralPath $checkpointPath).Hash)
if (($before -join '|') -cne ($after -join '|') -or (Get-Item -LiteralPath $db).Length -ne $databaseBytes) { throw 'Source descriptor or database size changed during preflight.' }
$hardware=Get-CimInstance Win32_ComputerSystem
$report=[ordered]@{
 schema='logforesight-round54-capacity-preflight-v1';createdUtc=[DateTimeOffset]::UtcNow.ToString('O')
 sourceRoot=$root;sourceRunId=$manifest.runId;manifestSha256=$before[0];checkpointSha256=$before[1]
 checkpointDeclaredRows=$checkpoint.committedRows;fixedRows=$manifest.requestedRows;databaseBytes=$databaseBytes;sidecarBytes=$sidecarBytes
 sourceDatabaseOpened=$false;rowCountsVerified=$false;sourceOwnershipAudited=$false
 volumeTotalBytes=$volume.TotalSize;volumeFreeBytes=$volume.AvailableFreeSpace;reserveBytes=$reserve
 totalPhysicalMemoryBytes=$hardware.TotalPhysicalMemory;logicalProcessors=$hardware.NumberOfLogicalProcessors
 forecast=[ordered]@{basis='linear bytes per checkpoint-declared row; not a measured peak or capacity acceptance';fullDatabaseBytes=$projected;remainingSourceGrowthBytes=$growth;additionalFullCopies=$AdditionalFullCopies;additionalWalCopies=$AdditionalWalCopies;additionalBytes=$additional;marginAfterForecastAndReserveBytes=$volume.AvailableFreeSpace-$additional-$reserve;fitsForecast=($volume.AvailableFreeSpace -ge $additional+$reserve)}
 requiredBeforeFullPreparation=@('E1 ownership/prefix/mapping audit','completed generator receipt','measured WAL/backup/export/retention peak and actual source costs','eligible SQL engine for full fixed shape')
 capacityAccepted=$false;retentionAccepted=$false;wholeRoundAccepted=$false
}
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output)) | Out-Null
[IO.File]::WriteAllText($output,($report | ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
$report | ConvertTo-Json -Depth 8
