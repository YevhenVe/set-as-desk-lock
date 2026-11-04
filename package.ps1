# Builds and signs dist\SetDeskLock.msix and exports the public certificate. Changes no machine trust.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$subject = 'CN=SetDeskLock Local'
$dist = Join-Path $root 'dist'
$stage = Join-Path $dist 'stage'
$msix = Join-Path $dist 'SetDeskLock.msix'
$cer = Join-Path $dist 'SetDeskLock.cer'

function Find-SdkTool($name) {
    $tool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\$name" -ErrorAction SilentlyContinue |
        Sort-Object FullName | Select-Object -Last 1
    if (-not $tool) { throw "$name not found. Install the Windows SDK first." }
    $tool.FullName
}

function Invoke-Tool($exe, [string[]]$arguments) {
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "$exe failed with exit code $LASTEXITCODE" }
}

& (Join-Path $root 'build.cmd')
if ($LASTEXITCODE -ne 0) { throw 'build.cmd failed' }

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $stage 'assets') | Out-Null
# Every build gets a new version: Windows refuses changed contents under an installed version.
$now = Get-Date
$version = '1.0.{0}.{1}' -f ($now.Date - [datetime]'2026-01-01').Days, [int]$now.TimeOfDay.TotalMinutes
$manifest = (Get-Content (Join-Path $root 'AppxManifest.xml') -Raw) -creplace '(<Identity [^>]*? Version=")[\d.]+', "`${1}$version"
Set-Content (Join-Path $stage 'AppxManifest.xml') $manifest -Encoding UTF8
Copy-Item (Join-Path $root 'bin\SetDeskLock.exe') $stage
Copy-Item (Join-Path $root 'assets\SetDeskLock*.ico') $stage
Copy-Item (Join-Path $root 'assets\*Logo.png') (Join-Path $stage 'assets')

$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $subject } | Select-Object -First 1
if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type Custom -Subject $subject -KeyUsage DigitalSignature `
        -FriendlyName 'SetDeskLock local signing' -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(10) `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
}

Invoke-Tool (Find-SdkTool 'makeappx.exe') @('pack', '/o', '/d', $stage, '/p', $msix)
Invoke-Tool (Find-SdkTool 'signtool.exe') @('sign', '/fd', 'SHA256', '/sha1', $cert.Thumbprint, $msix)
Export-Certificate -Cert $cert -FilePath $cer | Out-Null
