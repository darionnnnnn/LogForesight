#Requires -Version 7.0
<#
Read-only admission check for an isolated workload run. Uses an existing authenticated
WebRequestSession; does not log in, start/resume jobs, create bindings, or poll indefinitely.
Only explicit numeric loopback addresses are allowed. A Ready receipt is a prerequisite
for the workload matrix, never proof of native source, throughput, or retention acceptance.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][uri] $BaseUri,
    [Parameter(Mandatory)][string] $ManifestPath,
    [Parameter(Mandatory)][string] $AssemblyPath,
    [Parameter(Mandatory)][string] $OutputDirectory,
    [Parameter(Mandatory)][Microsoft.PowerShell.Commands.WebRequestSession] $WebSession,
    [ValidateRange(1,600)][int] $MaxSeconds = 300
)
$ErrorActionPreference = 'Stop'
$address = $null
if ($BaseUri.Scheme -notin @('http','https') -or
    -not [Net.IPAddress]::TryParse($BaseUri.DnsSafeHost.Trim('[',']'), [ref]$address) -or
    -not [Net.IPAddress]::IsLoopback($address) -or $BaseUri.Port -lt 1 -or
    $BaseUri.UserInfo -or $BaseUri.Query -or $BaseUri.Fragment) {
    throw 'Coverage collection requires an explicit isolated numeric loopback URL without credentials, query or fragment.'
}
$base = [uri]($BaseUri.AbsoluteUri.TrimEnd('/') + '/')
$manifestFile = Get-Item -LiteralPath $ManifestPath
if ($manifestFile.Length -gt 512KB) { throw 'Coverage manifest exceeds 512 KiB.' }
$assemblyFile = Get-Item -LiteralPath $AssemblyPath
$assemblyHash = (Get-FileHash -LiteralPath $assemblyFile.FullName -Algorithm SHA256).Hash
$manifestBytes = [IO.File]::ReadAllBytes($manifestFile.FullName)
$manifestHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($manifestBytes))
$options = [Text.Json.JsonDocumentOptions]::new()
$options.MaxDepth = 32
$manifestDocument = [Text.Json.JsonDocument]::Parse([ReadOnlyMemory[byte]]$manifestBytes, $options)
$documents = [Collections.Generic.List[Text.Json.JsonDocument]]::new()
$documents.Add($manifestDocument)
$manifest = $manifestDocument.RootElement
$jobId = $manifest.GetProperty('jobId').GetString()
$ids = $manifest.GetProperty('sensorIds')
if ($jobId -cnotmatch '^[a-fA-F0-9]{32}$' -or $ids.ValueKind -ne 'Array' -or
    $ids.GetArrayLength() -lt 1 -or $ids.GetArrayLength() -gt 15000) {
    $manifestDocument.Dispose()
    throw 'Coverage manifest requires a job ID and 1..15000 sensor IDs.'
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { $manifestDocument.Dispose(); throw 'Coverage output must be a new evidence directory.' }
$null = New-Item -ItemType Directory -Path $output
$script:totalBytes = 0L
function Save-NewBytes([string]$name, [byte[]]$bytes) {
    $script:totalBytes += $bytes.Length
    if ($script:totalBytes -gt 64MB) { throw 'Coverage evidence exceeds the 64 MiB aggregate limit.' }
    $file = [IO.File]::Open((Join-Path $output $name), [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
    try { $file.Write($bytes); $file.Flush($true) } finally { $file.Dispose() }
}
Save-NewBytes 'manifest.json' $manifestBytes
$handler = [Net.Http.HttpClientHandler]::new()
$handler.AllowAutoRedirect = $false
$handler.UseCookies = $true
$handler.CookieContainer = $WebSession.Cookies
$client = [Net.Http.HttpClient]::new($handler)
$client.Timeout = [Threading.Timeout]::InfiniteTimeSpan
$deadline = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds($MaxSeconds))
$started = [DateTimeOffset]::UtcNow
function Read-ApiSnapshot([string]$relativePath, [string]$fileName, [int]$maxBytes) {
    $requestDeadline = [Threading.CancellationTokenSource]::CreateLinkedTokenSource($deadline.Token)
    $requestDeadline.CancelAfter([TimeSpan]::FromSeconds(30))
    $response = $null; $stream = $null; $body = [IO.MemoryStream]::new()
    try {
        $response = $client.GetAsync([uri]::new($base, $relativePath),
            [Net.Http.HttpCompletionOption]::ResponseHeadersRead, $requestDeadline.Token).GetAwaiter().GetResult()
        if (-not $response.IsSuccessStatusCode) { throw 'Isolated coverage API refused the read; no partial receipt is accepted.' }
        if ($response.Content.Headers.ContentType.MediaType -ne 'application/json') { throw 'Coverage API returned a non-JSON body.' }
        if ($response.Content.Headers.ContentLength -gt $maxBytes) { throw 'Coverage API body exceeds its declared limit.' }
        $stream = $response.Content.ReadAsStreamAsync($requestDeadline.Token).GetAwaiter().GetResult()
        $buffer = [byte[]]::new(8192)
        while (($read = $stream.ReadAsync($buffer,0,$buffer.Length,$requestDeadline.Token).GetAwaiter().GetResult()) -gt 0) {
            if ($body.Length + $read -gt $maxBytes) { throw 'Coverage API body exceeds its streaming limit.' }
            $body.Write($buffer,0,$read)
        }
        $bytes = $body.ToArray()
        $document = [Text.Json.JsonDocument]::Parse([ReadOnlyMemory[byte]]$bytes, $options)
        $documents.Add($document)
        Save-NewBytes $fileName $bytes
        return $document.RootElement
    } finally {
        if ($stream) { $stream.Dispose() }
        if ($response) { $response.Dispose() }
        $body.Dispose(); $requestDeadline.Dispose()
    }
}
$receipt = $null
try {
    $loadedAssembly = [Reflection.Assembly]::LoadFrom($assemblyFile.FullName)
    $verifierType = [LogForesight.WorkloadAcceptance.QualificationCoverage]
    if (-not $loadedAssembly.Location -or -not $verifierType.Assembly.Location -or
        (Get-FileHash -LiteralPath $loadedAssembly.Location -Algorithm SHA256).Hash -ne $assemblyHash -or
        (Get-FileHash -LiteralPath $verifierType.Assembly.Location -Algorithm SHA256).Hash -ne $assemblyHash) {
        throw 'Loaded coverage verifier does not match the requested assembly.'
    }
    $prefix = 'api/prtg/monitoring/trusted-sampling/'
    $before = Read-ApiSnapshot ($prefix + 'qualification-jobs/current') 'job-before.json' 16KB
    $pages = [Collections.Generic.List[Text.Json.JsonElement]]::new()
    $profilePages = [Collections.Generic.List[Text.Json.JsonElement]]::new()
    if ($before.GetProperty('data').GetProperty('status').GetString() -ceq 'completed') {
        for ($offset = 0; $offset -lt $ids.GetArrayLength(); $offset += 100) {
            $pages.Add((Read-ApiSnapshot ($prefix + "qualification-jobs/$jobId/page?offset=$offset&limit=100") ("raw-{0:D5}.json" -f $offset) 128KB))
            $profilePages.Add((Read-ApiSnapshot ($prefix + "profiles?offset=$offset&limit=100") ("profiles-{0:D5}.json" -f $offset) 512KB))
        }
    }
    $after = Read-ApiSnapshot ($prefix + 'qualification-jobs/current') 'job-after.json' 16KB
    $result = [LogForesight.WorkloadAcceptance.QualificationCoverage]::Verify($manifest,$before,$pages,$profilePages,$after)
    if ((Get-FileHash -LiteralPath $assemblyFile.FullName -Algorithm SHA256).Hash -ne $assemblyHash -or
        (Get-FileHash -LiteralPath $manifestFile.FullName -Algorithm SHA256).Hash -ne $manifestHash) {
        throw 'Coverage collector input changed during collection.'
    }
    $receipt = [ordered]@{
        schema='prtg-workload-qualification-coverage-v1'; ready=$result.Ready; reason=$result.Reason
        expectedSensors=$result.ExpectedSensors; qualifiedSensors=$result.QualifiedSensors; readyProfiles=$result.ReadyProfiles
        jobId=$jobId; scopeFingerprint=$manifest.GetProperty('scopeFingerprint').GetString()
        manifestSha256=$manifestHash; verifierSha256=$assemblyHash
        startedAtUtc=$started.ToString('o'); completedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')
        nativeSourceVerified=$false; capacityAccepted=$false; retentionAccepted=$false; wholeRoundAccepted=$false
    }
} catch {
    $receipt = [ordered]@{
        schema='prtg-workload-qualification-coverage-v1'; ready=$false; reason='collection-failed'
        manifestSha256=$manifestHash; verifierSha256=$assemblyHash
        startedAtUtc=$started.ToString('o'); completedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')
        nativeSourceVerified=$false; capacityAccepted=$false; retentionAccepted=$false; wholeRoundAccepted=$false
    }
} finally {
    $client.Dispose(); $deadline.Dispose()
    foreach ($document in $documents) { $document.Dispose() }
}
Save-NewBytes 'coverage-receipt.json' ([Text.Encoding]::UTF8.GetBytes(($receipt | ConvertTo-Json -Depth 5)))
[pscustomobject]$receipt
