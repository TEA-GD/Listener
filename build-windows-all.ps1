$ErrorActionPreference = 'Stop'
$Root = $PSScriptRoot
Write-Host 'Building Watcher Collector (plain TCP, no TLS)...' -ForegroundColor Cyan
& (Join-Path $Root 'Collector\build\build-windows.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Collector build failed.' }
Write-Host 'Building Watcher Agent (plain TCP, no TLS)...' -ForegroundColor Cyan
& (Join-Path $Root 'Agent\build\build-windows.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Agent build failed.' }
Write-Host 'Done. See Collector\output\windows-installer and Agent\output\windows-installer' -ForegroundColor Green
