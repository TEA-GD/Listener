@echo off
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-windows-all.ps1"
if errorlevel 1 (
  echo BUILD FAILED. Review messages above.
  pause
  exit /b 1
)
echo Completed. Press any key to exit.
pause >nul
