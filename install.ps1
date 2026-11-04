$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$msix = Join-Path $root 'dist\SetDeskLock.msix'
$cer = Join-Path $root 'dist\SetDeskLock.cer'

& (Join-Path $root 'package.ps1')

$thumbprint = (New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 $cer).Thumbprint
if (-not (Test-Path "Cert:\LocalMachine\TrustedPeople\$thumbprint")) {
    $command = "Import-Certificate -FilePath '$cer' -CertStoreLocation Cert:\LocalMachine\TrustedPeople"
    $process = Start-Process powershell.exe -Verb RunAs -Wait -PassThru -WindowStyle Hidden `
        -ArgumentList '-NoProfile', '-Command', $command
    if ($process.ExitCode -ne 0) { throw 'Could not trust the signing certificate.' }
}

Add-AppxPackage -Path $msix -ForceUpdateFromAnyVersion -ForceApplicationShutdown
Stop-Process -Name explorer -Force
Write-Output 'Installed. Explorer was restarted.'
