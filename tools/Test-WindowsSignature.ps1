[CmdletBinding()]
param([string]$FixtureDirectory = "$PSScriptRoot/../tests/fixtures/windows-signature")

$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Run this test on Windows.' }
if ($env:GITHUB_ACTIONS -ne 'true') { throw 'This trust-store test is restricted to disposable GitHub Actions runners.' }

$expectedThumbprint = '7D4F16A72467762AED874F6B9193859E0F157506'
$expectedHashes = @{
    'gdap-acceptor.exe' = '803be717124528cee583141d7616eb59cb5c3d0e5cbfac38b603a0d051312b0c'
    'gdap-acceptor.dll' = '9907af86fb24fdcc28bc01a7fe5c2ea723a616b62c3167338a9b375ca654a4c2'
    'unsigned-control.exe' = '18ad25e1944ee4ceb46d50f072351a7aea71a68b1b365568ab6ed8f746cebd6d'
    'GDAP-Acceptor-Internal.cer' = '4b81e68427403471af859fd30935e8ca84bc8da7022ebffb688b107224ba39c9'
}
foreach ($name in $expectedHashes.Keys) {
    $actual = (Get-FileHash -LiteralPath (Join-Path $FixtureDirectory $name) -Algorithm SHA256).Hash
    if ($actual -ne $expectedHashes[$name]) { throw "Fixture hash mismatch: $name" }
    Write-Output "HASH $name $actual"
}

$unsigned = Get-AuthenticodeSignature -LiteralPath (Join-Path $FixtureDirectory 'unsigned-control.exe')
if ($unsigned.Status -ne 'NotSigned' -or $null -ne $unsigned.SignerCertificate) { throw 'Unsigned negative control was not rejected.' }
Write-Output 'PASS: unsigned control has no signer and is NotSigned.'

$stores = @('Cert:\CurrentUser\Root', 'Cert:\CurrentUser\TrustedPublisher')
foreach ($store in $stores) {
    if (Test-Path -LiteralPath "$store\$expectedThumbprint") { throw 'Disposable runner already trusts this certificate; refusing to change pre-existing trust.' }
}

$addedStores = @()
$tamperedFile = Join-Path $env:RUNNER_TEMP "gdap-signature-tampered-$([Guid]::NewGuid().ToString('N')).exe"
try {
    foreach ($name in @('gdap-acceptor.exe', 'gdap-acceptor.dll')) {
        $signature = Get-AuthenticodeSignature -LiteralPath (Join-Path $FixtureDirectory $name)
        Write-Output "BEFORE TRUST $name Status=$($signature.Status) Message=$($signature.StatusMessage)"
        if ($null -eq $signature.SignerCertificate) { throw "Windows cannot enumerate the embedded signer: $name" }
        if ($signature.SignerCertificate.Thumbprint -ne $expectedThumbprint) { throw "Unexpected signer: $name" }
        Write-Output "PASS: Windows enumerates $($signature.SignerCertificate.Subject) before certificate import."
    }

    foreach ($store in $stores) {
        $null = Import-Certificate -FilePath (Join-Path $FixtureDirectory 'GDAP-Acceptor-Internal.cer') -CertStoreLocation $store
        $addedStores += $store
    }
    foreach ($name in @('gdap-acceptor.exe', 'gdap-acceptor.dll')) {
        $signature = Get-AuthenticodeSignature -LiteralPath (Join-Path $FixtureDirectory $name)
        Write-Output "AFTER TRUST $name Status=$($signature.Status) Message=$($signature.StatusMessage)"
        if ($signature.Status -ne 'Valid') { throw "Windows signature validation failed: $name" }
        if ($signature.SignerCertificate.Thumbprint -ne $expectedThumbprint) { throw "Unexpected signer after import: $name" }
        if ($null -eq $signature.TimeStamperCertificate) { throw "Windows did not recognize the timestamp: $name" }
        Write-Output "PASS: Windows validates the signature and recognizes timestamp signer $($signature.TimeStamperCertificate.Subject)."
    }

    $bytes = [IO.File]::ReadAllBytes((Join-Path $FixtureDirectory 'gdap-acceptor.exe'))
    $bytes[128] = $bytes[128] -bxor 1
    [IO.File]::WriteAllBytes($tamperedFile, $bytes)
    $tampered = Get-AuthenticodeSignature -LiteralPath $tamperedFile
    Write-Output "TAMPERED Status=$($tampered.Status)"
    if ($tampered.Status -ne 'HashMismatch') { throw 'Tampered negative control was not detected as HashMismatch.' }
    Write-Output 'PASS: modified payload is rejected.'
} finally {
    foreach ($store in $addedStores) { Remove-Item -LiteralPath "$store\$expectedThumbprint" -ErrorAction Stop }
    if (Test-Path -LiteralPath $tamperedFile) { Remove-Item -LiteralPath $tamperedFile }
}
Write-Output 'PASS: exact delivered binaries verified by Windows. No application launch, CIPP calls or ASR changes performed.'
