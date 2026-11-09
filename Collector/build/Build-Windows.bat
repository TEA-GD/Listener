@echo off
setlocal
cd /d "%~dp0.."
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "build\build-windows.ps1"
if errorlevel 1 (
  echo.
  echo BUILD FAILED. Inspect the error messages above.
  pause
  exit /b 1
)
echo.
echo Build complete. See output\windows-installer\WatcherCollectorInstaller.exe
pause
