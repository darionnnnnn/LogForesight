$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'E2Contract.psm1') -Force

$descriptorFixture=Join-Path ([IO.Path]::GetTempPath()) ('e2-descriptor-' + [guid]::NewGuid().ToString('N') + '.json')
try {
    [IO.File]::WriteAllText($descriptorFixture,'{"prtgUrl":"http://127.0.0.1:5100/"}',[Text.UTF8Encoding]::new($false))
    $descriptorHash=(Get-FileHash -LiteralPath $descriptorFixture -Algorithm SHA256).Hash
    Assert-E2DescriptorUnchanged $descriptorFixture $descriptorHash
    [IO.File]::WriteAllText($descriptorFixture,'{"prtgUrl":"http://127.0.0.1:5101/"}',[Text.UTF8Encoding]::new($false))
    try {
        Assert-E2DescriptorUnchanged $descriptorFixture $descriptorHash
        throw 'descriptor drift was accepted'
    } catch {
        if($_.Exception.Message -notmatch 'descriptor file changed'){throw}
    }
} finally {
    Remove-Item -LiteralPath $descriptorFixture -Force -ErrorAction SilentlyContinue
}
'E2 descriptor hash checks passed.'
