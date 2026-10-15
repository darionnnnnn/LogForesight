param([string]$RepositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..')))
$ErrorActionPreference='Stop'
Import-Module (Join-Path $RepositoryRoot 'scripts/prtg-workload/qualification-preparation/E2Contract.psm1') -Force
# Exercise shipped functions without executing the full workload preflight.
$errors=$null;$tokens=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $RepositoryRoot 'scripts/prtg-workload/qualification-preparation/Prepare-Qualification.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Production script parse failed.'}
foreach($name in @('Assert-NoReparse','Invoke-Api')) {
    $definition=@($ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -CEQ $name)
    if($definition.Count -ne 1){throw "Expected one shipped $name function."}
    Invoke-Expression $definition[0].Extent.Text
}
Add-Type -TypeDefinition @'
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
public sealed class E2CountedTransport : HttpMessageHandler {
 public int Count;
 protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken t) {
  Count++;
  return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
   Content=new StringContent("{\"success\":true,\"data\":{\"accepted\":true}}",Encoding.UTF8,"application/json")
  });
 }
}
'@
$ownedTemp=Join-Path ([IO.Path]::GetTempPath()) ('e2-request-fence-'+[guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $ownedTemp
$descriptor=Join-Path $ownedTemp 'provider.json'
[IO.File]::WriteAllText($descriptor,'{"source":"owned-a"}')
$descriptorHash=(Get-FileHash -LiteralPath $descriptor -Algorithm SHA256).Hash
$preflight=@{dataRoot=$ownedTemp;databasePath=(Join-Path $ownedTemp 'not-opened.db')}
$BaseUri=[uri]'http://127.0.0.1:51900/'
$MaxBytes=512KB
$whole=[Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(15))
$transport=[E2CountedTransport]::new()
$client=[Net.Http.HttpClient]::new($transport)
$owner=@{}
foreach($name in @('processId','processStartUtcTicks','assemblyPath','assemblySha256','webAssemblyMvid','coreAssemblyPath','coreAssemblySha256','coreAssemblyMvid','commandTokenCount','commandTokensSha256','dataRoot','provider','databasePath','descriptorSha256','localAddress','port')){$owner[$name]='fixed-owned-value'}
# Cover dispatch after a successful owner check; runtime identity is tested separately.
$script:driftDuringOwner=$false
function Assert-OwnedListener {
    param($ExpectedRoot,$ExpectedDatabase,$Provider,$DescriptorHash)
    if($script:driftDuringOwner){[IO.File]::WriteAllText($descriptor,'{"source":"owned-b"}')}
    return $owner
}
try {
    $response=Invoke-Api POST 'api/test-owned-fence' @{value=1}
    if(-not $response.GetProperty('accepted').GetBoolean() -or $transport.Count -ne 1){throw 'Valid dispatch was not sent exactly once.'}
    $script:driftDuringOwner=$true
    try { $null=Invoke-Api POST 'api/test-owned-fence' @{value=2};throw 'Descriptor drift reached dispatch.' }
    catch {if($_.Exception.Message -notmatch 'descriptor file changed'){throw}}
    if($transport.Count -ne 1){throw 'A second request escaped the descriptor fence.'}
    [pscustomobject]@{Result='PASS';ValidRequests=1;NewRequestsAfterDescriptorDrift=0;FullWorkloadPreflightRun=$false;DatabaseOpened=$false;ExternalNetworkUsed=$false}|ConvertTo-Json
} finally {
    $client.Dispose();$whole.Dispose()
    Remove-Item -LiteralPath $descriptor -Force
    Remove-Item -LiteralPath $ownedTemp -Force
}
