@echo off
if "%~1"=="" (
  powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File "%~dp0Launch-Windows.ps1"
) else (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start-Windows.ps1" %*
)
if errorlevel 1 (
  pause
  exit /b 1
)
