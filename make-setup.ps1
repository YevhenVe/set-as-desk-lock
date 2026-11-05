# Produces dist\SetDeskLock-Setup.exe, the single file to share. It holds the public certificate only.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
& (Join-Path $root 'package.ps1')

$fw = "$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319"
$out = Join-Path $root 'dist\SetDeskLock-Setup.exe'
& "$fw\csc.exe" /nologo /target:winexe /optimize+ /codepage:65001 "/win32icon:$root\assets\SetDeskLock.ico" "/out:$out" `
    "/r:$fw\System.Windows.Forms.dll" "/r:$fw\System.Drawing.dll" `
    "/resource:$root\dist\SetDeskLock.msix,SetDeskLock.msix" `
    "/resource:$root\dist\SetDeskLock.cer,SetDeskLock.cer" `
    "$root\Setup.cs"
if ($LASTEXITCODE -ne 0) { throw 'Compiling Setup.cs failed' }
Write-Output "Created $out"
