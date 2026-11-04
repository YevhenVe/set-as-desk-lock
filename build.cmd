@echo off
setlocal
set "FW=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319"
set "WM=%SystemRoot%\System32\WinMetadata"
cd /d "%~dp0"
if not exist bin mkdir bin
"%FW%\csc.exe" /nologo /target:winexe /optimize+ /win32icon:assets\SetDeskLock.ico /out:bin\SetDeskLock.exe ^
  /r:"%FW%\System.Windows.Forms.dll" ^
  /r:"%FW%\System.Runtime.dll" ^
  /r:"%WM%\Windows.Foundation.winmd" ^
  /r:"%WM%\Windows.Storage.winmd" ^
  /r:"%WM%\Windows.System.winmd" ^
  SetDeskLock.cs
exit /b %errorlevel%
