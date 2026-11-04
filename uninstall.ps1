$subject = 'CN=SetDeskLock Local'

Get-AppxPackage -Name SetDeskLock | Remove-AppxPackage
Remove-Item 'HKCU:\Software\Classes\SystemFileAssociations\image\shell\SetDeskLock' -Recurse -ErrorAction SilentlyContinue

$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $subject } | Select-Object -First 1
if ($cert) {
    $path = "Cert:\LocalMachine\TrustedPeople\$($cert.Thumbprint)"
    if (Test-Path $path) {
        Start-Process powershell.exe -Verb RunAs -Wait -WindowStyle Hidden `
            -ArgumentList '-NoProfile', '-Command', "Remove-Item '$path'"
    }
    Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)"
}
Write-Output 'Removed.'
