# Builds and signs the sparse package for the Windows 11 menu and puts it, with its certificate, in the app folder.
param([Parameter(Mandatory)] [string] $AppDir)
$ErrorActionPreference = 'Stop'

$sdk = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Directory |
    Where-Object { Test-Path (Join-Path $_.FullName 'x64\makeappx.exe') } |
    Sort-Object Name -Descending | Select-Object -First 1
$tools = Join-Path $sdk.FullName 'x64'

$layout = Join-Path ([IO.Path]::GetTempPath()) "wds-package-$([guid]::NewGuid())"
New-Item -ItemType Directory -Path $layout | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'AppxManifest.xml') $layout
Copy-Item (Join-Path $PSScriptRoot 'Assets') $layout -Recurse

$package = Join-Path $AppDir 'WinDeepSearch.msix'
# /nv: a sparse package's executable lives outside the package, which the normal validation rejects.
& (Join-Path $tools 'makeappx.exe') pack /d $layout /p $package /nv /o
if ($LASTEXITCODE -ne 0) { throw "makeappx failed with $LASTEXITCODE" }

$pfx = Join-Path $layout 'signing.pfx'
if ($env:SIGNING_PFX) {
    # The project's certificate (a repository secret): every release is signed alike, so Windows asks to trust it
    # only once.
    [IO.File]::WriteAllBytes($pfx, [Convert]::FromBase64String($env:SIGNING_PFX))
    $password = $env:SIGNING_PASSWORD
} else {
    # Builds without the secret (local builds, pull requests from forks) get a certificate made for this build
    # only; its private key never leaves the build machine.
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=WinDeepSearch' -CertStoreLocation 'Cert:\CurrentUser\My' `
        -KeyUsage DigitalSignature -NotAfter (Get-Date).AddYears(10) `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
    $password = [guid]::NewGuid().ToString()
    Export-PfxCertificate -Cert $cert -FilePath $pfx -Password (ConvertTo-SecureString $password -AsPlainText -Force) | Out-Null
    Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)"
}
# The installer trusts this public part of the certificate.
$public = [Security.Cryptography.X509Certificates.X509Certificate2]::new($pfx, $password)
[IO.File]::WriteAllBytes((Join-Path $AppDir 'WinDeepSearch.cer'), $public.Export('Cert'))
Write-Host "Signing with $($public.Subject), thumbprint $($public.Thumbprint), valid until $($public.NotAfter)"

& (Join-Path $tools 'signtool.exe') sign /fd SHA256 /f $pfx /p $password $package
if ($LASTEXITCODE -ne 0) { throw "signtool failed with $LASTEXITCODE" }

Remove-Item $layout -Recurse -Force
