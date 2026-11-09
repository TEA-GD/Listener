@echo off
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-windows.ps1"
if errorlevel 1 (
  echo BUILD FAILED - see error messages above.
) else (
  echo BUILD SUCCEEDED. Look in ..\output\windows-installer\WatcherInstaller.exe
)
pause
