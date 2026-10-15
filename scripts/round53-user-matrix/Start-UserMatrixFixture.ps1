[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$WebOutputDirectory,
    [Parameter(Mandatory = $true)][string]$WebAssetsDirectory,
    [Parameter(Mandatory = $true)][string]$SourceFixtureBundlePath,
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9a-f]{40}$')][string]$ExpectedSourceCommit,
    [ValidateSet('Stub','Ad')][string]$AuthenticationProvider = 'Stub',
    [string]$ServerAdminFixturePassword
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion -lt [Version]'7.4') { throw 'Requires PowerShell 7.4+.' }
$stageRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$runsRoot = [IO.Path]::GetFullPath((Join-Path $stageRoot 'runs'))
$webSource = [IO.Path]::GetFullPath($WebOutputDirectory)
$assetSource = [IO.Path]::GetFullPath($WebAssetsDirectory)
$sourceBundle = [IO.Path]::GetFullPath($SourceFixtureBundlePath)
$serverAdminPasswordHash = $null
if ($AuthenticationProvider -ceq 'Ad') {
    if ([string]::IsNullOrWhiteSpace($ServerAdminFixturePassword)) { throw 'The owned rescue-auth fixture requires an explicit one-run serverAdmin password.' }
    $salt = [System.Security.Cryptography.RandomNumberGenerator]::GetBytes(16)
    $derived = [System.Security.Cryptography.Rfc2898DeriveBytes]::Pbkdf2(
        $ServerAdminFixturePassword, $salt, 210000, [System.Security.Cryptography.HashAlgorithmName]::SHA256, 32)
    $serverAdminPasswordHash = 'PBKDF2$210000$' + [Convert]::ToBase64String($salt) + '$' + [Convert]::ToBase64String($derived)
    $ServerAdminFixturePassword = $null
}
function Assert-BuildIdentity([object]$Manifest, [string]$Directory, [string]$Label, [string]$ExpectedCommit) {
    if ($Manifest.Schema -cne 'logforesight-build-identity-v1' -or @($Manifest.Assemblies).Count -ne 2) { throw "$Label build identity manifest shape invalid." }
    $entries = @{}
    foreach ($assemblyName in @('LogForesight.Web','LogForesight.Core')) {
        $entry = @($Manifest.Assemblies | Where-Object { $_.Name -ceq $assemblyName })
        $dll = Join-Path $Directory ($assemblyName + '.dll')
        if ($entry.Count -ne 1 -or !(Test-Path -LiteralPath $dll -PathType Leaf) -or
            ([string]$entry[0].FileSha256).ToLowerInvariant() -cne (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash.ToLowerInvariant()) {
            throw "$Label build identity does not match $assemblyName.dll."
        }
        $entries[$assemblyName] = $entry[0]
    }
    $web = $entries['LogForesight.Web']; $core = $entries['LogForesight.Core']
    $stamp = [string]$web.SourceStamp
    if ([string]::IsNullOrWhiteSpace($stamp) -or $stamp -cne [string]$core.SourceStamp -or
        $stamp -cnotmatch '^lfbuild-v1\|(clean|modified)\|([0-9a-f]{40})\|([0-9a-f]{64})$') { throw "$Label Web/Core source stamps are unknown, mixed or malformed." }
    $sourceToken = $Matches[2]
    if ($sourceToken -cne $ExpectedCommit) { throw "$Label source stamp commit $sourceToken does not match explicitly expected commit $ExpectedCommit." }
    if ([string]$web.InformationalVersion -cne [string]$core.InformationalVersion -or
        ([string]$web.InformationalVersion -split '\+',2)[-1] -cne $sourceToken) { throw "$Label informational versions do not match the source revision token." }
    if ([string]$web.SourceRevisionIdOverride -cne [string]$core.SourceRevisionIdOverride) { throw "$Label Web/Core source revision overrides differ." }
    foreach ($entry in @($web,$core)) {
        if (![string]::IsNullOrWhiteSpace([string]$entry.SourceRevisionIdOverride) -and [string]$entry.SourceRevisionIdOverride -cne $sourceToken) {
            throw "$Label source revision override conflicts with the source stamp."
        }
    }
    return $entries
}
$sourceOwnerPath = Join-Path $sourceBundle '.lf-u05-owner.json'
$sourceManifestPath = Join-Path $sourceBundle 'fixture-manifest.json'
$sourceFixtureManifestPath = Join-Path $sourceBundle 'source-fixture-manifest.json'
foreach ($path in @($sourceOwnerPath, $sourceManifestPath, $sourceFixtureManifestPath,
        (Join-Path $sourceBundle 'data\Db\logforesight.db'), (Join-Path $sourceBundle 'data\keys\lf-crypto.key'))) {
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Source role fixture file missing: $path" }
}
$sourceOwner = Get-Content -LiteralPath $sourceOwnerPath -Raw | ConvertFrom-Json
$sourceManifest = Get-Content -LiteralPath $sourceManifestPath -Raw | ConvertFrom-Json
$sourceFixtureRuns = Split-Path -Parent $sourceBundle
$sourceFixtureStage = [IO.Path]::GetFullPath((Split-Path -Parent $sourceFixtureRuns))
$sourceReceiptPath = Join-Path $sourceFixtureStage 'fixture-prep-receipt.json'
if (!(Test-Path -LiteralPath $sourceReceiptPath -PathType Leaf)) { throw 'Owned source fixture preparation receipt is missing.' }
$sourceReceipt = Get-Content -LiteralPath $sourceReceiptPath -Raw | ConvertFrom-Json
if ($sourceOwner.Schema -cne 'lf-u05-owner-v1' -or $sourceManifest.Schema -cne 'lf-u05-current-ui-fixture-v1' -or
    $sourceOwner.RunId -cne $sourceManifest.RunId -or [IO.Path]::GetFileName($sourceBundle) -cne "u05-roles-$($sourceOwner.RunId)" -or
    $sourceManifest.SyntheticOnly -ne $true -or $sourceReceipt.schema -cne 'lf-u05-fixture-preparation-receipt-v1' -or
    $sourceReceipt.syntheticOnly -ne $true -or $sourceReceipt.sourceConnectionsAttempted -ne $false -or
    $sourceReceipt.bundle -cne ('runs/u05-roles-' + $sourceOwner.RunId) -or $sourceReceipt.runId -cne $sourceOwner.RunId) { throw 'Source fixture identity/provenance is invalid or not synthetic.' }
$sourceDataRoot = [IO.Path]::GetFullPath([string]$sourceManifest.DataRoot)
if ($sourceDataRoot -ine [IO.Path]::GetFullPath((Join-Path $sourceBundle 'data'))) { throw 'Source data root is not canonical.' }
$sourceDb = Join-Path $sourceDataRoot 'Db\logforesight.db'
$sourceKey = Join-Path $sourceDataRoot 'keys\lf-crypto.key'
$sourceDbBefore = (Get-FileHash -LiteralPath $sourceDb -Algorithm SHA256).Hash.ToLowerInvariant()
$sourceKeyBefore = (Get-FileHash -LiteralPath $sourceKey -Algorithm SHA256).Hash.ToLowerInvariant()
$originalSourceDb = [IO.Path]::GetFullPath([string]$sourceManifest.SourceFixture.DatabasePath)
if (!(Test-Path -LiteralPath $originalSourceDb -PathType Leaf) -or
    (Get-FileHash -LiteralPath $originalSourceDb -Algorithm SHA256).Hash.ToLowerInvariant() -cne ([string]$sourceReceipt.sourceFixture.databaseSha256BeforeAndAfter).ToLowerInvariant() -or
    $sourceKeyBefore -cne ([string]$sourceReceipt.sourceFixture.keySha256).ToLowerInvariant() -or
    ([string]$sourceManifest.SourceFixture.DatabaseSha256Before).ToLowerInvariant() -cne ([string]$sourceReceipt.sourceFixture.databaseSha256BeforeAndAfter).ToLowerInvariant()) {
    throw 'Original synthetic seed DB/key differs from the immutable fixture preparation receipt.'
}
$sourceFileManifest = Get-Content -LiteralPath $sourceFixtureManifestPath -Raw | ConvertFrom-Json
$sourceManifestCopyHash = (Get-FileHash -LiteralPath $sourceFixtureManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($sourceFileManifest.Schema -cne 'lf-current-browser-seed-v1' -or
    $sourceManifest.SourceManifestCopy.Path -cne 'source-fixture-manifest.json' -or
    ([string]$sourceManifest.SourceManifestCopy.Sha256).ToLowerInvariant() -cne $sourceManifestCopyHash) { throw 'Copied source-fixture manifest hash does not match the role bundle receipt.' }

$identityPath = Join-Path $webSource 'build-identity.json'
$requiredBuildFiles = @('LogForesight.Web.dll','LogForesight.Core.dll','LogForesight.Web.deps.json','LogForesight.Web.runtimeconfig.json','LogForesight.Web.staticwebassets.runtime.json','appsettings.json')
foreach ($name in $requiredBuildFiles) { if (!(Test-Path -LiteralPath (Join-Path $webSource $name) -PathType Leaf)) { throw "Build output missing $name" } }
if (!(Test-Path -LiteralPath (Join-Path $webSource 'wwwroot') -PathType Container) -and !(Test-Path -LiteralPath $assetSource -PathType Container)) { throw 'Static assets folder missing.' }
if (!(Test-Path -LiteralPath $identityPath -PathType Leaf)) { throw 'Latest build must include build-identity.json; refusing an untraceable Web output.' }
$identity = Get-Content -LiteralPath $identityPath -Raw | ConvertFrom-Json
$sourceIdentityEntries = Assert-BuildIdentity $identity $webSource 'Selected source' $ExpectedSourceCommit
$webEntry = $sourceIdentityEntries['LogForesight.Web']
$coreEntry = $sourceIdentityEntries['LogForesight.Core']

$runId = [guid]::NewGuid().ToString('N')
$runRoot = Join-Path $runsRoot $runId
[IO.Directory]::CreateDirectory($runsRoot) | Out-Null
[IO.Directory]::CreateDirectory($runRoot) | Out-Null
$markerPath = Join-Path $runRoot '.lf-round53-user-matrix-owner.json'
$ownerPath = Join-Path $runRoot 'process-owner.json'
$manifestPath = Join-Path $runRoot 'fixture-manifest.json'
$manifest = [ordered]@{ Schema='lf-round53-user-matrix-fixture-v1'; RunId=$runId; Status='preparing'; BundlePath=$runRoot; SyntheticOnly=$true; CreatedAtUtc=[DateTimeOffset]::UtcNow.ToString('O') }
$runMarker = [ordered]@{ Schema='lf-round53-user-matrix-owner-v1'; RunId=$runId; BundlePath=$runRoot; OwnerPath=$ownerPath; ManifestPath=$manifestPath; ProcessOwnerPath=$ownerPath; Status='preparing'; CreatedAtUtc=[DateTimeOffset]::UtcNow.ToString('O') }
[IO.File]::WriteAllText($markerPath, ($runMarker | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
$sourceProcess = $null
$webProcess = $null
$dotnet = $null
$expectedSourceCommandLine = $null
$expectedWebCommandLine = $null

function Copy-SafeTree([string]$Source, [string]$Destination, [switch]$Assets) {
    foreach ($entryPath in [IO.Directory]::EnumerateFileSystemEntries($Source)) {
        $attr = [IO.File]::GetAttributes($entryPath)
        if (($attr -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse point refused in build/assets: $entryPath" }
        $name = [IO.Path]::GetFileName($entryPath)
        if (($attr -band [IO.FileAttributes]::Directory) -ne 0) {
            if (!$Assets -and $name -in @('logs','Db','keys','exports','backup','wwwroot')) { continue }
            $next = Join-Path $Destination $name; [IO.Directory]::CreateDirectory($next) | Out-Null; Copy-SafeTree $entryPath $next -Assets:$Assets
        } else {
            $to = Join-Path $Destination $name
            if (Test-Path -LiteralPath $to) { throw "Refusing overwrite: $to" }
            [IO.File]::Copy($entryPath, $to, $false)
        }
    }
}
function Get-TreeSha256([string]$Root) {
    $entries = foreach ($file in Get-ChildItem -LiteralPath $Root -File -Recurse | Sort-Object FullName) {
        $relative = [IO.Path]::GetRelativePath($Root, $file.FullName).Replace('\', '/')
        "$relative`n$((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant())`n"
    }
    $bytes = [System.Text.Encoding]::UTF8.GetBytes(($entries -join ''))
    [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

try {
    $appRoot = Join-Path $runRoot 'app'; [IO.Directory]::CreateDirectory($appRoot) | Out-Null
    $assetSourceSha256 = Get-TreeSha256 $assetSource
    Copy-SafeTree $webSource $appRoot
    $runtimeAssets = Join-Path $appRoot 'wwwroot'; [IO.Directory]::CreateDirectory($runtimeAssets) | Out-Null
    Copy-SafeTree $assetSource $runtimeAssets -Assets
    $runtimeAssetsSha256 = Get-TreeSha256 $runtimeAssets
    if ($runtimeAssetsSha256 -cne $assetSourceSha256 -or (Get-TreeSha256 $assetSource) -cne $assetSourceSha256) { throw 'Static assets changed while copying or do not match the selected frozen asset tree.' }
    $staticManifestPath = Join-Path $appRoot 'LogForesight.Web.staticwebassets.runtime.json'
    $staticManifest = Get-Content -LiteralPath $staticManifestPath -Raw | ConvertFrom-Json
    if (@($staticManifest.ContentRoots).Count -ne 1) { throw 'Unexpected static assets manifest roots.' }
    $staticManifest.ContentRoots = @([IO.Path]::GetFullPath($runtimeAssets) + '\')
    [IO.File]::WriteAllText($staticManifestPath, ($staticManifest | ConvertTo-Json -Depth 100 -Compress), [Text.UTF8Encoding]::new($false))

    $dataRoot = Join-Path $runRoot 'data'
    $destDb = Join-Path $dataRoot 'Db\logforesight.db'
    $destKey = Join-Path $dataRoot 'keys\lf-crypto.key'
    $pathLengths = @($destDb, "$destDb-journal", "$destDb-wal", "$destDb-shm", $destKey) | ForEach-Object { [IO.Path]::GetFullPath($_).Length }
    if (($pathLengths | Measure-Object -Maximum).Maximum -ge 240) { throw 'Run-owned SQLite DB/key/sidecar path would reach the Windows path ceiling.' }
    $seedScript = Join-Path $stageRoot 'seed_fixture.py'
    $pythonCandidates = @(Get-Command python.exe -CommandType Application -All -ErrorAction Stop | ForEach-Object { $_.Source } | Select-Object -Unique)
    $python = @($pythonCandidates | Where-Object { $_ -notmatch '\\Microsoft\\WindowsApps\\' -and (Test-Path -LiteralPath $_ -PathType Leaf) }) | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace([string]$python)) { throw 'A real python.exe was not found; WindowsApps execution aliases are not accepted for fixture cloning.' }
    $httpPortListener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0); $httpPortListener.Start(); $httpPort = ([Net.IPEndPoint]$httpPortListener.LocalEndpoint).Port; $httpPortListener.Stop()
    do { $smtpPortListener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0); $smtpPortListener.Start(); $smtpPort = ([Net.IPEndPoint]$smtpPortListener.LocalEndpoint).Port; $smtpPortListener.Stop() } while ($smtpPort -eq $httpPort)
    $httpBaseUrl = "http://127.0.0.1:$httpPort"
    $seedOutput = & $python $seedScript $sourceDb $sourceKey $destDb $destKey $httpBaseUrl $smtpPort $runId 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Isolated DB/key clone and safety seeding failed: $($seedOutput -join ' ')" }
    if ((Get-FileHash -LiteralPath $sourceDb -Algorithm SHA256).Hash.ToLowerInvariant() -cne $sourceDbBefore -or
        (Get-FileHash -LiteralPath $sourceKey -Algorithm SHA256).Hash.ToLowerInvariant() -cne $sourceKeyBefore) { throw 'Source fixture DB/key changed while creating the clone.' }
    $seedReceipt = ($seedOutput | Where-Object { $_ -is [string] -and $_.Trim().StartsWith('{') } | Select-Object -Last 1) | ConvertFrom-Json
    if ($seedReceipt.sourceUnchanged -ne $true -or $seedReceipt.localHostAnalysisEnabled -ne $false) { throw 'Fixture seed receipt did not confirm source immutability and local host analysis disabled.' }
    # Capture clone hashes before Web opens SQLite. Windows may deny a file-level hash
    # read while Microsoft.Data.Sqlite holds the database open; this is the exact seeded
    # state handed to the runtime, and later Web mutations are intentionally separate.
    $cloneDbSha256BeforeLaunch = (Get-FileHash -LiteralPath $destDb -Algorithm SHA256).Hash.ToLowerInvariant()
    $cloneKeySha256BeforeLaunch = (Get-FileHash -LiteralPath $destKey -Algorithm SHA256).Hash.ToLowerInvariant()

    $helperRoot = Join-Path $runRoot 'helper'
    $dotnet = (Get-Command dotnet.exe -CommandType Application -ErrorAction Stop).Source
    $project = Join-Path $stageRoot 'source-stub\Round53ControlledSources.csproj'
    $buildOutput = & $dotnet build $project -c Release --nologo -o $helperRoot 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Owned source helper build failed: $($buildOutput -join ' ')" }
    $stubDll = Join-Path $helperRoot 'Round53ControlledSources.dll'
    $nativeSqlite = Join-Path $appRoot 'runtimes\win-x64\native\e_sqlite3.dll'
    foreach ($required in @($stubDll, $nativeSqlite)) { if (!(Test-Path -LiteralPath $required)) { throw "Runtime dependency missing: $required" } }
    if ([IO.Path]::GetFullPath($nativeSqlite).Length -ge 240) { throw 'Run-owned native SQLite DLL path would reach the Windows path ceiling.' }

    # Independently read the identities embedded in the actual copied assemblies. Compile this tiny
    # reader under the run-owned directory so validation does not touch the product's bin/obj paths.
    $identitySource = Join-Path $runRoot 'identity-source'; [IO.Directory]::CreateDirectory($identitySource) | Out-Null
    $identityProject = Join-Path $identitySource 'BuildIdentityManifest.csproj'
    $identityProgram = Join-Path $identitySource 'Program.cs'
    [IO.File]::Copy((Join-Path (Split-Path $stageRoot -Parent) 'build-identity\BuildIdentityManifest.csproj'), $identityProject, $false)
    [IO.File]::Copy((Join-Path (Split-Path $stageRoot -Parent) 'build-identity\Program.cs'), $identityProgram, $false)
    $identityOutput = Join-Path $runRoot 'identity-helper'
    $identityBuild = & $dotnet build $identityProject -c Release --nologo -o $identityOutput 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Run-owned build identity reader failed to build: $($identityBuild -join ' ')" }
    $identityReader = Join-Path $identityOutput 'BuildIdentityManifest.dll'
    $actualIdentityPath = Join-Path $runRoot 'actual-build-identity.json'
    $copiedIdentityPath = Join-Path $appRoot 'build-identity.json'
    if ((Get-FileHash -LiteralPath $copiedIdentityPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne (Get-FileHash -LiteralPath $identityPath -Algorithm SHA256).Hash.ToLowerInvariant()) { throw 'Copied build identity manifest differs from the selected source manifest.' }
    $copiedIdentity = Get-Content -LiteralPath $copiedIdentityPath -Raw | ConvertFrom-Json
    $copiedIdentityEntries = Assert-BuildIdentity $copiedIdentity $appRoot 'Copied runtime' $ExpectedSourceCommit
    foreach ($name in @('LogForesight.Web','LogForesight.Core')) {
        if ((Get-FileHash -LiteralPath (Join-Path $appRoot ($name + '.dll')) -Algorithm SHA256).Hash.ToLowerInvariant() -cne [string]$sourceIdentityEntries[$name].FileSha256) { throw "Copied runtime $name.dll differs from the selected build source." }
    }
    $identityRead = & $dotnet $identityReader $appRoot $actualIdentityPath 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Could not read identity from actual Web/Core assemblies: $($identityRead -join ' ')" }
    $actualIdentity = Get-Content -LiteralPath $actualIdentityPath -Raw | ConvertFrom-Json
    $actualIdentityEntries = Assert-BuildIdentity $actualIdentity $appRoot 'Independently read runtime' $ExpectedSourceCommit
    foreach ($name in @('LogForesight.Web','LogForesight.Core')) {
        $expected = @($identity.Assemblies | Where-Object { $_.Name -ceq $name })[0]
        $actual = @($actualIdentity.Assemblies | Where-Object { $_.Name -ceq $name })[0]
        foreach ($field in @('InformationalVersion','ModuleVersionId','FileSha256','SourceStamp','SourceRevisionIdOverride')) {
            if ([string]$expected.$field -cne [string]$actual.$field) { throw "Build identity manifest does not match actual $name assembly metadata ($field)." }
        }
    }
    $modeHttp = Join-Path $runRoot 'source-mode.txt'; $modeSmtp = Join-Path $runRoot 'smtp-mode.txt'
    [IO.File]::WriteAllText($modeHttp, 'netiq-healthy', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($modeSmtp, 'accept', [Text.UTF8Encoding]::new($false))
    $sourceLogs = Join-Path $runRoot 'source-stub.log'; $sourceErrors = Join-Path $runRoot 'source-stub.err'
    $sourceArgs = @($stubDll, $runId, $httpBaseUrl, [string]$smtpPort, $modeHttp, $modeSmtp, $runRoot)
    $sourceArgLine = ($sourceArgs | ForEach-Object { '"' + $_ + '"' }) -join ' '
    $expectedSourceCommandLine = '"' + [IO.Path]::GetFullPath($dotnet) + '" ' + $sourceArgLine
    $sourceProcess = Start-Process -FilePath $dotnet -ArgumentList $sourceArgLine -WorkingDirectory $helperRoot -WindowStyle Hidden -RedirectStandardOutput $sourceLogs -RedirectStandardError $sourceErrors -PassThru
    $webPortListener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0); $webPortListener.Start(); $webPort = ([Net.IPEndPoint]$webPortListener.LocalEndpoint).Port; $webPortListener.Stop()
    $webBaseUrl = "http://127.0.0.1:$webPort"
    $runtimeDll = Join-Path $appRoot 'LogForesight.Web.dll'
    $webArgs = @($runtimeDll,'--contentRoot',$appRoot,'--urls',$webBaseUrl)
    $webArgLine = ($webArgs | ForEach-Object { '"' + $_ + '"' }) -join ' '
    $expectedWebCommandLine = '"' + [IO.Path]::GetFullPath($dotnet) + '" ' + $webArgLine
    $webEnvironment = @{
        ASPNETCORE_ENVIRONMENT='Development'; DOTNET_ENVIRONMENT='Development'; ASPNETCORE_URLS=$webBaseUrl
        Auth__Provider=$AuthenticationProvider; Storage__Type='Sqlite'; Storage__DataRoot=$dataRoot; Storage__ConnectionString=''
        Server__PathBase='/browser-fixture'; DOTNET_NOLOGO='1'; PATH=(Join-Path $appRoot 'runtimes\win-x64\native') + ';' + $env:PATH
    }
    if ($AuthenticationProvider -ceq 'Ad') {
        $webEnvironment['Auth__ServerAdmin__Account'] = 'serverAdmin'
        $webEnvironment['Auth__ServerAdmin__PasswordHash'] = $serverAdminPasswordHash
    }
    $webProcess = Start-Process -FilePath $dotnet -ArgumentList $webArgLine -WorkingDirectory $appRoot -WindowStyle Hidden -RedirectStandardOutput (Join-Path $runRoot 'web.stdout.log') -RedirectStandardError (Join-Path $runRoot 'web.stderr.log') -Environment $webEnvironment -PassThru
    $ready = $false
    for ($attempt = 0; $attempt -lt 100; $attempt++) {
        if ($sourceProcess.HasExited -or $webProcess.HasExited) { break }
        try {
            $stubHealth = Invoke-RestMethod -Uri "$httpBaseUrl/__fixture/health" -TimeoutSec 1
            $login = Invoke-WebRequest -Uri "$webBaseUrl/browser-fixture/login" -UseBasicParsing -TimeoutSec 1
            if ($stubHealth.runId -eq $runId -and $login.StatusCode -lt 500) { $ready = $true; break }
        } catch { }
        Start-Sleep -Milliseconds 200
    }
    if (!$ready) { throw 'Owned Web/source fixture did not reach its loopback readiness checks.' }

    function Read-ProcessOwner([Diagnostics.Process]$Process, [string]$Kind, [int[]]$Ports, [string]$ExpectedCommandLine) {
        $Process.Refresh(); $cim = Get-CimInstance Win32_Process -Filter "ProcessId = $($Process.Id)" -ErrorAction Stop
        if ($Process.HasExited -or [IO.Path]::GetFullPath($cim.ExecutablePath) -ine [IO.Path]::GetFullPath($dotnet) -or [string]$cim.CommandLine -cne $ExpectedCommandLine) { throw "$Kind process identity mismatch during start." }
        [ordered]@{ Kind=$Kind; ProcessId=$Process.Id; StartTimeUtcTicks=$Process.StartTime.ToUniversalTime().Ticks; Executable=[IO.Path]::GetFullPath($cim.ExecutablePath); CommandLine=[string]$cim.CommandLine; Ports=$Ports; AssemblyPath=if($Kind -eq 'web'){$runtimeDll}else{$stubDll}; AssemblySha256=if($Kind -eq 'web'){(Get-FileHash -LiteralPath $runtimeDll -Algorithm SHA256).Hash.ToLowerInvariant()}else{(Get-FileHash -LiteralPath $stubDll -Algorithm SHA256).Hash.ToLowerInvariant()} }
    }
    $sourceOwner = Read-ProcessOwner $sourceProcess 'sources' @($httpPort,$smtpPort) $expectedSourceCommandLine
    $webOwner = Read-ProcessOwner $webProcess 'web' @($webPort) $expectedWebCommandLine
    $fixtureManifest = [ordered]@{
        Schema='lf-round53-user-matrix-fixture-v1'; RunId=$runId; Status='running'; SyntheticOnly=$true
        CreatedAtUtc=[DateTimeOffset]::UtcNow.ToString('O'); BundlePath=$runRoot
        SourceBuild=[ordered]@{ ExpectedSourceCommit=$ExpectedSourceCommit; Directory=$webSource; IdentityPath=$identityPath; IdentitySha256=(Get-FileHash -LiteralPath $identityPath -Algorithm SHA256).Hash.ToLowerInvariant(); ActualIdentityPath=$actualIdentityPath; SourceStamp=[string]$webEntry.SourceStamp; SourceRevision=[string]$webEntry.SourceRevisionIdOverride; WebDllSha256=(Get-FileHash -LiteralPath (Join-Path $webSource 'LogForesight.Web.dll') -Algorithm SHA256).Hash.ToLowerInvariant(); CoreDllSha256=(Get-FileHash -LiteralPath (Join-Path $webSource 'LogForesight.Core.dll') -Algorithm SHA256).Hash.ToLowerInvariant(); StaticAssetsSourceDirectory=$assetSource; StaticAssetsSourceSha256=$assetSourceSha256; StaticAssetsRuntimeSha256=$runtimeAssetsSha256 }
        SourceFixture=[ordered]@{ Bundle=$sourceBundle; RunId=$sourceManifest.RunId; DatabaseSha256Before=$sourceDbBefore; DatabaseSha256After=(Get-FileHash -LiteralPath $sourceDb -Algorithm SHA256).Hash.ToLowerInvariant(); KeySha256Before=$sourceKeyBefore; KeySha256After=(Get-FileHash -LiteralPath $sourceKey -Algorithm SHA256).Hash.ToLowerInvariant(); CloneDatabaseSha256BeforeLaunch=$cloneDbSha256BeforeLaunch; CloneKeySha256BeforeLaunch=$cloneKeySha256BeforeLaunch; AccountManifest=$sourceManifest.Accounts; RunRoles=$sourceManifest.RolesForU05; InheritedAccountsAndData=$sourceManifest.InheritedAccountsAndData }
        Runtime=[ordered]@{ DataRoot=$dataRoot; WebBaseUrl=$webBaseUrl; PathBaseUrl="$webBaseUrl/browser-fixture"; SourceHttpBaseUrl=$httpBaseUrl; SourceSmtpEndpoint="127.0.0.1:$smtpPort"; AuthenticationProvider=$AuthenticationProvider; ServerAdminAccount=if($AuthenticationProvider -ceq 'Ad'){'serverAdmin'}else{$null}; ServerAdminPasswordRequired=($AuthenticationProvider -ceq 'Ad'); ServerAdminPasswordHashSha256=if($null -ne $serverAdminPasswordHash){[Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes($serverAdminPasswordHash))).ToLowerInvariant()}else{$null}; InitialHttpMode='netiq-healthy'; InitialSmtpMode='accept'; ExternalConnectionsEnabled=$false; ScheduleEnabled=$false; LocalHostAnalysisEnabled=$false; MailEnabled=$false; PrtgEnabled=$false; AiUrlEmpty=$true }
        Processes=@($sourceOwner,$webOwner)
        EvidenceLedger=(Get-Content -LiteralPath (Join-Path $stageRoot 'user-matrix.json') -Raw | ConvertFrom-Json)
        Limitations=@('The fixture is synthetic and loopback-only.', 'Run outcomes must be created through normal UI actions; the launcher writes no run, risk, finding, case, mail or progress outcomes.', 'The SMTP sink proves only local server acceptance/capture, not delivery to any external inbox.', 'Stub success/failure does not establish native source qualification or production outcomes.')
    }
    [IO.File]::WriteAllText($manifestPath, ($fixtureManifest | ConvertTo-Json -Depth 60), [Text.UTF8Encoding]::new($false))
    $runMarker.Status='running'
    [IO.File]::WriteAllText($markerPath, ($runMarker | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
    $owner = [ordered]@{ Schema='lf-round53-user-matrix-process-owner-v1'; RunId=$runId; BundlePath=$runRoot; DotnetPath=[IO.Path]::GetFullPath($dotnet); HttpModeFile=$modeHttp; SmtpModeFile=$modeSmtp; SourceHttpBaseUrl=$httpBaseUrl; SourceSmtpPort=$smtpPort; WebBaseUrl=$webBaseUrl; Processes=@($sourceOwner,$webOwner); Status='running'; UpdatedAtUtc=[DateTimeOffset]::UtcNow.ToString('O') }
    [IO.File]::WriteAllText($ownerPath, ($owner | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
    [pscustomobject]@{ RunId=$runId; BundlePath=$runRoot; OwnerFile=$ownerPath; ManifestPath=$manifestPath; LoginUrl="$webBaseUrl/browser-fixture/login"; SourceHttpBaseUrl=$httpBaseUrl; SourceSmtpEndpoint="127.0.0.1:$smtpPort"; Status='running'; ExpectedSourceCommit=$ExpectedSourceCommit; BuildSourceStamp=$webEntry.SourceStamp }
}
catch {
    foreach ($process in @($webProcess,$sourceProcess)) {
        if ($null -ne $process) {
            try {
                $process.Refresh()
                if (!$process.HasExited) {
                    $cim = Get-CimInstance Win32_Process -Filter "ProcessId = $($process.Id)" -ErrorAction Stop
                    $expectedLine = if ($null -ne $sourceProcess -and $process.Id -eq $sourceProcess.Id) { $expectedSourceCommandLine } else { $expectedWebCommandLine }
                    if (![string]::IsNullOrWhiteSpace([string]$expectedLine) -and
                        [IO.Path]::GetFullPath($cim.ExecutablePath) -ieq [IO.Path]::GetFullPath($dotnet) -and
                        [string]$cim.CommandLine -ceq [string]$expectedLine -and
                        $process.StartTime.ToUniversalTime().Ticks -gt 0) { Stop-Process -Id $process.Id -Force }
                }
            } catch { }
        }
    }
    if (Test-Path -LiteralPath $markerPath) { try { $runMarker.Status='failed'; $runMarker.Failure=$_.Exception.Message; [IO.File]::WriteAllText($markerPath, ($runMarker | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false)) } catch { } }
    throw
}
