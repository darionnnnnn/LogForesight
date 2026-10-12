[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion -lt [Version]'7.4') { throw 'Requires PowerShell 7.4+.' }
$stageRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$runsRoot = [IO.Path]::GetFullPath((Join-Path $stageRoot 'runs'))
$runRoot = Join-Path $runsRoot ([guid]::NewGuid().ToString('N'))
$artifactRoot = Join-Path $runRoot 'evidence\browser-artifacts'
[IO.Directory]::CreateDirectory($artifactRoot) | Out-Null
$ownerPath = Join-Path $runRoot 'process-owner.json'
$markerPath = Join-Path $runRoot '.lf-round53-user-matrix-owner.json'
$manifestPath = Join-Path $runRoot 'fixture-manifest.json'
$artifactPath = Join-Path $artifactRoot 'U53-05-contract.txt'
try {
    $runId = [IO.Path]::GetFileName($runRoot)
    $sourceBuild = [ordered]@{ ExpectedSourceCommit='0123456789abcdef0123456789abcdef01234567'; SourceStamp='lfbuild-v1|modified|0123456789abcdef0123456789abcdef01234567|0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef'; SourceRevision='0123456789abcdef0123456789abcdef01234567'; WebDllSha256=('a'*64); CoreDllSha256=('b'*64); StaticAssetsSourceSha256=('c'*64); StaticAssetsRuntimeSha256=('d'*64) }
    $owner = [ordered]@{ Schema='lf-round53-user-matrix-process-owner-v1'; RunId=$runId; BundlePath=$runRoot; Status='running' }
    $marker = [ordered]@{ Schema='lf-round53-user-matrix-owner-v1'; RunId=$runId; BundlePath=$runRoot; Status='running' }
    $fixture = [ordered]@{ Schema='lf-round53-user-matrix-fixture-v1'; RunId=$runId; Status='running'; SyntheticOnly=$true; SourceBuild=$sourceBuild }
    [IO.File]::WriteAllText($ownerPath,($owner | ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($markerPath,($marker | ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($manifestPath,($fixture | ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($artifactPath,'recorder contract fixture only; no browser was used.',[Text.UTF8Encoding]::new($false))
    $recorder = Join-Path $stageRoot 'Record-UserMatrixBrowserObservation.ps1'
    $result = & $recorder -OwnerFile $ownerPath -RequirementId 'U53-05' -ObservedResult inconclusive -Observation 'Contract check, not a browser result.' -EvidenceFile $artifactPath
    $recordPath = Join-Path $runRoot 'evidence\browser-observations.jsonl'
    $record = Get-Content -LiteralPath $recordPath -Raw | ConvertFrom-Json
    $expectedHash = (Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($result.AggregateAcceptanceChanged -ne $false -or $record.RequirementId -cne 'U53-05' -or $record.RequirementKind -cne 'userJourneys' -or
        $record.Evidence.sha256 -cne $expectedHash -or $record.SourceBuild.sourceStamp -cne $sourceBuild.SourceStamp -or
        $record.SourceBuild.expectedSourceCommit -cne $sourceBuild.ExpectedSourceCommit -or
        $record.SourceBuild.staticAssetsSourceSha256 -cne $sourceBuild.StaticAssetsSourceSha256 -or $record.SourceBuild.staticAssetsRuntimeSha256 -cne $sourceBuild.StaticAssetsRuntimeSha256 -or
        @($record.Limitations | Where-Object { $_ -match 'does not validate the screenshot' }).Count -ne 1) { throw 'Observation recorder did not bind IDs/hash/source or preserve its no-grade boundary.' }
    $before = (Get-Item -LiteralPath $recordPath).Length
    $badIdRejected = $false
    try { & $recorder -OwnerFile $ownerPath -RequirementId 'U53-99' -ObservedResult passed -Observation 'invalid id' -EvidenceFile $artifactPath | Out-Null }
    catch { $badIdRejected = $true }
    if (!$badIdRejected -or (Get-Item -LiteralPath $recordPath).Length -ne $before) { throw 'Unknown requirement ID changed the evidence ledger.' }
    [pscustomobject]@{ Result='PASS'; Contract='ID/source/hash bound; aggregate remains unchanged; unknown ID refused'; BrowserUsed=$false }
}
finally {
    $canonical = [IO.Path]::GetFullPath($runRoot)
    if ([IO.Path]::GetDirectoryName($canonical).TrimEnd('\') -ine $runsRoot.TrimEnd('\') -or [IO.Path]::GetFileName($canonical) -notmatch '^[0-9a-f]{32}$') { throw 'Refusing to remove recorder test data outside owned runs.' }
    if (Test-Path -LiteralPath $canonical) { Remove-Item -LiteralPath $canonical -Recurse -Force }
}
