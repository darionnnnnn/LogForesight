[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$WebOutputDirectory,
    [Parameter(Mandatory = $true)][string]$WebAssetsDirectory,
    [Parameter(Mandatory = $true)][string]$SourceFixtureBundlePath,
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9a-f]{40}$')][string]$ExpectedSourceCommit
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion -lt [Version]'7.4') { throw 'Requires PowerShell 7.4+.' }

$scriptRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$stageId = [guid]::NewGuid().ToString('N')
$stageRoot = Join-Path ([IO.Path]::GetTempPath()) "lf53-e08-api-$stageId"
$stageScripts = Join-Path $stageRoot 'scripts'
$matrixStage = Join-Path $stageScripts 'round53-user-matrix'
$identityStage = Join-Path $stageScripts 'build-identity'
[IO.Directory]::CreateDirectory($matrixStage) | Out-Null
[IO.Directory]::CreateDirectory((Join-Path $matrixStage 'source-stub')) | Out-Null
[IO.Directory]::CreateDirectory($identityStage) | Out-Null

# Give Start-UserMatrixFixture and its two tiny helpers private source/intermediate/output
# directories. In particular, do not rebuild source-stub/out while another owned run is live.
foreach ($name in @('Start-UserMatrixFixture.ps1','Stop-UserMatrixFixture.ps1','user-matrix.json','seed_fixture.py')) {
    Copy-Item -LiteralPath (Join-Path $scriptRoot $name) -Destination (Join-Path $matrixStage $name)
}
foreach ($name in @('Round53ControlledSources.csproj','Program.cs')) {
    Copy-Item -LiteralPath (Join-Path $scriptRoot "source-stub\$name") -Destination (Join-Path $matrixStage "source-stub\$name")
}
foreach ($name in @('BuildIdentityManifest.csproj','Program.cs')) {
    Copy-Item -LiteralPath (Join-Path $scriptRoot "..\build-identity\$name") -Destination (Join-Path $identityStage $name)
}

$startScript = Join-Path $matrixStage 'Start-UserMatrixFixture.ps1'
$stopScript = Join-Path $matrixStage 'Stop-UserMatrixFixture.ps1'
$started = $null
$manifest = $null
$observations = [Collections.Generic.List[object]]::new()
$exportArtifactPath = $null
$allRoleExpectationsMet = $true
$errorText = $null
$webRunStopped = $false
try {
    $started = & $startScript -WebOutputDirectory $WebOutputDirectory -WebAssetsDirectory $WebAssetsDirectory `
        -SourceFixtureBundlePath $SourceFixtureBundlePath -ExpectedSourceCommit $ExpectedSourceCommit
    $manifest = Get-Content -LiteralPath $started.ManifestPath -Raw | ConvertFrom-Json
    if ($manifest.SyntheticOnly -ne $true -or $manifest.Runtime.ExternalConnectionsEnabled -ne $false -or
        $manifest.Runtime.ScheduleEnabled -ne $false -or $manifest.Runtime.LocalHostAnalysisEnabled -ne $false) {
        throw 'Owned Web fixture did not confirm synthetic-only, external-disabled, schedules-disabled and local-host-analysis-disabled state.'
    }
    $pathBaseUrl = [string]$manifest.Runtime.PathBaseUrl
    $evidenceDirectory = Join-Path $started.BundlePath 'evidence'
    [IO.Directory]::CreateDirectory($evidenceDirectory) | Out-Null
    $pathBaseUri = [uri]$pathBaseUrl
    if ($pathBaseUri.Scheme -cne 'http' -or $pathBaseUri.Host -cne '127.0.0.1') {
        throw 'Owned Web fixture is not bound to the expected loopback-only HTTP origin.'
    }
    $sourceRun = Get-Content -LiteralPath (Join-Path $SourceFixtureBundlePath 'source-fixture-manifest.json') -Raw | ConvertFrom-Json
    $scenario = @($sourceRun.Scenarios | Where-Object { $_.Name -ceq 'current-evidence' })
    if ($scenario.Count -ne 1) { throw 'Expected exactly one current-evidence scenario in the owned synthetic role bundle.' }
    $scenario = $scenario[0]
    $roleBundle = Get-Content -LiteralPath (Join-Path $SourceFixtureBundlePath 'fixture-manifest.json') -Raw | ConvertFrom-Json
    $roleSpecs = @(
        [pscustomobject]@{ Id='maintain-control'; Account=[string]$scenario.MaintainAccount; ExpectsMaintain=$true },
        [pscustomobject]@{ Id='owner-handler'; Account=[string]$scenario.OrdinaryAccount; ExpectsMaintain=$false },
        [pscustomobject]@{ Id='readonly-manager'; Account=[string]$roleBundle.Accounts.manager.Account; ExpectsMaintain=$false },
        [pscustomobject]@{ Id='restricted-dev'; Account=[string]$roleBundle.Accounts.restrictedAdmin.Account; ExpectsMaintain=$false },
        [pscustomobject]@{ Id='case-grant-handler'; Account=[string]$scenario.CaseHandlerAccount; ExpectsMaintain=$false },
        [pscustomobject]@{ Id='empty-visible'; Account=[string]$scenario.EmptyVisibleAccount; ExpectsMaintain=$false }
    )
    if (@($roleSpecs | Where-Object { [string]::IsNullOrWhiteSpace($_.Account) }).Count -gt 0 -or
        @($roleSpecs.Id | Select-Object -Unique).Count -ne 6) {
        throw 'Owned role manifest did not provide six distinct matrix accounts.'
    }

    $exportUrl = "$($pathBaseUrl.TrimEnd('/'))/api/prtg/acceptance/export?from=$($scenario.CaseRecordDate)&through=$($scenario.CaseRecordDate)"
    foreach ($role in $roleSpecs) {
        $session = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
        $loginBody = @{ Account=$role.Account; Password='' } | ConvertTo-Json -Compress
        $login = Invoke-WebRequest -Uri "$($pathBaseUrl.TrimEnd('/'))/api/auth/login" -Method Post -WebSession $session `
            -ContentType 'application/json' -Headers @{ 'X-Requested-By'='LogForesight' } `
            -Body $loginBody -TimeoutSec 15 -SkipHttpErrorCheck
        if ($login.StatusCode -ne 200) { throw "Synthetic role login failed for $($role.Id) with HTTP $($login.StatusCode): $($login.Content)" }
        $meResponse = Invoke-WebRequest -Uri "$($pathBaseUrl.TrimEnd('/'))/api/auth/me" -WebSession $session `
            -TimeoutSec 15 -SkipHttpErrorCheck
        if ($meResponse.StatusCode -ne 200) { throw "Authenticated identity read failed for $($role.Id) with HTTP $($meResponse.StatusCode)." }
        $me = $meResponse.Content | ConvertFrom-Json
        $capabilities = @($me.data.capabilities | ForEach-Object { [string]$_ })
        $hasMaintain = $capabilities -contains 'Maintain'
        if ($hasMaintain -ne [bool]$role.ExpectsMaintain) {
            throw "Seeded role capability mismatch for $($role.Id): Maintain=$hasMaintain."
        }
        $response = Invoke-WebRequest -Uri $exportUrl -WebSession $session -TimeoutSec 30 -SkipHttpErrorCheck
        $body = [string]$response.Content
        $bodyBytes = [Text.Encoding]::UTF8.GetByteCount($body)
        $dataFieldsVisible = $body -match '(?i)"(?:HostIds|Records|Observations|Timelines|Labels)"\s*:'
        $row = [ordered]@{
            RequirementIds = @('AC21','U53-05')
            RoleId = $role.Id
            LoginHttpStatus = [int]$login.StatusCode
            CapabilityMaintain = $hasMaintain
            ExportHttpStatus = [int]$response.StatusCode
            ContentType = [string]$response.Headers['Content-Type']
            BodyBytes = $bodyBytes
            ExportPayloadFieldsVisible = $dataFieldsVisible
        }
        if ($role.ExpectsMaintain) {
            if ($response.StatusCode -ne 200 -or $response.Headers['Content-Type'] -notlike 'application/json*') {
                $allRoleExpectationsMet = $false
            } else {
                $export = $body | ConvertFrom-Json
                if ($export.FormatVersion -ne 2 -or $export.Purpose -cne 'manual-site-acceptance' -or
                    [string]::IsNullOrWhiteSpace([string]$export.Limitations)) { $allRoleExpectationsMet = $false }
                $evidenceDirectory = Join-Path $started.BundlePath 'evidence'
                [IO.Directory]::CreateDirectory($evidenceDirectory) | Out-Null
                $exportArtifactPath = Join-Path $evidenceDirectory 'maintain-api-export.json'
                [IO.File]::WriteAllText($exportArtifactPath, $body, [Text.UTF8Encoding]::new($false))
                $row.ExportBodySha256 = (Get-FileHash -LiteralPath $exportArtifactPath -Algorithm SHA256).Hash.ToLowerInvariant()
                $row.ExportPurpose = [string]$export.Purpose
                $row.ExportFormatVersion = [int]$export.FormatVersion
                $row.ExportScopeComplete = [bool]$export.ScopeComplete
            }
        } elseif ($response.StatusCode -ne 403 -or $dataFieldsVisible) {
            $allRoleExpectationsMet = $false
        }
        $observations.Add([pscustomobject]$row)
    }
} catch {
    $errorText = $_.Exception.Message
} finally {
    if ($null -ne $started) {
        try {
            $null = & $stopScript -OwnerFile $started.OwnerFile
            $stoppedOwner = Get-Content -LiteralPath $started.OwnerFile -Raw | ConvertFrom-Json
            if ($stoppedOwner.Status -cne 'stopped') { throw 'Owned fixture stop did not persist stopped status.' }
            $webRunStopped = $true
        }
        catch { if ($null -eq $errorText) { $errorText = "Owned fixture stop failed: $($_.Exception.Message)" } }
    }
}

if ($null -eq $started) { throw ($errorText ?? 'Owned API authority fixture failed to start.') }
$sourceDatabasePath = Join-Path $SourceFixtureBundlePath 'data\Db\logforesight.db'
$sourceKeyPath = Join-Path $SourceFixtureBundlePath 'data\keys\lf-crypto.key'
$sourceDatabaseBefore = [string]$manifest.SourceFixture.DatabaseSha256Before
$sourceKeyBefore = [string]$manifest.SourceFixture.KeySha256Before
$sourceDatabaseUnchanged = (Get-FileHash -LiteralPath $sourceDatabasePath -Algorithm SHA256).Hash.ToLowerInvariant() -ceq $sourceDatabaseBefore.ToLowerInvariant()
$sourceKeyUnchanged = (Get-FileHash -LiteralPath $sourceKeyPath -Algorithm SHA256).Hash.ToLowerInvariant() -ceq $sourceKeyBefore.ToLowerInvariant()
$observationPath = Join-Path $started.BundlePath 'evidence\api-export-authority-observation.json'
$report = [ordered]@{
    Schema = 'logforesight-round53-e08-api-export-authority-observation-v1'
    ObservedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    RunId = $started.RunId
    BuildSourceStamp = $started.BuildSourceStamp
    WebRunStopped = $webRunStopped
    SourceDatabaseUnchanged = $sourceDatabaseUnchanged
    SourceKeyUnchanged = $sourceKeyUnchanged
    Observations = @($observations)
    OperatorReviewRequired = $true
    BrowserAccepted = $false
    MatrixAccepted = $false
    WholeRoundAccepted = $false
    Limitation = 'This observes direct API authorization and one actual bounded export on an owned synthetic SQLite/Web fixture. It is not a browser observation and does not complete AC21, U53-05, the six-role matrix or any Q objective.'
}
[IO.File]::WriteAllText($observationPath, ($report | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
if ($null -ne $errorText) { throw $errorText }
if (!$sourceDatabaseUnchanged -or !$sourceKeyUnchanged) { throw 'The source role bundle database or key changed during the owned API run.' }
if (@($observations).Count -ne 6 -or !$allRoleExpectationsMet) {
    throw "Role API observations did not match the expected Maintain-only export contract; raw observations retained at $observationPath."
}
$report | ConvertTo-Json -Depth 12
