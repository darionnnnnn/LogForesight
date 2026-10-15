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
$rescueRunStopped = $false
$rescueStarted = $null
$serverAdminFixturePassword = $null
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
    $matrix = Get-Content -LiteralPath (Join-Path $matrixStage 'user-matrix.json') -Raw | ConvertFrom-Json
    $maintenanceRoles = @($matrix.maintenanceRoleMatrix.roles)
    if ($maintenanceRoles.Count -ne 4 -or @($maintenanceRoles.id | Select-Object -Unique).Count -ne 4) {
        throw 'Round54 maintenance fixture metadata must define four separate real-login scenarios.'
    }
    $expectedFixtureAccountKeys = @{
        'M54-rescue'='serverAdminBuiltin'
        'M54-global'='currentEvidence.MaintainAccount'
        'M54-readall'='accounts.manager.Account'
        'M54-nomaintain'='currentEvidence.OrdinaryAccount'
    }
    $expectedMaintenanceCapabilities = @{
        'M54-rescue'=@{ Maintain=$true; ViewAll=$false }
        'M54-global'=@{ Maintain=$true; ViewAll=$true }
        'M54-readall'=@{ Maintain=$false; ViewAll=$true }
        'M54-nomaintain'=@{ Maintain=$false; ViewAll=$false }
    }
    foreach ($maintenanceRole in $maintenanceRoles) {
        if ($maintenanceRole.fixtureAccountKey -cne $expectedFixtureAccountKeys[[string]$maintenanceRole.id] -or
            $maintenanceRole.maintain -ne $expectedMaintenanceCapabilities[[string]$maintenanceRole.id].Maintain -or
            $maintenanceRole.viewAll -ne $expectedMaintenanceCapabilities[[string]$maintenanceRole.id].ViewAll -or
            ($maintenanceRole.id -ceq 'M54-rescue' -and $maintenanceRole.loginMode -cne 'serverAdmin-password-required-owned-clone') -or
            ($maintenanceRole.id -cne 'M54-rescue' -and $maintenanceRole.loginMode -cne 'existing-source-role-account')) {
            throw "Round54 account mapping or login mode is invalid for $($maintenanceRole.id)."
        }
    }
    $roleSpecs = @(
        [pscustomobject]@{ Id='maintain-control'; Account=[string]$scenario.MaintainAccount; ExpectsMaintain=$true; MaintenanceScenarioId='M54-global'; ExpectsViewAll=$true },
        [pscustomobject]@{ Id='owner-handler'; Account=[string]$scenario.OrdinaryAccount; ExpectsMaintain=$false; MaintenanceScenarioId='M54-nomaintain'; ExpectsViewAll=$false },
        [pscustomobject]@{ Id='readonly-manager'; Account=[string]$roleBundle.Accounts.manager.Account; ExpectsMaintain=$false; MaintenanceScenarioId='M54-readall'; ExpectsViewAll=$true },
        [pscustomobject]@{ Id='restricted-dev'; Account=[string]$roleBundle.Accounts.restrictedAdmin.Account; ExpectsMaintain=$false; MaintenanceScenarioId=$null; ExpectsViewAll=$null },
        [pscustomobject]@{ Id='case-grant-handler'; Account=[string]$scenario.CaseHandlerAccount; ExpectsMaintain=$false; MaintenanceScenarioId=$null; ExpectsViewAll=$null },
        [pscustomobject]@{ Id='empty-visible'; Account=[string]$scenario.EmptyVisibleAccount; ExpectsMaintain=$false; MaintenanceScenarioId=$null; ExpectsViewAll=$null }
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
        $hasViewAll = $capabilities -contains 'ViewAll'
        $isServerAdmin = $me.data.isServerAdmin -eq $true
        if ($hasMaintain -ne [bool]$role.ExpectsMaintain) {
            throw "Seeded role capability mismatch for $($role.Id): Maintain=$hasMaintain."
        }
        if ($isServerAdmin -or ($null -ne $role.ExpectsViewAll -and $hasViewAll -ne [bool]$role.ExpectsViewAll)) {
            throw "Ordinary source role identity/capability mismatch for $($role.Id): IsServerAdmin=$isServerAdmin, ViewAll=$hasViewAll."
        }
        $response = Invoke-WebRequest -Uri $exportUrl -WebSession $session -TimeoutSec 30 -SkipHttpErrorCheck
        $body = [string]$response.Content
        $bodyBytes = [Text.Encoding]::UTF8.GetByteCount($body)
        $dataFieldsVisible = $body -match '(?i)"(?:HostIds|Records|Observations|Timelines|Labels)"\s*:'
        $row = [ordered]@{
            RequirementIds = if ($null -ne $role.MaintenanceScenarioId) { @('AC21','U53-05',[string]$role.MaintenanceScenarioId,'C54-01') } else { @('AC21','U53-05') }
            RoleId = $role.Id
            MaintenanceScenarioId = $role.MaintenanceScenarioId
            LoginHttpStatus = [int]$login.StatusCode
            CapabilityMaintain = $hasMaintain
            CapabilityViewAll = $hasViewAll
            IsServerAdmin = $isServerAdmin
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

if ($null -eq $errorText) {
    try {
        # Run rescue login separately with the password-required fallback provider. The ordinary
        # six-role pass remains Stub-backed; the rescue pass proves the real serverAdmin branch
        # validates a one-run PBKDF2 credential instead of inheriting Stub's password bypass.
        $serverAdminFixturePassword = [Convert]::ToBase64String([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
        $rescueStarted = & $startScript -WebOutputDirectory $WebOutputDirectory -WebAssetsDirectory $WebAssetsDirectory `
            -SourceFixtureBundlePath $SourceFixtureBundlePath -ExpectedSourceCommit $ExpectedSourceCommit `
            -AuthenticationProvider 'Ad' -ServerAdminFixturePassword $serverAdminFixturePassword
        $rescueManifest = Get-Content -LiteralPath $rescueStarted.ManifestPath -Raw | ConvertFrom-Json
        if ($rescueManifest.Runtime.AuthenticationProvider -cne 'Ad' -or
            $rescueManifest.Runtime.ServerAdminAccount -cne 'serverAdmin' -or
            $rescueManifest.Runtime.ServerAdminPasswordRequired -ne $true -or
            [string]$rescueManifest.Runtime.ServerAdminPasswordHashSha256 -notmatch '^[0-9a-f]{64}$') {
            throw 'Owned rescue clone did not record a password-required serverAdmin fixture without exposing its credential.'
        }
        $wrongCredentialBody = @{ Account='serverAdmin'; Password=([Convert]::ToBase64String([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32))) } | ConvertTo-Json -Compress
        $wrongCredential = Invoke-WebRequest -Uri "$($rescueManifest.Runtime.PathBaseUrl.TrimEnd('/'))/api/auth/login" `
            -Method Post -WebSession ([Microsoft.PowerShell.Commands.WebRequestSession]::new()) -ContentType 'application/json' `
            -Headers @{ 'X-Requested-By'='LogForesight' } -Body $wrongCredentialBody -TimeoutSec 15 -SkipHttpErrorCheck
        $wrongCredentialBody = $null
        if ($wrongCredential.StatusCode -ne 401) { throw "Owned serverAdmin fixture did not reject an incorrect password with HTTP 401." }
        $rescueSession = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
        $rescueLoginBody = @{ Account='serverAdmin'; Password=$serverAdminFixturePassword } | ConvertTo-Json -Compress
        $rescueLogin = Invoke-WebRequest -Uri "$($rescueManifest.Runtime.PathBaseUrl.TrimEnd('/'))/api/auth/login" `
            -Method Post -WebSession $rescueSession -ContentType 'application/json' `
            -Headers @{ 'X-Requested-By'='LogForesight' } -Body $rescueLoginBody -TimeoutSec 15 -SkipHttpErrorCheck
        $rescueMeResponse = if ($rescueLogin.StatusCode -eq 200) {
            Invoke-WebRequest -Uri "$($rescueManifest.Runtime.PathBaseUrl.TrimEnd('/'))/api/auth/me" `
                -WebSession $rescueSession -TimeoutSec 15 -SkipHttpErrorCheck
        } else { $null }
        if ($rescueLogin.StatusCode -ne 200 -or $null -eq $rescueMeResponse -or $rescueMeResponse.StatusCode -ne 200) {
            throw "Owned serverAdmin fixture login failed with HTTP $($rescueLogin.StatusCode)."
        }
        $rescueMe = $rescueMeResponse.Content | ConvertFrom-Json
        $rescueCapabilities = @($rescueMe.data.capabilities | ForEach-Object { [string]$_ })
        $rescueMaintain = $rescueCapabilities -contains 'Maintain'
        $rescueViewAll = $rescueCapabilities -contains 'ViewAll'
        if ($rescueMe.data.isServerAdmin -ne $true -or $rescueMaintain -ne $true -or $rescueViewAll -ne $false) {
            throw "Owned rescue identity did not resolve to serverAdmin/Maintain-only: IsServerAdmin=$($rescueMe.data.isServerAdmin), Maintain=$rescueMaintain, ViewAll=$rescueViewAll."
        }
        $observations.Add([pscustomobject]@{
            RequirementIds=@('M54-rescue','C54-01','R54-07')
            RoleId='M54-rescue'
            IncorrectPasswordHttpStatus=[int]$wrongCredential.StatusCode
            LoginHttpStatus=[int]$rescueLogin.StatusCode
            CapabilityMaintain=$rescueMaintain
            CapabilityViewAll=$rescueViewAll
            IsServerAdmin=$true
            AuthenticationProvider=[string]$rescueManifest.Runtime.AuthenticationProvider
            PasswordRequired=[bool]$rescueManifest.Runtime.ServerAdminPasswordRequired
            CredentialHashFingerprint=[string]$rescueManifest.Runtime.ServerAdminPasswordHashSha256
        })
    } catch {
        $errorText = $_.Exception.Message
    } finally {
        $serverAdminFixturePassword = $null
        if ($null -ne $rescueStarted) {
            try {
                $null = & $stopScript -OwnerFile $rescueStarted.OwnerFile
                $rescueOwner = Get-Content -LiteralPath $rescueStarted.OwnerFile -Raw | ConvertFrom-Json
                if ($rescueOwner.Status -cne 'stopped') { throw 'Owned rescue fixture stop did not persist stopped status.' }
                $rescueRunStopped = $true
            } catch { if ($null -eq $errorText) { $errorText = "Owned rescue fixture stop failed: $($_.Exception.Message)" } }
        }
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
    RescueRunId = if ($null -ne $rescueStarted) { $rescueStarted.RunId } else { $null }
    BuildSourceStamp = $started.BuildSourceStamp
    WebRunStopped = $webRunStopped
    RescueRunStopped = $rescueRunStopped
    SourceDatabaseUnchanged = $sourceDatabaseUnchanged
    SourceKeyUnchanged = $sourceKeyUnchanged
    Observations = @($observations)
    OperatorReviewRequired = $true
    BrowserAccepted = $false
    MatrixAccepted = $false
    WholeRoundAccepted = $false
    Limitation = 'This observes the baseline roles and Round54 maintenance identities through direct APIs on owned synthetic SQLite/Web clones. Rescue uses the real serverAdmin password-required branch with a one-run credential. It is not a browser observation and does not complete the maintenance matrix or any Q objective.'
}
[IO.File]::WriteAllText($observationPath, ($report | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
if ($null -ne $errorText) { throw $errorText }
if (!$sourceDatabaseUnchanged -or !$sourceKeyUnchanged) { throw 'The source role bundle database or key changed during the owned API run.' }
if (@($observations).Count -ne 7 -or !$allRoleExpectationsMet -or !$webRunStopped -or !$rescueRunStopped) {
    throw "Role API observations did not match the expected role contract or both owned clones did not stop; raw observations retained at $observationPath."
}
$report | ConvertTo-Json -Depth 12
