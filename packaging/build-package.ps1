# Builds and signs the sparse package for the Windows 11 menu and puts it, with its certificate, in the app folder.
# The certificate is created for this build only; its private key never leaves the build machine.
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

$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=WinDeepSearch' -CertStoreLocation 'Cert:\CurrentUser\My' `
    -KeyUsage DigitalSignature -NotAfter (Get-Date).AddYears(10) `
    -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
$pfx = Join-Path $layout 'signing.pfx'
$password = [guid]::NewGuid().ToString()
Export-PfxCertificate -Cert $cert -FilePath $pfx -Password (ConvertTo-SecureString $password -AsPlainText -Force) | Out-Null
Export-Certificate -Cert $cert -FilePath (Join-Path $AppDir 'WinDeepSearch.cer') | Out-Null

& (Join-Path $tools 'signtool.exe') sign /fd SHA256 /f $pfx /p $password $package
if ($LASTEXITCODE -ne 0) { throw "signtool failed with $LASTEXITCODE" }

Remove-Item $layout -Recurse -Force
Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)"
